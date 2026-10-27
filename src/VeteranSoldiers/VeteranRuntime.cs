using System;
using System.Collections.Generic;
using Components;
using Components.RawComponents;
using Components.RawComponents.ResearchComponents;
using Components.SingletonComponents;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using Utility.EnumsStorage;

namespace VeteranSoldiers;

public static partial class VeteranRuntime
{
    public static World? ActiveWorld { get; private set; }
    public static EntityManager Manager => ActiveWorld!.EntityManager;
    private static EntityQuery requests;
    private static EntityQuery clock;
    private static bool queriesValid;
    private static readonly Dictionary<Entity, VeteranRecord> records = new Dictionary<Entity, VeteranRecord>();
    private static readonly Dictionary<Entity, ImpactGroup> impacts = new Dictionary<Entity, ImpactGroup>();
    private static readonly Stack<ImpactGroup> pool = new Stack<ImpactGroup>();
    private static readonly List<Entity> removal = new List<Entity>();
    private static readonly Dictionary<Entity, DeathObservation> deathObservations = new Dictionary<Entity, DeathObservation>();
    private static float lastCleanup;
    private static float lastSessionTime = -1;
    private static int lastTickFrame = -1;
    private static int diagnosticHits;
    private static int diagnosticGroups;
    public static long TaggedHits { get; private set; }
    public static long CreditedHits { get; private set; }
    public static double TotalExperience { get; private set; }

    private sealed class ImpactGroup
    {
        public float Before;
        public double Total;
        public bool IsStructure;
        public bool IsPowerful;
        public readonly Dictionary<Entity, double> Contributions = new Dictionary<Entity, double>();
        public void Clear() { Total = 0; Contributions.Clear(); }
    }

    private readonly struct DeathObservation
    {
        internal readonly int ObjectId, Level;
        internal DeathObservation(int objectId, int level) { ObjectId = objectId; Level = level; }
    }

    internal static void SetWorld(World world)
    {
        if (ReferenceEquals(ActiveWorld, world) && queriesValid) return;
        ResetSession();
        if (ActiveWorld != null && ActiveWorld.IsCreated && queriesValid)
        {
            requests.Dispose();
            clock.Dispose();
        }
        ActiveWorld = world;
        VeteranChronicle.SetWorld(world);
        requests = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<HealthChangeRequest>());
        clock = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<CurrentSessionTimeSingleton>());
        queriesValid = true;
    }

    private static void ClearImpacts()
    {
        foreach (var group in impacts.Values)
        {
            group.Clear();
            if (pool.Count < 2048) pool.Push(group);
        }
        impacts.Clear();
        deathObservations.Clear();
    }

    public static bool IsAlive(Entity entity) => ActiveWorld != null && ActiveWorld.IsCreated &&
        Manager.Exists(entity) && !Manager.HasComponent<Prefab>(entity) && !Manager.HasComponent<Dead>(entity) &&
        !Manager.HasComponent<DelayedDestroy>(entity) && Manager.HasComponent<Health>(entity) &&
        Manager.GetComponentData<Health>(entity).currentHealth > 0;

    public static bool IsEligible(Entity entity)
    {
        if (!IsAlive(entity) || !Manager.HasComponent<Unit>(entity) || !Manager.HasComponent<UnitBase>(entity) || !Manager.HasComponent<HealthBase>(entity) ||
            Manager.HasComponent<InTraining>(entity) || Manager.HasComponent<GhostCitizen>(entity)) return false;
        if (!Settings.EnableEnemyVeterancy.Value && Manager.HasComponent<Enemy>(entity)) return false;
        if (!Settings.EnableSiegeUnits.Value && Manager.HasComponent<SiegeUnit>(entity)) return false;
        var type = Manager.GetComponentData<UnitBase>(entity).value.Value.type;
        if (type == UnitType.None || type == UnitType.Doctor || type == UnitType.NativesPlayerHealer || type == UnitType.BannerPeasant)
            return false;
        return Settings.IncludedUnitTypes.Value == "*" || Settings.Included.Contains(type);
    }

    public static bool TryGetExperience(Entity entity, out double xp)
    {
        xp = records.TryGetValue(entity, out var record) ? record.Experience : 0;
        return IsEligible(entity);
    }

    public static bool TryGetSnapshot(Entity entity, out VeteranSnapshot snapshot)
    {
        snapshot = default;
        if (!IsEligible(entity)) return false;
        records.TryGetValue(entity, out var record);
        snapshot = new VeteranSnapshot
        {
            Experience = record?.Experience ?? 0,
            AppliedHealthFactor = record?.AppliedHealthFactor ?? 1,
            CurrentHealth = Manager.GetComponentData<Health>(entity).currentHealth,
            VanillaMaxHealth = VanillaMaximum(entity, record?.AppliedHealthFactor ?? 1),
            Specialization = record?.Specialization ?? Specialization.None
        };
        return true;
    }

    public static float BaseDamage(Entity entity)
    {
        var value = Manager.GetComponentData<UnitBase>(entity).value.Value.damage;
        if (Manager.HasComponent<ResearchAffectedData>(entity))
        {
            var research = Manager.GetComponentData<ResearchAffectedData>(entity).affect;
            if (Manager.Exists(research) && Manager.HasComponent<ResearchUnitAffectData>(research))
                value = Manager.GetComponentData<ResearchUnitAffectData>(research).value.damage;
        }
        return value;
    }

    public static void BeforeHealth(World world)
    {
        if (!Ready) return;
        SetWorld(world);
        Manager.CompleteAllJobs();
        CheckSessionClock();
        ClearImpacts();
        using var entities = requests.ToEntityArray(Allocator.Temp);
        for (int i = 0; i < entities.Length; i++)
        {
            Entity requestEntity = entities[i];
            var request = Manager.GetComponentData<HealthChangeRequest>(requestEntity);
            if (request.Value > 0 && Progression.Finite(request.Value))
            {
                VeteranHall.BeforeHealthRecovery(requestEntity, ref request);
                continue;
            }
            if (!(request.Value < 0) || !Progression.Finite(request.Value) || !IsAlive(request.Target)) continue;
            // Memorial events require an observed transition from live health to zero.
            // A destroyed entity, world reset, dismissal or loss of eligibility is not a death.
            if (!Manager.HasComponent<Enemy>(request.Target) && IsEligible(request.Target) &&
                !deathObservations.ContainsKey(request.Target) && Manager.HasComponent<ObjectId>(request.Target))
            {
                VeteranChronicle.GetOrCreate(request.Target);
                records.TryGetValue(request.Target, out var observedRecord);
                deathObservations.Add(request.Target, new DeathObservation(
                    Manager.GetComponentData<ObjectId>(request.Target).value, observedRecord?.Level ?? 0));
            }
            if (!Settings.EnableEnemyVeterancy.Value && !Manager.HasComponent<Enemy>(request.Target)) continue;
            bool credited = false;
            Entity source = Entity.Null;
            if (Manager.HasComponent<CombatSource>(requestEntity))
            {
                var tag = Manager.GetComponentData<CombatSource>(requestEntity);
                source = tag.Source;
                bool hostile = Manager.Exists(source) && Manager.HasComponent<Enemy>(source) != Manager.HasComponent<Enemy>(request.Target);
                credited = tag.Processed == 0 && hostile && IsEligible(source);
                if (Settings.DebugLogging.Value && diagnosticHits++ < 30)
                    Log.LogInfo("Damage observation: request=" + requestEntity + " source=" + source + " target=" + request.Target +
                        " value=" + request.Value + " eligible=" + IsEligible(source) + " hostile=" + hostile + " processed=" + tag.Processed);
                if (tag.Processed == 0)
                {
                    TaggedHits++;
                    tag.Processed = 1;
                    Manager.SetComponentData(requestEntity, tag);
                    if (credited && records.TryGetValue(source, out var record))
                    {
                        double damage = -request.Value;
                        double boosted = Math.Min(damage * (1 + DamageBonus(source, record.Level)), Math.Max(damage, Settings.MaxDamage.Value));
                        request.Value = -(float)boosted;
                        Manager.SetComponentData(requestEntity, request);
                    }
                }
            }
            if (!impacts.TryGetValue(request.Target, out var group))
            {
                group = pool.Count > 0 ? pool.Pop() : new ImpactGroup();
                group.Before = Manager.GetComponentData<Health>(request.Target).currentHealth;
                group.IsStructure = Manager.HasComponent<BuildingBase>(request.Target);
                group.IsPowerful = Manager.HasComponent<SiegeUnit>(request.Target) ||
                    (Manager.HasComponent<UnitBase>(request.Target) && Manager.GetComponentData<UnitBase>(request.Target).value.Value.powerLevel >= 5);
                impacts.Add(request.Target, group);
            }
            double amount = -request.Value;
            group.Total += amount;
            if (credited && (!group.IsStructure || Settings.StructureExperience.Value))
            {
                group.Contributions.TryGetValue(source, out double previous);
                group.Contributions[source] = previous + amount;
            }
        }
    }

    public static void AfterHealth(World world)
    {
        if (!Ready || !ReferenceEquals(ActiveWorld, world)) return;
        Manager.CompleteAllJobs();
        foreach (var observation in deathObservations)
            if (Manager.Exists(observation.Key) && Manager.HasComponent<Health>(observation.Key) &&
                Manager.GetComponentData<Health>(observation.Key).currentHealth <= 0)
                VeteranChronicle.RecordDeath(observation.Key, observation.Value.ObjectId, observation.Value.Level);
        foreach (var pair in impacts)
        {
            Entity target = pair.Key;
            var group = pair.Value;
            if (!Manager.Exists(target) || !Manager.HasComponent<Health>(target)) continue;
            float after = Manager.GetComponentData<Health>(target).currentHealth;
            if (Settings.DebugLogging.Value && diagnosticGroups++ < 30)
                Log.LogInfo("Health observation: target=" + target + " before=" + group.Before + " after=" + after +
                    " totalDamage=" + group.Total + " contributors=" + group.Contributions.Count);
            Entity killer = Entity.Null;
            double largest = 0;
            if (after <= 0 && group.Before > 0 && group.Contributions.Count == 1)
            {
                foreach (var contribution in group.Contributions)
                    if (contribution.Value > largest || (contribution.Value == largest && contribution.Key.Index < killer.Index))
                    { largest = contribution.Value; killer = contribution.Key; }
                // The game's unordered damage batch has no last-hit ordering. Only one
                // fully attributed source makes the killing unit unambiguous.
                if (group.Total - Sum(group.Contributions) > .0001) killer = Entity.Null;
            }
            foreach (var contribution in group.Contributions)
            {
                if (!IsEligible(contribution.Key)) continue;
                double effective = Progression.EffectiveShare(group.Before, after, contribution.Value, group.Total);
                if (effective <= 0) continue;
                VeteranChronicle.RecordCombat(contribution.Key, effective, contribution.Key == killer,
                    group.IsPowerful, group.IsStructure);
                bool kill = contribution.Key == killer && Settings.ExperienceFromKills.Value;
                double categoryMultiplier = Manager.HasComponent<SiegeUnit>(contribution.Key) ? Settings.SiegeMultiplier.Value :
                    Manager.HasComponent<RangeUnit>(contribution.Key) ? Settings.RangedMultiplier.Value : Settings.MeleeMultiplier.Value;
                if (group.IsStructure) categoryMultiplier *= Settings.StructureMultiplier.Value;
                double killXp = Settings.KillExperience.Value * (group.IsPowerful ? Settings.PowerfulKillMultiplier.Value : 1);
                double xp = Progression.Award(Settings.ExperienceFromDamage.Value ? effective : 0, kill,
                    Settings.DamagePerExperience.Value, killXp, Settings.ExperienceMultiplier.Value, categoryMultiplier);
                if (xp <= 0) continue;
                AddExperience(contribution.Key, xp);
                CreditedHits++;
            }
        }
        ClearImpacts();
    }

    private static double Sum(Dictionary<Entity, double> contributions)
    { double result = 0; foreach (double value in contributions.Values) result += value; return result; }

    private static void AddExperience(Entity source, double experience)
    {
        if (!records.TryGetValue(source, out var record)) { record = new VeteranRecord(); records.Add(source, record); }
        VeteranHall.RecordCombatExperience(source, experience);
        experience *= Specializations.Experience(record.Specialization, record.Level, Settings.EnableSpecializations.Value);
        experience *= VeteranHall.ExperienceMultiplier(source);
        record.Experience = Progression.Accumulate(record.Experience, experience);
        TotalExperience += experience;
        int level = Progression.Level(record.Experience, Settings.Ranks, Settings.MaxLevel.Value);
        if (record.Level != level)
        {
            int previousLevel = record.Level;
            record.Level = level;
            ApplyHealth(source, record);
            if (level > previousLevel)
            {
                VeteranChronicle.RecordPromotion(source, level);
                VeteranPromotionFeed.Promoted(source, previousLevel, level);
            }
#if VETERAN_TEST_HARNESS
            if (!RuntimeBenchmark.IsMeasuring)
#endif
                Log.LogInfo("Rank up " + source + " => " + Settings.Ranks[level].Name + " XP=" + record.Experience.ToString("F2"));
        }
        if (Settings.DebugLogging.Value) Log.LogInfo("XP " + source + " +" + experience.ToString("F3") + " = " + record.Experience.ToString("F3"));
    }

    internal static float DamageBonus(Entity entity, int level)
    {
        records.TryGetValue(entity, out var record);
        return Settings.Ranks[level].DamageBonus + Specializations.Damage(
            record?.Specialization ?? Specialization.None, level, Settings.EnableSpecializations.Value);
    }

    internal static bool ChooseSpecialization(World expectedWorld, Entity entity, Specialization choice)
    {
        if (!Ready || !ReferenceEquals(expectedWorld, ActiveWorld) || !expectedWorld.IsCreated ||
            !Settings.EnableSpecializations.Value || !Specializations.Valid(choice) || choice == Specialization.None) return false;
        Manager.CompleteAllJobs();
        if (!IsEligible(entity) || Manager.HasComponent<Enemy>(entity) || !records.TryGetValue(entity, out var record) ||
            record.Level < Specializations.UnlockLevel || record.Specialization != Specialization.None) return false;
        record.Specialization = choice;
        ApplyHealth(entity, record);
        VeteranChronicle.RecordSpecialization(entity, choice);
        return true;
    }

    private static void CheckSessionClock()
    {
        if (!queriesValid || clock.CalculateEntityCount() != 1) return;
        float time = clock.GetSingleton<CurrentSessionTimeSingleton>().elapsedTime;
        if (lastSessionTime >= 0 && time + .01f < lastSessionTime) ResetSession();
        lastSessionTime = time;
    }

    public static void Tick()
    {
        if (lastTickFrame == Time.frameCount) return;
        lastTickFrame = Time.frameCount;
        if (!Ready)
        {
            if (records.Count > 0) ResetSession();
            return;
        }
        VeteranPersistence.Tick();
        VeteranHall.Tick();
        if (ActiveWorld == null || !ActiveWorld.IsCreated || Time.unscaledTime - lastCleanup < .5f) return;
        lastCleanup = Time.unscaledTime;
        Manager.CompleteAllJobs();
        CheckSessionClock();
        removal.Clear();
        foreach (var pair in records)
            if (!IsEligible(pair.Key)) { RemoveHealthFactor(pair.Key, pair.Value); removal.Add(pair.Key); }
            else ApplyHealth(pair.Key, pair.Value);
        foreach (var entity in removal) records.Remove(entity);
        VeteranChronicle.Prune();
    }
}

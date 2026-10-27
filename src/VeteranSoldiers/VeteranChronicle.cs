using System;
using System.Collections.Generic;
using Components;
using Components.RawComponents;
using Unity.Entities;

namespace VeteranSoldiers;

/// <summary>Main-thread session state. Entity keys include version and are discarded whenever the World changes.</summary>
internal static class VeteranChronicle
{
    private static World? world;
    private static readonly Dictionary<Entity, ChronicleRecord> records = new Dictionary<Entity, ChronicleRecord>();
    private static CampaignSnapshot campaign = new CampaignSnapshot();
    private static readonly List<Entity> expired = new List<Entity>();
    internal static IReadOnlyList<ChronicleRecord> Memorial => campaign.Memorial;
    internal static double EffectiveDamage => campaign.EffectiveDamage;
    internal static int ConfirmedKills => campaign.ConfirmedKills;
    internal static int Promotions => campaign.Promotions;
    internal static int ObservedDeaths => campaign.ObservedDeaths;

    internal static void SetWorld(World value)
    {
        if (ReferenceEquals(world, value)) return;
        world = value;
        ResetSession();
    }
    internal static void ResetSession() { records.Clear(); campaign = new CampaignSnapshot(); }
    internal static void Remove(Entity entity) { records.Remove(entity); }
    internal static void Prune()
    {
        if (world == null || !world.IsCreated) { ResetSession(); return; }
        expired.Clear();
        foreach (var pair in records) if (!VeteranRuntime.IsAlive(pair.Key)) expired.Add(pair.Key);
        foreach (var entity in expired) records.Remove(entity);
    }
    internal static bool TryGet(Entity entity, out ChronicleRecord record) { return records.TryGetValue(entity, out record!); }
    internal static ChronicleRecord GetOrCreate(Entity entity)
    {
        if (world == null || !world.IsCreated || entity == Entity.Null || !world.EntityManager.Exists(entity))
            throw new InvalidOperationException("A chronicle requires a live entity in the active world.");
        if (!records.TryGetValue(entity, out var record)) records.Add(entity, record = new ChronicleRecord());
        return record;
    }
    internal static bool SetName(Entity entity, string name)
    {
        if (!ReadyForMutation(entity)) return false;
        GetOrCreate(entity).Name = ChronicleRules.SanitizeName(name);
        return true;
    }
    internal static bool ToggleFavorite(Entity entity)
    {
        if (!ReadyForMutation(entity)) return false;
        var record = GetOrCreate(entity);
        record.Favorite = !record.Favorite;
        return record.Favorite;
    }
    internal static void RecordCombat(Entity entity, double effectiveDamage, bool confirmedKill = false, bool powerful = false, bool structure = false)
    {
        if (!Friendly(entity) || !ChronicleRules.Finite(effectiveDamage) || effectiveDamage <= 0) return;
        ChronicleRules.Combat(GetOrCreate(entity), effectiveDamage, confirmedKill, powerful, structure);
        campaign.EffectiveDamage = Math.Min(ChronicleRules.MaximumDamage, campaign.EffectiveDamage + effectiveDamage);
        if (confirmedKill) campaign.ConfirmedKills = ChronicleRules.Increment(campaign.ConfirmedKills);
    }
    internal static void RecordPromotion(Entity entity, int level)
    {
        if (!Friendly(entity)) return;
        var record = GetOrCreate(entity);
        if (level <= record.HighestLevel || level > 100) return;
        ChronicleRules.Promote(record, level);
        campaign.Promotions = ChronicleRules.Increment(campaign.Promotions);
    }
    internal static void RecordSpecialization(Entity entity, Specialization specialization)
    {
        if (!Friendly(entity) || specialization == Specialization.None || !Specializations.Valid(specialization)) return;
        ChronicleRules.AddEvent(GetOrCreate(entity), ChronicleEventKind.Specialization, (int)specialization);
    }
    internal static void RecordDeath(Entity entity, int objectId, int level)
    {
        if (world == null || !world.IsCreated || (world.EntityManager.Exists(entity) && world.EntityManager.HasComponent<Enemy>(entity))) return;
        // Caller proves an actual positive-health -> dead transition. Missing tracked identity
        // means the unit was never recorded; do not fabricate a career or infer death from cleanup.
        if (!records.TryGetValue(entity, out var record)) return;
        records.Remove(entity);
        var fallen = record.Clone();
        fallen.ObjectId = objectId;
        fallen.HighestLevel = Math.Max(fallen.HighestLevel, Math.Max(0, Math.Min(100, level)));
        ChronicleRules.AddEvent(fallen, ChronicleEventKind.Fallen, 0, fallen.HighestLevel);
        if (campaign.Memorial.Count >= ChronicleRules.MaximumMemorial) campaign.Memorial.RemoveAt(0);
        campaign.Memorial.Add(fallen);
        campaign.ObservedDeaths = ChronicleRules.Increment(campaign.ObservedDeaths);
    }
    internal static CampaignSnapshot CaptureCampaign()
    {
        var copy = new CampaignSnapshot { EffectiveDamage = campaign.EffectiveDamage, ConfirmedKills = campaign.ConfirmedKills,
            Promotions = campaign.Promotions, ObservedDeaths = campaign.ObservedDeaths };
        foreach (var record in campaign.Memorial) copy.Memorial.Add(record.Clone());
        return copy;
    }
    internal static void RestoreCampaign(CampaignSnapshot value)
    {
        ChronicleRules.ValidateCampaign(value);
        // Runtime mutations cannot alter the verified load snapshot retained by its caller.
        campaign = new CampaignSnapshot { EffectiveDamage = value.EffectiveDamage, ConfirmedKills = value.ConfirmedKills,
            Promotions = value.Promotions, ObservedDeaths = value.ObservedDeaths };
        foreach (var record in value.Memorial) campaign.Memorial.Add(record.Clone());
    }
    internal static void Restore(Entity entity, ChronicleRecord value)
    {
        ChronicleRules.Validate(value);
        records[entity] = value.Clone();
    }
    private static bool ReadyForMutation(Entity entity)
    {
        var activeWorld = world;
        if (activeWorld == null || !activeWorld.IsCreated || entity == Entity.Null) return false;
        // Native button callbacks can run between ECS updates. Finish pending health writes
        // before eligibility reads, then revalidate the exact world and entity incarnation.
        activeWorld.EntityManager.CompleteAllJobs();
        return ReferenceEquals(world, activeWorld) && activeWorld.IsCreated && Friendly(entity) && VeteranRuntime.IsEligible(entity);
    }
    private static bool Friendly(Entity entity) => world != null && world.IsCreated && world.EntityManager.Exists(entity) &&
        !world.EntityManager.HasComponent<Enemy>(entity);
}

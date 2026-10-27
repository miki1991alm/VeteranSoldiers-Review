using System;
using Components;
using Components.RawComponents;
using Components.RawComponents.ResearchComponents;
using Unity.Entities;

namespace VeteranSoldiers;

public sealed class VeteranRecord
{
    public double Experience;
    public float AppliedHealthFactor = 1;
    public int Level;
    public Specialization Specialization;
    internal bool PendingHealthClamp;
}

public static partial class VeteranRuntime
{
    public static void ResetSession()
    {
        // Entity equality includes Version. Keep records until every removal succeeds so
        // a temporary component-write failure can be retried without losing ownership.
        if (ActiveWorld != null && ActiveWorld.IsCreated)
        {
            Manager.CompleteAllJobs();
            foreach (var pair in records) RemoveHealthFactor(pair.Key, pair.Value);
        }
        records.Clear();
        ClearImpacts();
        VeteranPromotionFeed.Clear();
        VeteranChronicle.ResetSession();
        VeteranHall.ResetSession();
        VeteranPersistence.ResetFeedback();
        lastSessionTime = -1;
        lastCleanup = 0;
    }

    private static void RemoveHealthFactor(Entity entity, VeteranRecord record)
    {
        if (!Manager.Exists(entity) || !Manager.HasComponent<HealthBase>(entity) ||
            !Progression.Finite(record.AppliedHealthFactor) || record.AppliedHealthFactor < 1) return;
        if (record.AppliedHealthFactor == 1 && !record.PendingHealthClamp) return;
        float maximum = VanillaMaximum(entity, record.AppliedHealthFactor);
        if (!PositiveFinite(maximum)) return;
        if (record.AppliedHealthFactor > 1)
        {
            var data = Manager.GetComponentData<HealthBase>(entity);
            float multiplier = data.GetMaxHealthMultiplier() / record.AppliedHealthFactor;
            if (!PositiveFinite(multiplier)) return;
            data.SetMaxHealthMultiplier((Systems.GameWorldLoaderSystem)null!, multiplier);
            Manager.SetComponentData(entity, data);
            // Commit ownership immediately after the multiplier write. If the following
            // health write fails, a retry clamps health but never divides the factor twice.
            record.AppliedHealthFactor = 1;
            record.PendingHealthClamp = true;
        }
        ClampHealth(entity, maximum);
        record.PendingHealthClamp = false;
    }

    public static void RestoreExperience(Entity entity, double experience)
    {
        if (!Ready || !Progression.Finite(experience) || experience < 0 || !IsEligible(entity)) return;
        if (!records.TryGetValue(entity, out var record))
        {
            record = new VeteranRecord();
            records.Add(entity, record);
        }
        // Preserve the factor already owned by this entity, including repeated restores.
        record.Experience = experience;
        record.Level = Progression.Level(experience, Settings.Ranks, Settings.MaxLevel.Value);
        ApplyHealth(entity, record);
    }

    public static void RestoreSnapshot(Entity entity, VeteranSnapshot snapshot)
    {
        // Reject the whole snapshot before changing either XP or health. In particular,
        // an old record must not allow health restoration while the mod is disabled.
        if (!Ready || !IsEligible(entity) || !PersistenceStore.IsValidSnapshot(snapshot)) return;
        if (!records.TryGetValue(entity, out var existing))
        { existing = new VeteranRecord(); records.Add(entity, existing); }
        existing.Specialization = snapshot.Specialization;
        RestoreExperience(entity, snapshot.Experience);
        if (!records.TryGetValue(entity, out var record)) return;
        float maximum = VanillaMaximum(entity, record.AppliedHealthFactor) * record.AppliedHealthFactor;
        if (!PositiveFinite(maximum)) return;
        var health = Manager.GetComponentData<Health>(entity);
        health.currentHealth = Math.Min(snapshot.CurrentHealth, maximum);
        Manager.SetComponentData(entity, health);
    }

    public static float VanillaMaximum(Entity entity, float appliedFactor = 1)
    {
        if (!Progression.Finite(appliedFactor) || appliedFactor < 1) return 0;
        var data = Manager.GetComponentData<HealthBase>(entity);
        float baseline = data.value.Value.maxHealth;
        if (Manager.HasComponent<ResearchAffectedData>(entity))
        {
            var research = Manager.GetComponentData<ResearchAffectedData>(entity).affect;
            if (Manager.Exists(research) && Manager.HasComponent<ResearchHealthAffectData>(research))
                baseline = Manager.GetComponentData<ResearchHealthAffectData>(research).value.maxHealth;
        }
        float multiplier = data.GetMaxHealthMultiplier();
        if (!PositiveFinite(baseline) || !PositiveFinite(multiplier)) return 0;
        double maximum = (double)baseline * multiplier / appliedFactor;
        return maximum > 0 && maximum <= float.MaxValue ? (float)maximum : 0;
    }

    private static void ApplyHealth(Entity entity, VeteranRecord record)
    {
        if (!Manager.HasComponent<HealthBase>(entity) ||
            !Progression.Finite(record.AppliedHealthFactor) || record.AppliedHealthFactor < 1) return;
        var data = Manager.GetComponentData<HealthBase>(entity);
        float baselineMultiplier = data.GetMaxHealthMultiplier() / record.AppliedHealthFactor;
        float baselineMax = VanillaMaximum(entity, record.AppliedHealthFactor);
        if (!PositiveFinite(baselineMultiplier) || !PositiveFinite(baselineMax)) return;
        float bonus = Settings.Ranks[record.Level].HealthBonus +
            Specializations.Health(record.Specialization, record.Level, Settings.EnableSpecializations.Value);
        float factor = Progression.CombinedHealthFactor(baselineMax, bonus, Settings.MaxHealth.Value);
        float combinedMultiplier = baselineMultiplier * factor;
        float maximum = baselineMax * factor;
        if (!Progression.Finite(factor) || factor < 1 || !PositiveFinite(combinedMultiplier) || !PositiveFinite(maximum)) return;
        // Verified setter simply stores the value; the safety parameter is unused by the game.
        // An unchanged owned factor needs no divide/multiply write-back. Apart from avoiding
        // rounding drift, this preserves external multiplicative changes between our ticks.
        if (record.AppliedHealthFactor != factor)
        {
            data.SetMaxHealthMultiplier((Systems.GameWorldLoaderSystem)null!, combinedMultiplier);
            Manager.SetComponentData(entity, data);
        }
        record.AppliedHealthFactor = factor;
        // Increasing maximum health grants no healing. On decrease only excess health is clamped.
        record.PendingHealthClamp = true;
        ClampHealth(entity, maximum);
        record.PendingHealthClamp = false;
    }

    private static void ClampHealth(Entity entity, float maximum)
    {
        if (!Manager.HasComponent<Health>(entity)) return;
        var health = Manager.GetComponentData<Health>(entity);
        if (health.currentHealth > maximum)
        {
            health.currentHealth = maximum;
            Manager.SetComponentData(entity, health);
        }
    }

    private static bool PositiveFinite(float value) => value > 0 && Progression.Finite(value);
}

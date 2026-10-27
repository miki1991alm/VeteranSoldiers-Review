using System;
using Components;
using Components.RawComponents;
using Components.RawComponents.ResearchComponents;
using Unity.Entities;
using VeteranSoldiers;

internal static class HealthLifecycleTests
{
    internal static void Run(Action<bool, string> check)
    {
        VeteranRuntime.NewTestWorld();
        Entity unit = Add(1, 100, 1.5f, 90);
        VeteranRuntime.RestoreExperience(unit, 1500);
        VeteranRecord record = VeteranRuntime.TestRecord(unit);
        check(Near(Multiplier(unit), 1.95f) && Health(unit) == 90,
            "restoring rank preserves a pre-existing multiplicative modifier and grants no healing");
        int writes = VeteranRuntime.Manager.Writes;
        VeteranRuntime.RestoreExperience(unit, 1500);
        check(ReferenceEquals(record, VeteranRuntime.TestRecord(unit)) && Near(Multiplier(unit), 1.95f) &&
            VeteranRuntime.Manager.Writes == writes, "repeated experience restore keeps ownership and does not stack or rewrite health");

        VeteranSnapshot snapshot = Snapshot(1500, 180);
        VeteranRuntime.RestoreSnapshot(unit, snapshot);
        VeteranRuntime.RestoreSnapshot(unit, snapshot);
        check(Near(Multiplier(unit), 1.95f) && Health(unit) == 180 && record.Experience == 1500,
            "repeated full snapshot restoration is idempotent");
        SetMultiplier(unit, Multiplier(unit) * 2);
        VeteranRuntime.RestoreExperience(unit, 1500);
        check(Near(Multiplier(unit), 3.9f), "a multiplicative modifier added after veterancy survives another restore");
        SetHealth(unit, 350);
        VeteranRuntime.RestoreExperience(unit, 0);
        check(Near(Multiplier(unit), 3) && Health(unit) == 300 && record.AppliedHealthFactor == 1,
            "downgrading removes only the owned factor and clamps excess health");

        VeteranRuntime.Ready = false;
        VeteranRuntime.RestoreSnapshot(unit, Snapshot(1500, 50));
        check(record.Experience == 0 && Health(unit) == 300 && Near(Multiplier(unit), 3),
            "a disabled runtime cannot restore XP or health through an existing record");
        VeteranRuntime.Ready = true;
        snapshot = Snapshot(double.NaN, 50);
        VeteranRuntime.RestoreSnapshot(unit, snapshot);
        check(record.Experience == 0 && Health(unit) == 300, "invalid snapshot XP cannot partially change existing health");
        snapshot = Snapshot(1500, 50);
        snapshot.AppliedHealthFactor = 0;
        VeteranRuntime.RestoreSnapshot(unit, snapshot);
        check(record.Experience == 0 && Health(unit) == 300, "invalid snapshot ownership is rejected before mutation");
        snapshot = Snapshot(1500, 0);
        VeteranRuntime.RestoreSnapshot(unit, snapshot);
        check(record.Experience == 0 && Health(unit) == 300, "dead snapshot cannot kill a live soldier");
        SetHealth(unit, 0);
        VeteranRuntime.RestoreSnapshot(unit, Snapshot(1500, 100));
        check(Health(unit) == 0, "snapshot restore does not revive an ineligible dead entity");

        VeteranRuntime.NewTestWorld();
        unit = Add(2, 100, 1.5f, 90);
        Entity research = new Entity(3);
        VeteranRuntime.Manager.Add(research);
        VeteranRuntime.Manager.SetComponentData(research, new ResearchHealthAffectData { value = new HealthDefinition { maxHealth = 200 } });
        VeteranRuntime.Manager.SetComponentData(unit, new ResearchAffectedData { affect = research });
        VeteranRuntime.RestoreExperience(unit, 1500);
        check(Near(VeteranRuntime.VanillaMaximum(unit, 1.3f), 300) && Near(Multiplier(unit), 1.95f),
            "research health remains the baseline when restoring veterancy");
        SetMultiplier(unit, float.PositiveInfinity);
        writes = VeteranRuntime.Manager.Writes;
        VeteranRuntime.RestoreSnapshot(unit, Snapshot(1500, 50));
        check(VeteranRuntime.Manager.Writes == writes && Health(unit) == 90,
            "non-finite external maximum is not written back or used to restore health");

        VeteranRuntime.NewTestWorld();
        Entity first = Add(4, 100, 1.5f, 90);
        Entity second = Add(5, 100, 1.5f, 90);
        VeteranRuntime.RestoreExperience(first, 1500);
        VeteranRuntime.RestoreExperience(second, 1500);
        SetHealth(first, 190);
        SetHealth(second, 190);
        VeteranRuntime.Manager.BeforeWrite = (entity, type) =>
        {
            if (entity.Equals(second) && type == typeof(Health)) throw new InvalidOperationException("injected health write failure");
        };
        bool failed = false;
        try { VeteranRuntime.ResetSession(); }
        catch (InvalidOperationException) { failed = true; }
        check(failed && Near(Multiplier(first), 1.5f) && Near(Multiplier(second), 1.5f) &&
            VeteranRuntime.TestRecord(first).AppliedHealthFactor == 1 && VeteranRuntime.TestRecord(second).AppliedHealthFactor == 1,
            "ownership is committed before a later cleanup write can fail");
        VeteranRuntime.Manager.BeforeWrite = null;
        VeteranRuntime.ResetSession();
        check(Near(Multiplier(first), 1.5f) && Near(Multiplier(second), 1.5f) && Health(first) == 150 && Health(second) == 150 &&
            VeteranRuntime.TestRecordCount == 0, "retrying interrupted cleanup neither divides twice nor loses the pending health clamp");
        VeteranRuntime.ResetSession();
        check(Near(Multiplier(first), 1.5f) && Near(Multiplier(second), 1.5f), "completed session cleanup is idempotent");

        VeteranRuntime.NewTestWorld();
        first = Add(6, 100, 1, 90);
        VeteranRuntime.RestoreExperience(first, 0);
        SetHealth(first, 120);
        VeteranRuntime.ResetSession();
        check(Health(first) == 120, "cleanup does not clamp unrelated overheal when no bonus is owned");
        Entity recycled = Add(6, 100, 2, 90, 2);
        VeteranRuntime.RestoreExperience(first, 1500);
        VeteranRuntime.ResetSession();
        check(Multiplier(recycled) == 2 && Health(recycled) == 90, "full entity version prevents cleanup from touching a recycled index");

        snapshot = Snapshot(1, 1);
        snapshot.VanillaMaxHealth = float.MaxValue;
        snapshot.AppliedHealthFactor = 2;
        check(!PersistenceStore.IsValidSnapshot(snapshot), "overflowing snapshot maximum is rejected");

        VeteranRuntime.NewTestWorld();
        unit = Add(7, 100, 1.5f, 90);
        snapshot = Snapshot(700, 90); snapshot.Specialization = Specialization.Guardian;
        VeteranRuntime.RestoreSnapshot(unit, snapshot);
        check(Near(Multiplier(unit), 1.95f) && Health(unit) == 90 && VeteranRuntime.TestRecord(unit).Specialization == Specialization.Guardian,
            "Guardian composes rank and external health without healing");
        VeteranRuntime.RestoreSnapshot(unit, snapshot);
        check(Near(Multiplier(unit), 1.95f), "Guardian restore is idempotent");
        VeteranRuntime.Settings.EnableSpecializations.Value = false;
        VeteranRuntime.RestoreExperience(unit, 700);
        check(Near(Multiplier(unit), 1.8f) && VeteranRuntime.TestRecord(unit).Specialization == Specialization.Guardian,
            "disabled specialization removes only its effect and preserves the choice");
        VeteranRuntime.Settings.EnableSpecializations.Value = true;
        VeteranRuntime.RestoreExperience(unit, 700);
        check(Near(Multiplier(unit), 1.95f) && Health(unit) == 90, "reenabling Guardian does not stack or heal");
        VeteranRuntime.RestoreExperience(unit, 100);
        check(Near(Multiplier(unit), 1.5f), "a rank below the unlock suspends the specialization");
        VeteranRuntime.RestoreExperience(unit, 1500);
        VeteranRuntime.ResetSession();
        check(Near(Multiplier(unit), 1.5f), "reset removes rank and specialty while keeping the external factor");
    }

    private static Entity Add(int index, float baseline, float multiplier, float health, int version = 1)
    {
        Entity entity = new Entity(index, version);
        VeteranRuntime.Manager.Add(entity);
        var data = new HealthBase { value = new Blob<HealthDefinition> { Value = new HealthDefinition { maxHealth = baseline } } };
        data.SetMaxHealthMultiplier(null, multiplier);
        VeteranRuntime.Manager.SetComponentData(entity, data);
        SetHealth(entity, health);
        return entity;
    }
    private static VeteranSnapshot Snapshot(double xp, float health) => new VeteranSnapshot
    {
        Experience = xp, AppliedHealthFactor = 1.3f, CurrentHealth = health, VanillaMaxHealth = 150
    };
    private static float Multiplier(Entity entity) => VeteranRuntime.Manager.GetComponentData<HealthBase>(entity).GetMaxHealthMultiplier();
    private static float Health(Entity entity) => VeteranRuntime.Manager.GetComponentData<Health>(entity).currentHealth;
    private static void SetHealth(Entity entity, float value) => VeteranRuntime.Manager.SetComponentData(entity, new Health { currentHealth = value });
    private static void SetMultiplier(Entity entity, float value)
    {
        var data = VeteranRuntime.Manager.GetComponentData<HealthBase>(entity);
        data.SetMaxHealthMultiplier(null, value);
        VeteranRuntime.Manager.SetComponentData(entity, data);
    }
    private static bool Near(float actual, float expected) => Math.Abs(actual - expected) < .0001f;
}

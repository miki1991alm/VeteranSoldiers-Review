// Minimal in-memory ECS boundary for the actual VeteranHealth.cs methods. This test double
// deliberately supports injected component-write failures; it is not a Unity integration test.
using System;
using System.Collections.Generic;

namespace Unity.Entities
{
    public readonly struct Entity : IEquatable<Entity>
    {
        public readonly int Index;
        public readonly int Version;
        public Entity(int index, int version = 1) { Index = index; Version = version; }
        public bool Equals(Entity other) => Index == other.Index && Version == other.Version;
        public override bool Equals(object other) => other is Entity entity && Equals(entity);
        public override int GetHashCode() => (Index * 397) ^ Version;
    }
    public sealed class World { public bool IsCreated = true; }
}

namespace Components
{
    public struct Health { public float currentHealth; }
    public struct Blob<T> { public T Value; }
    public struct HealthDefinition { public float maxHealth; }
    public struct HealthBase
    {
        public Blob<HealthDefinition> value;
        private float multiplier;
        public float GetMaxHealthMultiplier() => multiplier;
        public void SetMaxHealthMultiplier(Systems.GameWorldLoaderSystem ignored, float newValue) => multiplier = newValue;
    }
}

namespace Components.RawComponents
{
    public struct ResearchAffectedData { public Unity.Entities.Entity affect; }
}

namespace Components.RawComponents.ResearchComponents
{
    public struct ResearchHealthAffectData { public Components.HealthDefinition value; }
}

namespace Systems { public sealed class GameWorldLoaderSystem { } }

namespace VeteranSoldiers
{
    internal static class VeteranPromotionFeed { internal static void Clear() { } }
    internal static class VeteranChronicle { internal static void ResetSession() { } }
    internal static class VeteranHall { internal static void ResetSession() { } }
    internal static class VeteranPersistence { internal static void ResetFeedback() { } }
    public sealed class TestValue<T>
    {
        public T Value;
        public TestValue(T value) { Value = value; }
    }
    public sealed class HealthTestSettings
    {
        public readonly Rank[] Ranks = Progression.Defaults();
        public readonly TestValue<int> MaxLevel = new TestValue<int>(5);
        public readonly TestValue<float> MaxHealth = new TestValue<float>(10000);
        public readonly TestValue<bool> EnableSpecializations = new TestValue<bool>(true);
    }
    public sealed class HealthTestManager
    {
        private readonly Dictionary<Unity.Entities.Entity, Dictionary<Type, object>> entities =
            new Dictionary<Unity.Entities.Entity, Dictionary<Type, object>>();
        public Action<Unity.Entities.Entity, Type> BeforeWrite;
        public int Writes;
        public void Add(Unity.Entities.Entity entity) => entities.Add(entity, new Dictionary<Type, object>());
        public bool Exists(Unity.Entities.Entity entity) => entities.ContainsKey(entity);
        public bool HasComponent<T>(Unity.Entities.Entity entity) => Exists(entity) && entities[entity].ContainsKey(typeof(T));
        public T GetComponentData<T>(Unity.Entities.Entity entity) => (T)entities[entity][typeof(T)];
        public void SetComponentData<T>(Unity.Entities.Entity entity, T value)
        {
            BeforeWrite?.Invoke(entity, typeof(T));
            entities[entity][typeof(T)] = value;
            Writes++;
        }
        public void CompleteAllJobs() { }
    }
    public static partial class VeteranRuntime
    {
        public static Unity.Entities.World ActiveWorld = new Unity.Entities.World();
        public static HealthTestManager Manager = new HealthTestManager();
        public static HealthTestSettings Settings = new HealthTestSettings();
        public static bool Ready = true;
        private static readonly Dictionary<Unity.Entities.Entity, VeteranRecord> records =
            new Dictionary<Unity.Entities.Entity, VeteranRecord>();
        internal static float lastSessionTime;
        internal static float lastCleanup;
        internal static int ClearedImpacts;
        private static void ClearImpacts() { ClearedImpacts++; }
        public static bool IsEligible(Unity.Entities.Entity entity) => ActiveWorld != null && ActiveWorld.IsCreated &&
            Manager.HasComponent<Components.Health>(entity) && Manager.HasComponent<Components.HealthBase>(entity) &&
            Manager.GetComponentData<Components.Health>(entity).currentHealth > 0;
        internal static VeteranRecord TestRecord(Unity.Entities.Entity entity) => records[entity];
        internal static int TestRecordCount => records.Count;
        internal static void NewTestWorld()
        {
            records.Clear();
            ActiveWorld = new Unity.Entities.World();
            Manager = new HealthTestManager();
            Settings = new HealthTestSettings();
            Ready = true;
            ClearedImpacts = 0;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

// Minimal Unity/game boundary. HarmonyX and the production research hooks are real;
// this fixture tests patch ordering/cleanup, not Unity's ECS research simulation.
namespace Unity.Entities
{
    public struct Entity { }
    public struct ComponentType { public static ComponentType ReadOnly<T>() => default; }
    public sealed class EntityQuery : IDisposable
    {
        public int CalculateEntityCount() => 1;
        public Entity GetSingletonEntity() => default;
        public void Dispose() { }
    }
    public sealed class EntityManager
    {
        public readonly Components.SingletonComponents.CurrentSettingsSingleton Settings = new Components.SingletonComponents.CurrentSettingsSingleton();
        public void CompleteAllJobs() { }
        public EntityQuery CreateEntityQuery(ComponentType type) => new EntityQuery();
        public T GetComponentObject<T>(Entity entity) => (T)(object)Settings;
    }
    public sealed class World
    {
        public bool IsCreated = true;
        public readonly EntityManager EntityManager = new EntityManager();
    }
}

namespace Components.SingletonComponents
{
    public sealed class CurrentSettingsSingleton
    {
        public readonly Difficulty missionDifficultyConfiguration = new Difficulty();
    }
    public sealed class Difficulty
    {
        public readonly ScriptableObjectDefinitions.ResearchSettingsDefinition researchConfig = new ScriptableObjectDefinitions.ResearchSettingsDefinition();
    }
}

namespace ScriptableObjectDefinitions
{
    public struct HealthEffect { public int objectId; public float multiplier; }
    public sealed class ResearchItem
    {
        public HealthEffect[] healthEffects = Array.Empty<HealthEffect>();
    }
    public sealed class ResearchSettingsDefinition
    {
        public readonly ResearchItem[] Items = new[] { new ResearchItem { healthEffects = new[] { new HealthEffect { objectId = 119, multiplier = .3f } } } };
        [MethodImpl(MethodImplOptions.NoInlining)]
        public ResearchItem[] GetAvailableResearches() => Items;
    }
}

namespace Systems
{
    public sealed class ResearchSystem
    {
        public readonly Unity.Entities.World World = new Unity.Entities.World();
        public Unity.Entities.EntityManager EntityManager => World.EntityManager;
        public int OriginalCalls;
        public bool ThrowBeforeRead, ThrowAfterRead;
        public readonly Exception ExpectedError = new InvalidOperationException("Native startup fixture failure");
        public ScriptableObjectDefinitions.ResearchItem[]? Consumed;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void OnStartRunning()
        {
            OriginalCalls++;
            if (ThrowBeforeRead) throw ExpectedError;
            Consumed = World.EntityManager.Settings.missionDifficultyConfiguration.researchConfig.GetAvailableResearches();
            if (ThrowAfterRead) throw ExpectedError;
        }
    }
}

namespace VeteranSoldiers
{
    internal sealed class Flag { internal bool Value = true; }
    internal sealed class SettingsFixture { internal readonly Flag Enabled = new Flag(); }
    internal sealed class LogFixture
    {
        internal readonly List<string> Errors = new List<string>();
        internal void LogInfo(string text) { }
        internal void LogError(string text) => Errors.Add(text);
    }
    internal static class VeteranRuntime
    {
        internal static readonly SettingsFixture Settings = new SettingsFixture();
        internal static readonly LogFixture Log = new LogFixture();
    }
    internal static class VeteranHall
    {
        internal const int BaseObjectId = 119;
        internal const int ObjectId = 191981074;
        internal static bool Available = true;
        internal static void EnsureRegistered(Unity.Entities.World world, bool verify) { }
    }
}

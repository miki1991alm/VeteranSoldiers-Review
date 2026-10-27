// This fixture links the production Bootstrap.cs hooks and registration. Only the
// Unity/ECS environment and health processor are doubles; Harmony is the loader's
// real local assembly. It does not load game assemblies or claim gameplay coverage.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace Unity.Entities
{
    public sealed class World
    {
        public Systems.HealthTrackSystem Target = null!;
    }

    public abstract class SystemBase
    {
        public World World { get; set; } = null!;
        protected virtual void OnCreate() { }
        protected abstract void OnUpdate();
        protected virtual void OnDestroy() { }
        protected void CompleteDependency()
        {
            VeteranSoldiers.VeteranRuntime.Completions++;
            VeteranSoldiers.VeteranRuntime.Trace.Add("complete");
        }
    }
}

namespace Systems
{
    public sealed class HealthTrackSystem : Unity.Entities.SystemBase
    {
        public double Health = 100, DamageA = 10, DamageB = 10;
        public int OriginalCalls;
        public HealthTrackSystem() { World = new Unity.Entities.World { Target = this }; }
        protected override void OnUpdate() { }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public void OnUpdateSimulation()
        {
            VeteranSoldiers.VeteranRuntime.Trace.Add("original");
            OriginalCalls++;
            Health = Math.Max(0, Health - DamageA - DamageB);
        }
    }
}

namespace UnityEngine
{
    public class MonoBehaviour { }
    public sealed class GameObject
    {
        public GameObject(string name) { }
        public T AddComponent<T>() where T : new() => new T();
    }
    public static class Object { public static void DontDestroyOnLoad(object target) { } }
}

namespace BepInEx
{
    public static class Paths { public static string ConfigPath => "."; }
}
namespace BepInEx.Configuration
{
    public sealed class ConfigFile
    {
        public readonly string ConfigFilePath;
        public ConfigFile(string path, bool saveOnInit) { ConfigFilePath = path; }
    }
    public sealed class ConfigEntry<T>
    {
        public T Value;
        public ConfigEntry(T value) { Value = value; }
    }
}
namespace BepInEx.Logging
{
    public sealed class ManualLogSource
    {
        public void LogInfo(object data) { }
        public void LogError(object data) { }
    }
    public static class Logger { public static ManualLogSource CreateLogSource(string name) => new ManualLogSource(); }
}

namespace VeteranSoldiers
{
    public sealed class InstrumentationMarker { }
    public sealed class ModSettings
    {
        public readonly BepInEx.Configuration.ConfigEntry<bool> Enabled = new BepInEx.Configuration.ConfigEntry<bool>(true);
        public readonly BepInEx.Configuration.ConfigEntry<bool> Persistence = new BepInEx.Configuration.ConfigEntry<bool>(false);
        public readonly BepInEx.Configuration.ConfigFile File;
        public ModSettings(BepInEx.Configuration.ConfigFile file) { File = file; }
    }
    public static class VeteranPersistence { public static void Initialize(Harmony harmony) { } }
    public static class VeteranHall { public static void Initialize(Harmony harmony) { } }
    public static class VeteranHallResearch { public static void Initialize(Harmony harmony) { } }
    public static class VeteranUiBridge { public static void Initialize(Harmony harmony) { } }
    public static class VeteranNativePanel
    {
        public static void Initialize(Harmony harmony) { }
        public static void Tick() { }
    }
    public sealed class VeteranWorldInsignias { }
    public sealed class VeteranPromotionFeed : UnityEngine.MonoBehaviour { }
    public sealed class VeteranArmyWindow : UnityEngine.MonoBehaviour { }

    public static partial class VeteranRuntime
    {
        public static Unity.Entities.World? ActiveWorld;
        public static readonly List<string> Trace = new List<string>();
        public static int Captures, Observations, Completions;
        public static bool ThrowOnCapture;
        public static double CreditA, CreditB, CapturedAfter;
        private static double before, contributionA, contributionB;

        public static void Prepare()
        {
            initialized = false; failed = false; ThrowOnCapture = false;
            ActiveWorld = null;
            Captures = Observations = Completions = 0;
            CreditA = CreditB = CapturedAfter = 0;
            ResetSession(); Trace.Clear();
        }
        public static void SetWorld(Unity.Entities.World world) { ActiveWorld = world; }
        public static void Tick() { }
        public static void ResetSession() { before = contributionA = contributionB = 0; }
        public static void BeforeHealth(Unity.Entities.World world)
        {
            Trace.Add("capture"); Captures++;
            if (ThrowOnCapture) throw new InvalidOperationException("Test capture failure");
            SetWorld(world);
            before = world.Target.Health;
            contributionA = world.Target.DamageA;
            contributionB = world.Target.DamageB;
        }
        public static void AfterHealth(Unity.Entities.World world)
        {
            Trace.Add("observe"); Observations++;
            CapturedAfter = world.Target.Health;
            double total = contributionA + contributionB;
            CreditA += Progression.EffectiveShare(before, CapturedAfter, contributionA, total);
            CreditB += Progression.EffectiveShare(before, CapturedAfter, contributionB, total);
            ResetSession();
        }
    }
}

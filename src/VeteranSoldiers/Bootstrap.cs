using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Systems;
using Unity.Entities;
using UnityEngine;

namespace VeteranSoldiers
{
    public sealed class VeteranBootstrapSystem : SystemBase
    {
        protected override void OnCreate()
        {
            VeteranRuntime.Initialize(World);
        }
        protected override void OnUpdate()
        {
            try
            {
                VeteranRuntime.Tick();
#if VETERAN_TEST_HARNESS
                RuntimeSelfTest.Tick(); RuntimeBenchmark.Tick(); RuntimeVisualTest.Tick();
#endif
            }
            catch (Exception ex) { VeteranRuntime.Fail(ex); }
        }
        protected override void OnDestroy()
        {
            if (ReferenceEquals(VeteranRuntime.ActiveWorld, World)) VeteranRuntime.ResetSession();
        }
    }

    public sealed class VeteranHost : MonoBehaviour
    {
        private void Update()
        {
            try
            {
                VeteranRuntime.Tick();
#if VETERAN_TEST_HARNESS
                RuntimeSelfTest.Tick(); RuntimeVisualTest.Tick();
#endif
            }
            catch (Exception ex) { VeteranRuntime.Fail(ex); }
            VeteranNativePanel.Tick();
        }
    }

    internal static class HealthHooks
    {
        // The postfix runs before SystemState.AfterOnUpdate registers Dependency in the
        // EntityManager. Complete this system's own handle explicitly before reading health.
        private static readonly Action<SystemBase> Complete = (Action<SystemBase>)Delegate.CreateDelegate(
            typeof(Action<SystemBase>), AccessTools.Method(typeof(SystemBase), "CompleteDependency"));
        // Observe ordinary mods' request edits before attribution, then measure health
        // before their postfix effects (for example healing). Explicit before/after
        // constraints from another mod can still override these priorities.
        [HarmonyPriority(Priority.Last)]
        public static void Before(HealthTrackSystem __instance, bool __runOriginal, out World? __state)
        {
            __state = null;
            if (!__runOriginal || !VeteranRuntime.Ready) return;
            try
            {
                Complete(__instance);
                var world = __instance.World;
                VeteranRuntime.BeforeHealth(world);
                if (VeteranRuntime.Ready && ReferenceEquals(VeteranRuntime.ActiveWorld, world))
                    __state = world;
            }
            catch (Exception ex) { VeteranRuntime.Fail(ex); }
        }

        [HarmonyPriority(Priority.First)]
        public static void After(HealthTrackSystem __instance, bool __runOriginal, World? __state)
        {
            if (!__runOriginal || __state == null || !VeteranRuntime.Ready) return;
            try
            {
                if (!ReferenceEquals(__state, __instance.World) ||
                    !ReferenceEquals(VeteranRuntime.ActiveWorld, __state)) return;
                Complete(__instance);
                VeteranRuntime.AfterHealth(__state);
            }
            catch (Exception ex) { VeteranRuntime.Fail(ex); }
        }
    }

    public static partial class VeteranRuntime
    {
        public const string Guid = "com.veteransoldiers.mod";
        public const string Version = "1.0.1";
        public static ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Veteran Soldiers");
        public static ModSettings Settings = null!;
        private static bool initialized;
        private static bool failed;
        public static bool Ready => initialized && !failed && Settings.Enabled.Value;
        public static bool PersistenceEnabled => Ready && Settings.Persistence.Value;

        public static void Initialize(World world)
        {
            if (initialized) return;
            try
            {
                Settings = new ModSettings(new ConfigFile(Path.Combine(Paths.ConfigPath, Guid + ".cfg"), true));
#if VETERAN_TEST_HARNESS
                RuntimeBenchmark.Initialize(world);
#endif
                if (!Settings.Enabled.Value)
                {
                    initialized = true;
                    Log.LogInfo("Veteran Soldiers disabled by configuration. No runtime hooks installed.");
                    return;
                }
                if (typeof(HealthTrackSystem).Assembly.GetType("VeteranSoldiers.InstrumentationMarker") == null)
                    throw new InvalidOperationException("Preloader instrumentation missing. Install VeteranSoldiers.Patcher.dll in BepInEx/patchers and restart.");
                SetWorld(world);
#if VETERAN_TEST_HARNESS
                RuntimeVisualTest.Initialize(world);
#endif
                var harmony = new Harmony(Guid);
                harmony.Patch(AccessTools.Method(typeof(HealthTrackSystem), "OnUpdateSimulation"),
                    new HarmonyMethod(typeof(HealthHooks), nameof(HealthHooks.Before)),
                    new HarmonyMethod(typeof(HealthHooks), nameof(HealthHooks.After)));
                VeteranPersistence.Initialize(harmony);
                VeteranHall.Initialize(harmony);
                VeteranHallResearch.Initialize(harmony);
                initialized = true;
                var host = new GameObject("VeteranSoldiersHost");
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.AddComponent<VeteranHost>();
                VeteranNativePanel.Initialize(harmony);
                VeteranUiBridge.Initialize(harmony);
                host.AddComponent<VeteranWorldInsignias>();
                host.AddComponent<VeteranPromotionFeed>();
                host.AddComponent<VeteranArmyWindow>();
                Log.LogInfo("Loaded Veteran Soldiers " + Version + " (ECS). Exact damage source instrumentation present. Config: " + Settings.File.ConfigFilePath);
            }
            catch (Exception ex) { Fail(ex); }
        }

        public static void Fail(Exception error)
        {
            if (!failed) Log.LogError("Veterancy disabled after an error. " + error);
            failed = true;
            try { ResetSession(); }
            catch (Exception cleanup) { Log.LogError("Could not remove veterancy health factors; reload a base save before continuing: " + cleanup); }
        }
    }
}

namespace Doorstop
{
    // ECS-aware Doorstop preloads the assembly before Unity enumerates its component types.
    public static class Entrypoint { public static void Start() { } }
}

using System;
using Components.SingletonComponents;
using HarmonyLib;
using ScriptableObjectDefinitions;
using Systems;
using Unity.Entities;

namespace VeteranSoldiers;

/// <summary>Feeds native research initialization a private effect view for the registered Hall prefab.</summary>
internal static class VeteranHallResearch
{
    private sealed class Scope
    {
        internal World World = null!;
        internal ResearchSettingsDefinition Configuration = null!;
        internal int BaseId;
    }
    [ThreadStatic] private static Scope? scope;
    internal static bool Compatible { get; private set; } = true;
    private static string diagnostic = "Native Hall research effects have not been evaluated in this world.";
    internal static string Describe() => diagnostic;

    internal static bool ValidateForLoad(World world)
    {
        // Loading buildings precedes ResearchSystem.OnStartRunning. Validate this world's
        // actual effect view now, before the loader can replace native University IDs.
        // A previous world's Compatible flag is not evidence about this save's settings.
        var previous = scope;
        scope = null;
        try
        {
            if (!world.IsCreated) throw new InvalidOperationException("The loaded research world is unavailable.");
            world.EntityManager.CompleteAllJobs();
            var configuration = ReadConfiguration(world);
            ResearchItem[] items = configuration.GetAvailableResearches() ?? Array.Empty<ResearchItem>();
            HallResearchAliases.Copy(items, VeteranHall.BaseObjectId, VeteranHall.ObjectId, out int added, out string targets);
            Compatible = true;
            diagnostic = "Hall load research preflight base=" + VeteranHall.BaseObjectId + "; targets=" + targets + "; aliases=" + added + "; assets unchanged.";
            return true;
        }
        catch (Exception error) { Reject(error); return false; }
        finally { scope = previous; }
    }

    private static ResearchSettingsDefinition ReadConfiguration(World world)
    {
        using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<CurrentSettingsSingleton>());
        if (query.CalculateEntityCount() != 1) throw new InvalidOperationException("The current research settings singleton is unavailable.");
        var settings = world.EntityManager.GetComponentObject<CurrentSettingsSingleton>(query.GetSingletonEntity());
        var configuration = settings.missionDifficultyConfiguration.researchConfig;
        if (configuration == null) throw new InvalidOperationException("The current research configuration is unavailable.");
        return configuration;
    }

    internal static void Initialize(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(ResearchSystem), "OnStartRunning"),
            prefix: new HarmonyMethod(typeof(VeteranHallResearch), nameof(BeforeStart)),
            postfix: new HarmonyMethod(typeof(VeteranHallResearch), nameof(AfterStart)),
            finalizer: new HarmonyMethod(typeof(VeteranHallResearch), nameof(FinishStart)));
        harmony.Patch(AccessTools.Method(typeof(ResearchSettingsDefinition), nameof(ResearchSettingsDefinition.GetAvailableResearches)),
            postfix: new HarmonyMethod(typeof(VeteranHallResearch), nameof(AfterAvailable)));
    }

    [HarmonyPriority(Priority.Last)]
    private static void BeforeStart(ResearchSystem __instance, bool __runOriginal, out Scope? __state)
    {
        __state = scope;
        scope = null;
        Compatible = true;
        diagnostic = "No registered Hall prefab during native research initialization.";
        if (!__runOriginal)
        {
            Reject(new InvalidOperationException("Another hook skipped native research initialization; the Hall research view was not consumed."));
            return;
        }
        if (!VeteranRuntime.Settings.Enabled.Value || !__instance.World.IsCreated) return;
        try
        {
            // Registration must precede native InitializeAffects, including a fresh process load.
            VeteranHall.EnsureRegistered(__instance.World, true);
            if (!VeteranHall.Available) return;
            __instance.EntityManager.CompleteAllJobs();
            var configuration = ReadConfiguration(__instance.World);
            scope = new Scope { World = __instance.World, Configuration = configuration, BaseId = VeteranHall.BaseObjectId };
        }
        catch (Exception error) { Reject(error); }
    }

    [HarmonyPriority(Priority.Last)]
    private static void AfterAvailable(ResearchSettingsDefinition __instance, ref ResearchItem[]? __result)
    {
        Scope? current = scope;
        if (current == null || !current.World.IsCreated || !ReferenceEquals(__instance, current.Configuration) || __result == null) return;
        try
        {
            // Publish only after the entire copied view validates. The original assets and base-ID
            // effects remain untouched, including modifiers supplied by other mods before this hook.
            ResearchItem[] result = HallResearchAliases.Copy(__result, current.BaseId, VeteranHall.ObjectId, out int added, out string targets);
            __result = result;
            diagnostic = "Native research base=" + current.BaseId + "; targets=" + targets + "; Hall aliases=" + added + ".";
            VeteranRuntime.Log.LogInfo(diagnostic);
        }
        catch (Exception error) { Reject(error); }
    }

    private static void AfterStart(Scope? __state, bool __runOriginal)
    {
        // A foreign prefix can explicitly order itself after ours and then cancel.
        // Only the final runOriginal value proves that native initialization ran.
        if (!__runOriginal && Compatible)
            Reject(new InvalidOperationException("A later hook skipped native research initialization; the Hall research view was not consumed."));
        scope = __state;
    }
    private static Exception? FinishStart(Scope? __state, Exception? __exception)
    {
        if (__exception != null)
            Reject(new InvalidOperationException("Native research initialization did not complete successfully.", __exception));
        scope = __state;
        return __exception; // Preserve the game's/other mod's exception; never claim successful startup.
    }
    private static void Reject(Exception error)
    {
        Compatible = false;
        scope = null;
        diagnostic = "Hall research compatibility unavailable: " + error.Message;
        VeteranRuntime.Log.LogError(diagnostic);
    }
}

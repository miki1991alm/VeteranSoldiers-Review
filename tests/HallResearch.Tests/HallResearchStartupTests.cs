using System;
using HarmonyLib;
using Systems;
using VeteranSoldiers;

internal static class HallResearchStartupTests
{
    private const string Owner = "veteransoldiers.tests.hall-research";
    private static bool compatibleAtCancellation;

    private static bool CancelLate()
    {
        compatibleAtCancellation = VeteranHallResearch.Compatible;
        return false;
    }

    internal static void Run(Action<bool, string> check)
    {
        var owned = new Harmony(Owner);
        var foreign = new Harmony("veteransoldiers.tests.later-research-mod");
        var target = AccessTools.Method(typeof(ResearchSystem), nameof(ResearchSystem.OnStartRunning));
        void Clean() { owned.UnpatchSelf(); foreign.UnpatchSelf(); VeteranRuntime.Settings.Enabled.Value = true; VeteranRuntime.Log.Errors.Clear(); }
        void InstallForeign()
        {
            foreign.Patch(target, prefix: new HarmonyMethod(typeof(HallResearchStartupTests), nameof(CancelLate))
            { priority = Priority.Last, after = new[] { Owner } });
        }
        bool OutsideScope(ResearchSystem system) =>
            system.World.EntityManager.Settings.missionDifficultyConfiguration.researchConfig.GetAvailableResearches()[0].healthEffects.Length == 1;

        try
        {
            Clean();
            VeteranHallResearch.Initialize(owned);
            var normal = new ResearchSystem();
            normal.OnStartRunning();
            check(normal.OriginalCalls == 1 && VeteranHallResearch.Compatible, "normal native startup remains compatible");
            check(normal.Consumed != null && normal.Consumed[0].healthEffects.Length == 2 && normal.Consumed[0].healthEffects[1].objectId == VeteranHall.ObjectId,
                "the real production hooks supply Hall aliases only inside native startup");
            check(OutsideScope(normal), "successful startup restores the original research asset view");

            foreach (bool foreignFirst in new[] { false, true })
            {
                Clean();
                if (foreignFirst) InstallForeign();
                VeteranHallResearch.Initialize(owned);
                if (!foreignFirst) InstallForeign();
                compatibleAtCancellation = false;
                var cancelled = new ResearchSystem();
                cancelled.OnStartRunning();
                string order = foreignFirst ? "foreign installed first" : "foreign installed last";
                check(compatibleAtCancellation && cancelled.OriginalCalls == 0,
                    "foreign prefix really cancels after our compatible prefix: " + order);
                check(!VeteranHallResearch.Compatible, "final runOriginal rejects late cancellation: " + order);
                check(OutsideScope(cancelled), "late cancellation never leaks the scoped alias view: " + order);
            }

            foreach (bool beforeRead in new[] { true, false })
            {
                Clean();
                VeteranHallResearch.Initialize(owned);
                var throwing = new ResearchSystem { ThrowBeforeRead = beforeRead, ThrowAfterRead = !beforeRead };
                Exception? observed = null;
                try { throwing.OnStartRunning(); } catch (Exception error) { observed = error; }
                string moment = beforeRead ? "before effect consumption" : "after effect consumption";
                check(ReferenceEquals(observed, throwing.ExpectedError), "startup preserves the exact native exception " + moment);
                check(!VeteranHallResearch.Compatible, "failed initialization disables Hall compatibility " + moment);
                check(OutsideScope(throwing), "exception finalizer cleans the alias scope " + moment);
            }

            Clean();
            VeteranHallResearch.Initialize(owned);
            var recovered = new ResearchSystem();
            recovered.OnStartRunning();
            check(VeteranHallResearch.Compatible && recovered.Consumed![0].healthEffects.Length == 2,
                "a later successful world can re-evaluate compatibility after failure");
            check(OutsideScope(recovered), "recovered startup retains original configuration outside its scope");
        }
        finally { Clean(); }
    }
}

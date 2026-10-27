using System;

namespace VeteranSoldiers;

internal enum HallDoctrine { None, Tradition, Replacements, Recovery }
internal enum DoctrineBlocker { None, Invalid, Research, Hall, Preparing, Active, Experience, Resources }

/// <summary>Pure rules shared by presentation, save validation and gameplay.</summary>
internal static class HallRules
{
    internal const double ResearchSeconds = 60;
    internal const int MaximumHalls = 128;
    internal static bool Valid(HallDoctrine value) => value >= HallDoctrine.None && value <= HallDoctrine.Recovery;
    internal static double ExperienceThreshold(HallDoctrine value) => value == HallDoctrine.Tradition ? 100 :
        value == HallDoctrine.Replacements ? 300 : value == HallDoctrine.Recovery ? 600 : double.PositiveInfinity;
    internal static int Mask(HallDoctrine value) => value == HallDoctrine.None ? 0 : 1 << ((int)value - 1);
    internal static DoctrineBlocker Blocker(HallDoctrine choice, bool compatible, bool operational,
        HallDoctrine pending, HallDoctrine active, int unlocked, bool enoughResources)
    {
        if (!Valid(choice) || choice == HallDoctrine.None) return DoctrineBlocker.Invalid;
        if (!compatible) return DoctrineBlocker.Research;
        if (!operational) return DoctrineBlocker.Hall;
        if (pending != HallDoctrine.None) return DoctrineBlocker.Preparing;
        if (active == choice) return DoctrineBlocker.Active;
        if ((unlocked & Mask(choice)) == 0) return DoctrineBlocker.Experience;
        return enoughResources ? DoctrineBlocker.None : DoctrineBlocker.Resources;
    }
    internal static float ComposeTraining(float existing) => float.IsNaN(existing) || float.IsInfinity(existing)
        ? existing : 1f - (1f - existing) * .85f;
    internal static double Advance(double elapsed, double delta, bool operational) =>
        !operational || double.IsNaN(delta) || double.IsInfinity(delta) || delta <= 0 ? elapsed :
        Math.Min(ResearchSeconds, elapsed + delta);
}

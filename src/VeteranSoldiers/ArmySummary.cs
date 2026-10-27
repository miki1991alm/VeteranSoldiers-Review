using System;

namespace VeteranSoldiers;

/// <summary>Bounded aggregate for one observed army snapshot, independent from Unity.</summary>
public sealed class ArmySummary
{
    public readonly int[] Counts = new int[5];
    public readonly int[] Selectable = new int[5];
    public readonly double[] Experience = new double[5];
    public int Total { get; private set; }
    public int Available { get; private set; }
    public double TotalExperience { get; private set; }
    public double Health { get; private set; }
    public double MaximumHealth { get; private set; }
    public void Clear()
    {
        Array.Clear(Counts, 0, 5); Array.Clear(Selectable, 0, 5); Array.Clear(Experience, 0, 5);
        Total = Available = 0; TotalExperience = Health = MaximumHealth = 0;
    }
    public void Add(int rank, double xp, double health, double maximum, bool selectable)
    {
        if (rank < 0 || rank >= 5 || !Progression.Finite(xp) || xp < 0 ||
            !Progression.Finite(health) || health <= 0 || !Progression.Finite(maximum) || maximum <= 0) return;
        Counts[rank]++; Experience[rank] = SaturatingSum(Experience[rank], xp); Total++; TotalExperience = SaturatingSum(TotalExperience, xp);
        Health += health; MaximumHealth += maximum;
        if (selectable) { Available++; Selectable[rank]++; }
    }
    private static double SaturatingSum(double current, double addition) => current > double.MaxValue - addition ? double.MaxValue : current + addition;
}

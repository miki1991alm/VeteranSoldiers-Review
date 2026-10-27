using System;

namespace VeteranSoldiers;

public sealed class Rank
{
    public string Name = "Recluta";
    public double Threshold;
    public float DamageBonus;
    public float HealthBonus;
}

public static class Progression
{
    public static Rank[] Defaults() => new[]
    {
        new Rank { Name = "Recluta", Threshold = 0 },
        new Rank { Name = "Entrenado", Threshold = 100, DamageBonus = .05f },
        new Rank { Name = "Veterano", Threshold = 300, DamageBonus = .10f, HealthBonus = .10f },
        new Rank { Name = "Élite", Threshold = 700, DamageBonus = .15f, HealthBonus = .20f },
        new Rank { Name = "Legendario", Threshold = 1500, DamageBonus = .25f, HealthBonus = .30f }
    };

    public static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    public static int Level(double experience, Rank[] ranks, int maxLevel)
    {
        int limit = Math.Max(1, Math.Min(maxLevel, ranks.Length));
        int level = 0;
        if (!Finite(experience)) return level;
        while (level + 1 < limit && experience >= ranks[level + 1].Threshold) level++;
        return level;
    }

    public static double Fraction(double experience, Rank[] ranks, int maxLevel)
    {
        int level = Level(experience, ranks, maxLevel);
        if (level + 1 >= Math.Min(maxLevel, ranks.Length)) return 1;
        return Math.Max(0, Math.Min(1, (experience - ranks[level].Threshold) /
            (ranks[level + 1].Threshold - ranks[level].Threshold)));
    }

    // Credit only the observed loss, never more than the target's pre-impact health.
    // Simultaneous attackers share that loss proportionally, including non-eligible sources.
    public static double EffectiveShare(double before, double after, double contribution, double totalDamage)
    {
        if (!Finite(before) || !Finite(after) || !Finite(contribution) || !Finite(totalDamage) ||
            before <= 0 || contribution <= 0 || totalDamage <= 0) return 0;
        double observed = Math.Min(before, Math.Max(0, before - Math.Max(0, after)));
        return Math.Min(observed, totalDamage) * Math.Min(1, contribution / totalDamage);
    }

    public static double Award(double damage, bool kill, double damagePerXp, double killXp,
        double multiplier, double classMultiplier)
    {
        double value = (damage / Math.Max(.001, damagePerXp) + (kill ? killXp : 0)) * multiplier * classMultiplier;
        return Finite(value) && value > 0 ? value : 0;
    }

    public static float CombinedHealthFactor(float vanillaMax, float bonus, float maxHealth)
    {
        if (vanillaMax <= 0 || !Finite(vanillaMax)) return 1;
        // A cap never removes pre-existing vanilla/research health.
        return Math.Max(1, Math.Min(1 + bonus, Math.Max(vanillaMax, maxHealth) / vanillaMax));
    }

    public static double Accumulate(double current, double earned)
    {
        if (!Finite(current) || current < 0 || !Finite(earned) || earned <= 0) return current;
        // Cap new growth, but never erase a larger finite value from an older/custom save.
        return Math.Max(current, Math.Min(1e12, current + earned));
    }

    public static void Validate(Rank[] ranks)
    {
        if (ranks.Length != 5 || ranks[0].Threshold != 0) throw new ArgumentException("Five ranks starting at zero are required.");
        if (ranks[0].DamageBonus != 0 || ranks[0].HealthBonus != 0)
            throw new ArgumentException("Recruit (Rank1) bonuses must remain zero; configure bonuses for Rank2 through Rank5.");
        for (int i = 0; i < ranks.Length; i++)
        {
            if (!Finite(ranks[i].Threshold) || ranks[i].Threshold < 0 ||
                (i > 0 && ranks[i].Threshold <= ranks[i - 1].Threshold) ||
                !Finite(ranks[i].DamageBonus) || ranks[i].DamageBonus < 0 || ranks[i].DamageBonus > 10 ||
                !Finite(ranks[i].HealthBonus) || ranks[i].HealthBonus < 0 || ranks[i].HealthBonus > 10)
                throw new ArgumentException("Rank thresholds must increase; bonuses must be finite and between 0 and 10.");
        }
    }
}

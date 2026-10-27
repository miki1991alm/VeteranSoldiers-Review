using System;

namespace VeteranSoldiers;

/// <summary>Bounded presentation batches; no entity references, gameplay writes or save data.</summary>
internal sealed class PromotionQueue
{
    private const double BatchDelay = .6;
    private const double DisplayDuration = 5;
    private const double MaximumHoverExtension = 10;
    private readonly int[] pending = new int[5];
    private readonly int[] visible = new int[5];
    private double firstPending = -1, started = -1, lastTick = -1, hoverExtension;
    internal int Total { get; private set; }
    internal int HighestRank { get; private set; }
    internal int Revision { get; private set; }
    internal bool HasPending => firstPending >= 0;
    internal int Count(int rank) => rank >= 0 && rank < visible.Length ? visible[rank] : 0;

    internal void Add(int oldRank, int newRank, double now)
    {
        if (oldRank < 0 || newRank <= oldRank || newRank >= pending.Length || !Progression.Finite(now) || now < 0) return;
        if (firstPending < 0) firstPending = now;
        // Saturating counters keep mass promotions bounded without allocating a card per unit.
        pending[newRank] = Math.Min(999999, pending[newRank] + 1);
    }

    internal bool Tick(double now, bool pointerInside = false)
    {
        if (!Progression.Finite(now) || now < 0 || (lastTick >= 0 && now < lastTick) || (started >= 0 && now < started) || (firstPending >= 0 && now < firstPending))
        { Clear(); return false; }
        if (started >= 0 && pointerInside && lastTick >= 0)
            hoverExtension += Math.Min(MaximumHoverExtension - hoverExtension, now - lastTick);
        lastTick = now;
        if (started >= 0 && now - started - hoverExtension < DisplayDuration) return true;
        if (started >= 0) { Array.Clear(visible, 0, visible.Length); Total = 0; started = -1; hoverExtension = 0; }
        if (firstPending < 0 || now - firstPending < BatchDelay) return false;
        Total = 0;
        HighestRank = 0;
        for (int i = 1; i < pending.Length; i++)
        {
            visible[i] = pending[i]; pending[i] = 0;
            Total += visible[i];
            if (visible[i] > 0) HighestRank = i;
        }
        firstPending = -1;
        if (Total == 0) return false;
        started = now; hoverExtension = 0;
        Revision++;
        return true;
    }

    internal float Opacity(double now)
    {
        if (started < 0 || !Progression.Finite(now)) return 0;
        double elapsed = now - started - hoverExtension;
        return (float)Math.Max(0, Math.Min(1, Math.Min((now - started) / .2, (DisplayDuration - elapsed) / .6)));
    }

    internal void Clear()
    {
        Array.Clear(pending, 0, pending.Length);
        Array.Clear(visible, 0, visible.Length);
        firstPending = started = lastTick = -1; hoverExtension = 0;
        Total = HighestRank = 0;
        Revision++;
    }
}

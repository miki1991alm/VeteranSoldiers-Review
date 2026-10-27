using Unity.Entities;

namespace VeteranSoldiers;

// This component is attached to the exact HealthChangeRequest, not to a guessed target.
public struct CombatSource : IComponentData
{
    public Entity Source;
    public byte Processed;
}

public static class CombatBridge
{
    // Kept for earlier preloaders: their request archetype does not include CombatSource.
    public static void Tag(ref EntityCommandBuffer.ParallelWriter buffer, int sortKey, Entity request, Entity source)
    {
        buffer.AddComponent(sortKey, request, new CombatSource { Source = source });
    }

    public static void TagDirect(ref EntityCommandBuffer buffer, Entity request, Entity source)
    {
        buffer.AddComponent(request, new CombatSource { Source = source });
    }

    // The matching preloader creates these requests with CombatSource already present.
    // SetComponent avoids a second archetype transition during ECB playback. Keep distinct
    // entry points so an old preloader never tries to SetComponent on its unprepared request.
    public static void TagPrepared(ref EntityCommandBuffer.ParallelWriter buffer, int sortKey, Entity request, Entity source)
    {
        buffer.SetComponent(sortKey, request, new CombatSource { Source = source });
    }

    public static void TagDirectPrepared(ref EntityCommandBuffer buffer, Entity request, Entity source)
    {
        buffer.SetComponent(request, new CombatSource { Source = source });
    }
}

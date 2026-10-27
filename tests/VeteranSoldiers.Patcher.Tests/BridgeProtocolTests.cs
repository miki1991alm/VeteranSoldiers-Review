using System;
using System.Collections.Generic;
using Unity.Entities;
using VeteranSoldiers;

// These managed protocol doubles record calls made by the real CombatBridge.cs.
// Counts describe requested structural operations, not Unity timings or memory traffic.
namespace Unity.Entities
{
    public interface IComponentData { }
    public struct Entity : IEquatable<Entity>
    {
        public int Index, Version;
        public Entity(int index, int version) { Index = index; Version = version; }
        public bool Equals(Entity other) => Index == other.Index && Version == other.Version;
        public override bool Equals(object? value) => value is Entity other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Index, Version);
    }
    public struct EntityCommandBuffer
    {
        private readonly CommandRecorder recorder;
        internal EntityCommandBuffer(CommandRecorder recorder) { this.recorder = recorder; }
        public void AddComponent<T>(Entity request, T value) where T : struct, IComponentData => recorder.Record(true, 0, request, value);
        public void SetComponent<T>(Entity request, T value) where T : struct, IComponentData => recorder.Record(false, 0, request, value);
        public ParallelWriter AsParallelWriter() => new ParallelWriter(recorder);
        public struct ParallelWriter
        {
            private readonly CommandRecorder recorder;
            internal ParallelWriter(CommandRecorder recorder) { this.recorder = recorder; }
            public void AddComponent<T>(int sortKey, Entity request, T value) where T : struct, IComponentData => recorder.Record(true, sortKey, request, value);
            public void SetComponent<T>(int sortKey, Entity request, T value) where T : struct, IComponentData => recorder.Record(false, sortKey, request, value);
        }
    }
}

internal sealed class CommandRecorder
{
    internal readonly List<(bool Add, int SortKey, Entity Request, CombatSource Source)> Commands = new();
    internal void Record<T>(bool add, int key, Entity request, T value) where T : struct, IComponentData
    {
        if (value is not CombatSource source) throw new InvalidOperationException("Unexpected bridge component.");
        Commands.Add((add, key, request, source));
    }
}

internal static class BridgeProtocolTests
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (bool direct in new[] { false, true })
        {
            foreach (int hits in new[] { 1, 1000, 10000 })
            {
                var legacy = Emit(hits, direct, false);
                var prepared = Emit(hits, direct, true);
                bool equal = legacy.Commands.Count == prepared.Commands.Count;
                int oldAdds = 0, newAdds = 0;
                for (int i = 0; i < hits; i++)
                {
                    var oldCommand = legacy.Commands[i];
                    var newCommand = prepared.Commands[i];
                    if (oldCommand.Add) oldAdds++;
                    if (newCommand.Add) newAdds++;
                    equal &= oldCommand.Request.Equals(newCommand.Request) && oldCommand.SortKey == newCommand.SortKey &&
                        oldCommand.Source.Source.Equals(newCommand.Source.Source) &&
                        oldCommand.Source.Processed == 0 && newCommand.Source.Processed == 0;
                }
                string route = direct ? "direct" : "parallel-writer";
                check(equal, $"prepared {route} bridge preserves exact request/source version, sort key and fresh attribution for {hits} hits");
                check(oldAdds == hits && newAdds == 0, $"prepared {route} bridge eliminates {hits} post-create component-add commands");
                // Unity playback still receives three commands per hit: CreateEntity,
                // Set<HealthChangeRequest>, Set<CombatSource>. Only Add becomes Set.
                check(legacy.Commands.Count == hits && prepared.Commands.Count == hits,
                    $"prepared {route} route does not duplicate or omit attribution writes");
                Console.WriteLine($"REQUEST_WORK route={route} hits={hits} legacy_component_adds={oldAdds} prepared_component_adds={newAdds} total_commands_per_hit=3 both_paths=true (not a timing benchmark)");
            }
        }
        var oldRuntimeContract = new CommandRecorder();
        var oldBuffer = new EntityCommandBuffer(oldRuntimeContract);
        var oldWriter = oldBuffer.AsParallelWriter();
        CombatBridge.Tag(ref oldWriter, 7, new Entity(-1, 1), new Entity(9, 4));
        CombatBridge.TagDirect(ref oldBuffer, new Entity(-2, 1), new Entity(9, 5));
        check(oldRuntimeContract.Commands.TrueForAll(command => command.Add),
            "legacy bridge entry points retain AddComponent for old preloaders");
        check(!oldRuntimeContract.Commands[0].Source.Source.Equals(oldRuntimeContract.Commands[1].Source.Source),
            "recycled source indices retain distinct versions through both bridge entry points");
    }

    private static CommandRecorder Emit(int hits, bool direct, bool prepared)
    {
        var recorder = new CommandRecorder();
        var buffer = new EntityCommandBuffer(recorder);
        var writer = buffer.AsParallelWriter();
        for (int i = 0; i < hits; i++)
        {
            var request = new Entity(-i - 1, 1);
            // Reuse source indices deliberately, with distinct versions.
            var source = new Entity(i % 137 + 1, i / 137 + 1);
            if (direct)
            {
                if (prepared) CombatBridge.TagDirectPrepared(ref buffer, request, source);
                else CombatBridge.TagDirect(ref buffer, request, source);
            }
            else
            {
                if (prepared) CombatBridge.TagPrepared(ref writer, i % 16, request, source);
                else CombatBridge.Tag(ref writer, i % 16, request, source);
            }
        }
        return recorder;
    }
}

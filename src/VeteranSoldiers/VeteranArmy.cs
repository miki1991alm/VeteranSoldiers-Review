using System;
using System.Collections.Generic;
using Components;
using Components.RawComponents;
using Components.SystemStateComponents;
using Components.Commands;
using Systems;
using Systems.GameStateSystems;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using Utility;
using Utility.EnumsStorage;
using UI;

namespace VeteranSoldiers;

/// <summary>On-demand roster. World and entity version are checked again at every action.</summary>
internal static class VeteranArmy
{
    internal static readonly ArmySummary Summary = new ArmySummary();
    private static World? world;
    private static EntityQuery query;
    private static bool queryCreated;
    private static readonly List<Entity> eligibleSelection = new List<Entity>();
    internal static readonly List<VeteranRosterEntry> Entries = new List<VeteranRosterEntry>();

    internal static void Refresh()
    {
        Summary.Clear();
        Entries.Clear();
        var active = VeteranRuntime.ActiveWorld;
        if (!VeteranRuntime.Ready || active == null || !active.IsCreated) return;
        if (!ReferenceEquals(world, active))
        {
            if (queryCreated && world != null && world.IsCreated) query.Dispose();
            world = active;
            query = active.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Unit>(), ComponentType.ReadOnly<Health>(),
                ComponentType.ReadOnly<HealthBase>(), ComponentType.Exclude<Enemy>());
            queryCreated = true;
        }
        VeteranRuntime.Manager.CompleteAllJobs();
        if (!IsCurrent(active)) return;
        using var units = query.ToEntityArray(Allocator.Temp);
        foreach (var unit in units)
        {
            if (!VeteranRuntime.TryGetSnapshot(unit, out var snapshot)) continue;
            int level = Progression.Level(snapshot.Experience, VeteranRuntime.Settings.Ranks, VeteranRuntime.Settings.MaxLevel.Value);
            Summary.Add(level, snapshot.Experience, snapshot.CurrentHealth,
                snapshot.VanillaMaxHealth * snapshot.AppliedHealthFactor, CanSelect(unit));
            Entries.Add(new VeteranRosterEntry(unit, VeteranRuntime.Manager.GetComponentData<UnitBase>(unit).value.Value.type,
                UnitName(unit), level, snapshot, CanSelect(unit)));
        }
        Entries.Sort((a, b) => { int rank = b.Level.CompareTo(a.Level); if (rank != 0) return rank;
            int xp = b.Snapshot.Experience.CompareTo(a.Snapshot.Experience); return xp != 0 ? xp : a.Entity.Index.CompareTo(b.Entity.Index); });
    }

    internal static string UnitName(Entity unit)
    {
        try
        {
            int id = VeteranRuntime.Manager.GetComponentData<ObjectId>(unit).value;
            if (GameState.ObjectsUIDataMap.TryGetValue(id, out var data))
            {
                string name = LocalizationStrings.GetObjectString(data.Name);
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
        }
        catch { }
        return VeteranText.Choose("Soldier", "Soldado");
    }

    internal static bool SelectOne(World? expectedWorld, Entity unit)
    {
        if (!IsCurrent(expectedWorld)) return false;
        expectedWorld!.EntityManager.CompleteAllJobs();
        if (!IsCurrent(expectedWorld)) return false;
        if (!CanSelect(unit)) return false;
        return SelectEntities(new[] { unit }) == 1;
    }

    internal static bool Locate(World? expectedWorld, Entity unit)
    {
        if (!IsCurrent(expectedWorld)) return false;
        expectedWorld!.EntityManager.CompleteAllJobs();
        if (!IsCurrent(expectedWorld)) return false;
        if (!VeteranRuntime.IsEligible(unit) ||
            !VeteranRuntime.Manager.HasComponent<Translation>(unit)) return false;
        var manager = VeteranRuntime.Manager;
        // Uses the native camera command; does not move or issue an order to the soldier.
        var command = manager.CreateEntity(typeof(CameraInstantMoveToPositionCommandData));
        manager.SetComponentData(command, new CameraInstantMoveToPositionCommandData { position = manager.GetComponentData<Translation>(unit).Value });
        return true;
    }

    internal static int SelectEntities(IReadOnlyList<Entity> entities)
    {
        var active = VeteranRuntime.ActiveWorld;
        if (!IsCurrent(active) || !VeteranNativePanel.GameplayOverlayAllowed) return 0;
        var manager = active!.EntityManager;
        manager.CompleteAllJobs();
        if (!IsCurrent(active)) return 0;
        var system = active!.GetExistingSystem<ObjectSelectionSystem>();
        if (system == null) return 0;
        eligibleSelection.Clear();
        foreach (var unit in entities) if (CanSelect(unit)) eligibleSelection.Add(unit);
        if (eligibleSelection.Count == 0) return 0;
        var utility = new ObjectSelectionSystem.SelectionUtility(system);
        using var selectedQuery = manager.CreateEntityQuery(ComponentType.ReadOnly<Selected>());
        using var selected = selectedQuery.ToEntityArray(Allocator.Temp);
        using var commands = new EntityCommandBuffer(Allocator.TempJob);
        var writer = commands.AsParallelWriter();
        foreach (var unit in selected) utility.Deselect(unit, 0, writer);
        foreach (var unit in eligibleSelection) utility.UnitSelect(unit, 1, writer);
        commands.Playback(manager);
        GameStateUpdateTasksUtility.UpdateGameState(manager, GameStateUpdateTaskType.Selection);
        return eligibleSelection.Count;
    }

    internal static bool CanSelect(Entity unit)
    {
        var manager = VeteranRuntime.Manager;
        if (!VeteranRuntime.IsEligible(unit) || manager.HasComponent<Enemy>(unit) || !manager.HasComponent<Selectable>(unit) ||
            manager.HasComponent<UnitOnTower>(unit) || manager.HasComponent<IsInside>(unit)) return false;
        var selection = manager.GetComponentData<Selectable>(unit).selectionEntity;
        return manager.Exists(selection) && manager.HasComponent<SelectableState>(selection);
    }

    private static bool IsCurrent(World? expected) => VeteranRuntime.Ready && expected != null && expected.IsCreated && ReferenceEquals(expected, VeteranRuntime.ActiveWorld);

    internal static int SelectRank(int rank, bool currentSelectionOnly = false)
    {
        if (rank < -1 || rank > 4 || !VeteranNativePanel.GameplayOverlayAllowed) return 0;
        Refresh(); // No stale entity lists survive destruction, load or a world change.
        if (!IsCurrent(world) || !queryCreated) return 0;
        var manager = world!.EntityManager;
        var system = world.GetExistingSystem<ObjectSelectionSystem>();
        if (system == null) return 0;
        eligibleSelection.Clear();
        using var candidates = query.ToEntityArray(Allocator.Temp);
        foreach (var unit in candidates)
        {
            if (!CanSelect(unit) || (currentSelectionOnly && !manager.HasComponent<Selected>(unit)) ||
                !VeteranRuntime.TryGetExperience(unit, out double xp)) continue;
            if (rank < 0 || Progression.Level(xp, VeteranRuntime.Settings.Ranks, VeteranRuntime.Settings.MaxLevel.Value) == rank)
                eligibleSelection.Add(unit);
        }
        // Empty filters preserve the user's current selection.
        if (eligibleSelection.Count == 0) return 0;
        var utility = new ObjectSelectionSystem.SelectionUtility(system);
        using var selectedQuery = manager.CreateEntityQuery(ComponentType.ReadOnly<Selected>());
        using var selected = selectedQuery.ToEntityArray(Allocator.Temp);
        using var commands = new EntityCommandBuffer(Allocator.TempJob);
        var writer = commands.AsParallelWriter();
        foreach (var unit in selected) utility.Deselect(unit, 0, writer);
        foreach (var unit in eligibleSelection) utility.UnitSelect(unit, 1, writer);
        commands.Playback(manager);
        GameStateUpdateTasksUtility.UpdateGameState(manager, GameStateUpdateTaskType.Selection);
        return eligibleSelection.Count;
    }
}

internal readonly struct VeteranRosterEntry
{
    internal readonly Entity Entity;
    internal readonly UnitType Type;
    internal readonly string UnitName;
    internal readonly int Level;
    internal readonly VeteranSnapshot Snapshot;
    internal readonly bool Selectable;
    internal VeteranRosterEntry(Entity entity, UnitType type, string name, int level, VeteranSnapshot snapshot, bool selectable)
    { Entity = entity; Type = type; UnitName = name; Level = level; Snapshot = snapshot; Selectable = selectable; }
}

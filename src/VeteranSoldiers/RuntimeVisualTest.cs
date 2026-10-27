using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using BepInEx;
using Components;
using Components.Commands;
using Components.RawComponents;
using Components.SingletonComponents;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Systems.GameStateSystems;
using UI;
using UI.MainMenuScripts;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Events;
using Utility;
using Utility.EnumsStorage;

namespace VeteranSoldiers;

public struct VisualTestUnit : IComponentData { public int Id; }

/// <summary>
/// Explicit, isolated screenshot fixture. XP is assigned, not earned: this is not a combat test.
/// Explicit hall_* commands may save/load only this isolated QA campaign; no automatic save or exit.
/// </summary>
internal static class RuntimeVisualTest
{
    private enum Phase { Menu, Loading, Spawning, Ready, Released, Failed }
    private static readonly string[] Arguments = Environment.GetCommandLineArgs();
    private static readonly bool Requested = Arguments.Contains("--veteran-visual-test");
    private static readonly Entity[] Units = new Entity[10];
    private static readonly double[] AssignedExperience = new double[10];
    private static Entity[] expectedSelection = Array.Empty<Entity>();
    private static World? world;
    private static Phase phase;
    private static bool initialized, stopped, oldBackground;
    private static float nextTick, nextReport, deadline, nextPanelAction;
    private static int lastCommandId, pendingCommandId;
    private static string directory = string.Empty, session = string.Empty, lastAction = string.Empty;
    private static string commandStatus = "none", commandError = string.Empty, failure = string.Empty;
    private static string capturePath = string.Empty;
    private static string? pendingReport;
    private static int reportWriteAttempts;
    private static float reportRetryAt, reportWarningAt;
    private static float3 origin;
    private static EntityManager Manager => world!.EntityManager;

    internal static void Initialize(World candidate)
    {
        if (!Requested || initialized || stopped) return;
        try
        {
            ValidateIsolation();
            if (Arguments.Any(argument => argument.StartsWith("--veteran-soldiers-benchmark", StringComparison.Ordinal) ||
                argument == "--veteran-soldiers-self-test" || argument == "--veteran-soldiers-resume-test" ||
                argument == "--veteran-soldiers-vanilla-resume-test" || argument == "--veteran-soldiers-selection-test"))
                throw new InvalidOperationException("Visual, self-test and benchmark modes are mutually exclusive.");
            if (!VeteranRuntime.Settings.Enabled.Value || VeteranRuntime.Settings.MaxLevel.Value != 5)
                throw new InvalidOperationException("Visual fixture requires Enabled=true and MaxLevel=5; configuration was not changed.");
            world = candidate;
            directory = Path.Combine(Path.GetFullPath(Paths.GameRootPath), "VeteranSoldiersVisualTest");
            RejectReparse(directory);
            Directory.CreateDirectory(directory);
            session = System.Guid.NewGuid().ToString("N");
            oldBackground = Application.runInBackground;
            Application.runInBackground = true;
            initialized = true;
            phase = Phase.Menu;
            deadline = Time.unscaledTime + 180;
            WriteState();
            VeteranRuntime.Log.LogInfo("VISUALTEST initialized; synthetic rank fixture only. Commands and state: " + directory);
        }
        catch (Exception error) { Fail(error); }
    }

    internal static void Tick()
    {
        if (!Requested || !initialized || Time.unscaledTime < nextTick) return;
        nextTick = Time.unscaledTime + .2f;
        try
        {
            if (pendingReport != null && Time.unscaledTime >= reportRetryAt) PublishPendingState();
            if (stopped || !VeteranRuntime.Ready) return;
            if (world == null || !world.IsCreated || !ReferenceEquals(world, VeteranRuntime.ActiveWorld))
                throw new InvalidOperationException("Visual fixture world changed; no commands will be applied to another world.");
            if (phase != Phase.Ready && Time.unscaledTime > deadline)
                throw new TimeoutException("Visual fixture timed out in " + phase + ".");
            if (phase == Phase.Menu)
            {
                var menu = MainMenu.Instance;
                if (menu == null || menu.inGame || menu.defaultSettings == null) return;
                if (Arguments.Contains("--veteran-hall-test") && !RuntimeHallTest.MenuLocalizationReady()) return;
                ValidateProfilePath();
                if (Arguments.Contains("--veteran-hall-test")) RuntimeHallTest.StartMission();
                else menu.NewTutorialGame();
                phase = Phase.Loading;
                deadline = Time.unscaledTime + 240;
                WriteState();
                return;
            }
            if (phase == Phase.Loading)
            {
                ContinueLoadingScreen();
                ContinueMissionPanel();
                using var running = Manager.CreateEntityQuery(ComponentType.ReadOnly<GameRunningSingleton>());
                if (running.CalculateEntityCount() != 1) return;
                Manager.CompleteAllJobs();
                ValidateProfilePath();
                if (!SpawnFixture()) return;
                phase = Phase.Spawning;
                deadline = Time.unscaledTime + 30;
                WriteState();
                return;
            }
            Manager.CompleteAllJobs();
            RuntimeHallTest.Tick();
            // The native request is consumed before the loading screen's start action is available.
            // Explicit Hall QA reloads therefore need the same continued polling as the first load.
            ContinueLoadingScreen();
            if (phase == Phase.Spawning)
            {
                ContinueMissionPanel();
                if (!ResolveFixture()) return;
                for (int i = 0; i < Units.Length; i++)
                {
                    int rank = i % 5;
                    var ranks = VeteranRuntime.Settings.Ranks;
                    double span = rank < 4 ? ranks[rank + 1].Threshold - ranks[rank].Threshold : 1000;
                    AssignedExperience[i] = ranks[rank].Threshold + span * .25;
                    VeteranRuntime.RestoreExperience(Units[i], AssignedExperience[i]);
                    if (!VeteranRuntime.TryGetSnapshot(Units[i], out var snapshot) ||
                        Progression.Level(snapshot.Experience, ranks, 5) != rank)
                        throw new InvalidOperationException("Could not prepare fixture rank " + rank + ".");
                }
                ClearSelection();
                FocusCamera();
                phase = Phase.Ready;
                nextReport = Time.unscaledTime + .6f;
                WriteState();
                VeteranRuntime.Log.LogInfo("VISUALTEST ready: five Archer ranks and five Swordsman ranks. No unit selected. XP was assigned directly for visual QA.");
                return;
            }
            if (phase != Phase.Ready) return;
            // The operator controls menus, pause and normal exit after fixture preparation.
            ReadCommand();
            if (Time.unscaledTime >= nextReport)
            {
                if (pendingCommandId > 0) { commandStatus = "applied"; pendingCommandId = 0; }
                WriteState();
                nextReport = Time.unscaledTime + 1;
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private static void ValidateIsolation()
    {
        if (Application.companyName != "VS Tests" || Application.productName != "Veteran Soldiers Test Copy")
            throw new InvalidOperationException("Visual fixture requires the exact isolated VS Tests product identity.");
        string root = Path.GetFullPath(Paths.GameRootPath).TrimEnd(Path.DirectorySeparatorChar);
        var game = new DirectoryInfo(root);
        if (!string.Equals(game.Name, "game", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(game.Parent?.Name, "sandbox", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Visual fixture must run from sandbox/game.");
        RejectReparse(root);
        string expectedData = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "..", "LocalLow", "VS Tests", "Veteran Soldiers Test Copy"));
        if (!string.Equals(Path.GetFullPath(Application.persistentDataPath), expectedData, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unity persistentDataPath does not match the isolated product.");
        RejectReparse(expectedData);
        string manifestPath = Path.Combine(root, "veteran-soldiers.sandbox.json");
        RejectReparse(manifestPath);
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 65536)
            throw new InvalidDataException("Missing or oversized sandbox manifest.");
        var manifest = JObject.Parse(File.ReadAllText(manifestPath));
        if ((string?)manifest["Company"] != "VS Tests" || (string?)manifest["Product"] != "Veteran Soldiers Test Copy")
            throw new InvalidDataException("Sandbox manifest identity is invalid.");
        string[] required = { "Diplomacy is Not an Option_Data\\globalgamemanagers", "Diplomacy is Not an Option_Data\\app.info",
            "Diplomacy is Not an Option_Data\\Managed\\Door407.Integrations.Runtime.dll", "Diplomacy is Not an Option_Data\\boot.config" };
        if (!(manifest["Files"] is JArray files) || files.Count != required.Length)
            throw new InvalidDataException("Sandbox manifest must identify all four isolated files.");
        foreach (string relative in required)
        {
            var matches = files.Where(file => string.Equals((string?)file["RelativePath"], relative, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Incomplete sandbox isolation manifest.");
            string path = Path.Combine(root, relative);
            RejectReparse(path);
            using var algorithm = SHA256.Create();
            using var stream = File.OpenRead(path);
            string digest = BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
            if (!string.Equals(digest, (string?)matches[0]["ModifiedHash"], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("An isolated game file no longer matches its manifest.");
        }
    }

    private static void ValidateProfilePath()
    {
        string root = Path.GetFullPath(Application.persistentDataPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string profile = Path.GetFullPath(Profiles.CurrentProfilePath);
        if (!profile.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The current profile is outside isolated persistentDataPath.");
        RejectReparse(profile);
    }

    private static void RejectReparse(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Visual test refuses filesystem reparse points.");
            current = Path.GetDirectoryName(current);
        }
    }

    private static bool SpawnFixture()
    {
        using var prefabsQuery = Manager.CreateEntityQuery(ComponentType.ReadOnly<PrefabsMapSingleton>());
        using var settingsQuery = Manager.CreateEntityQuery(ComponentType.ReadOnly<CurrentSettingsSingleton>());
        var prefabs = EntityQueryManagedComponentExtensions.GetSingleton<PrefabsMapSingleton>(prefabsQuery).prefabsMap;
        var settings = EntityQueryManagedComponentExtensions.GetSingleton<CurrentSettingsSingleton>(settingsQuery);
        Entity archer = Entity.Null, swordsman = Entity.Null;
        using var query = Manager.CreateEntityQuery(new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly<Unit>(), ComponentType.ReadOnly<UnitBase>(), ComponentType.ReadOnly<ObjectId>(), ComponentType.ReadOnly<Translation>() },
            None = new[] { ComponentType.ReadOnly<Enemy>(), ComponentType.ReadOnly<Prefab>(), ComponentType.ReadOnly<Dead>() }
        });
        using var candidates = query.ToEntityArray(Allocator.Temp);
        foreach (Entity candidate in candidates)
        {
            if (!VeteranRuntime.IsEligible(candidate)) continue;
            UnitType type = Manager.GetComponentData<UnitBase>(candidate).value.Value.type;
            int id = Manager.GetComponentData<ObjectId>(candidate).value;
            if (type == UnitType.Archer && archer == Entity.Null && prefabs.TryGetValue(id, out archer))
                origin = Manager.GetComponentData<Translation>(candidate).Value;
            if (type == UnitType.Swordsman && swordsman == Entity.Null) prefabs.TryGetValue(id, out swordsman);
        }
        // Endless initialization can expose GameRunningSingleton before its starting units.
        // Wait for an actual player unit; a normal preset need not start with both types.
        if (candidates.Length == 0) return false;
        if (Arguments.Contains("--veteran-hall-test") && (archer == Entity.Null || swordsman == Entity.Null))
        {
            origin = Manager.GetComponentData<Translation>(candidates[0]).Value;
            using var keys = prefabs.GetKeyArray(Allocator.Temp);
            keys.Sort();
            foreach (int key in keys)
            {
                Entity prefab = prefabs[key];
                if (!Manager.Exists(prefab) || Manager.HasComponent<Enemy>(prefab) || !Manager.HasComponent<UnitBase>(prefab)) continue;
                UnitType type = Manager.GetComponentData<UnitBase>(prefab).value.Value.type;
                if (type == UnitType.Archer && archer == Entity.Null) archer = prefab;
                if (type == UnitType.Swordsman && swordsman == Entity.Null) swordsman = prefab;
            }
        }
        if (archer == Entity.Null || swordsman == Entity.Null) return false;
        var spawner = UnitSpawnUtility.MainThread.Init(prefabs, settings);
        using var commands = new EntityCommandBuffer(Allocator.TempJob);
        for (int i = 0; i < Units.Length; i++)
        {
            var position = origin + new float3((i % 5 - 2) * 3, 0, i < 5 ? -6 : -10);
            Entity entity = spawner.Spawn(i < 5 ? archer : swordsman, position, quaternion.identity, true, false, false, commands, Manager);
            commands.AddComponent(entity, new VisualTestUnit { Id = i });
        }
        commands.Playback(Manager);
        origin += new float3(0, 0, -8);
        return true;
    }

    private static bool ResolveFixture()
    {
        using var query = Manager.CreateEntityQuery(ComponentType.ReadOnly<VisualTestUnit>());
        using var entities = query.ToEntityArray(Allocator.Temp);
        using var markers = query.ToComponentDataArray<VisualTestUnit>(Allocator.Temp);
        if (entities.Length != Units.Length) return false;
        var found = new bool[Units.Length];
        for (int i = 0; i < entities.Length; i++)
        {
            int id = markers[i].Id;
            if (id < 0 || id >= Units.Length || found[id]) throw new InvalidDataException("Duplicate or invalid visual fixture identity.");
            if (!VeteranRuntime.IsEligible(entities[i])) return false;
            found[id] = true;
            Units[id] = entities[i];
        }
        return true;
    }

    private static void ReadCommand()
    {
        string path = Path.Combine(directory, "command.json");
        RejectReparse(path);
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > 8192) throw new InvalidDataException("Visual command exceeds 8 KiB.");
        JObject command;
        try { command = JObject.Parse(File.ReadAllText(path)); }
        catch (IOException) { return; } // A writer may be replacing the file; retry next tick.
        catch (JsonException) { return; }
        if ((string?)command["Session"] != session) return; // Never replay commands from a previous process.
        int id = (int?)command["Id"] ?? 0;
        if (id <= lastCommandId) return;
        lastCommandId = id;
        lastAction = ((string?)command["Action"] ?? string.Empty).ToLowerInvariant();
        commandError = string.Empty;
        try
        {
            if (lastAction.StartsWith("hall_", StringComparison.Ordinal))
            {
                RuntimeHallTest.Execute(lastAction, command, Units, origin);
                if (lastAction == "hall_load")
                {
                    // The old synthetic fixture is not part of the native save. Never reuse its
                    // entity handles across a load, even if the ECS World itself is retained.
                    Array.Clear(Units, 0, Units.Length);
                    expectedSelection = Array.Empty<Entity>();
                }
            }
            else switch (lastAction)
            {
                case "individual":
                    int index = (int?)command["Index"] ?? -1;
                    if (index < 0 || index >= 5) throw new ArgumentOutOfRangeException("Index", "Individual uses Archer index 0..4.");
                    Select(new[] { Units[index] });
                    break;
                case "group": Select(Units.Take(5)); break;
                case "mixed": Select(Units); break;
                case "none": ClearSelection(); break;
                case "army": VeteranArmyWindow.Toggle(); break;
                case "promotion":
                    int promotionRank = (int?)command["Rank"] ?? 3;
                    int promotionCount = (int?)command["Count"] ?? 1;
                    if (promotionRank < 1 || promotionRank > 4 || promotionCount < 1 || promotionCount > 20)
                        throw new ArgumentException("Promotion preview uses Rank 1..4 and Count 1..20.");
                    for (int i = 0; i < promotionCount; i++) VeteranPromotionFeed.Promoted(Units[0], 0, promotionRank);
                    VeteranRuntime.Log.LogInfo("VISUALTEST: synthetic promotion-card preview; fixture XP was not changed.");
                    break;
                case "expire_promotion_target":
                    ValidateIsolation();
                    if (!VeteranRuntime.IsEligible(Units[0])) throw new InvalidOperationException("The exact fixture promotion target is unavailable.");
                    Manager.DestroyEntity(Units[0]);
                    VeteranRuntime.Log.LogInfo("VISUALTEST: synthetic fixture target removal; not a confirmed combat death.");
                    break;
                case "focus": FocusCamera(); break;
                case "resolution":
                    int width = (int?)command["Width"] ?? 0, height = (int?)command["Height"] ?? 0;
                    if (!((width == 1280 && height == 720) || (width == 1920 && height == 1080)))
                        throw new ArgumentException("Allowed visual resolutions: 1280x720 or 1920x1080.");
                    Screen.SetResolution(width, height, false);
                    break;
                case "capture":
                    // Fixed identity-derived path only. The Unity API writes asynchronously at frame end.
                    ValidateIsolation();
                    string captures = Path.Combine(directory, "captures");
                    RejectReparse(captures);
                    Directory.CreateDirectory(captures);
                    capturePath = Path.Combine(captures, session + "-" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png");
                    RejectReparse(capturePath);
                    if (File.Exists(capturePath)) throw new IOException("Capture output already exists; use a new command ID.");
                    ScreenCapture.CaptureScreenshot(capturePath);
                    break;
                case "status":
                case "dump": break;
                case "release":
                    ClearSelection();
                    phase = Phase.Released;
                    stopped = true;
                    Application.runInBackground = oldBackground;
                    break;
                default: throw new ArgumentException("Unknown visual action; expected individual, group, mixed, none, promotion, focus, resolution, capture, status, dump or release.");
            }
            commandStatus = stopped ? "applied" : "accepted";
            pendingCommandId = stopped ? 0 : id;
            nextReport = Time.unscaledTime + .6f;
        }
        catch (Exception error) { commandStatus = "rejected"; commandError = error.Message; pendingCommandId = 0; }
        WriteState();
    }

    private static void Select(IEnumerable<Entity> selection)
    {
        Entity[] chosen = selection.ToArray();
        if (chosen.Any(entity => !VeteranRuntime.IsEligible(entity)))
            throw new InvalidOperationException("A fixture soldier died or disappeared; this selection was not applied.");
        ClearSelection();
        foreach (Entity entity in chosen) Manager.AddComponent<Selected>(entity);
        expectedSelection = chosen;
        GameStateUpdateTasksUtility.UpdateGameState(Manager, GameStateUpdateTaskType.Selection);
    }

    private static void ClearSelection()
    {
        using var query = Manager.CreateEntityQuery(ComponentType.ReadOnly<Selected>());
        Manager.RemoveComponent<Selected>(query);
        expectedSelection = Array.Empty<Entity>();
        GameStateUpdateTasksUtility.UpdateGameState(Manager, GameStateUpdateTaskType.Selection);
    }

    private static void FocusCamera()
    {
        using var moveQuery = Manager.CreateEntityQuery(ComponentType.ReadOnly<CameraInstantMoveToPositionCommandData>());
        using var zoomQuery = Manager.CreateEntityQuery(ComponentType.ReadOnly<CameraSetZoomCommandData>());
        if (moveQuery.CalculateEntityCount() != 0 || zoomQuery.CalculateEntityCount() != 0)
            throw new InvalidOperationException("A native camera command is already pending; retry focus.");
        Entity move = Manager.CreateEntity(typeof(CameraInstantMoveToPositionCommandData));
        Manager.SetComponentData(move, new CameraInstantMoveToPositionCommandData { position = origin });
        Entity zoom = Manager.CreateEntity(typeof(CameraSetZoomCommandData));
        Manager.SetComponentData(zoom, new CameraSetZoomCommandData { zoom = 35 });
    }

    private static void ContinueLoadingScreen()
    {
        foreach (LoadingProgressBar bar in Resources.FindObjectsOfTypeAll<LoadingProgressBar>())
        {
            var field = AccessTools.Field(typeof(LoadingProgressBar), "_startAction");
            if (!(field.GetValue(bar) is UnityAction action)) continue;
            field.SetValue(bar, null);
            action.Invoke();
        }
    }

    private static void ContinueMissionPanel()
    {
        if (Time.unscaledTime < nextPanelAction) return;
        foreach (MissionPanel panel in Resources.FindObjectsOfTypeAll<MissionPanel>())
        {
            if (!panel.IsOpened) continue;
            AccessTools.Method(typeof(MissionPanel), "Toggle").Invoke(panel, null);
            nextPanelAction = Time.unscaledTime + 2;
            break;
        }
    }

    private static void WriteState()
    {
        if (string.IsNullOrEmpty(directory)) return;
        var observations = new List<object>();
        object? camera = null;
        int aliveCount = 0, observedSelectedCount = 0;
        bool ranksMatch = true, experienceMatches = true, selectionMatches = false;
        bool gameStateMatches = false, nativePanelMatches = false;
        var rankCounts = new int[5];
        var selectedRanks = new int[5];
        var selectedTags = new List<object>();
        var gameStateSelection = new List<object>();
        var nativePanelSelection = new List<object>();
        if (world != null && world.IsCreated && VeteranRuntime.Ready)
        {
            Manager.CompleteAllJobs();
            for (int i = 0; i < Units.Length; i++)
            {
                Entity entity = Units[i];
                bool alive = VeteranRuntime.TryGetSnapshot(entity, out var snapshot);
                bool selected = Manager.Exists(entity) && Manager.HasComponent<Selected>(entity);
                int actualRank = alive ? Progression.Level(snapshot.Experience, VeteranRuntime.Settings.Ranks, 5) : -1;
                if (alive)
                {
                    aliveCount++;
                    rankCounts[actualRank]++;
                    if (selected) selectedRanks[actualRank]++;
                }
                ranksMatch &= actualRank == i % 5;
                experienceMatches &= alive && Math.Abs(snapshot.Experience - AssignedExperience[i]) < .001;
                float3 position = Manager.Exists(entity) && Manager.HasComponent<Translation>(entity)
                    ? Manager.GetComponentData<Translation>(entity).Value : default;
                observations.Add(new { Index = i, EntityIndex = entity.Index, EntityVersion = entity.Version,
                    Type = Manager.Exists(entity) && Manager.HasComponent<UnitBase>(entity) ? Manager.GetComponentData<UnitBase>(entity).value.Value.type.ToString() : "missing",
                    IntendedType = i < 5 ? "Archer" : "Swordsman", IntendedRank = i % 5 + 1, AssignedXP = AssignedExperience[i],
                    ActualRank = actualRank + 1, Alive = alive, Selected = selected,
                    XP = snapshot.Experience, CurrentHealth = snapshot.CurrentHealth,
                    MaximumHealth = snapshot.VanillaMaxHealth * snapshot.AppliedHealthFactor,
                    Position = new[] { position.x, position.y, position.z } });
            }
            using (var selectionQuery = Manager.CreateEntityQuery(ComponentType.ReadOnly<Selected>()))
            using (var selectedEntities = selectionQuery.ToEntityArray(Allocator.Temp))
            {
                observedSelectedCount = selectedEntities.Length;
                selectionMatches = selectedEntities.Length == expectedSelection.Length;
                foreach (Entity entity in selectedEntities)
                {
                    selectionMatches &= expectedSelection.Contains(entity);
                    selectedTags.Add(DescribeEntity(entity));
                }
            }
            if (GameState.SelectedEntities.IsCreated)
            {
                gameStateMatches = GameState.SelectedEntities.Length == expectedSelection.Length;
                foreach (Entity entity in GameState.SelectedEntities)
                {
                    gameStateMatches &= expectedSelection.Contains(entity);
                    gameStateSelection.Add(DescribeEntity(entity));
                }
            }
            var native = VeteranNativePanel.SelectedVeterans;
            nativePanelMatches = native.Count == expectedSelection.Length;
            foreach (var entry in native)
            {
                nativePanelMatches &= expectedSelection.Contains(entry.Entity);
                nativePanelSelection.Add(new { EntityIndex = entry.Entity.Index, EntityVersion = entry.Entity.Version,
                    Rank = entry.Level + 1, XP = entry.Experience });
            }
            using var rootQuery = Manager.CreateEntityQuery(ComponentType.ReadOnly<MainCameraRoot>(), ComponentType.ReadOnly<Translation>());
            if (rootQuery.CalculateEntityCount() == 1)
            {
                float3 position = rootQuery.GetSingleton<Translation>().Value;
                var cameraEntity = rootQuery.GetSingleton<MainCameraRoot>().camera;
                float3 offset = Manager.Exists(cameraEntity) && Manager.HasComponent<Translation>(cameraEntity)
                    ? Manager.GetComponentData<Translation>(cameraEntity).Value : default;
                camera = new { RootPosition = new[] { position.x, position.y, position.z }, LocalPosition = new[] { offset.x, offset.y, offset.z } };
            }
        }
        // Optional diagnostics never determine screenshot success and cannot fail the fixture.
        object? ui;
        try { ui = VeteranNativePanel.DebugDump(); }
        catch (Exception error) { ui = new { Error = error.Message }; }
        string report = JsonConvert.SerializeObject(new { Session = session, Phase = phase.ToString(), Utc = DateTime.UtcNow.ToString("O"),
            Scope = "Synthetic visual fixture: assigned XP. Explicit hall_* commands exercise native construction/save/load; inspect Hall evidence separately. Not performance validation.",
            Company = Application.companyName, Product = Application.productName, Width = Screen.width, Height = Screen.height,
            LastCommand = new { Id = lastCommandId, Action = lastAction, Status = commandStatus, Error = commandError }, Failure = failure,
            Camera = camera, Units = observations, UI = ui, Hall = RuntimeHallTest.Snapshot, PersistenceStatus = VeteranPersistence.Status,
            Capture = new { Path = capturePath, FileExists = capturePath.Length > 0 && File.Exists(capturePath), VisuallyVerified = false },
            PromotionCardVisible = VeteranPromotionFeed.HasVisibleCard,
            Checks = new { ExpectedFixtureCount = 10, AliveFixtureCount = aliveCount,
                FiveRanksPerType = aliveCount == 10 && ranksMatch, AssignedExperienceUnchanged = aliveCount == 10 && experienceMatches,
                RankCounts = rankCounts, SelectedRankCounts = selectedRanks,
                ExpectedSelectionCount = expectedSelection.Length, ObservedSelectedCount = observedSelectedCount,
                SelectionMatchesRequestedEntities = selectionMatches, NativePanelSelectionCount = VeteranNativePanel.SelectedVeterans.Count,
                GameStateMatchesRequestedEntities = gameStateMatches, NativePanelMatchesRequestedEntities = nativePanelMatches,
                SyntheticXP = true, GameplayValidated = false, ScreenshotValidated = false },
            Selection = new { Expected = expectedSelection.Select(entity => new { EntityIndex = entity.Index, EntityVersion = entity.Version }).ToArray(),
                SelectedTags = selectedTags, GameState = gameStateSelection, NativePanel = nativePanelSelection },
            Example = new { Session = session, Id = lastCommandId + 1, Action = "individual", Index = 4 }
        }, Formatting.Indented);
        // Coalesce to the newest observation without restarting an outstanding retry budget.
        pendingReport = report;
        if (reportWriteAttempts == 0) PublishPendingState();
    }

    private static void PublishPendingState()
    {
        if (pendingReport == null) return;
        string path = Path.Combine(directory, "state.json"), temporary = Path.Combine(directory, "state.pending.json");
        // A path-safety failure remains fatal; only report I/O is allowed to degrade.
        RejectReparse(path);
        RejectReparse(temporary);
        try
        {
            File.WriteAllText(temporary, pendingReport);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            pendingReport = null;
            reportWriteAttempts = 0;
        }
        catch (IOException error)
        {
            // Windows readers without FileShare.Delete can temporarily reject File.Replace.
            // Keep the committed report intact; retry at most five times on later ticks.
            // No sleeps, deletion fallback or fixture shutdown for diagnostic-file contention.
            reportWriteAttempts++;
            reportRetryAt = Time.unscaledTime + .2f;
            if (reportWriteAttempts < 5) return;
            pendingReport = null;
            reportWriteAttempts = 0;
            if (Time.unscaledTime >= reportWarningAt)
            {
                reportWarningAt = Time.unscaledTime + 30;
                VeteranRuntime.Log.LogWarning("VISUALTEST report update skipped after five I/O attempts; fixture continues. " + error.Message);
            }
        }
    }

    private static object DescribeEntity(Entity entity)
    {
        bool alive = VeteranRuntime.TryGetSnapshot(entity, out var snapshot);
        return new { EntityIndex = entity.Index, EntityVersion = entity.Version, FixtureIndex = Array.IndexOf(Units, entity),
            Type = Manager.Exists(entity) && Manager.HasComponent<UnitBase>(entity) ? Manager.GetComponentData<UnitBase>(entity).value.Value.type.ToString() : "missing",
            Alive = alive, XP = snapshot.Experience };
    }

    private static void Fail(Exception error)
    {
        stopped = true;
        phase = Phase.Failed;
        failure = error.Message;
        if (initialized) Application.runInBackground = oldBackground;
        VeteranRuntime.Log.LogWarning("VISUALTEST stopped; no visual pass is inferred: " + error);
        try { WriteState(); }
        catch (Exception reportError) { VeteranRuntime.Log.LogWarning("VISUALTEST report unavailable: " + reportError.Message); }
    }
}

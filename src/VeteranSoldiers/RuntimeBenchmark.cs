using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using Components;
using Components.Commands;
using Components.Commands.UnitCommands;
using Components.RawComponents;
using Components.RawComponents.FightComponents;
using Components.SingletonComponents;
using HarmonyLib;
using Newtonsoft.Json;
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

public struct BenchmarkUnit : IComponentData { public int Id; }

/// <summary>Opt-in prepared stress experiment. Never awards XP or injects damage; scales initial arena health only.</summary>
internal static class RuntimeBenchmark
{
    private const float ArenaHealthMultiplier = 100;
    private const float ArenaTownHallHealthMultiplier = 10000;
    private enum Phase { Menu, Loading, Spawning, Warmup, Measuring, Finished }
    private sealed class Observation
    {
        public string Phase = string.Empty;
        public double WallSeconds, GameSeconds, ObservedDamage, ObserverMilliseconds;
        public int Alive, Fighting, DamagedUnits, Missing, Collections0, Collections1, Collections2;
        public int VsyncCount, TargetFrameRate, ScreenWidth, ScreenHeight;
        public bool Focused;
        public long ManagedBytes;
    }

    private static readonly string[] Arguments = Environment.GetCommandLineArgs();
    private static readonly bool Requested = Arguments.Contains("--veteran-soldiers-benchmark");
    private static readonly List<Observation> Observations = new List<Observation>(180);
    private static readonly double[] FrameMilliseconds = Requested ? new double[120000] : Array.Empty<double>();
    private static readonly Func<long>? AllocatedBytes = FindAllocatedBytesCounter();
    private static World? world;
    private static Phase phase;
    private static bool initialized, finished, enabled, oldBackground, renderingChanged;
    private static int lastFrame = -1, count, seed, sampleCount, oldVsync, oldFrameCap;
    private static int playerMeleeId, playerArcherId, enemyId, effectiveSeed, firstAlive, peakFighting, focusedFrames;
    private static double warmupSeconds, measurementSeconds, deadline, phaseStart, measureStart, previousFrame;
    private static double nextObservation, observedDamage, measuredDamageStart, gameStart, gameEnd, observerMilliseconds;
    private static long allocationStart = -1, allocationEnd = -1, memoryStart, memoryEnd;
    private static int[] collectionsStart = new int[3], collectionsEnd = new int[3];
    private static Entity[] units = Array.Empty<Entity>();
    private static float[] previousHealth = Array.Empty<float>();
    private static bool[] damaged = Array.Empty<bool>();
    private static float3 origin;
    private static string reportPath = string.Empty, missionName = string.Empty, layoutHash = string.Empty, configHash = string.Empty;
    private static string failure = string.Empty;
    private static double nextPanelCheck;
    private static MissionPanel[] panels = Array.Empty<MissionPanel>();
    private static int panelActionsDuringMeasurement;
    private static int protectedTownHalls;
    private static int otherGameProcesses = -1;
    private static int measuredVsyncStart, measuredVsyncEnd, measuredFrameCapStart, measuredFrameCapEnd;
    private static string gameHash = string.Empty, runtimeHash = string.Empty;
    private static EntityQuery clock;
    private static bool clockCreated;
    private static EntityManager Manager => world!.EntityManager;
    internal static bool IsMeasuring => initialized && !finished && (phase == Phase.Warmup || phase == Phase.Measuring);

    // Called after ModSettings is loaded, including General.Enabled=false. Owns its world,
    // so the uninstrumented baseline does not need VeteranRuntime.SetWorld or Harmony hooks.
    internal static void Initialize(World candidate)
    {
        if (!Requested || initialized || finished) return;
        try
        {
            if (Application.companyName != "VS Tests" ||
                !Application.productName.StartsWith("Veteran Soldiers Test Copy", StringComparison.Ordinal) ||
                !File.Exists(Path.Combine(Paths.GameRootPath, "veteran-soldiers.sandbox.json")))
                throw new InvalidOperationException("Benchmark requires the verified VS Tests / Veteran Soldiers Test Copy product and sandbox manifest.");
            if (Arguments.Contains("--veteran-soldiers-self-test") || Arguments.Contains("--veteran-soldiers-resume-test"))
                throw new InvalidOperationException("Benchmark and self-test cannot run in the same process.");
            if (VeteranRuntime.Settings.DebugLogging.Value)
                throw new InvalidOperationException("Set General.DebugLogging=false before benchmarking; per-hit logging invalidates timing.");
            count = ReadInteger("--veteran-soldiers-benchmark-units=", 200, 20, 2000);
            if (count % 4 != 0) throw new ArgumentException("Benchmark units must be divisible by four: half enemies, one quarter player melee, one quarter player archers.");
            seed = ReadInteger("--veteran-soldiers-benchmark-seed=", 970590331, 1, int.MaxValue);
            warmupSeconds = ReadInteger("--veteran-soldiers-benchmark-warmup=", 15, 1, 60);
            measurementSeconds = ReadInteger("--veteran-soldiers-benchmark-seconds=", 30, 5, 120);
            world = candidate;
            enabled = VeteranRuntime.Settings.Enabled.Value;
            bool instrumented = typeof(Systems.HealthTrackSystem).Assembly.GetType("VeteranSoldiers.InstrumentationMarker") != null;
            if (instrumented != enabled) throw new InvalidOperationException("Enabled flag and preloader instrumentation disagree; baseline would be invalid.");
            clock = Manager.CreateEntityQuery(ComponentType.ReadOnly<CurrentSessionTimeSingleton>());
            clockCreated = true;
            oldBackground = Application.runInBackground;
            oldVsync = QualitySettings.vSyncCount;
            oldFrameCap = Application.targetFrameRate;
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            renderingChanged = true;
            units = new Entity[count];
            previousHealth = new float[count];
            damaged = new bool[count];
            configHash = ConfigurationHash();
            gameHash = HashFile(Path.Combine(Paths.ManagedPath, "DNO.Main.dll"));
            runtimeHash = HashFile(typeof(RuntimeBenchmark).Assembly.Location);
            otherGameProcesses = CountOtherGameProcesses();
            string directory = Path.Combine(Paths.GameRootPath, "VeteranSoldiersBenchmark");
            Directory.CreateDirectory(directory);
            reportPath = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
                + "-" + (enabled ? "enabled" : "disabled") + "-" + count + ".json");
            initialized = true;
            SetPhase(Phase.Menu, 180);
            WriteReport("running");
            VeteranRuntime.Log.LogInfo("BENCHMARK initialized: " + count + " units; enabled=" + enabled + "; report=" + reportPath);
        }
        catch (Exception exception) { Fail(exception); }
    }

    internal static void Tick()
    {
        if (!Requested || !initialized || finished || lastFrame == Time.frameCount) return;
        lastFrame = Time.frameCount;
        double now = Now();
        try
        {
            if (world == null || !world.IsCreated) throw new InvalidOperationException("Benchmark world was destroyed.");
            if (now > deadline) throw new TimeoutException("Benchmark timed out in " + phase + "; no valid result is inferred.");
            if (phase == Phase.Menu)
            {
                var menu = MainMenu.Instance;
                if (menu == null || menu.inGame || menu.defaultSettings == null) return;
                // All changes are confined to the isolated process's mission setup.
                menu.defaultSettings.сommonConfiguration.tutorialMission.difficulties[0].mapGenerator.data.seedOverride = seed;
                UnityEngine.Random.InitState(seed);
                menu.NewTutorialGame();
                SetPhase(Phase.Loading, 240);
                return;
            }
            if (phase == Phase.Loading)
            {
                ContinueLoadingScreen();
                ContinueMissionPanel(now);
                using var running = Manager.CreateEntityQuery(ComponentType.ReadOnly<GameRunningSingleton>());
                if (running.CalculateEntityCount() != 1 || clock.CalculateEntityCount() != 1) return;
                Manager.CompleteAllJobs();
                if (!PrepareArmy()) return;
                SetPhase(Phase.Spawning, 60);
                return;
            }
            if (phase == Phase.Spawning)
            {
                ContinueMissionPanel(now);
                if (now - phaseStart < .5) return;
                // The game can reapply its graphics preferences during mission loading.
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
                ResolveArmy();
                IssueAttackCommand();
                Observe(now);
                SetPhase(Phase.Warmup, warmupSeconds + 120);
                return;
            }
            if (phase == Phase.Warmup)
            {
                ContinueMissionPanel(now);
                if (now >= nextObservation) Observe(now);
                if (now - phaseStart < warmupSeconds) return;
                Observe(now);
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
                measuredVsyncStart = QualitySettings.vSyncCount;
                measuredFrameCapStart = Application.targetFrameRate;
                gameStart = GameTime();
                firstAlive = AliveCount();
                measuredDamageStart = observedDamage;
                memoryStart = GC.GetTotalMemory(false);
                allocationStart = AllocatedBytes?.Invoke() ?? -1;
                for (int generation = 0; generation < 3; generation++) collectionsStart[generation] = GC.CollectionCount(generation);
                sampleCount = 0;
                focusedFrames = 0;
                peakFighting = 0;
                observerMilliseconds = 0;
                measureStart = previousFrame = Now();
                SetPhase(Phase.Measuring, measurementSeconds + 120);
                return;
            }
            if (phase == Phase.Measuring)
            {
                ContinueMissionPanel(now);
                if (sampleCount == FrameMilliseconds.Length) throw new InvalidOperationException("Frame sample capacity exhausted.");
                FrameMilliseconds[sampleCount++] = (now - previousFrame) * 1000;
                previousFrame = now;
                if (Application.isFocused) focusedFrames++;
                if (now >= nextObservation) Observe(now);
                if (now - measureStart < measurementSeconds) return;
                Observe(now);
                gameEnd = GameTime();
                measuredVsyncEnd = QualitySettings.vSyncCount;
                measuredFrameCapEnd = Application.targetFrameRate;
                allocationEnd = AllocatedBytes?.Invoke() ?? -1;
                memoryEnd = GC.GetTotalMemory(false);
                for (int generation = 0; generation < 3; generation++) collectionsEnd[generation] = GC.CollectionCount(generation);
                Finish();
            }
        }
        catch (Exception exception) { Fail(exception); }
    }

    private static bool PrepareArmy()
    {
        using var prefabsQuery = Manager.CreateEntityQuery(ComponentType.ReadOnly<PrefabsMapSingleton>());
        using var settingsQuery = Manager.CreateEntityQuery(ComponentType.ReadOnly<CurrentSettingsSingleton>());
        if (prefabsQuery.CalculateEntityCount() != 1 || settingsQuery.CalculateEntityCount() != 1) return false;
        var prefabs = EntityQueryManagedComponentExtensions.GetSingleton<PrefabsMapSingleton>(prefabsQuery).prefabsMap;
        var settings = EntityQueryManagedComponentExtensions.GetSingleton<CurrentSettingsSingleton>(settingsQuery);
        missionName = settings.missionConfiguration.name;
        effectiveSeed = settings.missionDifficultyConfiguration.mapGenerator.data.seed;
        using var armyQuery = Manager.CreateEntityQuery(new EntityQueryDesc {
            All = new[] { ComponentType.ReadOnly<Unit>(), ComponentType.ReadOnly<UnitBase>(), ComponentType.ReadOnly<ObjectId>(), ComponentType.ReadOnly<Health>(), ComponentType.ReadOnly<Translation>() },
            None = new[] { ComponentType.ReadOnly<Prefab>(), ComponentType.ReadOnly<Dead>(), ComponentType.ReadOnly<BenchmarkUnit>() }
        });
        using var originals = armyQuery.ToEntityArray(Allocator.Temp);
        Entity melee = Entity.Null, ranged = Entity.Null, enemy = Entity.Null;
        foreach (Entity entity in originals)
        {
            if (Manager.GetComponentData<Health>(entity).currentHealth <= 0) continue;
            var type = Manager.GetComponentData<UnitBase>(entity).value.Value.type;
            if (!Manager.HasComponent<Enemy>(entity))
            {
                if (Manager.HasComponent<MeleeUnit>(entity) && Earlier(entity, melee)) melee = entity;
                if (type == UnitType.Archer && Earlier(entity, ranged)) ranged = entity;
            }
            else if (type == UnitType.ForkPeasant && Earlier(entity, enemy)) enemy = entity;
        }
        if (melee == Entity.Null || ranged == Entity.Null) return false;
        playerMeleeId = Manager.GetComponentData<ObjectId>(melee).value;
        playerArcherId = Manager.GetComponentData<ObjectId>(ranged).value;
        enemyId = enemy == Entity.Null ? int.MaxValue : Manager.GetComponentData<ObjectId>(enemy).value;
        if (enemy == Entity.Null)
        {
            // The tutorial need not have spawned its first enemy wave. Choose a stable
            // canonical id, never the last item of the hash map's unspecified order.
            using var candidates = prefabs.GetValueArray(Allocator.Temp);
            foreach (Entity candidate in candidates)
                if (Manager.HasComponent<Enemy>(candidate) && Manager.HasComponent<UnitBase>(candidate) && Manager.HasComponent<ObjectId>(candidate)
                    && Manager.GetComponentData<UnitBase>(candidate).value.Value.type == UnitType.ForkPeasant)
                    enemyId = Math.Min(enemyId, Manager.GetComponentData<ObjectId>(candidate).value);
        }
        if (enemyId == int.MaxValue) throw new InvalidOperationException("Enemy ForkPeasant prefab is unavailable.");
        if (!prefabs.TryGetValue(playerMeleeId, out Entity meleePrefab) || !prefabs.TryGetValue(playerArcherId, out Entity rangedPrefab)
            || !prefabs.TryGetValue(enemyId, out Entity hostilePrefab))
            throw new InvalidOperationException("Original tutorial unit ObjectIds do not resolve to canonical prefabs.");
        ProtectTownHalls();
        origin = Manager.GetComponentData<Translation>(melee).Value;
        origin.x = math.round(origin.x);
        origin.z = math.round(origin.z);
        int perSide = count / 2;
        int columns = (int)Math.Ceiling(Math.Sqrt(perSide * 2));
        var layout = new StringBuilder();
        UnityEngine.Random.InitState(seed);
        var spawner = UnitSpawnUtility.MainThread.Init(prefabs, settings);
        using var commands = new EntityCommandBuffer(Allocator.TempJob);
        for (int i = 0; i < count; i++)
        {
            bool hostile = i >= perSide;
            int local = i % perSide;
            float x = (local % columns - (columns - 1) * .5f) * 1.6f;
            float z = 4 + (local / columns) * 1.6f;
            float3 position = origin + new float3(x, 0, hostile ? z : -z);
            Entity prefab = hostile ? hostilePrefab : (local < count / 4 ? meleePrefab : rangedPrefab);
            Entity unit = spawner.Spawn(prefab, position, quaternion.identity, true, false, false, commands, Manager);
            commands.AddComponent(unit, new BenchmarkUnit { Id = i });
            layout.Append(i).Append(':').Append(hostile ? enemyId : local < count / 4 ? playerMeleeId : playerArcherId)
                .Append(':').Append(position.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(position.y.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(position.z.ToString("R", CultureInfo.InvariantCulture)).Append(';');
        }
        commands.Playback(Manager);
        layoutHash = Hash(layout.ToString());
        VeteranRuntime.Log.LogInfo("BENCHMARK arena spawned from original tutorial ObjectIds: melee=" + playerMeleeId + ", archer=" + playerArcherId + ", enemy=" + enemyId);
        return true;
    }

    private static bool Earlier(Entity candidate, Entity prior) => prior == Entity.Null || candidate.Index < prior.Index;

    private static void ProtectTownHalls()
    {
        using var query = Manager.CreateEntityQuery(new EntityQueryDesc {
            All = new[] { ComponentType.ReadOnly<HealthBase>(), ComponentType.ReadOnly<Health>() },
            Any = new[] { ComponentType.ReadOnly<TownHall>(), ComponentType.ReadOnly<TownHallAlternative>() },
            None = new[] { ComponentType.ReadOnly<Prefab>(), ComponentType.ReadOnly<Dead>() }
        });
        using var entities = query.ToEntityArray(Allocator.Temp);
        foreach (Entity entity in entities)
        {
            var healthBase = Manager.GetComponentData<HealthBase>(entity);
            healthBase.SetMaxHealthMultiplier((Systems.GameWorldLoaderSystem)null!, healthBase.GetMaxHealthMultiplier() * ArenaTownHallHealthMultiplier);
            Manager.SetComponentData(entity, healthBase);
            var health = Manager.GetComponentData<Health>(entity);
            health.currentHealth *= ArenaTownHallHealthMultiplier;
            Manager.SetComponentData(entity, health);
            protectedTownHalls++;
        }
        if (protectedTownHalls == 0) throw new InvalidOperationException("No live TownHall was found to protect the stress scenario from an early defeat.");
    }

    private static void ResolveArmy()
    {
        Manager.CompleteAllJobs();
        using var query = Manager.CreateEntityQuery(ComponentType.ReadOnly<BenchmarkUnit>());
        using var entities = query.ToEntityArray(Allocator.Temp);
        using var ids = query.ToComponentDataArray<BenchmarkUnit>(Allocator.Temp);
        if (entities.Length != count) throw new InvalidOperationException("Benchmark spawn count differs from requested count.");
        for (int i = 0; i < entities.Length; i++)
        {
            int id = ids[i].Id;
            if (id < 0 || id >= count || units[id] != Entity.Null) throw new InvalidOperationException("Benchmark identity is not unique.");
            units[id] = entities[i];
            // Prepared stress fixture only: keep both sides alive long enough to measure
            // their actual attacks. Never alter prefab blobs, attack stats, or HP afterward.
            var healthBase = Manager.GetComponentData<HealthBase>(entities[i]);
            healthBase.SetMaxHealthMultiplier((Systems.GameWorldLoaderSystem)null!, healthBase.GetMaxHealthMultiplier() * ArenaHealthMultiplier);
            Manager.SetComponentData(entities[i], healthBase);
            var health = Manager.GetComponentData<Health>(entities[i]);
            health.currentHealth *= ArenaHealthMultiplier;
            Manager.SetComponentData(entities[i], health);
            previousHealth[id] = health.currentHealth;
        }
    }

    private static void IssueAttackCommand()
    {
        using var selected = Manager.CreateEntityQuery(ComponentType.ReadOnly<Selected>());
        Manager.RemoveComponent<Selected>(selected);
        for (int i = 0; i < count / 2; i++) Manager.AddComponent<Selected>(units[i]);
        var command = Manager.CreateEntity(typeof(UnitAttackCommand), typeof(CommandTargetData), typeof(CommandParameterData), typeof(CommandDataTag));
        Manager.SetComponentData(command, new CommandTargetData { value = units[count / 2] });
        Manager.SetComponentData(command, new CommandParameterData { parameter = 0 });
    }

    private static void Observe(double now)
    {
        double start = Now();
        Manager.CompleteAllJobs();
        int alive = 0, fighting = 0, missing = 0, damagedUnits = 0;
        for (int i = 0; i < units.Length; i++)
        {
            Entity entity = units[i];
            if (!Manager.Exists(entity) || !Manager.HasComponent<Health>(entity)) { missing++; continue; }
            float health = Math.Max(0, Manager.GetComponentData<Health>(entity).currentHealth);
            observedDamage += Math.Max(0, previousHealth[i] - health);
            if (health < previousHealth[i]) damaged[i] = true;
            previousHealth[i] = health;
            if (health > 0 && !Manager.HasComponent<Dead>(entity))
            {
                alive++;
                if (Manager.HasComponent<InFight>(entity)) fighting++;
            }
            if (damaged[i]) damagedUnits++;
        }
        peakFighting = Math.Max(peakFighting, fighting);
        var observation = new Observation {
            Phase = phase.ToString(),
            WallSeconds = now - (phase == Phase.Measuring ? measureStart : phaseStart), GameSeconds = GameTime(),
            Alive = alive, Fighting = fighting, Missing = missing, DamagedUnits = damagedUnits,
            ObservedDamage = observedDamage, ManagedBytes = GC.GetTotalMemory(false),
            VsyncCount = QualitySettings.vSyncCount, TargetFrameRate = Application.targetFrameRate,
            ScreenWidth = Screen.width, ScreenHeight = Screen.height, Focused = Application.isFocused,
            Collections0 = GC.CollectionCount(0), Collections1 = GC.CollectionCount(1), Collections2 = GC.CollectionCount(2)
        };
        observation.ObserverMilliseconds = (Now() - start) * 1000;
        observerMilliseconds += observation.ObserverMilliseconds;
        Observations.Add(observation);
        nextObservation = now + 1;
    }

    private static int AliveCount()
    {
        int alive = 0;
        foreach (Entity unit in units)
            if (Manager.Exists(unit) && Manager.HasComponent<Health>(unit) && Manager.GetComponentData<Health>(unit).currentHealth > 0 && !Manager.HasComponent<Dead>(unit)) alive++;
        return alive;
    }

    private static void Finish()
    {
        var reasons = new List<string>();
        if (sampleCount < 100) reasons.Add("Fewer than 100 real frame intervals.");
        if (observedDamage - measuredDamageStart <= 0) reasons.Add("No actual HP loss observed during measurement.");
        if (gameEnd - gameStart < measurementSeconds * .8) reasons.Add("Simulation advanced too little; possible pause or severe throttling.");
        if (firstAlive < count / 2) reasons.Add("Fewer than half requested units survived to measurement start.");
        if (peakFighting < Math.Min(20, count / 10)) reasons.Add("Insufficient observed fighting load.");
        if (effectiveSeed != seed) reasons.Add("Effective map seed differs from requested seed.");
        if (panelActionsDuringMeasurement != 0) reasons.Add("Mission panel paused/interrupted the measured window.");
        if (otherGameProcesses != 0 || CountOtherGameProcesses() != 0) reasons.Add("Another game process is running or could not be excluded; timing is not comparable.");
        if (enabled && !VeteranRuntime.Ready) reasons.Add("Veterancy failed or disabled itself during the run.");
        failure = string.Join(" ", reasons);
        finished = true;
        phase = Phase.Finished;
        try { WriteReport(reasons.Count == 0 ? "measured" : "invalid_measurement"); }
        finally { RestoreRendering(); }
        VeteranRuntime.Log.LogInfo("BENCHMARK " + (reasons.Count == 0 ? "measured" : "invalid measurement") + ": " + reportPath + "; " + failure);
    }

    private static void Fail(Exception exception)
    {
        failure = exception.ToString();
        finished = true;
        phase = Phase.Finished;
        try { if (!string.IsNullOrEmpty(reportPath)) WriteReport("failed"); }
        catch (Exception reportException) { VeteranRuntime.Log.LogError("BENCHMARK report write failed: " + reportException); }
        finally { RestoreRendering(); }
        VeteranRuntime.Log.LogError("BENCHMARK failed: " + failure);
    }

    private static void RestoreRendering()
    {
        if (renderingChanged)
        {
            Application.runInBackground = oldBackground;
            QualitySettings.vSyncCount = oldVsync;
            Application.targetFrameRate = oldFrameCap;
            renderingChanged = false;
        }
        if (clockCreated && world != null && world.IsCreated) { clock.Dispose(); clockCreated = false; }
    }

    private static void WriteReport(string status)
    {
        double[] samples = new double[sampleCount];
        Array.Copy(FrameMilliseconds, samples, sampleCount);
        double[] ordered = (double[])samples.Clone();
        Array.Sort(ordered);
        double total = samples.Sum();
        File.WriteAllText(reportPath, JsonConvert.SerializeObject(new {
            Schema = 1, Status = status, Failure = failure, Utc = DateTime.UtcNow.ToString("O"),
            Scope = "prepared_stress_real_combat", ArenaHealthMultiplier, ArenaTownHallHealthMultiplier, ProtectedTownHallCount = protectedTownHalls,
            Product = Application.productName, UnityVersion = Application.unityVersion,
            OriginalGameAssemblySha256 = gameHash, RuntimeAssemblySha256 = runtimeHash, OtherGameProcessesAtStart = otherGameProcesses,
            ModEnabled = enabled, RequestedUnits = count, PlayerMeleeUnits = count / 4, PlayerArcherUnits = count / 4, EnemyUnits = count / 2,
            RequestedSeed = seed, EffectiveSeed = effectiveSeed, Mission = missionName, LayoutHash = layoutHash, ConfigurationHashIgnoringEnabled = configHash,
            PrefabObjectIds = new[] { playerMeleeId, playerArcherId, enemyId }, Origin = new[] { origin.x, origin.y, origin.z },
            WarmupSeconds = warmupSeconds, MeasurementSeconds = measurementSeconds, MeasuredWallSeconds = total / 1000,
            GameTimeStart = gameStart, GameTimeEnd = gameEnd, AliveAtMeasurementStart = firstAlive, PeakFighting = peakFighting,
            ObservedHealthLossDuringMeasurement = Math.Max(0, observedDamage - measuredDamageStart),
            Frames = sampleCount, FocusedFrames = focusedFrames, MeanFrameMilliseconds = sampleCount == 0 ? 0 : total / sampleCount,
            P95FrameMilliseconds = sampleCount == 0 ? 0 : ordered[(int)Math.Ceiling(sampleCount * .95) - 1],
            AverageFramesPerSecond = total <= 0 ? 0 : sampleCount * 1000 / total,
            ScreenWidth = Screen.width, ScreenHeight = Screen.height, QualityLevel = QualitySettings.GetQualityLevel(),
            VsyncAtMeasurementStart = measuredVsyncStart, VsyncAtMeasurementEnd = measuredVsyncEnd,
            TargetFrameRateAtMeasurementStart = measuredFrameCapStart, TargetFrameRateAtMeasurementEnd = measuredFrameCapEnd,
            Device = new { SystemInfo.processorType, SystemInfo.processorCount, SystemInfo.graphicsDeviceName, SystemInfo.systemMemorySize },
            ManagedMemoryStartBytes = memoryStart, ManagedMemoryEndBytes = memoryEnd, CollectionCountsStart = collectionsStart, CollectionCountsEnd = collectionsEnd,
            MainThreadAllocatedBytes = allocationStart >= 0 && allocationEnd >= allocationStart ? (long?)(allocationEnd - allocationStart) : null,
            ObserverMilliseconds = observerMilliseconds, FrameMilliseconds = samples, Observations,
            Limitations = new[] {
                "Wall frame intervals include rendering, job waits, OS scheduling and benchmark observer overhead; not pure mod CPU cost.",
                "The harness requests vSyncCount=0 and targetFrameRate=-1 after loading; reports and observations contain actual values. A driver/OS/occlusion limit can still cap frames, so an apparently fixed refresh-rate result cannot isolate mod CPU overhead.",
                "Both sides' spawned BenchmarkUnit entities receive 100x initial current/max health to sustain combat. This is a prepared stress fixture, not a vanilla-balance battle; no healing or damage/XP injection occurs during measurement.",
                "Live TownHall/TownHallAlternative entities receive 10000x current/max health once during preparation to avoid defeat stopping the simulation. This does not heal them during the measurement.",
                "HP loss is an observed lower bound sampled once per second; entities removed between samples are not assigned guessed damage.",
                "GetTotalMemory(false) is approximate retained managed memory; optional allocation counter covers this thread only, not Unity native allocations or worker threads.",
                "Fixed seed and layout do not guarantee identical combat: Burst/managed scheduling and veterancy can change RNG, deaths and workload. Compare population and combat observations before comparing timing.",
                "Player prefabs come from live original tutorial units; when no original enemy exists, ForkPeasant uses the lowest matching registered ObjectId. The observed-damage validity checks must confirm the resulting load.",
                "A measured report is one sample, not an optimization or production claim. Repeat both configurations with matching fingerprints and no competing game process."
            }
        }, Formatting.Indented));
    }

    private static string ConfigurationHash()
    {
        string text = string.Join("\n", VeteranRuntime.Settings.File.Select(pair => new { pair.Key.Section, pair.Key.Key, Value = pair.Value.GetSerializedValue() })
            .Where(item => !(item.Section == "General" && item.Key == "Enabled"))
            .OrderBy(item => item.Section, StringComparer.Ordinal).ThenBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Section + "/" + item.Key + "=" + item.Value));
        return Hash(text);
    }

    private static string Hash(string value)
    {
        using var hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", string.Empty);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", string.Empty);
    }

    private static int CountOtherGameProcesses()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            var processes = Process.GetProcessesByName(current.ProcessName);
            int others = processes.Count(process => process.Id != current.Id);
            foreach (var process in processes) process.Dispose();
            return others;
        }
        catch (Exception) { return -1; }
    }

    private static int ReadInteger(string prefix, int fallback, int min, int max)
    {
        string? argument = Arguments.SingleOrDefault(item => item.StartsWith(prefix, StringComparison.Ordinal));
        if (argument == null) return fallback;
        if (!int.TryParse(argument.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < min || value > max)
            throw new ArgumentException("Invalid benchmark argument " + prefix + "; permitted " + min + ".." + max);
        return value;
    }

    private static Func<long>? FindAllocatedBytesCounter()
    {
        if (!Requested) return null;
        try
        {
            var method = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
            if (method == null) return null;
            var counter = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), method);
            long before = counter();
            var probe = new byte[4096];
            long after = counter();
            GC.KeepAlive(probe);
            return after > before ? counter : null;
        }
        catch (Exception) { return null; }
    }

    private static double Now() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
    private static double GameTime() => clock.GetSingleton<CurrentSessionTimeSingleton>().elapsedTime;
    private static void SetPhase(Phase next, double timeout)
    {
        phase = next;
        phaseStart = Now();
        deadline = phaseStart + timeout;
        VeteranRuntime.Log.LogInfo("BENCHMARK phase=" + phase);
    }

    private static void ContinueLoadingScreen()
    {
        foreach (var bar in Resources.FindObjectsOfTypeAll<LoadingProgressBar>())
        {
            var field = AccessTools.Field(typeof(LoadingProgressBar), "_startAction");
            if (!(field.GetValue(bar) is UnityAction action)) continue;
            field.SetValue(bar, null);
            action.Invoke();
        }
    }

    private static void ContinueMissionPanel(double now)
    {
        if (now < nextPanelCheck) return;
        nextPanelCheck = now + 1;
        if (panels.Length == 0) panels = Resources.FindObjectsOfTypeAll<MissionPanel>();
        foreach (MissionPanel panel in panels)
        {
            if (panel == null || !panel.IsOpened) continue;
            // Use the same close action as the game's UI, including its pause transition.
            AccessTools.Method(typeof(MissionPanel), "Toggle").Invoke(panel, null);
            if (phase == Phase.Measuring) panelActionsDuringMeasurement++;
            nextPanelCheck = now + 2;
            break;
        }
    }
}

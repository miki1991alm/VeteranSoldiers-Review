using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using Components;
using Components.RawComponents;
using Components.RawComponents.FightComponents;
using Components.Commands;
using Components.Commands.UnitCommands;
using Components.SingletonComponents;
using HarmonyLib;
using Newtonsoft.Json;
using Systems;
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

public struct SelfTestUnit : IComponentData { public int Id; }

/// <summary>
/// Explicitly opt-in engineering harness, restricted to the isolated test product identity.
/// Its synthetic request checks are reported separately from real projectile gameplay.
/// The initial combat observation assigns no XP directly. Later labeled synthetic integration
/// checks deliberately restore snapshots and temporary stat modifiers in the isolated world.
/// </summary>
internal static class RuntimeSelfTest
{
    private sealed class Result
    {
        public string Check = string.Empty;
        public string Kind = string.Empty;
        public bool Passed;
        public string Evidence = string.Empty;
    }

    private sealed class ResumeState
    {
        public string SaveName = string.Empty;
        public VeteranSnapshot Snapshot;
        public int ObjectId;
        public int ReferenceId;
        public string ChronicleIdentity = string.Empty;
        public string SoldierName = string.Empty;
        public bool Favorite;
        public double EffectiveDamage;
        public int ConfirmedKills;
    }

    private static readonly List<Result> Results = new List<Result>();
    private static readonly bool Requested = Environment.GetCommandLineArgs().Contains("--veteran-soldiers-self-test");
    private static readonly bool ResumeRequested = Environment.GetCommandLineArgs().Contains("--veteran-soldiers-resume-test");
    private static readonly bool PortableResumeRequested = Environment.GetCommandLineArgs().Contains("--veteran-soldiers-portable-resume-test");
    private static readonly bool VanillaResumeRequested = Environment.GetCommandLineArgs().Contains("--veteran-soldiers-vanilla-resume-test");
    private static readonly bool SelectionRequested = Environment.GetCommandLineArgs().Contains("--veteran-soldiers-selection-test");
    private static bool initialized;
    private static bool finished;
    private static int stage;
    private static float deadline;
    private static float nextTick;
    private static Entity archer, idleArcher, target, archerPrefab, enemyPrefab;
    private static float3 origin;
    private static double previousExperience;
    private static float previousHealth, targetHealth, expectedDamage;
    private static double originalMultiplier;
    private static VeteranSnapshot savedSnapshot;
    private static ResumeState savedCareer = new ResumeState();
    private static int memorialBeforeDeath;
    private static int savedObjectId;
    private static int savedReferenceId;
    private static Entity loadedTestEntity;
    private static float loadedMappedAt;
    private static string saveName = string.Empty;
    private static string protectedSidecarHash = string.Empty, disabledSaveHash = string.Empty, sessionOnlySave = string.Empty;
    private static float saveRequestedAt;
    private static bool originalPersistence;
    private static string reportPath = string.Empty;
    private static double maxExpectedExperience;
    private static int oldIndex;
    private static float nextDiagnostic;
    private static float nextPanelAction;
    private static VeteranSnapshot beforeSpecialty, beforeMentor;
    private static EntityManager Manager => VeteranRuntime.Manager;

    internal static void Tick()
    {
        if (!Requested || finished || !VeteranRuntime.Ready) return;
        if (!Application.productName.StartsWith("Veteran Soldiers Test Copy", StringComparison.Ordinal))
        {
            VeteranRuntime.Log.LogWarning("Self-test refused: the running product is not the isolated test copy.");
            finished = true;
            return;
        }
        if (Time.unscaledTime < nextTick) return;
        nextTick = Time.unscaledTime + .2f;
        try
        {
            if (!initialized)
            {
                initialized = true;
                Application.runInBackground = true;
                string reportDirectory = Path.Combine(Paths.GameRootPath, "VeteranSoldiersSelfTest");
                Directory.CreateDirectory(reportDirectory);
                reportPath = Path.Combine(reportDirectory, "results-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
                originalMultiplier = VeteranRuntime.Settings.ExperienceMultiplier.Value;
                originalPersistence = VeteranRuntime.Settings.Persistence.Value;
                if (VanillaResumeRequested && (!ResumeRequested || VeteranRuntime.Settings.Persistence.Value))
                    throw new InvalidOperationException("Vanilla resume requires --veteran-soldiers-resume-test and explicit General.Persistence=false in the isolated configuration.");
                var harnessHarmony = new Harmony(VeteranRuntime.Guid + ".SelfTest");
                harnessHarmony.Patch(AccessTools.Method(typeof(SaveLoadSystem), "SaveUnits"),
                    prefix: new HarmonyMethod(typeof(RuntimeSelfTest), nameof(CaptureSavedReference)));
                harnessHarmony.Patch(AccessTools.Method(typeof(GameWorldLoaderSystem), "LoadUnits"),
                    postfix: new HarmonyMethod(typeof(RuntimeSelfTest), nameof(CaptureLoadedUnit)));
                SetStage(0, 180);
                WriteReport("running");
                VeteranRuntime.Log.LogInfo("SELFTEST: isolated product confirmed; waiting for menu.");
            }
            ContinueLoadingScreen();
            ContinueMissionPanel();
            if (Time.unscaledTime > deadline)
                throw new TimeoutException("Self-test timed out in stage " + stage + ". No success is inferred.");
            if (stage == 0)
            {
                MainMenu menu = MainMenu.Instance;
                if (menu == null || menu.inGame || menu.defaultSettings == null) return;
                if (ResumeRequested)
                {
                    string manifest = Path.Combine(Paths.GameRootPath, "VeteranSoldiersSelfTest", "resume.json");
                    var resume = JsonConvert.DeserializeObject<ResumeState>(File.ReadAllText(manifest));
                    if (resume == null || Path.GetFileName(resume.SaveName) != resume.SaveName ||
                        !resume.SaveName.StartsWith("VeteranSoldiersSelfTest_", StringComparison.Ordinal))
                        throw new InvalidDataException("Invalid isolated resume manifest.");
                    PersistenceStore.Validate(new PersistentVeteran { ReferenceId = 1, ObjectId = resume.ObjectId, Snapshot = resume.Snapshot });
                    saveName = resume.SaveName;
                    savedSnapshot = resume.Snapshot;
                    savedObjectId = resume.ObjectId;
                    savedReferenceId = resume.ReferenceId;
                    savedCareer = resume;
                    if (savedReferenceId <= 0) throw new InvalidDataException("Resume manifest lacks its exact save reference ID.");
                    if (PortableResumeRequested)
                    {
                        string source = Path.Combine(Profiles.CurrentProfilePath, saveName);
                        var portable = PortableSave.Read(source, out string sourceHash);
                        if (portable == null) throw new InvalidDataException("Portable restart requires a newly saved portable test slot.");
                        saveName += "_Portable_" + DateTime.UtcNow.ToString("HHmmss");
                        string destination = Path.Combine(Profiles.CurrentProfilePath, saveName);
                        string sidecar = PersistenceStore.GetSidecarPath(Path.Combine(Paths.ConfigPath, "VeteranSoldiers", "saves"), destination);
                        if (File.Exists(sidecar)) throw new InvalidDataException("Portable destination unexpectedly already has a local sidecar.");
                        File.Copy(source + ".dat", destination + ".dat", false);
                        File.Copy(source, destination, false);
                        Require(PersistenceStore.HashFile(destination, true) == sourceHash && !File.Exists(sidecar),
                            "portable_copy_no_local_sidecar", "portable_process_restart", "Copied only native save and menu header to a new exact path; no XML backup is present at that destination.");
                    }
                    var loadRequest = Manager.CreateEntity(typeof(LoadRequest));
                    Manager.SetComponentData(loadRequest, new LoadRequest { NameToLoad = new FixedString128Bytes(saveName), FromMenu = true });
                    Record("restart_load_requested", "process_restart", true, "Fresh process loading isolated verified slot " + saveName);
                    SetStage(10, 240);
                    return;
                }
                menu.NewTutorialGame();
                Record("new_test_mission_requested", "harness", true, "MainMenu.NewTutorialGame; isolated fresh profile.");
                SetStage(1, 240);
                return;
            }
            if (VeteranRuntime.ActiveWorld == null || !VeteranRuntime.ActiveWorld.IsCreated) return;
            Manager.CompleteAllJobs();
            bool gameRunning = HasSingleton<GameRunningSingleton>();
            if (stage == 1)
            {
                if (!gameRunning) return;
                PrepareArmy();
                SetStage(2, 90);
                return;
            }
            if (!gameRunning && stage != 10) return;
            switch (stage)
            {
                case 2:
                    archer = ResolveMarker(1);
                    idleArcher = ResolveMarker(2);
                    target = ResolveMarker(3);
                    if (!VeteranRuntime.IsEligible(archer) || !VeteranRuntime.IsEligible(idleArcher) || !VeteranRuntime.IsAlive(target)) return;
                    SetTargetHealth(target, 1000);
                    IssueAttackCommand(archer, target);
                    Record("archer_identity", "gameplay", true,
                        "Real Archer prefab objectId=" + Manager.GetComponentData<ObjectId>(archer).value + "; active=" + archer + "; idle=" + idleArcher);
                    SetStage(3, 120);
                    return;
                case 3:
                    ObserveRealProjectile();
                    return;
                case 4:
                    ObserveOverkillAndRank();
                    return;
                case 5:
                    ObserveDamageBonus();
                    return;
                case 6:
                    RequestSave();
                    return;
                case 7:
                    WaitForSave();
                    return;
                case 8:
                    WaitForDeath();
                    return;
                case 9:
                    CheckReplacementAndLoad();
                    return;
                case 10:
                    if (gameRunning) VerifyLoadedVeteran();
                    return;
                case 11: ObserveAssault(); return;
                case 12: ObserveMentor(); return;
                case 13: VerifyDisabledOverwrite(); return;
                case 14: VerifyDisabledNewSave(); return;
                case 15: VerifyReenabledSave(); return;
            }
        }
        catch (Exception exception)
        {
            Record("runtime_self_test_exception", "harness", false, "Stage " + stage + ": " + exception);
            Finish("failed");
        }
    }

    private static void ContinueLoadingScreen()
    {
        if (stage != 1 && stage != 10) return;
        foreach (LoadingProgressBar bar in Resources.FindObjectsOfTypeAll<LoadingProgressBar>())
        {
            var field = AccessTools.Field(typeof(LoadingProgressBar), "_startAction");
            if (!(field.GetValue(bar) is UnityAction action)) continue;
            field.SetValue(bar, null);
            action.Invoke();
            VeteranRuntime.Log.LogInfo("SELFTEST: continued the isolated game's loading screen.");
        }
    }

    private static void ContinueMissionPanel()
    {
        if (stage == 0 || Time.unscaledTime < nextPanelAction) return;
        foreach (MissionPanel panel in Resources.FindObjectsOfTypeAll<MissionPanel>())
        {
            if (!panel.IsOpened) continue;
            // The normal close button queues PlayerActionType.MissionPanel, including the game's pause transition.
            AccessTools.Method(typeof(MissionPanel), "Toggle").Invoke(panel, null);
            nextPanelAction = Time.unscaledTime + 2;
            VeteranRuntime.Log.LogInfo("SELFTEST: queued the normal close action for the isolated mission objectives panel.");
            break;
        }
    }

    private static void PrepareArmy()
    {
        using var prefabsQuery = Manager.CreateEntityQuery(ComponentType.ReadOnly<PrefabsMapSingleton>());
        var prefabs = EntityQueryManagedComponentExtensions.GetSingleton<PrefabsMapSingleton>(prefabsQuery).prefabsMap;
        using (NativeArray<Entity> candidates = prefabs.GetValueArray(Allocator.Temp))
        {
            for (int i = 0; i < candidates.Length; i++)
            {
                Entity candidate = candidates[i];
                if (!Manager.HasComponent<UnitBase>(candidate) || !Manager.HasComponent<HealthBase>(candidate)) continue;
                UnitType type = Manager.GetComponentData<UnitBase>(candidate).value.Value.type;
                if (type == UnitType.Archer && !Manager.HasComponent<Enemy>(candidate)) archerPrefab = candidate;
                if (type == UnitType.ForkPeasant && Manager.HasComponent<Enemy>(candidate)) enemyPrefab = candidate;
            }
        }
        using (var units = Manager.CreateEntityQuery(new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly<Unit>(), ComponentType.ReadOnly<UnitBase>(), ComponentType.ReadOnly<ObjectId>(), ComponentType.ReadOnly<Translation>() },
            None = new[] { ComponentType.ReadOnly<Enemy>(), ComponentType.ReadOnly<Prefab>(), ComponentType.ReadOnly<Dead>() }
        }))
        using (var entities = units.ToEntityArray(Allocator.Temp))
        {
            foreach (Entity candidate in entities)
            {
                if (!VeteranRuntime.IsEligible(candidate) || Manager.GetComponentData<UnitBase>(candidate).value.Value.type != UnitType.Archer) continue;
                archer = candidate;
                origin = Manager.GetComponentData<Translation>(candidate).Value;
                int objectId = Manager.GetComponentData<ObjectId>(candidate).value;
                if (!prefabs.TryGetValue(objectId, out archerPrefab))
                    throw new InvalidOperationException("The existing tutorial Archer's exact prefab was not found.");
                Manager.AddComponentData(archer, new SelfTestUnit { Id = 1 });
                break;
            }
        }
        if (archer == Entity.Null || archerPrefab == Entity.Null || enemyPrefab == Entity.Null)
            throw new InvalidOperationException("Required existing tutorial Archer and installed-game ForkPeasant prefabs were not discovered.");
        Spawn(archerPrefab, origin + new float3(-20, 0, 0), 2);
        Spawn(enemyPrefab, origin + new float3(6, 0, 0), 3);
        VeteranRuntime.Log.LogInfo("SELFTEST: using an existing tutorial Archer as active source; spawned its exact prefab as an idle control and one hostile target via UnitSpawnUtility.");
    }

    private static void Spawn(Entity prefab, float3 position, int marker)
    {
        using var prefabsQuery = Manager.CreateEntityQuery(ComponentType.ReadOnly<PrefabsMapSingleton>());
        using var settingsQuery = Manager.CreateEntityQuery(ComponentType.ReadOnly<CurrentSettingsSingleton>());
        var prefabs = EntityQueryManagedComponentExtensions.GetSingleton<PrefabsMapSingleton>(prefabsQuery).prefabsMap;
        var settings = EntityQueryManagedComponentExtensions.GetSingleton<CurrentSettingsSingleton>(settingsQuery);
        var spawner = UnitSpawnUtility.MainThread.Init(prefabs, settings);
        using var commands = new EntityCommandBuffer(Allocator.TempJob);
        Entity entity = spawner.Spawn(prefab, position, quaternion.identity, true, false, false, commands, Manager);
        commands.AddComponent(entity, new SelfTestUnit { Id = marker });
        commands.Playback(Manager);
    }

    private static Entity ResolveMarker(int marker)
    {
        using var query = Manager.CreateEntityQuery(ComponentType.ReadOnly<SelfTestUnit>());
        using var entities = query.ToEntityArray(Allocator.Temp);
        using var markers = query.ToComponentDataArray<SelfTestUnit>(Allocator.Temp);
        for (int i = 0; i < entities.Length; i++) if (markers[i].Id == marker && VeteranRuntime.IsAlive(entities[i])) return entities[i];
        return Entity.Null;
    }

    private static void ObserveRealProjectile()
    {
        if (Time.unscaledTime >= nextDiagnostic)
        {
            nextDiagnostic = Time.unscaledTime + 5;
            using var timeQuery = Manager.CreateEntityQuery(ComponentType.ReadOnly<CurrentSessionTimeSingleton>());
            float gameTime = timeQuery.CalculateEntityCount() == 1 ? timeQuery.GetSingleton<CurrentSessionTimeSingleton>().elapsedTime : -1;
            string targetState = Manager.Exists(target) && Manager.HasComponent<Health>(target) ? Manager.GetComponentData<Health>(target).currentHealth.ToString() : "missing";
            string fightTarget = Manager.Exists(archer) && Manager.HasComponent<FightTarget>(archer) ? Manager.GetComponentData<FightTarget>(archer).Target.ToString() : "none";
            string cooldown = Manager.Exists(archer) && Manager.HasComponent<AttackCooldown>(archer) ? Manager.GetComponentData<AttackCooldown>(archer).value.ToString() : "none";
            VeteranRuntime.Log.LogInfo("SELFTEST diagnostics utc=" + DateTime.UtcNow.ToString("O") + "; gameTime=" + gameTime + "; timeScale=" + Time.timeScale +
                "; targetHp=" + targetState + "; archerAlive=" + VeteranRuntime.IsAlive(archer) + "; inFight=" + Manager.HasComponent<InFight>(archer) +
                "; fightTarget=" + fightTarget + "; cooldown=" + cooldown + "; tagged=" + VeteranRuntime.TaggedHits);
        }
        if (!VeteranRuntime.IsAlive(archer)) throw new InvalidOperationException("Active archer died before real-projectile proof.");
        VeteranRuntime.TryGetExperience(archer, out double xp);
        VeteranRuntime.TryGetExperience(idleArcher, out double idleXp);
        if (xp <= 0) return;
        Record("real_projectile_experience", "gameplay", true,
            "Archer XP=" + xp + "; instrumented hits=" + VeteranRuntime.TaggedHits + "; credited=" + VeteranRuntime.CreditedHits + ". No requests or XP had been injected before this observation.");
        Require(idleXp == 0, "same_type_independence", "gameplay", "Idle archer XP=" + idleXp);
        if (Manager.HasComponent<InForceAttack>(archer)) Manager.RemoveComponent<InForceAttack>(archer);
        if (Manager.Exists(target)) Manager.DestroyEntity(target);
        // Engineering phase: amplify XP only in this isolated test configuration to exercise ranks promptly.
        VeteranRuntime.Settings.ExperienceMultiplier.Value = 100;
        Spawn(enemyPrefab, origin + new float3(35, 0, 0), 4);
        target = ResolveMarker(4);
        if (target == Entity.Null) throw new InvalidOperationException("Synthetic request target did not spawn.");
        var health = Manager.GetComponentData<Health>(target);
        health.currentHealth = 10;
        Manager.SetComponentData(target, health);
        previousExperience = xp;
        previousHealth = Manager.GetComponentData<Health>(archer).currentHealth;
        maxExpectedExperience = (10 / VeteranRuntime.Settings.DamagePerExperience.Value +
            VeteranRuntime.Settings.KillExperience.Value * VeteranRuntime.Settings.PowerfulKillMultiplier.Value) * 100 * VeteranRuntime.Settings.RangedMultiplier.Value;
        InjectDamage(archer, target, 10000);
        SetStage(4, 20);
    }

    private static void ObserveOverkillAndRank()
    {
        VeteranRuntime.TryGetExperience(archer, out double xp);
        if (xp <= previousExperience) return;
        double delta = xp - previousExperience;
        Require(delta <= maxExpectedExperience + .01, "effective_overkill_credit", "synthetic_request",
            "Injected damage=10000; target pre-health=10; XP delta=" + delta + "; bounded maximum=" + maxExpectedExperience);
        if (!VeteranRuntime.TryGetSnapshot(archer, out VeteranSnapshot snapshot)) throw new InvalidOperationException("Veteran disappeared.");
        int level = Progression.Level(snapshot.Experience, VeteranRuntime.Settings.Ranks, VeteranRuntime.Settings.MaxLevel.Value);
        Require(level >= 2, "rank_and_health_bonus", "synthetic_request",
            "XP=" + snapshot.Experience + "; level=" + (level + 1) + "; own health factor=" + snapshot.AppliedHealthFactor);
        Require(snapshot.AppliedHealthFactor > 1 && snapshot.CurrentHealth <= previousHealth + .01f,
            "no_free_healing", "synthetic_request", "Health before=" + previousHealth + "; after=" + snapshot.CurrentHealth);
        VeteranRuntime.TryGetSnapshot(idleArcher, out VeteranSnapshot idle);
        Require(idle.Experience == 0 && Math.Abs(idle.AppliedHealthFactor - 1) < .0001f,
            "idle_archer_remains_recruit", "synthetic_request", "XP=" + idle.Experience + "; factor=" + idle.AppliedHealthFactor);
        Spawn(enemyPrefab, origin + new float3(35, 0, 1), 5);
        target = ResolveMarker(5);
        if (target == Entity.Null) throw new InvalidOperationException("Damage target did not spawn.");
        SetTargetHealth(target, 100);
        targetHealth = 100;
        expectedDamage = 10 * (1 + VeteranRuntime.Settings.Ranks[level].DamageBonus);
        InjectDamage(archer, target, 10);
        SetStage(5, 20);
    }

    private static void ObserveDamageBonus()
    {
        if (!Manager.Exists(target)) throw new InvalidOperationException("Damage target unexpectedly destroyed.");
        float health = Manager.GetComponentData<Health>(target).currentHealth;
        if (health >= targetHealth) return;
        Require(Math.Abs((targetHealth - health) - expectedDamage) < .02f,
            "per_unit_damage_bonus", "synthetic_request", "Observed loss=" + (targetHealth - health) + "; expected=" + expectedDamage);
        Manager.DestroyEntity(target);
        VeteranRuntime.Settings.ExperienceMultiplier.Value = originalMultiplier;
        VerifyHealthRestoration();
        BeginSpecialtyChecks();
    }

    private static void BeginSpecialtyChecks()
    {
        if (!VeteranRuntime.Settings.EnableSpecializations.Value) throw new InvalidOperationException("Self-test requires Specializations.Enabled=true.");
        VeteranRuntime.TryGetSnapshot(archer, out beforeSpecialty);
        VeteranRuntime.TryGetSnapshot(idleArcher, out beforeMentor);
        Require(!VeteranRuntime.ChooseSpecialization(VeteranRuntime.ActiveWorld!, idleArcher, Specialization.Guardian),
            "recruit_cannot_specialize", "synthetic-integration", "A recruit cannot choose an unlocked perk.");
        Require(VeteranRuntime.ChooseSpecialization(VeteranRuntime.ActiveWorld!, archer, Specialization.Assault),
            "assault_choice", "synthetic-integration", "Eligible soldier chooses Assault once.");
        Require(!VeteranRuntime.ChooseSpecialization(VeteranRuntime.ActiveWorld!, archer, Specialization.Mentor),
            "specialty_choice_is_permanent", "synthetic-integration", "A second choice is rejected without mutation.");
        Spawn(enemyPrefab, origin + new float3(35, 0, 3), 6);
        target = ResolveMarker(6); SetTargetHealth(target, 100); targetHealth = 100;
        int level = Progression.Level(beforeSpecialty.Experience, VeteranRuntime.Settings.Ranks, VeteranRuntime.Settings.MaxLevel.Value);
        expectedDamage = 10 * (1 + VeteranRuntime.Settings.Ranks[level].DamageBonus + .05f);
        InjectDamage(archer, target, 10); SetStage(11, 20);
    }

    private static void ObserveAssault()
    {
        float health = Manager.GetComponentData<Health>(target).currentHealth;
        if (health >= targetHealth) return;
        Require(Math.Abs(targetHealth - health - expectedDamage) < .02f, "assault_effective_damage", "synthetic_request",
            "Observed damage=" + (targetHealth - health) + "; expected=" + expectedDamage);
        Manager.DestroyEntity(target);
        VeteranRuntime.RestoreSnapshot(archer, beforeSpecialty); // QA-only rollback to test the other choice independently.
        VeteranRuntime.RestoreExperience(idleArcher, VeteranRuntime.Settings.Ranks[2].Threshold);
        Require(VeteranRuntime.ChooseSpecialization(VeteranRuntime.ActiveWorld!, idleArcher, Specialization.Mentor),
            "mentor_choice", "synthetic-integration", "Independent soldier chooses Mentor.");
        VeteranRuntime.TryGetExperience(idleArcher, out previousExperience);
        Spawn(enemyPrefab, origin + new float3(35, 0, 4), 7);
        target = ResolveMarker(7); SetTargetHealth(target, 100); targetHealth = 100;
        expectedDamage = 10 * (1 + VeteranRuntime.Settings.Ranks[2].DamageBonus);
        InjectDamage(idleArcher, target, 10); SetStage(12, 20);
    }

    private static void ObserveMentor()
    {
        float health = Manager.GetComponentData<Health>(target).currentHealth;
        if (health >= targetHealth) return;
        VeteranRuntime.TryGetExperience(idleArcher, out double xp);
        double expected = Progression.Award(expectedDamage, false, VeteranRuntime.Settings.DamagePerExperience.Value, 0,
            originalMultiplier, VeteranRuntime.Settings.RangedMultiplier.Value) * 1.2;
        Require(Math.Abs(xp - previousExperience - expected) < .02, "mentor_effective_experience", "synthetic_request",
            "Earned XP=" + (xp - previousExperience) + "; expected=" + expected);
        Manager.DestroyEntity(target); VeteranRuntime.RestoreSnapshot(idleArcher, beforeMentor);
        Require(VeteranRuntime.ChooseSpecialization(VeteranRuntime.ActiveWorld!, archer, Specialization.Guardian),
            "guardian_choice", "synthetic-integration", "QA restores pre-choice snapshot then chooses Guardian for save/restart coverage.");
        VeteranRuntime.TryGetSnapshot(archer, out var guardian);
        int level = Progression.Level(guardian.Experience, VeteranRuntime.Settings.Ranks, VeteranRuntime.Settings.MaxLevel.Value);
        float expectedFactor = Progression.CombinedHealthFactor(guardian.VanillaMaxHealth,
            VeteranRuntime.Settings.Ranks[level].HealthBonus + .1f, VeteranRuntime.Settings.MaxHealth.Value);
        Require(Math.Abs(guardian.AppliedHealthFactor - expectedFactor) < .0001f && guardian.CurrentHealth <= beforeSpecialty.CurrentHealth + .01f,
            "guardian_health_no_healing", "synthetic-integration", "Individual health factor=" + guardian.AppliedHealthFactor + "; no free healing.");
        VeteranArmy.Refresh();
        Require(VeteranArmy.Summary.Total >= 2 && VeteranArmy.Summary.Available <= VeteranArmy.Summary.Total,
            "army_live_summary", "synthetic-integration", "Army counted=" + VeteranArmy.Summary.Total + "; selectable=" + VeteranArmy.Summary.Available);
        int selected = VeteranArmy.SelectRank(level);
        using (var query = Manager.CreateEntityQuery(ComponentType.ReadOnly<Selected>()))
        using (var entities = query.ToEntityArray(Allocator.Temp))
            Require(selected > 0 && entities.Length == selected && Manager.HasComponent<Selected>(archer),
                "army_native_rank_selection", "synthetic-integration", "Native selection utility selected=" + selected + " including the exact veteran entity.");
        SetStage(6, 20);
    }

    private static void VerifyHealthRestoration()
    {
        Manager.CompleteAllJobs();
        if (!VeteranRuntime.TryGetSnapshot(archer, out VeteranSnapshot original))
            throw new InvalidOperationException("Health restoration test veteran missing.");
        var originalBase = Manager.GetComponentData<HealthBase>(archer);
        var originalHealth = Manager.GetComponentData<Health>(archer);
        float originalMultiplier = originalBase.GetMaxHealthMultiplier();
        float originalMaximum = original.VanillaMaxHealth * original.AppliedHealthFactor;
        // ResetSession affects the complete runtime; remember every owned/snapshottable soldier
        // before this synthetic check so both progress and live component bytes can be restored.
        var originals = new List<Tuple<Entity, VeteranSnapshot, HealthBase, Health>>();
        using (EntityQuery query = Manager.CreateEntityQuery(ComponentType.ReadOnly<HealthBase>(), ComponentType.ReadOnly<Health>()))
        using (NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp))
        {
            foreach (Entity entity in entities)
                if (VeteranRuntime.TryGetSnapshot(entity, out VeteranSnapshot saved))
                    originals.Add(Tuple.Create(entity, saved, Manager.GetComponentData<HealthBase>(entity), Manager.GetComponentData<Health>(entity)));
        }
        try
        {
            VeteranRuntime.RestoreSnapshot(archer, original);
            VeteranRuntime.RestoreSnapshot(archer, original);
            if (!VeteranRuntime.TryGetSnapshot(archer, out VeteranSnapshot repeated))
                throw new InvalidOperationException("Repeated snapshot test veteran missing.");
            Require(Math.Abs(repeated.Experience - original.Experience) < .0001 &&
                Math.Abs(repeated.CurrentHealth - originalHealth.currentHealth) < .0001f &&
                Math.Abs(repeated.VanillaMaxHealth * repeated.AppliedHealthFactor - originalMaximum) < .0001f &&
                Math.Abs(Manager.GetComponentData<HealthBase>(archer).GetMaxHealthMultiplier() - originalMultiplier) < .0001f,
                "snapshot_restore_idempotent", "synthetic-integration", "Two restores preserve exact soldier XP, current health and maximum health.");

            var modified = originalBase;
            modified.SetMaxHealthMultiplier((GameWorldLoaderSystem)null!, originalMultiplier * 1.5f);
            Manager.SetComponentData(archer, modified);
            VeteranRuntime.RestoreSnapshot(archer, original);
            Require(Math.Abs(Manager.GetComponentData<HealthBase>(archer).GetMaxHealthMultiplier() - originalMultiplier * 1.5f) < .0001f &&
                Math.Abs(Manager.GetComponentData<Health>(archer).currentHealth - originalHealth.currentHealth) < .0001f,
                "external_multiplier_preserved_on_restore", "synthetic-integration", "A reversible x1.5 modifier survives restoration; simulated by this harness; not produced by a third-party mod.");
            VeteranRuntime.ResetSession();
            Require(Math.Abs(Manager.GetComponentData<HealthBase>(archer).GetMaxHealthMultiplier() -
                originalMultiplier * 1.5f / original.AppliedHealthFactor) < .0001f,
                "external_multiplier_preserved_on_reset", "synthetic-integration", "Reset removes only the recorded veteran factor from the simulated modifier.");
        }
        finally
        {
            // A failed assertion must also leave the fixture's later save phase unchanged.
            VeteranRuntime.ResetSession();
            foreach (var entry in originals)
            {
                if (!Manager.Exists(entry.Item1)) continue;
                var baseline = entry.Item3;
                baseline.SetMaxHealthMultiplier((GameWorldLoaderSystem)null!, baseline.GetMaxHealthMultiplier() / entry.Item2.AppliedHealthFactor);
                Manager.SetComponentData(entry.Item1, baseline);
                Manager.SetComponentData(entry.Item1, entry.Item4);
                VeteranRuntime.RestoreSnapshot(entry.Item1, entry.Item2);
                Manager.SetComponentData(entry.Item1, entry.Item3);
                Manager.SetComponentData(entry.Item1, entry.Item4);
            }
        }
    }

    private static void RequestSave()
    {
        if (!VeteranRuntime.TryGetSnapshot(archer, out savedSnapshot)) throw new InvalidOperationException("Save veteran missing.");
        // Explicit test setup checks that health above the vanilla cap survives only in the sidecar.
        var health = Manager.GetComponentData<Health>(archer);
        health.currentHealth = savedSnapshot.VanillaMaxHealth * savedSnapshot.AppliedHealthFactor - .125f;
        Manager.SetComponentData(archer, health);
        VeteranRuntime.TryGetSnapshot(archer, out savedSnapshot);
        Require(VeteranChronicle.SetName(archer, "Aldric <b>the Brave</b>"), "soldier_name_assignment", "synthetic-integration", "A name is assigned to the exact live entity.");
        var career = VeteranChronicle.GetOrCreate(archer);
        if (!career.Favorite) VeteranChronicle.ToggleFavorite(archer);
        savedCareer = new ResumeState { ChronicleIdentity = career.Identity, SoldierName = career.Name,
            Favorite = career.Favorite, EffectiveDamage = career.EffectiveDamage, ConfirmedKills = career.ConfirmedKills };
        Require(career.Name.IndexOf('<') < 0 && career.Name.Length <= ChronicleRules.MaximumNameLength,
            "soldier_name_safe_markup", "synthetic-integration", "Display name is bounded plain text: " + career.Name);
        memorialBeforeDeath = VeteranChronicle.Memorial.Count;
        savedObjectId = Manager.GetComponentData<ObjectId>(archer).value;
        saveName = "VeteranSoldiersSelfTest_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var request = Manager.CreateEntity(typeof(SaveRequest));
        Manager.SetComponentData(request, new SaveRequest { SaveName = new FixedString128Bytes(saveName), IsAutoSave = false });
        SetStage(7, 60);
    }

    private static void WaitForSave()
    {
        if (HasSingleton<SaveRequest>()) return;
        string original = Path.Combine(Profiles.CurrentProfilePath, saveName);
        string sidecar = PersistenceStore.GetSidecarPath(Path.Combine(Paths.ConfigPath, "VeteranSoldiers", "saves"), original);
        if (!File.Exists(original) || !File.Exists(sidecar)) return;
        string digest = PersistenceStore.HashFile(original, true);
        var portable = PortableSave.Read(original, out string portableHash);
        Require(portable != null && portableHash == digest && portable.Records.TryGetValue(savedReferenceId, out var portableUnit) &&
            Math.Abs(portableUnit.Snapshot.Experience - savedSnapshot.Experience) < .001,
            "portable_save_exact_snapshot", "game_save", "The native slot contains its own verified exact-reference veteran snapshot.");
        var stored = PersistenceStore.Read(sidecar, digest, PersistenceStore.HashText(Path.GetFullPath(original).ToUpperInvariant()));
        var savedUnits = stored.Values.Where(item => item.ObjectId == savedObjectId && Math.Abs(item.Snapshot.Experience - savedSnapshot.Experience) < .001).ToArray();
        Require(savedUnits.Length == 1,
            "save_fingerprint_sidecar", "game_save", "Exact file fingerprint accepted; records=" + stored.Count + "; test save=" + saveName);
        Require(savedReferenceId > 0 && savedUnits[0].ReferenceId == savedReferenceId,
            "save_reference_matches_live_archer", "game_save", "Independent SaveUnits map reference=" + savedReferenceId + "; sidecar reference=" + savedUnits[0].ReferenceId);
        File.WriteAllText(Path.Combine(Paths.GameRootPath, "VeteranSoldiersSelfTest", "resume.json"),
            JsonConvert.SerializeObject(new ResumeState { SaveName = saveName, Snapshot = savedSnapshot, ObjectId = savedObjectId, ReferenceId = savedReferenceId,
                ChronicleIdentity = savedCareer.ChronicleIdentity, SoldierName = savedCareer.SoldierName, Favorite = savedCareer.Favorite,
                EffectiveDamage = savedCareer.EffectiveDamage, ConfirmedKills = savedCareer.ConfirmedKills }, Formatting.Indented));
        protectedSidecarHash = PersistenceStore.HashFile(sidecar, false);
        disabledSaveHash = digest;
        VeteranRuntime.Settings.Persistence.Value = false;
        var health = Manager.GetComponentData<Health>(archer);
        health.currentHealth -= .25f;
        Manager.SetComponentData(archer, health);
        RequestAdditionalSave(saveName, 13);
    }

    private static void RequestAdditionalSave(string name, int next)
    {
        if (HasSingleton<SaveRequest>()) throw new InvalidOperationException("Another self-test save is pending.");
        var request = Manager.CreateEntity(typeof(SaveRequest));
        Manager.SetComponentData(request, new SaveRequest { SaveName = new FixedString128Bytes(name), IsAutoSave = false });
        saveRequestedAt = Time.unscaledTime;
        SetStage(next, 60);
    }

    private static bool AdditionalSaveClosed(string name)
    {
        if (HasSingleton<SaveRequest>() || Time.unscaledTime < saveRequestedAt + 2) return false;
        string path = Path.Combine(Profiles.CurrentProfilePath, name);
        if (!File.Exists(path)) return false;
        try { PersistenceStore.HashFile(path, true); return true; }
        catch (IOException) { return false; }
    }

    private static void VerifyDisabledOverwrite()
    {
        if (!AdditionalSaveClosed(saveName)) return;
        string path = Path.Combine(Profiles.CurrentProfilePath, saveName);
        string sidecar = PersistenceStore.GetSidecarPath(Path.Combine(Paths.ConfigPath, "VeteranSoldiers", "saves"), path);
        Require(PersistenceStore.HashFile(path, false) != disabledSaveHash, "disabled_persistence_base_save_completed", "game_save",
            "The native save replaced the isolated slot after a controlled health change.");
        Require(PersistenceStore.HashFile(sidecar, false) == protectedSidecarHash, "disabled_persistence_preserves_existing_sidecar", "game_save",
            "Persistence=false did not replace the previous campaign XML with an empty snapshot.");
        Require(PortableSave.Read(path, out _) == null, "disabled_persistence_writes_no_portable_progress", "game_save",
            "A native overwrite with persistence disabled contains no stale or empty portable mod snapshot.");
        Require(VeteranPersistence.Status.IndexOf("SESSION", StringComparison.OrdinalIgnoreCase) >= 0 ||
            VeteranPersistence.Status.IndexOf("SESIÓN", StringComparison.OrdinalIgnoreCase) >= 0,
            "disabled_persistence_feedback", "game_save", "The journal reports session-only progress while persistence is disabled.");
        sessionOnlySave = saveName + "_SessionOnly";
        RequestAdditionalSave(sessionOnlySave, 14);
    }

    private static void VerifyDisabledNewSave()
    {
        if (!AdditionalSaveClosed(sessionOnlySave)) return;
        string path = Path.Combine(Profiles.CurrentProfilePath, sessionOnlySave);
        string sidecar = PersistenceStore.GetSidecarPath(Path.Combine(Paths.ConfigPath, "VeteranSoldiers", "saves"), path);
        Require(!File.Exists(sidecar), "disabled_persistence_creates_no_sidecar", "game_save",
            "A second completed native save with Persistence=false created no campaign XML.");
        VeteranRuntime.Settings.Persistence.Value = originalPersistence;
        var health = Manager.GetComponentData<Health>(archer);
        health.currentHealth = savedSnapshot.CurrentHealth;
        Manager.SetComponentData(archer, health);
        RequestAdditionalSave(saveName, 15);
    }

    private static void VerifyReenabledSave()
    {
        if (!AdditionalSaveClosed(saveName)) return;
        string path = Path.Combine(Profiles.CurrentProfilePath, saveName);
        string sidecar = PersistenceStore.GetSidecarPath(Path.Combine(Paths.ConfigPath, "VeteranSoldiers", "saves"), path);
        Dictionary<int, PersistentVeteran> stored;
        try { stored = PersistenceStore.Read(sidecar, PersistenceStore.HashFile(path, true), PersistenceStore.HashText(Path.GetFullPath(path).ToUpperInvariant())); }
        catch (InvalidDataException) { return; }
        Require(stored.TryGetValue(savedReferenceId, out var record) && Math.Abs(record.Snapshot.Experience - savedSnapshot.Experience) < .001 &&
            record.Chronicle != null && record.Chronicle.Identity == savedCareer.ChronicleIdentity,
            "reenabled_persistence_updates_exact_career", "game_save", "Re-enabling persistence writes a matching fingerprint and the same exact saved soldier identity.");
        // Native reference IDs belong to each individual save capture; an overwrite can
        // renumber them even when the live entity and its chronicle identity are unchanged.
        File.WriteAllText(Path.Combine(Paths.GameRootPath, "VeteranSoldiersSelfTest", "resume.json"),
            JsonConvert.SerializeObject(new ResumeState { SaveName = saveName, Snapshot = savedSnapshot, ObjectId = savedObjectId, ReferenceId = savedReferenceId,
                ChronicleIdentity = savedCareer.ChronicleIdentity, SoldierName = savedCareer.SoldierName, Favorite = savedCareer.Favorite,
                EffectiveDamage = savedCareer.EffectiveDamage, ConfirmedKills = savedCareer.ConfirmedKills }, Formatting.Indented));
        oldIndex = archer.Index;
        InjectDamage(Entity.Null, archer, 1000000);
        SetStage(8, 20);
    }

    private static void WaitForDeath()
    {
        if (VeteranRuntime.IsAlive(archer)) return;
        // Wait until the game's death commands have played back before destroying its corpse.
        if (Manager.Exists(archer) && !Manager.HasComponent<Dead>(archer) && !Manager.HasComponent<DelayedDestroy>(archer)) return;
        Require(!VeteranRuntime.TryGetExperience(archer, out _), "veteran_death_removes_eligibility", "synthetic_request",
            "Old entity=" + archer + "; no live XP entry can be queried.");
        Require(VeteranChronicle.Memorial.Count == memorialBeforeDeath + 1 &&
            VeteranChronicle.Memorial.Any(item => item.Identity == savedCareer.ChronicleIdentity && item.Name == savedCareer.SoldierName),
            "observed_death_enters_memorial", "synthetic_request", "A proven health transition records the exact soldier once.");
        VerifyRecycledEntity();
        Spawn(archerPrefab, origin + new float3(2, 0, 2), 6);
        SetStage(9, 20);
    }

    private static void VerifyRecycledEntity()
    {
        Entity old = archer;
        if (Manager.Exists(old)) Manager.DestroyEntity(old);
        var probes = new List<Entity>();
        Entity recycled = Entity.Null;
        try
        {
            // Allocate only our own transient probes and remove them before any game system runs.
            for (int i = 0; i < 16; i++)
            {
                Entity probe = Manager.CreateEntity();
                probes.Add(probe);
                if (probe.Index != old.Index) continue;
                recycled = probe;
                break;
            }
            if (recycled == Entity.Null)
            {
                Record("entity_index_reuse_not_observed", "entity_registry", true,
                    "No reuse of index " + old.Index + " in 16 bounded allocations; no claim of reuse validation.");
                return;
            }
            bool eligible = VeteranRuntime.TryGetExperience(recycled, out double experience);
            Require(recycled.Version != old.Version && !eligible && experience == 0,
                "entity_index_reuse_drops_experience", "entity_registry",
                "Old=" + old + "; recycled=" + recycled + "; old handle exists=" + Manager.Exists(old) + "; XP=" + experience);
        }
        finally
        {
            foreach (Entity probe in probes) if (Manager.Exists(probe)) Manager.DestroyEntity(probe);
        }
    }

    private static void CheckReplacementAndLoad()
    {
        Entity replacement = ResolveMarker(6);
        if (replacement == Entity.Null) return;
        Require(VeteranRuntime.TryGetExperience(replacement, out double xp) && xp == 0,
            "new_archer_recruit", "game_entity", "New=" + replacement + "; XP=" + xp + "; actual index reuse=" + (replacement.Index == oldIndex));
        var request = Manager.CreateEntity(typeof(LoadRequest));
        Manager.SetComponentData(request, new LoadRequest { NameToLoad = new FixedString128Bytes(saveName), FromMenu = false });
        SetStage(10, 240);
    }

    private static void VerifyLoadedVeteran()
    {
        if (HasSingleton<LoadRequest>()) return;
        if (VanillaResumeRequested)
        {
            if (!HasSingleton<GameRunningSingleton>() || loadedTestEntity == Entity.Null || Time.unscaledTime < loadedMappedAt + 1) return;
            if (!VeteranRuntime.TryGetSnapshot(loadedTestEntity, out var vanilla)) return;
            Require(Manager.GetComponentData<ObjectId>(loadedTestEntity).value == savedObjectId,
                "base_save_exact_identity", "vanilla_resume", "Mapped saved reference=" + savedReferenceId + "; loaded entity=" + loadedTestEntity);
            Require(vanilla.Experience == 0 && Math.Abs(vanilla.AppliedHealthFactor - 1) < .0001f,
                "base_save_no_veterancy", "vanilla_resume", "Persistence=false; XP=" + vanilla.Experience + "; factor=" + vanilla.AppliedHealthFactor);
            float expectedHealth = Math.Min(savedSnapshot.CurrentHealth, savedSnapshot.VanillaMaxHealth);
            Require(Math.Abs(vanilla.CurrentHealth - expectedHealth) < .02f && Math.Abs(vanilla.VanillaMaxHealth - savedSnapshot.VanillaMaxHealth) < .02f,
                "base_save_normalized_health", "vanilla_resume", "Expected HP=" + expectedHealth + "; actual=" + vanilla.CurrentHealth + "; vanilla max=" + vanilla.VanillaMaxHealth);
            Finish("passed");
            return;
        }
        if (!HasSingleton<GameRunningSingleton>() || loadedTestEntity == Entity.Null) return;
        if (!VeteranRuntime.TryGetSnapshot(loadedTestEntity, out var expectedUnit)) return;
        using var query = Manager.CreateEntityQuery(new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly<Unit>(), ComponentType.ReadOnly<UnitBase>(), ComponentType.ReadOnly<ObjectId>(), ComponentType.ReadOnly<Health>() },
            None = new[] { ComponentType.ReadOnly<Enemy>(), ComponentType.ReadOnly<Dead>(), ComponentType.ReadOnly<Prefab>() }
        });
        using var entities = query.ToEntityArray(Allocator.Temp);
        int matches = 0;
        VeteranSnapshot restored = default;
        Entity restoredEntity = Entity.Null;
        foreach (Entity entity in entities)
        {
            if (Manager.GetComponentData<ObjectId>(entity).value != savedObjectId || !VeteranRuntime.TryGetSnapshot(entity, out var snapshot)) continue;
            if (Math.Abs(snapshot.Experience - savedSnapshot.Experience) > .001) continue;
            matches++;
            restored = snapshot;
            restoredEntity = entity;
        }
        if (matches == 0) return;
        Require(matches == 1, "save_load_identity", "game_save", "Exactly one loaded soldier has the saved XP=" + savedSnapshot.Experience);
        Require(restoredEntity == loadedTestEntity && Manager.GetComponentData<ObjectId>(loadedTestEntity).value == savedObjectId &&
            Math.Abs(expectedUnit.Experience - savedSnapshot.Experience) < .001,
            "save_load_exact_reference", "game_save", "Saved map reference=" + savedReferenceId + "; mapped entity=" + loadedTestEntity + "; XP-bearing entity=" + restoredEntity);
        Require(Math.Abs(restored.AppliedHealthFactor - savedSnapshot.AppliedHealthFactor) < .0001f &&
            Math.Abs(restored.CurrentHealth - savedSnapshot.CurrentHealth) < .02f,
            "save_load_exact_stats", "game_save", "Saved HP=" + savedSnapshot.CurrentHealth + ", restored=" + restored.CurrentHealth +
            "; saved factor=" + savedSnapshot.AppliedHealthFactor + ", restored=" + restored.AppliedHealthFactor);
        Require(restored.Specialization == savedSnapshot.Specialization, "save_load_specialization", "game_save",
            "Saved specialization=" + savedSnapshot.Specialization + "; restored=" + restored.Specialization);
        if (savedCareer.ChronicleIdentity.Length > 0)
        {
            Require(VeteranChronicle.TryGet(restoredEntity, out var restoredCareer) &&
                restoredCareer.Identity == savedCareer.ChronicleIdentity && restoredCareer.Name == savedCareer.SoldierName &&
                restoredCareer.Favorite == savedCareer.Favorite && Math.Abs(restoredCareer.EffectiveDamage - savedCareer.EffectiveDamage) < .001 &&
                restoredCareer.ConfirmedKills == savedCareer.ConfirmedKills,
                "save_load_soldier_chronicle", "game_save", "Exact reference restores persistent identity, name, favorite and verified contribution counters.");
            Require(!VeteranChronicle.Memorial.Any(item => item.Identity == savedCareer.ChronicleIdentity),
                "load_discards_future_memorial", "game_save", "Loading a snapshot before death does not keep a future memorial entry.");
        }
        if (ResumeRequested)
            Record("full_process_restart_persistence", "process_restart", true, "Experience, individual identity and current/max health matched after starting this fresh process.");
        using (var selected = Manager.CreateEntityQuery(ComponentType.ReadOnly<Selected>())) Manager.RemoveComponent<Selected>(selected);
        if (SelectionRequested)
        {
            int selectedCount = 0;
            foreach (Entity entity in entities)
            {
                if (!VeteranRuntime.IsEligible(entity)) continue;
                Manager.AddComponent<Selected>(entity);
                selectedCount++;
            }
            VeteranRuntime.Log.LogInfo("SELFTEST visual setup: selected " + selectedCount + " eligible loaded units. Group rendering still requires visual inspection.");
        }
        else Manager.AddComponent<Selected>(restoredEntity);
        Finish("passed");
    }

    private static void CaptureSavedReference(SaveLoadSystem __instance, NativeParallelHashMap<Entity, EntityMappingRef> mapping)
    {
        if (!Requested || finished || ResumeRequested || (stage != 7 && stage != 15) || archer == Entity.Null) return;
        using var query = __instance.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SaveRequest>());
        if (query.CalculateEntityCount() != 1 || query.GetSingleton<SaveRequest>().SaveName.Value != saveName) return;
        if (mapping.TryGetValue(archer, out EntityMappingRef reference)) savedReferenceId = reference.referenceId;
    }

    private static void CaptureLoadedUnit(NativeParallelHashMap<int, Entity> mapping)
    {
        if (!Requested || finished || stage != 10 || savedReferenceId <= 0) return;
        if (!mapping.TryGetValue(savedReferenceId, out loadedTestEntity)) return;
        loadedMappedAt = Time.unscaledTime;
    }

    private static void InjectDamage(Entity source, Entity victim, float amount)
    {
        Entity request = Manager.CreateEntity(typeof(HealthChangeRequest));
        Manager.SetComponentData(request, HealthChangeRequest.ApplyWithoutSource(-amount, victim, HealthRequestFlags.None));
        if (source != Entity.Null) Manager.AddComponentData(request, new CombatSource { Source = source });
    }

    private static void SetTargetHealth(Entity entity, float healthValue)
    {
        var healthBase = Manager.GetComponentData<HealthBase>(entity);
        healthBase.SetMaxHealthMultiplier((Systems.GameWorldLoaderSystem)null!,
            Math.Max(1, healthValue / healthBase.value.Value.maxHealth));
        Manager.SetComponentData(entity, healthBase);
        var health = Manager.GetComponentData<Health>(entity);
        health.currentHealth = healthValue;
        Manager.SetComponentData(entity, health);
    }

    private static void IssueAttackCommand(Entity attacker, Entity victim)
    {
        // This is the same command consumed by UnitCommandSystem.ProcessAttackCommand.
        // The system performs the queue and fight-state transitions required by real ranged attacks.
        using var selected = Manager.CreateEntityQuery(ComponentType.ReadOnly<Selected>());
        Manager.RemoveComponent<Selected>(selected);
        Manager.AddComponent<Selected>(attacker);
        Entity command = Manager.CreateEntity(typeof(UnitAttackCommand), typeof(CommandTargetData), typeof(CommandParameterData), typeof(CommandDataTag));
        Manager.SetComponentData(command, new CommandTargetData { value = victim });
        Manager.SetComponentData(command, new CommandParameterData { parameter = 0 });
    }

    private static void SetComponent<T>(Entity entity, T value) where T : struct, IComponentData
    {
        if (Manager.HasComponent<T>(entity)) Manager.SetComponentData(entity, value);
        else Manager.AddComponentData(entity, value);
    }

    private static bool HasSingleton<T>() where T : struct, IComponentData
    {
        using var query = Manager.CreateEntityQuery(ComponentType.ReadOnly<T>());
        return query.CalculateEntityCount() == 1;
    }

    private static void SetStage(int next, float timeoutSeconds)
    {
        stage = next;
        deadline = Time.unscaledTime + timeoutSeconds;
        VeteranRuntime.Log.LogInfo("SELFTEST stage=" + next);
        if (!string.IsNullOrEmpty(reportPath)) WriteReport("running");
    }

    private static void Require(bool condition, string name, string kind, string evidence)
    {
        Record(name, kind, condition, evidence);
        if (!condition) throw new InvalidOperationException("Self-test check failed: " + name);
    }

    private static void Record(string name, string kind, bool passed, string evidence)
    {
        Results.Add(new Result { Check = name, Kind = kind, Passed = passed, Evidence = evidence });
        VeteranRuntime.Log.LogInfo("SELFTEST " + (passed ? "PASS " : "FAIL ") + name + ": " + evidence);
        if (!string.IsNullOrEmpty(reportPath)) WriteReport("running");
    }

    private static void Finish(string status)
    {
        finished = true;
        VeteranRuntime.Settings.ExperienceMultiplier.Value = originalMultiplier;
        VeteranRuntime.Settings.Persistence.Value = originalPersistence;
        WriteReport(status);
        VeteranRuntime.Log.LogInfo("SELFTEST finished=" + status + "; report=" + reportPath);
    }

    private static void WriteReport(string status)
    {
        File.WriteAllText(reportPath, JsonConvert.SerializeObject(new
        {
            Status = status,
            Stage = stage,
            Product = Application.productName,
            Utc = DateTime.UtcNow.ToString("O"),
            Scope = VanillaResumeRequested ? "Fresh-process base-save verification with Persistence=false; exact saved reference ID, normalized health and zero veterancy. No performance claim." :
                ResumeRequested ? "Fresh-process persistence verification against the prior isolated self-test save. No performance claim." :
                "Real projectile separately from synthetic post-mitigation health requests; save/load in same process. No restart persistence or performance claim.",
            Results
        }, Formatting.Indented));
    }
}

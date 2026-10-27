#if VETERAN_TEST_HARNESS
using System;
using System.Collections.Generic;
using System.Linq;
using Components;
using Components.Commands;
using Components.Commands.ResearchCommands;
using Components.Commands.UnitCommands;
using Components.RawComponents;
using Components.SharedContainerSingletons;
using Components.SingletonComponents;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ScriptableObjectDefinitions;
using Systems;
using Systems.GameStateSystems;
using Systems.InputSystems;
using Systems.InputSystems.PlayerActionGroups;
using UI.Research;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using Utility.EnumsStorage;

namespace VeteranSoldiers;

internal static partial class RuntimeHallTest
{
    private sealed class MedicalEvidence
    {
        internal World World = null!;
        internal Entity TargetEntity;
        public string Request = "", Source = "", Target = "", SourceType = "", Doctrine = "";
        public bool SourceFriendly, TargetFriendly, Operational, Applied;
        public int Rank, RequestsForTarget;
        public byte TagBefore, TagAfter;
        public float BaseAmount, ModifiedAmount, HealthBefore, HealthAfter, Maximum, Time;
    }
    private sealed class TrainingEvidence
    {
        internal World World = null!;
        internal Entity TrainerEntity, CitizenEntity;
        public string Trainer = "", Citizen = "", Doctrine = "";
        public int UnitId;
        public bool Operational, Applied, OriginalMapEntry, MapRestored;
        public float OriginalReduction, AppliedReduction, RemainingBefore, RemainingAfter, FullTime, DeltaTime, ExpectedProgress, ActualProgress, Time;
    }
    private static readonly List<MedicalEvidence> medicalEvidence = new List<MedicalEvidence>();
    private static readonly List<MedicalEvidence> pendingMedical = new List<MedicalEvidence>();
    private static readonly List<TrainingEvidence> trainingEvidence = new List<TrainingEvidence>();
    private static readonly List<TrainingEvidence> pendingTraining = new List<TrainingEvidence>();
    private static bool medicalArmed, trainingArmed;
    private static float nextTrainingSample;
    private static int observedResearchId = -1;
    private static bool researchCompletionAccelerated;
    private static object? researchBefore;
    private static string evidenceError = "";
    private sealed class EvidenceInput
    {
        internal World World = null!;
        internal PlayerActionType Action;
        internal int Parameter;
        internal float Deadline;
        internal Entity[] Selected = Array.Empty<Entity>();
    }
    private static EvidenceInput? queuedEvidenceInput;
    private static object? evidenceInputStatus;
    private static readonly Action<SystemBase> CompleteEvidenceJobs = (Action<SystemBase>)Delegate.CreateDelegate(
        typeof(Action<SystemBase>), AccessTools.Method(typeof(SystemBase), "CompleteDependency"));

    private static void InitializeEvidence(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(VeteranHall), nameof(VeteranHall.BeforeHealthRecovery)),
            prefix: new HarmonyMethod(typeof(RuntimeHallTest), nameof(BeforeMedicalEvidence)),
            postfix: new HarmonyMethod(typeof(RuntimeHallTest), nameof(AfterMedicalEvidence)));
        harmony.Patch(AccessTools.Method(typeof(HealthTrackSystem), "OnUpdateSimulation"),
            postfix: new HarmonyMethod(typeof(RuntimeHallTest), nameof(ObserveMedicalApplication)) { priority = Priority.Last });
        harmony.Patch(AccessTools.Method(typeof(VeteranHall), "BeforeTraining"),
            prefix: new HarmonyMethod(typeof(RuntimeHallTest), nameof(BeforeTrainingEvidence)),
            postfix: new HarmonyMethod(typeof(RuntimeHallTest), nameof(AfterTrainingMapEvidence)));
        harmony.Patch(AccessTools.Method(typeof(ArmyTrainingSystem), "OnUpdateSimulation"),
            postfix: new HarmonyMethod(typeof(RuntimeHallTest), nameof(ObserveTrainingApplication)) { priority = Priority.Last });
        harmony.Patch(AccessTools.Method(typeof(UIInputHandler), "OnUpdateSimulation"),
            prefix: new HarmonyMethod(typeof(EvidenceUiHooks), nameof(EvidenceUiHooks.Before)),
            postfix: new HarmonyMethod(typeof(EvidenceUiHooks), nameof(EvidenceUiHooks.After)));
    }

    // Harmony keys __state by patch DeclaringType for each original method. Construction
    // patches this same native method from RuntimeHallTest, so Evidence needs its own type.
    private static class EvidenceUiHooks
    {
        internal static void Before(UIInputHandler __instance, out EvidenceInput? __state) => BeforeEvidenceInput(__instance, out __state);
        internal static void After(UIInputHandler __instance, EvidenceInput? __state) => AfterEvidenceInput(__instance, __state);
    }

    private static void BeginMedicalObservation() { medicalArmed = true; medicalEvidence.Clear(); pendingMedical.Clear(); }
    private static bool ExecuteEvidence(string action, JObject command)
    {
        switch (action)
        {
            case "hall_medical_observe": BeginMedicalObservation(); return true;
            case "hall_medical_wound":
                if (!VeteranRuntime.IsEligible(healingTarget)) throw new InvalidOperationException("No living medical fixture target.");
                float amount = (float?)command["Amount"] ?? 10;
                if (!Progression.Finite(amount) || amount <= 0 || amount > 1000) throw new ArgumentException("Wound amount must be finite, positive and at most 1000.");
                var health = Manager.GetComponentData<Health>(healingTarget);
                health.currentHealth = Math.Max(1, health.currentHealth - amount);
                Manager.SetComponentData(healingTarget, health); healingBefore = health.currentHealth; medicalArmed = true;
                VeteranRuntime.Log.LogWarning("HALLQA synthetic wound; subsequent healing must come from the native Doctor route."); return true;
            case "hall_training_observe": trainingArmed = true; trainingEvidence.Clear(); pendingTraining.Clear(); nextTrainingSample = 0; return true;
            case "hall_trainer_select": SelectEvidenceTrainer((int?)command["Index"] ?? 0); return true;
            case "hall_train": StartEvidenceTraining((int?)command["UnitId"] ?? -1); return true;
            case "hall_train_cancel":
                int index = (int?)command["Index"] ?? 0;
                using (var trainers = Manager.CreateEntityQuery(ComponentType.ReadOnly<Selected>(), ComponentType.ReadOnly<UnitTrainingProcess>()))
                    if (trainers.CalculateEntityCount() != 1 || index < 0 || index >= Manager.GetBuffer<UnitTrainingProcess>(trainers.GetSingletonEntity()).Length)
                        throw new InvalidOperationException("Select exactly one trainer and an existing queue index to cancel.");
                QueueEvidenceInput(PlayerActionType.CancelTrainUnit, index, true); return true;
            case "hall_research_inspect": return true;
            case "hall_research": StartEvidenceResearch((int?)command["ResearchId"] ?? -1); return true;
            case "hall_research_cancel":
                using (var query = Manager.CreateEntityQuery(ComponentType.ReadOnly<ResearchContainerSingleton>()))
                    if (query.CalculateEntityCount() != 1 || observedResearchId < 0 || Manager.GetComponentObject<ResearchContainerSingleton>(query.GetSingletonEntity()).Container.CurrentResearchId.Value != observedResearchId)
                        throw new InvalidOperationException("Only the explicitly observed active research may be cancelled.");
                QueueEvidenceInput(PlayerActionType.CloseResearch, -1, false); return true;
            case "hall_research_complete":
                using (var query = Manager.CreateEntityQuery(ComponentType.ReadOnly<ResearchContainerSingleton>()))
                {
                    if (query.CalculateEntityCount() != 1) throw new InvalidOperationException("Native research container unavailable.");
                    var container = Manager.GetComponentObject<ResearchContainerSingleton>(query.GetSingletonEntity()).Container;
                    if (observedResearchId < 0 || container.CurrentResearchId.Value != observedResearchId)
                        throw new InvalidOperationException("Only the explicitly observed active research may be accelerated.");
                    container.ResearchProgress.Value = 1;
                }
                researchCompletionAccelerated = true;
                VeteranRuntime.Log.LogWarning("HALLQA synthetic research progress completion; native effects will apply on their next update. This is not evidence of elapsed research time."); return true;
            default: return false;
        }
    }

    private static void ResetEvidenceAfterLoad()
    {
        medicalArmed = trainingArmed = false; pendingMedical.Clear(); pendingTraining.Clear();
        healingTarget = Entity.Null; healingBefore = 0; queuedEvidenceInput = null;
        observedResearchId = -1; researchBefore = null; researchCompletionAccelerated = false;
        evidenceInputStatus = new { Status = "Load invalidated fixture targets and pending QA input; no unit identity rebinding is inferred." };
    }
    private static void QueueEvidenceInput(PlayerActionType action, int parameter, bool bindSelection)
    {
        if (queuedEvidenceInput != null) throw new InvalidOperationException("A previous QA native action is still pending.");
        CheckEvidenceInputContext(action, parameter);
        Entity[] selected = Array.Empty<Entity>();
        if (bindSelection)
        {
            using var query = Manager.CreateEntityQuery(ComponentType.ReadOnly<Selected>(), ComponentType.ReadOnly<UnitTrainer>());
            using var entities = query.ToEntityArray(Allocator.Temp); selected = entities.ToArray();
            if (selected.Length == 0) throw new InvalidOperationException("No exact native trainer selection is available.");
        }
        queuedEvidenceInput = new EvidenceInput { World = VeteranRuntime.ActiveWorld!, Action = action, Parameter = parameter,
            Deadline = Time.unscaledTime + 5, Selected = selected };
        evidenceInputStatus = new { Action = action.ToString(), Parameter = parameter, Status = "Queued for native input; no recruitment or research success inferred." };
    }

    private static void CheckEvidenceInputContext(PlayerActionType action, int parameter)
    {
        if (action == PlayerActionType.RunResearch || action == PlayerActionType.CloseResearch)
        {
            // Native ResearchItemBehaviour/ResearchPanel enqueue these directly. They are
            // validated by UIInputHandler.FilterResearchActions, not the grid availability set.
            // Require the same visible panel context; native filtering still validates the
            // callback, research state, queue and mission restrictions before emitting a command.
            var ui = GameState.UIState;
            var specials = GameState.Settings.missionDifficultyConfiguration.UISpecialsConfig;
            if (!ui.researchWindowOpened || ui.mainMenuOpened || ui.missionPanelOpened || ui.sandboxWavePanelOpened ||
                GameState.CommonState.inWinOrLoseProcess || GameState.CommonState.inputBlockedByInnerEventSystem ||
                (specials != null && specials.DeactivateResearchButton))
                throw new InvalidOperationException("Open the native research panel in an unrestricted gameplay context before submitting this QA action.");
            return;
        }
        if (!GameState.PlayerActionState.IsAvailable(new PlayerActionCallback(action, parameter)))
            throw new InvalidOperationException("The native UI currently restricts this action.");
    }

    private static void BeforeEvidenceInput(UIInputHandler __instance, out EvidenceInput? __state)
    {
        __state = null;
        var input = queuedEvidenceInput;
        if (input == null || !initialized) return;
        queuedEvidenceInput = null;
        try
        {
            Guard();
            if (!ReferenceEquals(input.World, __instance.World) || !input.World.IsCreated || Time.unscaledTime > input.Deadline)
                throw new InvalidOperationException("QA native action expired or its world changed.");
            if (input.Selected.Length > 0)
            {
                using var selection = Manager.CreateEntityQuery(ComponentType.ReadOnly<Selected>(), ComponentType.ReadOnly<UnitTrainer>());
                using var entities = selection.ToEntityArray(Allocator.Temp);
                if (entities.Length != input.Selected.Length || entities.Any(entity => !input.Selected.Contains(entity)))
                    throw new InvalidOperationException("The exact trainer selection changed before the native action.");
            }
            CheckEvidenceInputContext(input.Action, input.Parameter);
            var callback = new PlayerActionCallback(input.Action, input.Parameter);
            UIInputHandler.PlayerActionContainer.PlayerActions.Remove(input.Action);
            UIInputHandler.PlayerActionContainer.Add(callback); __state = input;
        }
        catch (Exception error) { evidenceError = error.Message; evidenceInputStatus = new { Action = input.Action.ToString(), NativeCommandEmitted = false, Error = error.Message }; }
    }
    private static void AfterEvidenceInput(UIInputHandler __instance, EvidenceInput? __state)
    {
        if (__state == null) return;
        try
        {
            ComponentType type = __state.Action == PlayerActionType.StartTrainUnit ? ComponentType.ReadOnly<UnitTrainCommand>() :
                __state.Action == PlayerActionType.CancelTrainUnit ? ComponentType.ReadOnly<UnitTrainCancelCommand>() :
                __state.Action == PlayerActionType.RunResearch ? ComponentType.ReadOnly<RunResearchCommand>() : ComponentType.ReadOnly<CloseResearchCommand>();
            using var query = __instance.EntityManager.CreateEntityQuery(type);
            bool emitted = query.CalculateEntityCount() == 1;
            if (emitted && __state.Parameter >= 0)
            { var entity = query.GetSingletonEntity(); emitted = __instance.EntityManager.HasComponent<CommandParameterData>(entity) && __instance.EntityManager.GetComponentData<CommandParameterData>(entity).parameter == __state.Parameter; }
            evidenceInputStatus = new { Action = __state.Action.ToString(), __state.Parameter, NativeCommandEmitted = emitted,
                Status = emitted ? "Native command emitted; verify queue, resource and simulation changes separately." : "Native input filtered the action; no success claimed." };
        }
        catch (Exception error) { evidenceError = "Native action observation: " + error.Message; }
    }

    private static void BeforeMedicalEvidence(Entity requestEntity, ref HealthChangeRequest request, out MedicalEvidence? __state)
    {
        __state = null;
        if (!initialized || !medicalArmed || pendingMedical.Count >= 128 || !(request.Value > 0)) return;
        try
        {
            Guard();
            if (!Manager.Exists(requestEntity) || !Manager.HasComponent<CombatSource>(requestEntity) || !Manager.Exists(request.Target) || !Manager.HasComponent<Health>(request.Target)) return;
            var tag = Manager.GetComponentData<CombatSource>(requestEntity);
            if (!Manager.Exists(tag.Source) || !Manager.HasComponent<UnitBase>(tag.Source)) return;
            UnitType type = Manager.GetComponentData<UnitBase>(tag.Source).value.Value.type;
            if (type != UnitType.Doctor && type != UnitType.NativesPlayerHealer) return;
            VeteranRuntime.TryGetExperience(request.Target, out double xp);
            __state = new MedicalEvidence { World = VeteranRuntime.ActiveWorld!, TargetEntity = request.Target,
                Request = requestEntity.ToString(), Source = tag.Source.ToString(), Target = request.Target.ToString(), SourceType = type.ToString(),
                SourceFriendly = !Manager.HasComponent<Enemy>(tag.Source), TargetFriendly = !Manager.HasComponent<Enemy>(request.Target),
                Rank = Progression.Level(xp, VeteranRuntime.Settings.Ranks, VeteranRuntime.Settings.MaxLevel.Value), TagBefore = tag.Processed, BaseAmount = request.Value,
                HealthBefore = Manager.GetComponentData<Health>(request.Target).currentHealth,
                Maximum = Manager.HasComponent<HealthBase>(request.Target) ? VeteranRuntime.VanillaMaximum(request.Target) : 0,
                Doctrine = VeteranHall.ActiveDoctrine.ToString(), Operational = VeteranHall.IsOperational, Time = Time.unscaledTime };
        }
        catch (Exception error) { evidenceError = "Medical before: " + error.Message; }
    }
    private static void AfterMedicalEvidence(Entity requestEntity, ref HealthChangeRequest request, MedicalEvidence? __state)
    {
        if (__state == null) return;
        try
        {
            Guard(); __state.ModifiedAmount = request.Value;
            __state.TagAfter = Manager.Exists(requestEntity) && Manager.HasComponent<CombatSource>(requestEntity) ? Manager.GetComponentData<CombatSource>(requestEntity).Processed : (byte)255;
            pendingMedical.Add(__state);
        }
        catch (Exception error) { evidenceError = "Medical after: " + error.Message; }
    }
    private static void ObserveMedicalApplication(HealthTrackSystem __instance)
    {
        if (!initialized || pendingMedical.Count == 0) return;
        try
        {
            CompleteEvidenceJobs(__instance); Guard();
            foreach (var sample in pendingMedical)
            {
                if (!ReferenceEquals(sample.World, __instance.World) || !Manager.Exists(sample.TargetEntity) || !Manager.HasComponent<Health>(sample.TargetEntity)) continue;
                sample.HealthAfter = Manager.GetComponentData<Health>(sample.TargetEntity).currentHealth;
                sample.Applied = true;
                sample.RequestsForTarget = pendingMedical.Count(other => other.TargetEntity == sample.TargetEntity && ReferenceEquals(other.World, sample.World));
                if (medicalEvidence.Count == 64) medicalEvidence.RemoveAt(0);
                medicalEvidence.Add(sample);
            }
        }
        catch (Exception error) { evidenceError = "Medical applied: " + error.Message; }
        finally { pendingMedical.Clear(); }
    }

    private static void BeforeTrainingEvidence(ArmyTrainingSystem __0)
    {
        if (!initialized || !trainingArmed || Time.unscaledTime < nextTrainingSample) return;
        try
        {
            Guard(); nextTrainingSample = Time.unscaledTime + 1; pendingTraining.Clear();
            using var research = Manager.CreateEntityQuery(ComponentType.ReadOnly<ResearchContainerSingleton>());
            if (research.CalculateEntityCount() != 1) return;
            var map = Manager.GetComponentObject<ResearchContainerSingleton>(research.GetSingletonEntity()).Container.TrainingTimeModify;
            using var query = Manager.CreateEntityQuery(ComponentType.ReadOnly<UnitTrainingProcess>(), ComponentType.Exclude<Prefab>(), ComponentType.Exclude<InConstruction>());
            using var trainers = query.ToEntityArray(Allocator.Temp);
            foreach (Entity trainer in trainers)
            {
                var queue = Manager.GetBuffer<UnitTrainingProcess>(trainer);
                if (queue.Length == 0 || pendingTraining.Count >= 16) continue;
                var process = queue[0]; bool present = map.TryGetValue(process.UnitId, out float reduction);
                pendingTraining.Add(new TrainingEvidence { World = __0.World, TrainerEntity = trainer, CitizenEntity = process.CitizenEntity,
                    Trainer = trainer.ToString(), Citizen = process.CitizenEntity.ToString(), UnitId = process.UnitId,
                    RemainingBefore = process.TimeToTrainingEnd, FullTime = process.FullTime, DeltaTime = __0.Time.DeltaTime,
                    OriginalMapEntry = present, OriginalReduction = reduction, AppliedReduction = reduction,
                    Doctrine = VeteranHall.ActiveDoctrine.ToString(), Operational = VeteranHall.IsOperational, Time = Time.unscaledTime });
            }
        }
        catch (Exception error) { evidenceError = "Training before: " + error.Message; pendingTraining.Clear(); }
    }
    private static void AfterTrainingMapEvidence()
    {
        if (pendingTraining.Count == 0) return;
        try
        {
            Guard();
            using var research = Manager.CreateEntityQuery(ComponentType.ReadOnly<ResearchContainerSingleton>());
            var map = Manager.GetComponentObject<ResearchContainerSingleton>(research.GetSingletonEntity()).Container.TrainingTimeModify;
            foreach (var sample in pendingTraining)
            { map.TryGetValue(sample.UnitId, out sample.AppliedReduction); sample.ExpectedProgress = sample.DeltaTime / Math.Max(.001f, 1 - sample.AppliedReduction); }
        }
        catch (Exception error) { evidenceError = "Training map: " + error.Message; }
    }
    private static void ObserveTrainingApplication(ArmyTrainingSystem __instance)
    {
        if (!initialized || pendingTraining.Count == 0) return;
        try
        {
            CompleteEvidenceJobs(__instance); Guard();
            using var research = Manager.CreateEntityQuery(ComponentType.ReadOnly<ResearchContainerSingleton>());
            var map = Manager.GetComponentObject<ResearchContainerSingleton>(research.GetSingletonEntity()).Container.TrainingTimeModify;
            foreach (var sample in pendingTraining)
            {
                if (!ReferenceEquals(sample.World, __instance.World) || !Manager.Exists(sample.TrainerEntity) || !Manager.HasComponent<UnitTrainingProcess>(sample.TrainerEntity)) continue;
                var queue = Manager.GetBuffer<UnitTrainingProcess>(sample.TrainerEntity);
                foreach (var process in queue)
                    if (process.UnitId == sample.UnitId && process.CitizenEntity == sample.CitizenEntity)
                    { sample.RemainingAfter = process.TimeToTrainingEnd; sample.ActualProgress = sample.RemainingBefore - sample.RemainingAfter; sample.Applied = true; break; }
                bool present = map.TryGetValue(sample.UnitId, out float reduction);
                sample.MapRestored = present == sample.OriginalMapEntry && (!present || reduction == sample.OriginalReduction);
                if (trainingEvidence.Count == 64) trainingEvidence.RemoveAt(0);
                trainingEvidence.Add(sample);
            }
        }
        catch (Exception error) { evidenceError = "Training applied: " + error.Message; }
        finally { pendingTraining.Clear(); }
    }

    private static void SelectEvidenceTrainer(int index)
    {
        using var query = Manager.CreateEntityQuery(ComponentType.ReadOnly<UnitTrainer>(), ComponentType.ReadOnly<BuildingBase>(), ComponentType.Exclude<Prefab>(), ComponentType.Exclude<Enemy>(), ComponentType.Exclude<InConstruction>(), ComponentType.Exclude<CurrentBuildingForConstruction>());
        using var trainers = query.ToEntityArray(Allocator.Temp);
        if (index < 0 || index >= trainers.Length) throw new InvalidOperationException("No completed native trainer at this index; construct a Barracks first.");
        using (var selected = Manager.CreateEntityQuery(ComponentType.ReadOnly<Selected>())) Manager.RemoveComponent<Selected>(selected);
        Manager.AddComponent<Selected>(trainers[index]); GameStateUpdateTasksUtility.UpdateGameState(Manager, GameStateUpdateTaskType.Selection);
    }
    private static void StartEvidenceTraining(int id)
    {
        if (GameState.SelectedBuildingsState.currentTrainerType == UnitTrainerType.Non) throw new InvalidOperationException("Select a completed native trainer and wait for its shop state.");
        var shop = GameState.GetShopStatesByType(GameState.SelectedBuildingsState.currentTrainerType);
        if (!shop.TryGetValue(id, out var item) || item.researchLocked || !item.hasTrainer || !item.hasNotOverloadedTrainer || item.buyCount <= 0)
            throw new InvalidOperationException("The selected native training shop cannot recruit this unit now.");
        QueueEvidenceInput(PlayerActionType.StartTrainUnit, id, true);
        trainingArmed = true; nextTrainingSample = 0;
        VeteranRuntime.Log.LogWarning("HALLQA real native training command queued for unit " + id + "; native citizens, resources, queue and time remain authoritative.");
    }
    private static ResearchItem[] EvidenceResearchItems()
    {
        using var query = Manager.CreateEntityQuery(ComponentType.ReadOnly<CurrentSettingsSingleton>());
        if (query.CalculateEntityCount() != 1) return Array.Empty<ResearchItem>();
        return Manager.GetComponentObject<CurrentSettingsSingleton>(query.GetSingletonEntity()).missionDifficultyConfiguration.researchConfig.GetAvailableResearches() ?? Array.Empty<ResearchItem>();
    }
    private static void StartEvidenceResearch(int id)
    {
        var item = EvidenceResearchItems().FirstOrDefault(value => value.researchSetting.id == id);
        if (item == null || !GameState.ResearchesState.ResearchStateMap.TryGetValue(id, out ResearchState state) || state != ResearchState.Active)
            throw new InvalidOperationException("Choose an actually available native research ID; prerequisites and costs are not bypassed.");
        using var query = Manager.CreateEntityQuery(ComponentType.ReadOnly<ResearchContainerSingleton>());
        var container = Manager.GetComponentObject<ResearchContainerSingleton>(query.GetSingletonEntity()).Container;
        if (container.CurrentResearchId.Value >= 0) throw new InvalidOperationException("Native research is already running.");
        researchBefore = EvidenceResearchHealth(); observedResearchId = id; researchCompletionAccelerated = false;
        QueueEvidenceInput(PlayerActionType.RunResearch, id, false);
        VeteranRuntime.Log.LogWarning("HALLQA real native research command queued: " + id + "; normal prerequisites, costs and researcher progress apply.");
    }
    private static object EvidenceResearchHealth()
    {
        object Describe(Entity entity) => new { Entity = entity.ToString(), Maximum = VeteranRuntime.VanillaMaximum(entity),
            Current = Manager.HasComponent<Health>(entity) ? Manager.GetComponentData<Health>(entity).currentHealth : 0 };
        return new { UniversityPrefab = GameState.PrefabsMap.TryGetValue(VeteranHall.BaseObjectId, out Entity university) ? Describe(university) : null,
            HallPrefab = GameState.PrefabsMap.TryGetValue(VeteranHall.ObjectId, out Entity hall) ? Describe(hall) : null,
            Halls = Halls().Select(Describe).ToArray() };
    }
    private static object EvidenceSnapshot
    {
        get
        {
            var candidates = EvidenceResearchItems().Where(item => (item.healthEffects?.Any(effect => effect.objectId == VeteranHall.BaseObjectId) ?? false) ||
                (item.maxHealthAdditiveResearch?.Any(effect => effect.objectId == VeteranHall.BaseObjectId) ?? false)).Select(item => new {
                    item.researchSetting.id, item.researchName, item.researchSetting.parentId, item.researchSetting.ownerObjectId, item.researchSetting.researchTime,
                    State = GameState.ResearchesState.ResearchStateMap.TryGetValue(item.researchSetting.id, out ResearchState state) ? state.ToString() : "unavailable" }).ToArray();
            object? research = null;
            using (var query = Manager.CreateEntityQuery(ComponentType.ReadOnly<ResearchContainerSingleton>()))
                if (query.CalculateEntityCount() == 1)
                { var container = Manager.GetComponentObject<ResearchContainerSingleton>(query.GetSingletonEntity()).Container;
                  research = new { CurrentId = container.CurrentResearchId.Value, Progress = container.ResearchProgress.Value, Speed = container.ResearchSpeed.Value,
                      ObservedId = observedResearchId, ObservedCompleted = observedResearchId >= 0 && container.CompleteResearches.Contains(observedResearchId),
                      AcceleratedCompletion = researchCompletionAccelerated, Before = researchBefore, Now = EvidenceResearchHealth(), UniversityHealthResearch = candidates }; }
            var shop = new List<object>();
            if (GameState.SelectedBuildingsState.currentTrainerType != UnitTrainerType.Non)
            {
                var values = GameState.GetShopStatesByType(GameState.SelectedBuildingsState.currentTrainerType);
                using var keys = values.GetKeyArray(Allocator.Temp);
                foreach (int id in keys) { var item = values[id]; shop.Add(new { UnitId = id, item.buyCount, item.researchLocked, item.hasTrainer, item.hasNotOverloadedTrainer, item.timeToTrain, item.buyPrice }); }
            }
            var queues = new List<object>();
            using (var query = Manager.CreateEntityQuery(ComponentType.ReadOnly<UnitTrainer>(), ComponentType.ReadOnly<UnitTrainingProcess>(), ComponentType.Exclude<Prefab>(), ComponentType.Exclude<CurrentBuildingForConstruction>()))
            using (var entities = query.ToEntityArray(Allocator.Temp))
                foreach (Entity entity in entities)
                {
                    var entries = Manager.GetBuffer<UnitTrainingProcess>(entity).ToNativeArray(Allocator.Temp);
                    try { queues.Add(new { Entity = entity.ToString(), Selected = Manager.HasComponent<Selected>(entity),
                        Entries = entries.Select(entry => new { Citizen = entry.CitizenEntity.ToString(), entry.UnitId, entry.TimeToTrainingEnd, entry.FullTime }).ToArray() }); }
                    finally { entries.Dispose(); }
                }
            return new { Error = evidenceError, NativeInput = evidenceInputStatus, MedicalArmed = medicalArmed, Medical = medicalEvidence.ToArray(), TrainingArmed = trainingArmed,
                Training = trainingEvidence.ToArray(), TrainingShop = shop, TrainingQueues = queues, Research = research };
        }
    }
}
#endif

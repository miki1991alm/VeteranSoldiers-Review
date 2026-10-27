using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Components;
using Components.RawComponents;
using Components.SharedContainerSingletons;
using Components.SingletonComponents;
using Components.Structs;
using DataProcessors;
using HarmonyLib;
using ScriptableObjectDefinitions;
using Systems;
using Systems.GameStateSystems;
using Systems.InputSystems.PlayerActionGroups;
using UI;
using UI.HelpersStorage;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using Utility;
using Utility.EnumsStorage;

namespace VeteranSoldiers;

/// <summary>
/// A University extension with the native University's entire construction/worker/price contract.
/// Only presentation/ObjectId differ. On-disk buildings always retain the ordinary University ID;
/// the exact-save sidecar restores Hall identity before native prefab instantiation.
/// </summary>
internal static class VeteranHall
{
    internal const int ObjectId = 191981074;
    internal const string NameKey = "veteransoldiers_hall_name";
    internal const string DescriptionKey = "veteransoldiers_hall_description";
    internal static int BaseObjectId { get; private set; }
    internal static bool Available { get; private set; }
    internal static HallDoctrine ActiveDoctrine { get; private set; }
    internal static HallDoctrine PendingDoctrine { get; private set; }
    internal static float ResearchProgress01 => (float)(researchProgress / HallRules.ResearchSeconds);
    internal static double CombatExperience { get; private set; }
    private static double researchProgress;
    private static int unlocked;
    private static float lastSessionTime = -1;
    private static float nextScan;
    private static World? queryWorld;
    private static EntityQuery buildings;
    private static bool queryValid;
    private static readonly List<Entity> halls = new List<Entity>();
    private static readonly List<int> trainingPrefabIds = new List<int>();
    private static int trainingPrefabCount = -1;
    private static HallTrainingIdleGuard? trainingIdleGuard;
    private static BuildingsShopConfig? injectedShop;
    private static int shopCategory = -1;
    private static int pendingCategory = -1;
    private static World? pendingCategoryWorld;
    private static bool catalogFailed;
    private static string failure = "";
    private static HallVisuals.Model? model;
    private static World? catalogWorld;
    private static Entity catalogPrefab;
    private static bool loadResearchFallback;
    private static readonly Action<SystemBase> Complete = (Action<SystemBase>)Delegate.CreateDelegate(
        typeof(Action<SystemBase>), AccessTools.Method(typeof(SystemBase), "CompleteDependency"));

    internal static int CompletedCount
    {
        get
        {
            int count = 0;
            foreach (Entity entity in halls) if (IsCompleted(entity)) count++;
            return count;
        }
    }
    internal static int HallCount => halls.Count;
    internal static int OrdinaryUniversityCount { get; private set; }
    internal static bool IsOperational => VeteranRuntime.Ready && VeteranHallResearch.Compatible && CompletedCount > 0;
    internal static string Name => VeteranText.Choose("Hall of Veterans", "Salón de los Veteranos");
    internal static string Status => !Available ? (failure.Length > 0 ? failure : VeteranText.Choose("Waiting for the building catalog", "Esperando el catálogo de edificios")) :
        !VeteranHallResearch.Compatible ? VeteranText.Choose("Hall unavailable with the current research configuration. See diagnostics.", "Salón no disponible con la configuración de investigación actual. Consulta el diagnóstico.") :
        !IsOperational ? MissingHallReason :
        PendingDoctrine != HallDoctrine.None ? VeteranText.Choose("Preparing: ", "Preparando: ") + DoctrineName(PendingDoctrine) + " · " + Math.Round(ResearchProgress01 * 100) + "% · " + PreparationStatus :
        ActiveDoctrine == HallDoctrine.None ? VeteranText.Choose("Hall operational · choose one doctrine", "Salón operativo · elige una doctrina") :
        VeteranText.Choose("Active: ", "Activa: ") + DoctrineName(ActiveDoctrine);

    internal static string PreparationStatus => !IsOperational ? VeteranText.Choose("Waiting for a completed Hall", "Esperando un Salón terminado") :
        Time.timeScale <= 0 ? VeteranText.Choose("Paused · resume the game", "En pausa · reanuda el juego") :
        VeteranText.Choose("Preparation in progress", "Preparación en curso");

    internal static int RequiredTownHallLevel
    {
        get
        {
            if (!Available || !VeteranRuntime.Ready || !GameState.PrefabsMap.IsCreated ||
                !GameState.PrefabsMap.TryGetValue(ObjectId, out Entity prefab) || !VeteranRuntime.Manager.HasComponent<BuildingBase>(prefab)) return 0;
            return VeteranRuntime.Manager.GetComponentData<BuildingBase>(prefab).value.Value.townHallLevelRestriction;
        }
    }
    private static string MissingHallReason
    {
        get
        {
            if (HallCount > 0) return VeteranText.Choose("Finish building the Hall to activate doctrines.", "Termina la construcción del Salón para activar doctrinas.");
            if (CanDedicate(out _)) return VeteranText.Choose("Your University is ready. Dedicate it for free to activate doctrines.", "Tu Universidad está lista. Dedícala gratis para activar doctrinas.");
            if (OrdinaryUniversityCount > 0) return VeteranText.Choose("Finish your University, then dedicate it as a Hall for free.", "Termina tu Universidad y después dedícala como Salón gratis.");
            int required = RequiredTownHallLevel;
            if (required > GameState.CommonState.townHallLevel)
                return VeteranText.Choose("Hall requires Town Hall level ", "El Salón requiere Ayuntamiento nivel ") + required +
                    VeteranText.Choose(" · current: ", " · actual: ") + GameState.CommonState.townHallLevel;
            if (shopCategory < 0) return VeteranText.Choose("The University is not available in this mission's construction menu.", "La Universidad no está disponible en el menú de construcción de esta misión.");
            return VeteranText.Choose("Build a Hall, or dedicate a completed University for free.", "Construye un Salón o dedica gratis una Universidad terminada.");
        }
    }

    internal static void Initialize(Harmony harmony)
    {
        // Optional fast path only: unsupported query layouts retain the conservative
        // modifier transaction instead of disabling doctrines or changing training.
        trainingIdleGuard = new HallTrainingIdleGuard(typeof(ArmyTrainingSystem));
        try
        {
            harmony.Patch(AccessTools.Method(typeof(DataHub), "ConvertDefaultData"),
                new HarmonyMethod(typeof(VeteranHall), nameof(BeforeConvert)));
            harmony.Patch(AccessTools.Method(typeof(GameStateUpdateHandler), "StartInitState"),
                new HarmonyMethod(typeof(VeteranHall), nameof(BeforeInitState)));
            harmony.Patch(AccessTools.Method(typeof(GameStateUpdateHandler), "InitObjectUIDataMap"),
                postfix: new HarmonyMethod(typeof(VeteranHall), nameof(AfterUiMap)));
            harmony.Patch(AccessTools.Method(typeof(GameStateUpdateHandler), "InitBuildingShop"),
                new HarmonyMethod(typeof(VeteranHall), nameof(BeforeBuildingShop)));
            harmony.Patch(AccessTools.Method(typeof(GameStateUpdateHandler), "UpdateShopActionState"),
                postfix: new HarmonyMethod(typeof(VeteranHall), nameof(RestrictHallCount)));
            harmony.Patch(AccessTools.Method(typeof(UIInputHandler), "OnUpdateSimulation"),
                postfix: new HarmonyMethod(typeof(VeteranHall), nameof(AfterNativeInput)));
            foreach (string method in new[] { "GetObjectString", "GetHintsString" })
                foreach (var target in typeof(LocalizationStrings).GetMethods().Where(m => m.Name == method))
                    harmony.Patch(target, new HarmonyMethod(typeof(VeteranHall), nameof(Localize)));
            harmony.Patch(AccessTools.Method(typeof(ArmyTrainingSystem), "OnUpdateSimulation"),
                new HarmonyMethod(typeof(VeteranHall), nameof(BeforeTraining)),
                new HarmonyMethod(typeof(VeteranHall), nameof(AfterTraining)),
                finalizer: new HarmonyMethod(typeof(VeteranHall), nameof(FinalizeTraining)));
        }
        catch (Exception error) { DisableCatalog("Hall integration unavailable", error); }
    }

    private static bool Localize(string key, ref string __result)
    {
        if (key == NameKey) { __result = Name; return false; }
        if (key == DescriptionKey)
        {
            __result = VeteranText.Choose("A University dedicated to your veterans. Retains native researchers and research; houses the army chronicle, memorial and one active doctrine. Shares the University's limit of one. An existing University can be dedicated for free.",
                "Una Universidad dedicada a tus veteranos. Conserva investigadores e investigación; alberga el registro, el memorial y una doctrina activa. Comparte el límite de una Universidad. Puedes dedicar gratis una Universidad existente.");
            return false;
        }
        return true;
    }

    [HarmonyPriority(Priority.Last)]
    private static void BeforeConvert(Dictionary<GameObject, int> objects)
    {
        if (catalogFailed || !VeteranRuntime.Settings.Enabled.Value) return;
        try
        {
            if (objects.Values.Contains(ObjectId)) throw new InvalidOperationException("Hall object ID is already registered; no existing prefab was overwritten.");
            var candidate = objects.Where(pair => pair.Key != null && pair.Key.GetComponent<BuildingProxy>() != null &&
                pair.Key.GetComponent<BuildingProxy>().baseData.type == BuildingType.University && pair.Key.GetComponent<ResearchProducerProxy>() != null && pair.Key.GetComponent<EnemyProxy>() == null)
                .OrderBy(pair => pair.Key.GetComponent<BuildingProxy>().baseData.level).ThenBy(pair => pair.Value).FirstOrDefault();
            if (candidate.Key == null || candidate.Key.GetComponent<PriceProxy>() == null)
                throw new InvalidOperationException("The native University prefab was not found.");
            VeteranRuntime.Log.LogInfo("Hall University candidates: " + string.Join("; ", objects.Where(pair => pair.Key != null && pair.Key.GetComponent<BuildingProxy>() != null && pair.Key.GetComponent<BuildingProxy>().baseData.type == BuildingType.University)
                .Select(pair => pair.Value + ":" + pair.Key.name + ":level=" + pair.Key.GetComponent<BuildingProxy>().baseData.level + ":enemy=" + (pair.Key.GetComponent<EnemyProxy>() != null))));
            BaseObjectId = candidate.Value;
            // Scene GameObjects are not Unity prefab assets. Never insert a clone here: native
            // conversion would get Entity.Null. Clone the finished ECS prefab before mission caches.
        }
        catch (Exception error) { DisableCatalog("Hall catalog discovery unavailable", error); }
    }

    private static void BeforeInitState(GameStateUpdateHandler __instance) => EnsureRegistered(__instance.World, true);

    private static void AfterUiMap(ref Dictionary<int, ObjectUiData> __result)
    {
        if (!Available || !__result.TryGetValue(BaseObjectId, out ObjectUiData ui)) return;
        // ObjectUiData is a struct. Preserve the University metadata and the native dictionary.
        ui.Name = NameKey; ui.Description = DescriptionKey; ui.Icon = HallVisuals.Icon;
        __result = new Dictionary<int, ObjectUiData>(__result) { [ObjectId] = ui };
    }

    internal static void EnsureRegistered(World world, bool verify = false)
    {
        if (catalogFailed || BaseObjectId <= 0 || !world.IsCreated || !VeteranRuntime.Settings.Enabled.Value) return;
        if (!verify && Available && ReferenceEquals(catalogWorld, world)) return;
        Available = false;
        Entity hall = Entity.Null;
        try
        {
            var manager = world.EntityManager;
            using var query = manager.CreateEntityQuery(ComponentType.ReadOnly<PrefabsMapSingleton>());
            if (query.CalculateEntityCount() != 1) return;
            var catalog = manager.GetComponentObject<PrefabsMapSingleton>(query.GetSingletonEntity());
            if (!catalog.prefabsMap.IsCreated || !catalog.prefabsMap.TryGetValue(BaseObjectId, out Entity source)) return;
            if (catalog.prefabsMap.TryGetValue(ObjectId, out Entity existing))
            {
                if (ReferenceEquals(catalogWorld, world) && existing == catalogPrefab && manager.Exists(existing))
                { Available = true; return; }
                throw new InvalidOperationException("Hall object ID is already in the ECS catalog.");
            }
            if (!manager.Exists(source) || !manager.HasComponent<Prefab>(source) || !manager.HasComponent<LinkedEntityGroup>(source))
                throw new InvalidOperationException("The native University is not a complete ECS prefab.");
            manager.CompleteAllJobs();
            hall = manager.Instantiate(source);
            var members = new List<Entity>();
            foreach (var linked in manager.GetBuffer<LinkedEntityGroup>(hall)) members.Add(linked.Value);
            foreach (Entity child in members) if (!manager.HasComponent<Prefab>(child)) manager.AddComponent<Prefab>(child);
            ApplyHallModel(manager, hall, true);
            manager.SetComponentData(hall, new Components.RawComponents.ObjectId { value = ObjectId });
            if (catalog.prefabsMap.Capacity < catalog.prefabsMap.Count() + 1) catalog.prefabsMap.Capacity = catalog.prefabsMap.Count() + 16;
            catalog.prefabsMap.Add(ObjectId, hall);
            catalogWorld = world;
            catalogPrefab = hall;
            Available = true;
            VeteranRuntime.Log.LogInfo("Hall ECS prefab registered; native University fallback=" + BaseObjectId + ", linked entities=" + manager.GetBuffer<LinkedEntityGroup>(hall).Length + ". Native conversion catalog remained unchanged.");
        }
        catch (Exception error)
        {
            if (hall != Entity.Null && world.EntityManager.Exists(hall)) world.EntityManager.DestroyEntity(hall);
            DisableCatalog("Hall ECS registration unavailable", error);
        }
    }

    private static void Set<T>(EntityManager manager, Entity entity, T value) where T : struct, IComponentData
    { if (manager.HasComponent<T>(entity)) manager.SetComponentData(entity, value); else manager.AddComponentData(entity, value); }

    private static void ApplyHallModel(EntityManager manager, Entity hall, bool prefab)
    {
        Entity render = Entity.Null;
        float largest = 0;
        foreach (var member in manager.GetBuffer<LinkedEntityGroup>(hall))
        {
            if (member.Value == hall || !manager.HasComponent<RenderMesh>(member.Value)) continue;
            RenderMesh candidate = manager.GetSharedComponentData<RenderMesh>(member.Value);
            if (candidate.mesh == null || candidate.material == null) continue;
            float size = candidate.mesh.bounds.size.sqrMagnitude;
            if (size > largest) { largest = size; render = member.Value; }
        }
        if (render == Entity.Null) throw new InvalidOperationException("The University has no compatible ECS mesh renderer.");
        RenderMesh original = manager.GetSharedComponentData<RenderMesh>(render);
        // BuildingBase dimensions count map cells; the mesh is in world units (the native
        // University is about 30 units wide for six cells). Match the donor's real footprint.
        if (model == null) model = HallVisuals.Create(original.mesh.bounds.size.x, original.mesh.bounds.size.z, original.material);
        // Preserve every root/gameplay entity and the native renderer reference. Prepare all
        // extra submeshes before changing the existing renderer, so failed allocation is reversible.
        var generated = new List<Entity>();
        try
        {
            for (int i = 1; i < model.Materials.Length; i++)
            {
                Entity child = manager.Instantiate(render);
                generated.Add(child);
                if (prefab) manager.AddComponent<Prefab>(child);
                ConfigureHallRenderer(manager, child, hall, original, i);
            }
            foreach (Entity child in generated) manager.GetBuffer<LinkedEntityGroup>(hall).Add(new LinkedEntityGroup { Value = child });
            ConfigureHallRenderer(manager, render, hall, original, 0);
        }
        catch
        {
            foreach (Entity child in generated) if (manager.Exists(child)) manager.DestroyEntity(child);
            var linked = manager.GetBuffer<LinkedEntityGroup>(hall);
            for (int i = linked.Length - 1; i >= 0; i--) if (generated.Contains(linked[i].Value)) linked.RemoveAt(i);
            if (manager.Exists(render)) manager.SetSharedComponentData(render, original);
            throw;
        }
    }

    private static void ConfigureHallRenderer(EntityManager manager, Entity child, Entity hall, RenderMesh original, int index)
    {
        var mesh = original; mesh.mesh = model!.Mesh; mesh.material = model.Materials[index]; mesh.subMesh = index;
        manager.SetSharedComponentData(child, mesh);
        Set(manager, child, new Parent { Value = hall });
        Set(manager, child, new LocalToParent { Value = float4x4.identity });
        Set(manager, child, new Translation { Value = float3.zero });
        Set(manager, child, new Rotation { Value = quaternion.identity });
        if (manager.HasComponent<NonUniformScale>(child)) manager.SetComponentData(child, new NonUniformScale { Value = new float3(1) });
        if (manager.HasComponent<Scale>(child)) manager.SetComponentData(child, new Scale { Value = 1 });
        Set(manager, child, new RenderBounds { Value = new AABB { Center = model.Mesh.bounds.center, Extents = model.Mesh.bounds.extents } });
    }

    private static void DisableCatalog(string message, Exception error)
    {
        catalogFailed = true;
        failure = VeteranText.Choose("Hall unavailable; see diagnostics.", "Salón no disponible; consulta el diagnóstico.");
        VeteranRuntime.Log.LogError(message + ": " + error);
    }

    private static void BeforeBuildingShop()
    {
        shopCategory = -1;
        if (!Available || !GameState.PrefabsMap.IsCreated || !GameState.PrefabsMap.ContainsKey(ObjectId)) return;
        try
        {
            BuildingsShopConfig source = GameState.CurrentBuildingsShop;
            if (source == null || source.Categories == null || GameState.IsUndead) return;
            foreach (var category in source.Categories)
                if (category.types != null && category.types.Any(t => t.buildingUniqueIds.Contains(ObjectId)))
                { shopCategory = category.index; return; }
            // Respect mission restrictions: only extend a shop which exposes the native University.
            if (!source.Categories.Any(c => c.types != null && c.types.Any(t => t.buildingUniqueIds.Contains(BaseObjectId)))) return;
            var used = new HashSet<int>(source.Categories.Select(c => c.index));
            int free = Enumerable.Range(0, 14).FirstOrDefault(i => i != 9 && !used.Contains(i));
            if (used.Contains(free)) throw new InvalidOperationException("No free native construction-category slot.");
            var copy = ScriptableObject.Instantiate(source);
            copy.name = "VeteranSoldiers_HallConstructionShop";
            copy.Categories = new List<BuildingsCategory>(source.Categories);
            copy.Categories.Add(new BuildingsCategory
            {
                index = free, Icon = HallVisuals.Icon,
                hintData = new HintData { Tittle = NameKey, Description = DescriptionKey, type = HintType.Full },
                types = new List<BuildingTypeContainer>
                {
                    new BuildingTypeContainer { type = BuildingType.University, buildingUniqueIds = new List<int> { ObjectId }, firstLevelUIIndex = 0, maxCount = 1 }
                }
            });
            GameState.CurrentBuildingsShop = copy;
            if (injectedShop != null && injectedShop != source) UnityEngine.Object.Destroy(injectedShop);
            injectedShop = copy;
            shopCategory = free;
        }
        catch (Exception error) { VeteranRuntime.Log.LogWarning("Hall construction menu was not extended: " + error.Message); }
    }

    internal static bool BeginConstruction(out string reason)
    {
        reason = "";
        if (!VeteranHallResearch.Compatible)
        { reason = VeteranText.Choose("Native research compatibility is unavailable; see diagnostics.", "La compatibilidad con la investigación nativa no está disponible; consulta el diagnóstico."); return false; }
        if (!VeteranRuntime.Ready || !Available || shopCategory < 0)
        { reason = VeteranText.Choose("The University must be available in this mission's construction menu.", "La Universidad debe estar disponible en el menú de construcción de esta misión."); return false; }
        // Open the genuine menu/category. Its button handles affordability, placement and mission restrictions.
        OpenConstructionCategory(shopCategory);
        return true;
    }

    internal static void OpenConstructionCategory(int category)
    {
        // OpenBuildingShop itself emits SelectConstructionCategory(-1). Dispatch the chosen
        // category only after that input batch has been consumed, otherwise two native commands
        // of the same type race and the category can close immediately again.
        pendingCategory = category;
        pendingCategoryWorld = VeteranRuntime.ActiveWorld;
        UIInputHandler.PlayerActionContainer.Add(new PlayerActionCallback(PlayerActionType.OpenBuildingShop));
    }

    private static void AfterNativeInput(UIInputHandler __instance)
    {
        int category = pendingCategory;
        if (category < 0) return;
        pendingCategory = -1;
        World? expected = pendingCategoryWorld;
        pendingCategoryWorld = null;
        if (!VeteranRuntime.Ready || !ReferenceEquals(expected, __instance.World) ||
            GameState.UIState.mainMenuOpened || GameState.UIState.researchWindowOpened ||
            GameState.UIState.missionPanelOpened || GameState.UIState.sandboxWavePanelOpened ||
            GameState.CommonState.inWinOrLoseProcess || GameState.CommonState.inputBlockedByInnerEventSystem) return;
        if (GameState.CurrentBuildingsShop?.Categories == null || !GameState.CurrentBuildingsShop.Categories.Any(c => c.index == category)) return;
        UIInputHandler.PlayerActionContainer.Add(new PlayerActionCallback(PlayerActionType.SelectConstructionCategory, category));
    }

    internal static bool CanDedicate(out string reason) => FindUniversityToDedicate(out _, out reason);

    private static bool FindUniversityToDedicate(out Entity university, out string reason)
    {
        university = Entity.Null;
        reason = "";
        if (!VeteranHallResearch.Compatible)
        { reason = VeteranText.Choose("Native research compatibility is unavailable; see diagnostics.", "La compatibilidad con la investigación nativa no está disponible; consulta el diagnóstico."); return false; }
        if (!VeteranRuntime.Ready || !Available || VeteranRuntime.ActiveWorld == null || !VeteranRuntime.ActiveWorld.IsCreated || GameState.IsUndead)
        { reason = VeteranText.Choose("The Hall is unavailable in this world.", "El Salón no está disponible en este mundo."); return false; }
        var manager = VeteranRuntime.Manager;
        using var query = manager.CreateEntityQuery(ComponentType.ReadOnly<BuildingBase>(), ComponentType.ReadOnly<Components.RawComponents.ObjectId>(),
            ComponentType.Exclude<Prefab>(), ComponentType.Exclude<Enemy>(), ComponentType.Exclude<CurrentBuildingForConstruction>());
        using var found = query.ToEntityArray(Allocator.Temp);
        int count = 0;
        foreach (Entity entity in found)
        {
            if (!VeteranRuntime.IsAlive(entity) || manager.HasComponent<BuildingDestroyRequest>(entity)) continue;
            int id = manager.GetComponentData<Components.RawComponents.ObjectId>(entity).value;
            if (id == ObjectId)
            { reason = VeteranText.Choose("A Hall already exists or is being built.", "Ya existe un Salón o se está construyendo."); return false; }
            if (manager.GetComponentData<BuildingBase>(entity).value.Value.type == BuildingType.University) count++;
            if (id == BaseObjectId && !manager.HasComponent<InConstruction>(entity) && manager.HasComponent<ResearchProducerData>(entity)) university = entity;
        }
        if (count != 1 || university == Entity.Null)
        { reason = VeteranText.Choose("Finish one ordinary University first. The University and Hall share a limit of one.", "Termina una Universidad primero. La Universidad y el Salón comparten un límite de uno."); return false; }
        if (!manager.HasComponent<LinkedEntityGroup>(university))
        { reason = VeteranText.Choose("This University has no compatible native visual group.", "Esta Universidad no tiene un grupo visual nativo compatible."); return false; }
        return true;
    }

    internal static bool TryDedicate(out string reason)
    {
        if (!FindUniversityToDedicate(out Entity university, out reason)) return false;
        try
        {
            var manager = VeteranRuntime.Manager;
            manager.CompleteAllJobs();
            ApplyHallModel(manager, university, false);
            manager.SetComponentData(university, new Components.RawComponents.ObjectId { value = ObjectId });
            if (!halls.Contains(university)) halls.Add(university);
            nextScan = 0;
            foreach (GameStateUpdateTaskType task in Enum.GetValues(typeof(GameStateUpdateTaskType))) GameStateUpdateTasksUtility.UpdateGameState(manager, task);
            VeteranRuntime.Log.LogInfo("Existing University dedicated as Hall; exact entity, researchers, health and native orders retained; no resources charged.");
            return true;
        }
        catch (Exception error)
        {
            VeteranRuntime.Log.LogError("University dedication failed: " + error);
            reason = VeteranText.Choose("Dedication failed; see diagnostics.", "La dedicación falló; consulta el diagnóstico.");
            return false;
        }
    }

    private static void RestrictHallCount()
    {
        if (!Available || shopCategory < 0) return;
        if (!VeteranHallResearch.Compatible || GameState.ObjectsDataState.ResearchLocksByObjectID.Contains(BaseObjectId))
            GameState.PlayerActionState.AddRestriction(PlayerActionType.ConstructionSelectBuilding, ObjectId, ActionRestrictionType.ResearchRestricted);
        // Both menu paths share the native research producer singleton limit.
        GameState.ObjectsDataState.BuildingsMaxCountsByType[(byte)BuildingType.University] = 1;
        if (GameState.ObjectsDataState.BuildingLimitReached(ObjectId))
            GameState.PlayerActionState.AddRestriction(PlayerActionType.ConstructionSelectBuilding, ObjectId, ActionRestrictionType.CountRestricted);
    }

    internal static bool IsHall(Entity entity) => Available && VeteranRuntime.ActiveWorld != null && VeteranRuntime.ActiveWorld.IsCreated &&
        VeteranRuntime.Manager.Exists(entity) && VeteranRuntime.Manager.HasComponent<Components.RawComponents.ObjectId>(entity) &&
        VeteranRuntime.Manager.GetComponentData<Components.RawComponents.ObjectId>(entity).value == ObjectId;

    private static bool IsCompleted(Entity entity) => IsHall(entity) && VeteranRuntime.IsAlive(entity) &&
        !VeteranRuntime.Manager.HasComponent<Enemy>(entity) && !VeteranRuntime.Manager.HasComponent<InConstruction>(entity) &&
        !VeteranRuntime.Manager.HasComponent<CurrentBuildingForConstruction>(entity) && !VeteranRuntime.Manager.HasComponent<BuildingDestroyRequest>(entity);

    internal static bool IsSelectedHall()
    {
        foreach (Entity entity in halls)
            if (IsHall(entity) && VeteranRuntime.Manager.HasComponent<Selected>(entity)) return true;
        return false;
    }

    internal static void Tick()
    {
        if (!VeteranRuntime.Ready || VeteranRuntime.ActiveWorld == null || !VeteranRuntime.ActiveWorld.IsCreated) return;
        var world = VeteranRuntime.ActiveWorld;
        EnsureRegistered(world);
        if (!Available) return;
        if (!ReferenceEquals(queryWorld, world))
        {
            if (queryValid && queryWorld != null && queryWorld.IsCreated) buildings.Dispose();
            queryWorld = world;
            buildings = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<BuildingBase>(), ComponentType.ReadOnly<Components.RawComponents.ObjectId>(), ComponentType.Exclude<Prefab>(), ComponentType.Exclude<Enemy>());
            queryValid = true;
            nextScan = 0;
        }
        if (Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + .5f;
            world.EntityManager.CompleteAllJobs();
            halls.Clear();
            OrdinaryUniversityCount = 0;
            using var found = buildings.ToEntityArray(Allocator.Temp);
            foreach (Entity entity in found)
            {
                if (!VeteranRuntime.IsAlive(entity) || world.EntityManager.HasComponent<CurrentBuildingForConstruction>(entity) || world.EntityManager.HasComponent<BuildingDestroyRequest>(entity)) continue;
                if (IsHall(entity)) halls.Add(entity);
                else if (world.EntityManager.GetComponentData<Components.RawComponents.ObjectId>(entity).value == BaseObjectId) OrdinaryUniversityCount++;
            }
            if (IsOperational)
            {
                using var research = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ResearchContainerSingleton>());
                if (research.CalculateEntityCount() == 1)
                {
                    // A genuinely completed Hall is also a University for native research prerequisites.
                    var completed = world.EntityManager.GetComponentObject<ResearchContainerSingleton>(research.GetSingletonEntity()).Container.CompleteBuildings;
                    if (completed.IsCreated && !completed.Contains(BaseObjectId))
                    {
                        if (completed.Capacity < completed.Count() + 1) completed.Capacity = completed.Count() + 1;
                        completed.Add(BaseObjectId);
                        GameStateUpdateTasksUtility.UpdateGameState(world.EntityManager, GameStateUpdateTaskType.Researches);
                    }
                }
            }
        }
        if (PendingDoctrine == HallDoctrine.None) { lastSessionTime = -1; return; }
        using var clock = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<CurrentSessionTimeSingleton>());
        if (clock.CalculateEntityCount() != 1) return;
        float now = clock.GetSingleton<CurrentSessionTimeSingleton>().elapsedTime;
        double delta = lastSessionTime < 0 ? 0 : Math.Max(0, now - lastSessionTime);
        lastSessionTime = now;
        researchProgress = HallRules.Advance(researchProgress, delta, IsOperational);
        if (researchProgress >= HallRules.ResearchSeconds)
        {
            ActiveDoctrine = PendingDoctrine;
            PendingDoctrine = HallDoctrine.None;
            researchProgress = 0;
            VeteranRuntime.Log.LogInfo("Hall doctrine completed: " + ActiveDoctrine);
        }
    }

    internal static void ResetSession()
    {
        ActiveDoctrine = PendingDoctrine = HallDoctrine.None;
        researchProgress = CombatExperience = 0;
        unlocked = 0;
        lastSessionTime = -1;
        nextScan = 0;
        pendingCategory = -1;
        pendingCategoryWorld = null;
        loadResearchFallback = false;
        halls.Clear();
        OrdinaryUniversityCount = 0;
        trainingPrefabIds.Clear();
        trainingPrefabCount = -1;
    }

    internal static void RecordCombatExperience(Entity source, double amount)
    {
        if (!VeteranRuntime.Ready || !VeteranRuntime.IsEligible(source) || VeteranRuntime.Manager.HasComponent<Enemy>(source) || !Progression.Finite(amount) || amount <= 0) return;
        CombatExperience = Math.Min(1e12, CombatExperience + amount);
        for (int i = 1; i <= 3; i++) if (CombatExperience >= HallRules.ExperienceThreshold((HallDoctrine)i)) unlocked |= HallRules.Mask((HallDoctrine)i);
    }

    internal static double ExperienceMultiplier(Entity source) => ActiveDoctrine == HallDoctrine.Tradition && IsOperational &&
        VeteranRuntime.IsEligible(source) && !VeteranRuntime.Manager.HasComponent<Enemy>(source) ? 1.1 : 1;

    internal static void BeforeHealthRecovery(Entity requestEntity, ref HealthChangeRequest request)
    {
        if (!(request.Value > 0) || !Progression.Finite(request.Value) || ActiveDoctrine != HallDoctrine.Recovery || !IsOperational) return;
        var manager = VeteranRuntime.Manager;
        if (!manager.HasComponent<CombatSource>(requestEntity)) return;
        var tag = manager.GetComponentData<CombatSource>(requestEntity);
        if (tag.Processed != 0) return;
        tag.Processed = 1;
        manager.SetComponentData(requestEntity, tag);
        if (!VeteranRuntime.IsAlive(tag.Source) || manager.HasComponent<Enemy>(tag.Source) || !manager.HasComponent<UnitBase>(tag.Source) ||
            !VeteranRuntime.TryGetExperience(request.Target, out double xp) || manager.HasComponent<Enemy>(request.Target) ||
            Progression.Level(xp, VeteranRuntime.Settings.Ranks, VeteranRuntime.Settings.MaxLevel.Value) < 2) return;
        UnitType type = manager.GetComponentData<UnitBase>(tag.Source).value.Value.type;
        if (type != UnitType.Doctor && type != UnitType.NativesPlayerHealer) return;
        request.Value = (float)Math.Min(float.MaxValue, (double)request.Value * 1.15);
        manager.SetComponentData(requestEntity, request);
    }

    internal static string DoctrineName(HallDoctrine value) => value == HallDoctrine.Tradition ? VeteranText.Choose("Military tradition", "Tradición militar") :
        value == HallDoctrine.Replacements ? VeteranText.Choose("Replacement corps", "Cuerpo de reemplazos") : value == HallDoctrine.Recovery ? VeteranText.Choose("Veteran recovery", "Recuperación veterana") : VeteranText.Choose("None", "Ninguna");
    internal static string DoctrineDescription(HallDoctrine value) => value == HallDoctrine.Tradition ? VeteranText.Choose("+10% experience earned through effective hostile combat.", "+10 % de experiencia obtenida por combate hostil efectivo.") :
        value == HallDoctrine.Replacements ? VeteranText.Choose("15% less human troop training time, combined with existing research.", "15 % menos tiempo de entrenamiento de tropas humanas, combinado con la investigación existente.") :
        VeteranText.Choose("+15% healing from doctors and allied healers received by Veteran rank or higher. No passive healing.", "+15 % de curación de médicos y sanadores aliados para rango Veterano o superior. Sin curación pasiva.");
    internal static string DoctrineRequirement(HallDoctrine value) => VeteranText.Number(CombatExperience, "N0") + " / " +
        VeteranText.Number(HallRules.ExperienceThreshold(value), "N0") + VeteranText.Choose(" combat XP", " XP de combate");
    internal static string DoctrineCostText(HallDoctrine value) => value == HallDoctrine.Tradition ? VeteranText.Choose("100 wood · 50 stone · 60 s", "100 madera · 50 piedra · 60 s") :
        value == HallDoctrine.Replacements ? VeteranText.Choose("150 wood · 50 iron · 60 s", "150 madera · 50 hierro · 60 s") : VeteranText.Choose("100 food · 50 iron · 60 s", "100 comida · 50 hierro · 60 s");
    internal static PriceBaseData DoctrinePrice(HallDoctrine value) => value == HallDoctrine.Tradition ? new PriceBaseData { woodPrice = 100, stonePrice = 50 } :
        value == HallDoctrine.Replacements ? new PriceBaseData { woodPrice = 150, ironPrice = 50 } : new PriceBaseData { foodPrice = 100, ironPrice = 50 };

    internal static DoctrineBlocker DoctrineBlock(HallDoctrine choice)
    {
        bool operational = IsOperational;
        bool enough = !operational || !HallRules.Valid(choice) || choice == HallDoctrine.None ||
            GameState.ResourceState.AsResourcesContainer.Enough(DoctrinePrice(choice));
        return HallRules.Blocker(choice, VeteranHallResearch.Compatible, operational, PendingDoctrine, ActiveDoctrine, unlocked, enough);
    }
    internal static string DoctrineBlockLabel(HallDoctrine choice)
    {
        switch (DoctrineBlock(choice))
        {
            case DoctrineBlocker.Research: return VeteranText.Choose("Research compatibility unavailable", "Investigación no compatible");
            case DoctrineBlocker.Hall:
                if (HallCount > 0) return VeteranText.Choose("Finish the Hall", "Termina el Salón");
                if (CanDedicate(out _)) return VeteranText.Choose("Dedicate your University for free", "Dedica tu Universidad gratis");
                if (OrdinaryUniversityCount > 0) return VeteranText.Choose("Finish your University", "Termina tu Universidad");
                if (RequiredTownHallLevel > GameState.CommonState.townHallLevel)
                    return VeteranText.Choose("Requires Town Hall level ", "Requiere Ayuntamiento nivel ") + RequiredTownHallLevel;
                return VeteranText.Choose("Build a Hall first", "Construye un Salón primero");
            case DoctrineBlocker.Preparing: return VeteranText.Choose("Another doctrine is being prepared", "Otra doctrina está en preparación");
            case DoctrineBlocker.Active: return VeteranText.Choose("Already active", "Ya está activa");
            case DoctrineBlocker.Experience: return VeteranText.Choose("Earn more combat XP", "Gana más experiencia de combate");
            case DoctrineBlocker.Resources: return VeteranText.Choose("Not enough resources", "Recursos insuficientes");
            default: return "";
        }
    }
    internal static bool CanStartDoctrine(HallDoctrine choice, out string reason)
    {
        var blocked = DoctrineBlock(choice);
        reason = blocked == DoctrineBlocker.Invalid ? VeteranText.Choose("Invalid doctrine.", "Doctrina no válida.") :
            blocked == DoctrineBlocker.Research ? VeteranText.Choose("Native research compatibility is unavailable; see diagnostics.", "La compatibilidad con la investigación nativa no está disponible; consulta el diagnóstico.") :
            blocked == DoctrineBlocker.Hall ? MissingHallReason :
            blocked == DoctrineBlocker.Experience ? DoctrineBlockLabel(choice) + ": " + DoctrineRequirement(choice) : DoctrineBlockLabel(choice);
        return blocked == DoctrineBlocker.None;
    }

    internal static bool TryStartDoctrine(HallDoctrine choice, out string reason)
    {
        if (!CanStartDoctrine(choice, out reason)) return false;
        var world = VeteranRuntime.ActiveWorld;
        if (world == null || !world.IsCreated)
        { reason = VeteranText.Choose("The game simulation is unavailable.", "La simulación no está disponible."); return false; }
        try
        {
            world.EntityManager.CompleteAllJobs();
            var system = world.GetExistingSystem<VeteranBootstrapSystem>();
            if (system == null) { reason = VeteranText.Choose("The game simulation is unavailable.", "La simulación no está disponible."); return false; }
            var resources = new NativeReference<ResourcesChangeTracker>(Allocator.TempJob);
            using var changes = new EntityCommandBuffer(Allocator.TempJob);
            try
            {
                system.ScheduleBeginUpdateResource(resources, default).Complete();
                var tracker = resources.Value;
                // Recheck real current resources at the transaction boundary, not only the HUD snapshot.
                if (!tracker.SpentCurrent(DoctrinePrice(choice))) { reason = VeteranText.Choose("Not enough resources.", "Recursos insuficientes."); return false; }
                resources.Value = tracker;
                system.ScheduleEndUpdateResource(resources, changes, default).Complete();
                changes.Playback(world.EntityManager);
            }
            finally { resources.Dispose(); }
            PendingDoctrine = choice;
            researchProgress = 0;
            lastSessionTime = -1;
            return true;
        }
        catch (Exception error)
        {
            VeteranRuntime.Log.LogError("Could not start Hall doctrine transaction: " + error);
            reason = VeteranText.Choose("Doctrine could not start. See diagnostics before retrying.", "No se pudo iniciar la doctrina. Consulta el diagnóstico antes de reintentar.");
            return false;
        }
    }

    internal static HallSnapshot Capture(NativeParallelHashMap<Entity, EntityMappingRef> mapping)
    {
        var result = new HallSnapshot { ActiveDoctrine = (int)ActiveDoctrine, PendingDoctrine = (int)PendingDoctrine,
            ResearchProgress = researchProgress, UnlockedDoctrines = unlocked, CombatExperience = CombatExperience };
        using var entities = mapping.GetKeyArray(Allocator.Temp);
        foreach (Entity entity in entities)
        {
            if (!IsHall(entity)) continue;
            if (result.Buildings.Count >= HallRules.MaximumHalls)
            {
                VeteranRuntime.Log.LogWarning("Hall identity limit exceeded outside the normal construction menu; excess Halls are saved as ordinary Universities.");
                break;
            }
            result.Buildings.Add(new HallBuildingSnapshot { ReferenceId = mapping[entity].referenceId, BaseObjectId = BaseObjectId });
        }
        return result;
    }

    internal static void NormalizeSavedBuildings(ref SaveContainer save)
    {
        if (!Available) return;
        if (save.buildings != null)
            for (int i = 0; i < save.buildings.Length; i++)
                if (save.buildings[i].objectId == ObjectId) save.buildings[i].objectId = BaseObjectId;
        // No unknown custom building IDs remain in the native research history either.
        if (save.research.completeBuildings != null)
            save.research.completeBuildings = save.research.completeBuildings.Select(id => id == ObjectId ? BaseObjectId : id).Distinct().ToArray();
        if (save.freeBuildings != null) save.freeBuildings = save.freeBuildings.Where(item => item.x != ObjectId).ToArray();
    }

    internal static void PrepareLoadBuildings(HallSnapshot snapshot, ref SaveContainer save)
    {
        if (VeteranRuntime.ActiveWorld != null) EnsureRegistered(VeteranRuntime.ActiveWorld, true);
        // GameState.PrefabsMap is initialized later during a fresh load. Registration uses the
        // world's actual PrefabsMapSingleton, the same map consumed by GameWorldLoaderSystem.
        if (!Available || save.buildings == null) return;
        loadResearchFallback = VeteranRuntime.ActiveWorld == null || !VeteranHallResearch.ValidateForLoad(VeteranRuntime.ActiveWorld);
        if (loadResearchFallback) return;
        var byReference = snapshot.Buildings.ToDictionary(b => b.ReferenceId);
        var saved = save.buildings.ToDictionary(b => b.referenceId);
        // Validate the entire exact-save mapping before changing a single in-memory prefab ID.
        foreach (var hall in snapshot.Buildings)
            if (hall.BaseObjectId != BaseObjectId || !saved.TryGetValue(hall.ReferenceId, out var building) || building.objectId != BaseObjectId)
                throw new InvalidDataException("Hall reference does not match the saved native University.");
        for (int i = 0; i < save.buildings.Length; i++)
            if (byReference.TryGetValue(save.buildings[i].referenceId, out var hall) && hall.BaseObjectId == BaseObjectId && save.buildings[i].objectId == BaseObjectId)
                save.buildings[i].objectId = ObjectId;
    }

    internal static void Restore(HallSnapshot snapshot, NativeParallelHashMap<int, Entity> mapping)
    {
        if (!Available) return;
        if (loadResearchFallback) { ResetSession(); return; }
        var loaded = new List<Entity>();
        foreach (var entry in snapshot.Buildings)
        {
            if (entry.BaseObjectId != BaseObjectId || !mapping.TryGetValue(entry.ReferenceId, out Entity entity) || !IsHall(entity))
                throw new InvalidDataException("Hall building was not restored from its verified save reference.");
            loaded.Add(entity);
        }
        ResetSession();
        ActiveDoctrine = (HallDoctrine)snapshot.ActiveDoctrine;
        PendingDoctrine = (HallDoctrine)snapshot.PendingDoctrine;
        researchProgress = snapshot.ResearchProgress;
        CombatExperience = snapshot.CombatExperience;
        unlocked = snapshot.UnlockedDoctrines;
        halls.AddRange(loaded);
        nextScan = 0;
    }

    private sealed class TrainingTransaction
    {
        internal NativeParallelHashMap<int, float> Map;
        internal readonly Dictionary<int, float?> Before = new Dictionary<int, float?>();
        internal bool Restored;
    }

    [HarmonyPriority(Priority.Last)]
    private static void BeforeTraining(ArmyTrainingSystem __instance, bool __runOriginal, out TrainingTransaction? __state)
    {
        __state = null;
        if (!__runOriginal || ActiveDoctrine != HallDoctrine.Replacements || !IsOperational || !ReferenceEquals(__instance.World, VeteranRuntime.ActiveWorld)) return;
        // This exact native query schedules ProcessTrainersJob. Newly queued training
        // buffers are added through its deferred command buffer, so an empty query
        // cannot consume the modifier until a following update.
        if (trainingIdleGuard?.IsEmpty(__instance) == true) return;
        try
        {
            __instance.EntityManager.CompleteAllJobs();
            using var query = __instance.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ResearchContainerSingleton>());
            if (query.CalculateEntityCount() != 1) return;
            var research = __instance.EntityManager.GetComponentObject<ResearchContainerSingleton>(query.GetSingletonEntity());
            var state = new TrainingTransaction { Map = research.Container.TrainingTimeModify };
            __state = state;
            int count = GameState.PrefabsMap.Count();
            if (trainingPrefabCount != count)
            {
                trainingPrefabIds.Clear();
                using var units = __instance.EntityManager.CreateEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Unit>(), ComponentType.ReadOnly<Components.RawComponents.ObjectId>(), ComponentType.ReadOnly<Prefab>() }, None = new[] { ComponentType.ReadOnly<Enemy>() }, Options = EntityQueryOptions.IncludePrefab });
                using var entities = units.ToEntityArray(Allocator.Temp);
                foreach (Entity entity in entities)
                {
                    int id = __instance.EntityManager.GetComponentData<Components.RawComponents.ObjectId>(entity).value;
                    if (!trainingPrefabIds.Contains(id)) trainingPrefabIds.Add(id);
                }
                trainingPrefabCount = count;
            }
            if (state.Map.Capacity < state.Map.Count() + trainingPrefabIds.Count) state.Map.Capacity = state.Map.Count() + trainingPrefabIds.Count;
            foreach (int id in trainingPrefabIds)
            {
                bool exists = state.Map.TryGetValue(id, out float original);
                if (float.IsNaN(original) || float.IsInfinity(original)) continue;
                state.Before.Add(id, exists ? original : (float?)null);
                state.Map[id] = HallRules.ComposeTraining(original);
            }
        }
        catch (Exception error) { RestoreTraining(__instance, __state); VeteranRuntime.Log.LogWarning("Hall training modifier skipped: " + error.Message); }
    }

    [HarmonyPriority(Priority.First)]
    private static void AfterTraining(ArmyTrainingSystem __instance, TrainingTransaction? __state) => RestoreTraining(__instance, __state);
    private static Exception? FinalizeTraining(ArmyTrainingSystem __instance, TrainingTransaction? __state, Exception? __exception)
    { RestoreTraining(__instance, __state); return __exception; }
    private static void RestoreTraining(ArmyTrainingSystem system, TrainingTransaction? state)
    {
        if (state == null || state.Restored) return;
        Complete(system);
        foreach (var pair in state.Before)
        {
            float expected = HallRules.ComposeTraining(pair.Value ?? 0);
            // Preserve a deliberate third-party change made after our prefix.
            if (!state.Map.TryGetValue(pair.Key, out float current) || current != expected) continue;
            if (pair.Value.HasValue) state.Map[pair.Key] = pair.Value.Value;
            else state.Map.Remove(pair.Key);
        }
        state.Restored = true;
    }
}

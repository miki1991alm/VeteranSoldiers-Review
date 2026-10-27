#if VETERAN_TEST_HARNESS
using System;
using System.Collections.Generic;
using System.Linq;
using Components;
using Components.Commands.ConstructionCommands;
using Components.RawComponents;
using Components.SingletonComponents;
using Newtonsoft.Json.Linq;
using HarmonyLib;
using Systems.GameStateSystems;
using Systems.InputSystems;
using Systems.InputSystems.PlayerActionGroups;
using UI;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using Utility;
using Utility.EnumsStorage;

namespace VeteranSoldiers;

internal static partial class RuntimeHallTest
{
    private static World? constructionInputWorld;
    private static float2? constructionPointer;
    private static float constructionPointerUntil;
    private static PlayerActionType queuedConstructionAction=PlayerActionType.Non;
    private static int queuedConstructionParameter=-1;
    private static object? constructionInputEvidence;

    private static void InitializeConstruction(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(UIInputHandler),"OnUpdateSimulation"),
            new HarmonyMethod(typeof(RuntimeHallTest),nameof(BeforeConstructionInput)),
            new HarmonyMethod(typeof(RuntimeHallTest),nameof(AfterConstructionInput)),
            finalizer:new HarmonyMethod(typeof(RuntimeHallTest),nameof(FinalizeConstructionInput)));
    }

    private sealed class ConstructionInputScope
    {
        internal float2 Ground;
        internal bool Minimap,Restored;
        internal PlayerActionType Action;
    }

    private static void SetConstructionPointer(float2 point)
    {
        if(!math.all(math.isfinite(point)) || !GameState.CommonState.inConstruction)
            throw new InvalidOperationException("A real native construction preview and finite point are required.");
        constructionInputWorld=VeteranRuntime.ActiveWorld;
        constructionPointer=point;
        constructionPointerUntil=Time.unscaledTime+30;
        QueueConstructionAction(PlayerActionType.ConstructionMove);
    }

    private static void QueueConstructionAction(PlayerActionType action,int parameter=-1)
    {
        constructionInputWorld=VeteranRuntime.ActiveWorld;
        queuedConstructionAction=action;
        queuedConstructionParameter=parameter;
        constructionInputEvidence=new{Action=action.ToString(),Status="Queued for native input; no construction success inferred."};
    }

    private static void BeforeConstructionInput(UIInputHandler __instance,out ConstructionInputScope? __state)
    {
        __state=null;
        if(!ReferenceEquals(constructionInputWorld,__instance.World))return;
        if(!VeteranRuntime.Ready){constructionPointer=null;queuedConstructionAction=PlayerActionType.Non;return;}
        if(constructionPointer.HasValue && (Time.unscaledTime>constructionPointerUntil || !GameState.CommonState.inConstruction))constructionPointer=null;
        PlayerActionType action=queuedConstructionAction;
        if(!constructionPointer.HasValue && action==PlayerActionType.Non)return;
        __state=new ConstructionInputScope{Ground=PlayerInputSystem.PlayerInputState.groundPosition,Minimap=PlayerInputSystem.PlayerInputState.isOverMinimap,Action=action};
        if(constructionPointer.HasValue)
        {
            // Synthetic pointer only. The native construction system still validates terrain,
            // fog, occupancy, limits, workforce and payment using the requested position.
            PlayerInputSystem.PlayerInputState.groundPosition=constructionPointer.Value;
            PlayerInputSystem.PlayerInputState.isOverMinimap=false;
            UIInputHandler.PlayerActionContainer.PlayerActions.Remove(PlayerActionType.ConstructionMove);
            UIInputHandler.PlayerActionContainer.Add(PlayerActionType.ConstructionMove);
        }
        if(action!=PlayerActionType.Non)
        {
            UIInputHandler.PlayerActionContainer.PlayerActions.Remove(action);
            UIInputHandler.PlayerActionContainer.Add(new PlayerActionCallback(action,queuedConstructionParameter));
            queuedConstructionAction=PlayerActionType.Non;
        }
    }

    private static void RestoreConstructionInput(ConstructionInputScope? scope)
    {
        if(scope==null || scope.Restored)return;
        PlayerInputSystem.PlayerInputState.groundPosition=scope.Ground;
        PlayerInputSystem.PlayerInputState.isOverMinimap=scope.Minimap;
        scope.Restored=true;
    }

    private static void AfterConstructionInput(UIInputHandler __instance,ConstructionInputScope? __state)
    {
        RestoreConstructionInput(__state);
        if(__state==null || __state.Action==PlayerActionType.Non)return;
        bool emitted=false;
        ComponentType command=__state.Action==PlayerActionType.ConstructionBuild?ComponentType.ReadOnly<ConstructionBuildCommand>():
            __state.Action==PlayerActionType.ConstructionCancel?ComponentType.ReadOnly<ConstructionCancelCommand>():
            __state.Action==PlayerActionType.ConstructionSelectBuilding?ComponentType.ReadOnly<ConstructionSelectCommand>():ComponentType.ReadOnly<ConstructionMoveCommand>();
        using(var query=__instance.EntityManager.CreateEntityQuery(command))emitted=query.CalculateEntityCount()==1;
        constructionInputEvidence=new{Action=__state.Action.ToString(),NativeCommandEmitted=emitted,
            Status=emitted?"Native command emitted; verify actual building and resource delta separately.":"Native input filtered the request; no successful construction claimed."};
        if(__state.Action==PlayerActionType.ConstructionBuild || __state.Action==PlayerActionType.ConstructionCancel)constructionPointer=null;
    }

    private static Exception? FinalizeConstructionInput(Exception? __exception,ConstructionInputScope? __state)
    {RestoreConstructionInput(__state);return __exception;}

    private static bool ExecuteConstruction(string action, JObject command, float3 origin)
    {
        switch (action)
        {
            case "hall_townhall_select":
                Entity townHall = FindTownHall();
                using (var selected = Manager.CreateEntityQuery(ComponentType.ReadOnly<Selected>())) Manager.RemoveComponent<Selected>(selected);
                Manager.AddComponent<Selected>(townHall);
                GameStateUpdateTasksUtility.UpdateGameState(Manager, GameStateUpdateTaskType.Selection);
                return true;
            case "hall_townhall_upgrade":
                if (!Manager.HasComponent<Selected>(FindTownHall()) || !GameState.PlayerActionState.IsAvailable(PlayerActionType.UpgradeBuilding))
                    throw new InvalidOperationException("Select the Town Hall and satisfy its native upgrade requirements first; no unlock is bypassed.");
                UIInputHandler.PlayerActionContainer.Add(new PlayerActionCallback(PlayerActionType.UpgradeBuilding));
                return true;
            case "hall_shop_select":
                int objectId = (int?)command["ObjectId"] ?? VeteranHall.ObjectId;
                if (!GameState.PrefabsMap.ContainsKey(objectId) || !Manager.HasComponent<BuildingBase>(GameState.PrefabsMap[objectId]))
                    throw new InvalidOperationException("Requested construction prefab is not registered.");
                var callback = new PlayerActionCallback(PlayerActionType.ConstructionSelectBuilding, objectId);
                if (!GameState.PlayerActionState.IsAvailable(callback)) throw new InvalidOperationException("The native construction menu currently restricts this building.");
                QueueConstructionAction(PlayerActionType.ConstructionSelectBuilding,objectId);
                return true;
            case "hall_shop_menu":
                int menuObjectId = (int?)command["ObjectId"] ?? VeteranHall.ObjectId;
                var category = GameState.CurrentBuildingsShop.Categories.FirstOrDefault(c => c.types.Any(t => t.buildingUniqueIds.Contains(menuObjectId)));
                if (category.types == null) throw new InvalidOperationException("The requested building is not in this mission's native shop.");
                VeteranHall.OpenConstructionCategory(category.index);
                return true;
            case "hall_speed":
                int speed = (int?)command["Speed"] ?? 3;
                if (speed < 1 || speed > 3) throw new ArgumentException("Native speed must be 1, 2 or 3.");
                var task = speed == 1 ? TimeScaleTaskType.FirstSpeed : speed == 2 ? TimeScaleTaskType.SecondSpeed : TimeScaleTaskType.ThirdSpeed;
                UIInputHandler.PlayerActionContainer.Add(new PlayerActionCallback(PlayerActionType.GameTimeControl, (int)task));
                return true;
            default: return false;
        }
    }

    private static Entity FindTownHall()
    {
        using var query = Manager.CreateEntityQuery(ComponentType.ReadOnly<BuildingBase>(), ComponentType.Exclude<Prefab>(), ComponentType.Exclude<Enemy>());
        using var entities = query.ToEntityArray(Allocator.Temp);
        foreach (Entity entity in entities)
            if (Manager.GetComponentData<BuildingBase>(entity).value.Value.type == BuildingType.TownHall && VeteranRuntime.IsAlive(entity)) return entity;
        throw new InvalidOperationException("No living native Town Hall is available.");
    }

    private static object ConstructionSnapshot
    {
        get
        {
            var buildings = new List<object>();
            var trainers = new List<object>();
            foreach(var category in GameState.CurrentBuildingsShop.Categories)
            foreach(var type in category.types)
            foreach(int id in type.buildingUniqueIds)
            {
                if(!GameState.PrefabsMap.TryGetValue(id,out Entity trainer) || !Manager.HasComponent<UnitTrainer>(trainer) || !Manager.HasComponent<BuildingBase>(trainer))continue;
                var definition=Manager.GetComponentData<BuildingBase>(trainer).value.Value;
                trainers.Add(new{ObjectId=id,Type=definition.type.ToString(),definition.level,Category=category.index,
                    Available=GameState.PlayerActionState.IsAvailable(new PlayerActionCallback(PlayerActionType.ConstructionSelectBuilding,id))});
            }
            using var query = Manager.CreateEntityQuery(ComponentType.ReadOnly<BuildingBase>(), ComponentType.ReadOnly<Components.RawComponents.ObjectId>(), ComponentType.Exclude<Prefab>(), ComponentType.Exclude<Enemy>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            foreach (Entity entity in entities)
            {
                var data = Manager.GetComponentData<BuildingBase>(entity).value.Value;
                if (data.type != BuildingType.TownHall && data.type != BuildingType.BuildersHouse && data.type != BuildingType.University && !Manager.HasComponent<UnitTrainer>(entity) && !Manager.HasComponent<ConstructionWorkCollector>(entity)) continue;
                object? nextPrice = null;
                if (Manager.HasComponent<NextLevelBase>(entity))
                {
                    int next = Manager.GetComponentData<NextLevelBase>(entity).value.Value.objectId;
                    if (GameState.PrefabsMap.TryGetValue(next, out Entity prefab) && Manager.HasComponent<PriceBase>(prefab))
                    {
                        var price = Manager.GetComponentData<PriceBase>(prefab).Value(false) - Manager.GetComponentData<PriceBase>(entity).Value(false);
                        nextPrice = new { ObjectId = next, price.woodPrice, price.stonePrice, price.ironPrice, price.foodPrice, price.moneyPrice, price.peoplePrice };
                    }
                }
                var work = Manager.HasComponent<ConstructionWorkCollector>(entity) ? Manager.GetComponentData<ConstructionWorkCollector>(entity) : default;
                buildings.Add(new { Entity = entity.ToString(), ObjectId = Manager.GetComponentData<Components.RawComponents.ObjectId>(entity).value, Type = data.type.ToString(), data.level,
                    Position = Manager.HasComponent<Translation>(entity) ? Manager.GetComponentData<Translation>(entity).Value.ToString() : "", Selected = Manager.HasComponent<Selected>(entity),
                    InConstruction = Manager.HasComponent<InConstruction>(entity), InUpgrade = Manager.HasComponent<InUpgrade>(entity), InRepair = Manager.HasComponent<InRepair>(entity),
                    work.WorkAmountDone, work.WorkAmountRequired, work.WorkersCount, NextUpgrade = nextPrice });
            }
            using var builders = Manager.CreateEntityQuery(ComponentType.ReadOnly<Builder>(), ComponentType.Exclude<Prefab>(), ComponentType.Exclude<Enemy>());
            return new { TownHallLevel = GameState.CommonState.townHallLevel, UpgradeAllowed = GameState.PlayerActionState.IsAvailable(PlayerActionType.UpgradeBuilding),
                Input=constructionInputEvidence,PointerOverride=constructionPointer?.ToString(),InConstructionMode=GameState.CommonState.inConstruction,
                Builders = builders.CalculateEntityCount(), Buildings = buildings, TrainingCatalog=trainers, ResourceFunding = "Explicit hall_resources grants are synthetic; upgrades, costs and builder labor remain native." };
        }
    }
}
#endif

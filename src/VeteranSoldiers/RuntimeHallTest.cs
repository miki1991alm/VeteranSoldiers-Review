#if VETERAN_TEST_HARNESS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using Components;
using Components.Commands;
using Components.Commands.ConstructionCommands;
using Components.RawComponents;
using Components.SingletonComponents;
using Components.Structs;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ScriptableObjectDefinitions;
using ScriptableObjectDefinitions.ConfigOverrides;
using Systems;
using Systems.GameStateSystems;
using Systems.InputSystems.PlayerActionGroups;
using UI;
using UI.MainMenuScripts;
using UI.Sandbox;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using Utility;
using Utility.EnumsStorage;

namespace VeteranSoldiers;

/// <summary>Explicit operator-driven isolated QA. Never compiled into public DLLs.</summary>
internal static partial class RuntimeHallTest
{
    private static bool initialized;
    private static string saveName="",lastAction="",lastError="";
    private static object? savedPayload;
    private static readonly Dictionary<int,int> savedHallReferences=new Dictionary<int,int>();
    private static object? loadedReferences;
    private static Entity healingTarget;
    private static float healingBefore;
    private static float nextTick;
    private static EntityManager Manager => VeteranRuntime.Manager;

    internal static bool MenuLocalizationReady()
    {
        // Sandbox.Show builds localized unit cards immediately. Native localization loads each
        // table asynchronously, and an existing table can still lack its SharedTableData asset.
        // Inspect readiness only; never open/retry a partially initialized menu.
        var tables=AccessTools.Field(typeof(LocalizationStrings),"currentTables").GetValue(null) as System.Collections.IDictionary;
        if(tables==null || VeteranHall.BaseObjectId<=0)return false;
        foreach(string key in new[]{"UI","Missions","Hints","Buildings","Units","Environments","Diseases","Researches","Tutorial","CutScenes"})
        {
            if(!tables.Contains(key) || !(tables[key] is UnityEngine.Object table) || table==null)return false;
            var property=AccessTools.Property(table.GetType(),"SharedData");
            if(property==null || !(property.GetValue(table,null) is UnityEngine.Object shared) || shared==null)return false;
        }
        return true;
    }

    internal static void StartMission()
    {
        Guard();
        if(!MenuLocalizationReady())throw new InvalidOperationException("Native Sandbox localization is not ready; menu was not opened.");
        if (!Environment.GetCommandLineArgs().Contains("--veteran-hall-test"))
            throw new InvalidOperationException("Hall mission startup requires its explicit QA argument.");
        var menu=UnityEngine.Object.FindObjectOfType<MainMenu>();
        if(menu==null)throw new InvalidOperationException("Main menu is unavailable.");
        var sandbox=GameState.DefaultSettings.сommonConfiguration.globalSandboxConfig;
        var missions=sandbox.presets;
        using var catalogQuery=Manager.CreateEntityQuery(ComponentType.ReadOnly<PrefabsMapSingleton>());
        if(catalogQuery.CalculateEntityCount()!=1)throw new InvalidOperationException("Native Sandbox prefab catalog is unavailable.");
        var map=Manager.GetComponentObject<PrefabsMapSingleton>(catalogQuery.GetSingletonEntity()).prefabsMap;
        var candidates=missions.Select((config,index)=>new {config,index})
            .Where(item=>item.config.faction==MissionConfiguration.GameFaction.Default && item.config.difficulties.Length>0 &&
                item.config.difficulties[0].optionalSandboxConfig!=null && !item.config.difficulties[0].optionalSandboxConfig.enemyCampsToggleDisabled &&
                item.config.difficulties[0].buildingsShopConfig.Categories.Any(category=>category.types.Any(type=>type.buildingUniqueIds.Contains(VeteranHall.BaseObjectId))))
            .Select(item=>new{item.config,item.index,City=InspectSandboxCity(item.config,map)})
            .Where(item=>item.City.TownHallLevel>0)
            .OrderByDescending(item=>item.City.TownHallLevel).ThenByDescending(item=>item.City.StorageCapacity)
            .ThenBy(item=>item.config.difficulties[0].mapGenerator.data.common.playableMapSize).ToArray();
        if(candidates.Length==0)
        {
            VeteranRuntime.Log.LogWarning("HALLQA Sandbox preset inspection: " + string.Join("; ", missions.Take(12).Select(config => "id="+config.missionId+",faction="+config.faction+",shop="+config.difficulties[0].buildingsShopConfig.name)));
            throw new InvalidOperationException("No native human Sandbox preset exposes the University and allows disabling enemy camps.");
        }
        var selected=candidates[0];
        menu.ShowSandbox();
        var panel=UnityEngine.Object.FindObjectOfType<SandboxPanel>(true);
        if(panel==null)throw new InvalidOperationException("Native Sandbox panel is unavailable.");
        var settings=sandbox.defaultSandboxOverride;
        settings.faction=MissionConfiguration.GameFaction.Default;
        settings.enemyCamps=false;
        settings.cheats=new SandboxCheats{removeHunger=true,removeDisease=true};
        settings.initialResources.wood=selected.City.StorageCapacity/3;
        settings.initialResources.stone=selected.City.StorageCapacity/3;
        settings.initialResources.iron=selected.City.StorageCapacity/3;
        settings.initialResources.food=selected.City.FoodCapacity;
        settings.initialResources.initialPeople=100;
        // These are native Sandbox options in the already-authorized isolated profile.
        // StartMission performs the game's own clamps, configuration copy and initial placement.
        Profiles.CurrentProfileData.lastSandBoxOverrideData=settings;
        Profiles.CurrentProfileData.lastSandBoxPresetId=selected.index;
        AccessTools.Field(typeof(SandboxPanel),"_presetIndex").SetValue(panel,selected.index);
        AccessTools.Field(typeof(SandboxPanel),"_selectedFaction").SetValue(panel,UnitFaction.Player);
        AccessTools.Field(typeof(SandboxPanel),"_sandboxOverride").SetValue(panel,settings);
        AccessTools.Method(typeof(SandboxPanel),"UpdateFieldsFromData").Invoke(panel,Array.Empty<object>());
        VeteranRuntime.Log.LogWarning("HALLQA native Sandbox selected: id="+selected.config.missionId+", preset="+selected.index+
            ", initialTownHall="+selected.City.TownHallLevel+", initialWSICapacity="+selected.City.StorageCapacity+
            "; enemy camps off, waves manual; native costs, construction work, hiring and prerequisites retained. Native no-hunger/no-disease QA options enabled; initial resources/population are explicit synthetic setup.");
        panel.StartMission();
    }

    private sealed class SandboxCity
    {
        internal int TownHallLevel,StorageCapacity,FoodCapacity;
    }

    private static SandboxCity InspectSandboxCity(MissionConfiguration configuration,NativeParallelHashMap<int,Entity> map)
    {
        var result=new SandboxCity();
        foreach(var item in configuration.difficulties[0].mapGenerator.data.initialMapObjects)
        {
            if(!map.TryGetValue(item.placement.objectId,out Entity prefab))continue;
            if(Manager.HasComponent<BuildingBase>(prefab))
            {
                var data=Manager.GetComponentData<BuildingBase>(prefab).value.Value;
                if(data.type==BuildingType.TownHall)result.TownHallLevel=Math.Max(result.TownHallLevel,data.level);
            }
            if(Manager.HasComponent<StorageBase>(prefab))
            {
                var data=Manager.GetComponentData<StorageBase>(prefab).value.Value;
                result.StorageCapacity+=data.woodStoneIronCapacity;
                result.FoodCapacity+=data.foodCapacity;
            }
        }
        return result;
    }

    private static void Guard()
    {
        if (!Environment.GetCommandLineArgs().Contains("--veteran-visual-test") || Application.companyName!="VS Tests" || Application.productName!="Veteran Soldiers Test Copy" ||
            !Path.GetFullPath(Paths.GameRootPath).TrimEnd('\\','/').EndsWith(Path.Combine("sandbox","game"),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Hall QA requires the isolated visual-test process.");
        string profile=Path.GetFullPath(Profiles.CurrentProfilePath), persistent=Path.GetFullPath(Application.persistentDataPath).TrimEnd('\\','/')+Path.DirectorySeparatorChar;
        if (!profile.StartsWith(persistent,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Hall QA profile is not isolated.");
        if (!initialized)
        {
            var harmony=new Harmony("com.veteransoldiers.hall-qa");
            harmony.Patch(AccessTools.Method(typeof(SaveLoadSystem),"CollectSaveData"),postfix:new HarmonyMethod(typeof(RuntimeHallTest),nameof(ObserveSave)) {priority=Priority.Last});
            harmony.Patch(AccessTools.Method(typeof(GameWorldLoaderSystem),"LoadBuildings"),postfix:new HarmonyMethod(typeof(RuntimeHallTest),nameof(ObserveLoad)) {priority=Priority.Last});
            InitializeEvidence(harmony);
            InitializeConstruction(harmony);
            initialized=true;
        }
        Manager.CompleteAllJobs();
    }

    internal static void Execute(string action,JObject command,Entity[] units,float3 origin)
    {
        Guard(); lastAction=action; lastError="";
        try
        {
            if(ExecuteEvidence(action,command))return;
            if (ExecuteConstruction(action, command, origin)) return;
            switch(action)
            {
                case "hall_inspect": break;
                case "hall_medical_selected":
                    using (var selected = Manager.CreateEntityQuery(ComponentType.ReadOnly<Selected>()))
                    {
                        using var entities = selected.ToEntityArray(Allocator.Temp);
                        if (entities.Length != 1 || !VeteranRuntime.IsEligible(entities[0]))
                            throw new InvalidOperationException("Select one exact living veteran before medical QA.");
                        BeginMedicalObservation(); SpawnDoctor(entities.ToArray(), origin);
                    }
                    break;
                case "hall_dedicate": if(!VeteranHall.TryDedicate(out string dedicateReason))throw new InvalidOperationException(dedicateReason);break;
                case "hall_menu": if(!VeteranHall.BeginConstruction(out string menuReason))throw new InvalidOperationException(menuReason);break;
                case "hall_resources": GrantResources();break;
                case "hall_native_select":
                    QueueConstructionAction(PlayerActionType.ConstructionSelectBuilding,VeteranHall.ObjectId);break;
                case "hall_move":
                    float x=(float?)command["X"]??origin.x+18,z=(float?)command["Z"]??origin.z;
                    SetConstructionPointer(new float2(x,z));
                    break;
                case "hall_build":
                    if(!GameState.CommonState.inConstruction||!GameState.ConstructionAllowed)throw new InvalidOperationException("Native construction preview is absent or not allowed at the current location/resources.");
                    QueueConstructionAction(PlayerActionType.ConstructionBuild);break;
                case "hall_cancel": QueueConstructionAction(PlayerActionType.ConstructionCancel);break;
                case "hall_select": SelectHall((int?)command["Index"]??0);break;
                case "hall_complete":
                    Entity unfinished=HallAt((int?)command["Index"]??0);
                    if(!Manager.HasComponent<ConstructionWorkCollector>(unfinished))throw new InvalidOperationException("Hall has no construction work collector.");
                    var work=Manager.GetComponentData<ConstructionWorkCollector>(unfinished);work.WorkAmountDone=work.WorkAmountRequired;
                    Manager.SetComponentData(unfinished,work);
                    VeteranRuntime.Log.LogWarning("HALLQA synthetic completion: native work collector filled by QA, not evidence of real builder labor.");
                    break;
                case "hall_destroy":
                    Entity destroy=HallAt((int?)command["Index"]??0);
                    if(!Manager.HasComponent<BuildingDestroyRequest>(destroy))Manager.AddComponentData(destroy,new BuildingDestroyRequest());
                    break;
                case "hall_unlock":
                    double amount=(double?)command["Amount"]??600;
                    if(amount<=0 || amount>10000 || !Progression.Finite(amount))throw new ArgumentException("QA amount must be 0..10000.");
                    Entity unit=units.FirstOrDefault(VeteranRuntime.IsEligible);
                    if(unit==Entity.Null)throw new InvalidOperationException("No fixture unit available.");
                    VeteranHall.RecordCombatExperience(unit,amount);
                    VeteranRuntime.Log.LogWarning("HALLQA synthetic milestone grant: "+amount+"; not evidence of real combat earning.");break;
                case "hall_doctrine":
                    if(!VeteranHall.TryStartDoctrine((HallDoctrine)((int?)command["Doctrine"]??1),out string reason))throw new InvalidOperationException(reason);
                    break;
                case "hall_save":
                    saveName="VeteranSoldiersHallQA_"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
                    using(var saves=Manager.CreateEntityQuery(ComponentType.ReadOnly<SaveRequest>()))if(saves.CalculateEntityCount()!=0)throw new InvalidOperationException("Another save is pending.");
                    var request=Manager.CreateEntity(typeof(SaveRequest));Manager.SetComponentData(request,new SaveRequest {SaveName=new FixedString128Bytes(saveName),IsAutoSave=false});break;
                case "hall_load":
                    string name=(string?)command["Name"]??saveName;
                    if(!name.StartsWith("VeteranSoldiersHallQA_",StringComparison.Ordinal) || Path.GetFileName(name)!=name || !File.Exists(Path.Combine(Profiles.CurrentProfilePath,name)))
                        throw new InvalidOperationException("Only an existing isolated Hall QA save may be loaded.");
                    using(var loads=Manager.CreateEntityQuery(ComponentType.ReadOnly<LoadRequest>()))if(loads.CalculateEntityCount()!=0)throw new InvalidOperationException("Another load is pending.");
                    string qaLoadPath=Path.GetFullPath(Path.Combine(Profiles.CurrentProfilePath,name));
                    string qaSidecar=PersistenceStore.GetSidecarPath(Path.Combine(Paths.ConfigPath,"VeteranSoldiers","saves"),qaLoadPath);
                    var expectedCampaign = new CampaignSnapshot();
                    if ((bool?)command["WithoutSidecar"] == true)
                    {
                        if (File.Exists(qaSidecar)) throw new InvalidOperationException("Sidecar-absent test requires an actually absent sidecar.");
                    }
                    else if ((bool?)command["ExpectRejectedSidecar"] == true)
                    {
                        if (!File.Exists(qaSidecar)) throw new InvalidOperationException("Rejected-sidecar test requires an existing invalid fixture.");
                        bool rejected = false;
                        try { PersistenceStore.Read(qaSidecar,PersistenceStore.HashFile(qaLoadPath,false),PersistenceStore.HashText(qaLoadPath.ToUpperInvariant())); }
                        catch (Exception error) when (error is InvalidDataException || error is System.Xml.XmlException || error is FormatException) { rejected = true; }
                        if (!rejected) throw new InvalidOperationException("The rejected-sidecar fixture is actually valid.");
                    }
                    else PersistenceStore.Read(qaSidecar,PersistenceStore.HashFile(qaLoadPath,false),PersistenceStore.HashText(qaLoadPath.ToUpperInvariant()),out expectedCampaign);
                    var expectedHallReferences=expectedCampaign.Hall.Buildings.ToDictionary(item=>item.ReferenceId,item=>item.BaseObjectId);
                    savedHallReferences.Clear();
                    foreach(var expected in expectedHallReferences)savedHallReferences.Add(expected.Key,expected.Value);
                    loadedReferences=null;
                    ResetEvidenceAfterLoad();
                    saveName=name;
                    var load=Manager.CreateEntity(typeof(LoadRequest));Manager.SetComponentData(load,new LoadRequest{NameToLoad=new FixedString128Bytes(name),FromMenu=false});break;
                case "hall_medical": BeginMedicalObservation();SpawnDoctor(units,origin);break;
                default:throw new ArgumentException("Unknown Hall QA action.");
            }
        }
        catch(Exception error){lastError=error.Message;throw;}
    }

    private static Entity CreateCommand<T>() where T:struct,IComponentData
    {
        using var existing=Manager.CreateEntityQuery(ComponentType.ReadOnly<T>());
        if(existing.CalculateEntityCount()!=0)throw new InvalidOperationException("A native "+typeof(T).Name+" is already pending.");
        return Manager.CreateEntity(typeof(T),typeof(CommandDataTag));
    }

    private static List<Entity> Halls()
    {
        var list=new List<Entity>();
        using var query=Manager.CreateEntityQuery(ComponentType.ReadOnly<BuildingBase>(),ComponentType.ReadOnly<Components.RawComponents.ObjectId>(),ComponentType.Exclude<Prefab>(),ComponentType.Exclude<CurrentBuildingForConstruction>());
        using var entities=query.ToEntityArray(Allocator.Temp);
        foreach(Entity entity in entities)if(VeteranHall.IsHall(entity))list.Add(entity);
        list.Sort((a,b)=>a.Index.CompareTo(b.Index));return list;
    }
    private static Entity HallAt(int index)
    {var halls=Halls();if(index<0 || index>=halls.Count)throw new InvalidOperationException("Hall index not present.");return halls[index];}
    private static void SelectHall(int index)
    {
        Entity hall=HallAt(index);
        using(var selected=Manager.CreateEntityQuery(ComponentType.ReadOnly<Selected>()))Manager.RemoveComponent<Selected>(selected);
        Manager.AddComponent<Selected>(hall);GameStateUpdateTasksUtility.UpdateGameState(Manager,GameStateUpdateTaskType.Selection);
        using var move=Manager.CreateEntityQuery(ComponentType.ReadOnly<CameraInstantMoveToPositionCommandData>());
        if(move.CalculateEntityCount()==0)
        {
            Entity command=Manager.CreateEntity(typeof(CameraInstantMoveToPositionCommandData));
            Manager.SetComponentData(command,new CameraInstantMoveToPositionCommandData{position=Manager.GetComponentData<Translation>(hall).Value});
        }
    }

    private static void GrantResources()
    {
        var system=VeteranRuntime.ActiveWorld!.GetExistingSystem<VeteranBootstrapSystem>();
        var resources=new NativeReference<ResourcesChangeTracker>(Allocator.TempJob);
        using var commands=new EntityCommandBuffer(Allocator.TempJob);
        try
        {
            system.ScheduleBeginUpdateResource(resources,default).Complete();
            var tracker=resources.Value;tracker.AddToCurrent(new PriceBaseData{woodPrice=2000,stonePrice=2000,ironPrice=2000,foodPrice=2000,moneyPrice=2000});resources.Value=tracker;
            system.ScheduleEndUpdateResource(resources,commands,default).Complete();commands.Playback(Manager);
        }
        finally{resources.Dispose();}
        VeteranRuntime.Log.LogWarning("HALLQA native resource grant requested; normal storage limits still apply.");
    }

    private static void SpawnDoctor(Entity[] units,float3 origin)
    {
        healingTarget=units.FirstOrDefault(e=>VeteranRuntime.TryGetExperience(e,out double xp)&&Progression.Level(xp,VeteranRuntime.Settings.Ranks,5)>=2);
        if(healingTarget==Entity.Null)throw new InvalidOperationException("No living veteran fixture target.");
        using var settingsQuery=Manager.CreateEntityQuery(ComponentType.ReadOnly<CurrentSettingsSingleton>());
        var settings=Manager.GetComponentObject<CurrentSettingsSingleton>(settingsQuery.GetSingletonEntity());
        Entity doctor=Entity.Null;
        using var prefabQuery=Manager.CreateEntityQuery(new EntityQueryDesc{All=new[]{ComponentType.ReadOnly<UnitBase>(),ComponentType.ReadOnly<Prefab>()},Options=EntityQueryOptions.IncludePrefab});
        using var candidates=prefabQuery.ToEntityArray(Allocator.Temp);
        foreach(Entity candidate in candidates)
            if(!Manager.HasComponent<Enemy>(candidate) && Manager.GetComponentData<UnitBase>(candidate).value.Value.type==UnitType.Doctor){doctor=candidate;break;}
        if(doctor==Entity.Null)throw new InvalidOperationException("Friendly Doctor prefab was not found.");
        var health=Manager.GetComponentData<Health>(healingTarget);health.currentHealth=Math.Max(1,health.currentHealth-10);Manager.SetComponentData(healingTarget,health);healingBefore=health.currentHealth;
        float3 position=Manager.GetComponentData<Translation>(healingTarget).Value+new float3(1.5f,0,0);
        var spawner=UnitSpawnUtility.MainThread.Init(GameState.PrefabsMap,settings);
        using var commands=new EntityCommandBuffer(Allocator.TempJob);
        spawner.Spawn(doctor,position,quaternion.identity,true,false,false,commands,Manager);commands.Playback(Manager);
        VeteranRuntime.Log.LogWarning("HALLQA actual Doctor spawned beside a synthetically wounded veteran. Observe native healing and its preloader-tagged request.");
    }

    [HarmonyPriority(Priority.Last)]
    private static void ObserveSave(SaveLoadSystem __instance,NativeParallelHashMap<Entity,EntityMappingRef> mapping,ref SaveContainer saveContainer)
    {
        if(!initialized || saveName.Length==0)return;
        savedHallReferences.Clear();
        using(var keys=mapping.GetKeyArray(Allocator.Temp))foreach(Entity entity in keys)if(VeteranHall.IsHall(entity))savedHallReferences[mapping[entity].referenceId]=VeteranHall.BaseObjectId;
        var captured=saveContainer;
        savedPayload=new
        {
            Name=saveName,HallReferences=savedHallReferences.ToArray(),
            AllHallIdsNormalized=saveContainer.buildings.All(b=>b.objectId!=VeteranHall.ObjectId),
            ExactHallReferencesNormalized=savedHallReferences.All(p=>captured.buildings.Any(b=>b.referenceId==p.Key&&b.objectId==p.Value)),
            CustomIdAbsentFromResearch=saveContainer.research.completeBuildings==null || !saveContainer.research.completeBuildings.Contains(VeteranHall.ObjectId),
            Buildings=saveContainer.buildings.Where(b=>savedHallReferences.ContainsKey(b.referenceId)).Select(b=>new{b.referenceId,b.objectId,b.inBuild,b.progress,b.health}).ToArray()
        };
    }

    private static void ObserveLoad(GameWorldLoaderSystem __instance,NativeParallelHashMap<int,Entity> mapping)
    {
        if(!initialized)return;
        ResetEvidenceAfterLoad();
        var observed=savedHallReferences.Select(p=>
        {
            bool found=mapping.TryGetValue(p.Key,out Entity entity)&&__instance.EntityManager.Exists(entity);
            int actualId=found&&__instance.EntityManager.HasComponent<Components.RawComponents.ObjectId>(entity)?__instance.EntityManager.GetComponentData<Components.RawComponents.ObjectId>(entity).value:0;
            return new{ReferenceId=p.Key,ExpectedBaseObjectId=p.Value,ExpectedHallObjectId=VeteranHall.ObjectId,Found=found,
                Entity=found?entity.ToString():"",ActualObjectId=actualId,IsHall=found&&actualId==VeteranHall.ObjectId&&p.Value==VeteranHall.BaseObjectId};
        }).ToArray();
        loadedReferences=new{ExpectedCount=savedHallReferences.Count,MatchedCount=observed.Count(item=>item.IsHall),
            NonVacuousMatch=observed.Length>0&&observed.All(item=>item.IsHall),Entries=observed};
    }

    internal static void Tick()
    {
        if(!initialized || Time.unscaledTime<nextTick)return;
        nextTick=Time.unscaledTime+.2f;
        // Loading screens are still dismissed by RuntimeVisualTest; no game state is advanced here.
    }

    internal static object Snapshot
    {
        get
        {
            if(!initialized)return new {Initialized=false,Available=VeteranHall.Available,BaseObjectId=VeteranHall.BaseObjectId};
            try
            {
                Manager.CompleteAllJobs();
                var halls=Halls();
                object? catalog=null;
                if(GameState.PrefabsMap.IsCreated&&GameState.PrefabsMap.TryGetValue(VeteranHall.ObjectId,out Entity prefab))
                {
                    var data=Manager.GetComponentData<BuildingBase>(prefab).value.Value;
                    var price=Manager.GetComponentData<PriceBase>(prefab).Value(false);
                    using var types=Manager.GetComponentTypes(prefab,Allocator.Temp);
                    catalog=new{data.width,data.length,data.level,data.townHallLevelRestriction,Price=new{price.woodPrice,price.stonePrice,price.ironPrice,price.foodPrice,price.moneyPrice,price.peoplePrice},
                        Components=types.Select(t=>t.ToString()).ToArray(),Renderers=DescribeRenderers(prefab),NativeRenderers=DescribeRenderers(GameState.PrefabsMap[VeteranHall.BaseObjectId]),
                        SharedUniversityLimit=GameState.ObjectsDataState.BuildingsMaxCountsByType[(byte)BuildingType.University],
                        HallLimitReached=GameState.ObjectsDataState.BuildingLimitReached(VeteranHall.ObjectId),
                        UniversityLimitReached=GameState.ObjectsDataState.BuildingLimitReached(VeteranHall.BaseObjectId)};
                }
                string path=saveName.Length==0?"":Path.Combine(Profiles.CurrentProfilePath,saveName);
                string sidecar=path.Length==0?"":PersistenceStore.GetSidecarPath(Path.Combine(Paths.ConfigPath,"VeteranSoldiers","saves"),path);
                object? persisted=null;
                if(path.Length>0&&File.Exists(path)&&File.Exists(sidecar))
                {
                    PersistenceStore.Read(sidecar,PersistenceStore.HashFile(path,false),PersistenceStore.HashText(Path.GetFullPath(path).ToUpperInvariant()),out var campaign);
                    persisted=new{Name=saveName,HallRecords=campaign.Hall.Buildings.Count,campaign.Hall.ActiveDoctrine,campaign.Hall.PendingDoctrine,campaign.Hall.ResearchProgress};
                }
                return new {Initialized=true,LastAction=lastAction,Error=lastError,VeteranHall.Available,VeteranHall.BaseObjectId,VeteranHall.CompletedCount,VeteranHall.IsOperational,
                    Scenario=new{Mode=GameState.Settings.missionConfiguration.type.ToString(),MissionId=GameState.Settings.missionConfiguration.missionId,
                        EnemyCamps=GameState.Settings.sandboxOverride?.enemyCamps,Cheats=GameState.Settings.sandboxOverride?.cheats},
                    ActiveDoctrine=VeteranHall.ActiveDoctrine.ToString(),PendingDoctrine=VeteranHall.PendingDoctrine.ToString(),VeteranHall.ResearchProgress01,VeteranHall.CombatExperience,
                    VeteranHall.Status,VeteranHall.OrdinaryUniversityCount,VeteranHall.RequiredTownHallLevel,
                    CanDedicate=VeteranHall.CanDedicate(out string dedicationReason),DedicationReason=dedicationReason,
                    Doctrines=Enumerable.Range(1,3).Select(index=>{var choice=(HallDoctrine)index;bool allowed=VeteranHall.CanStartDoctrine(choice,out string reason);
                        return new{Choice=choice.ToString(),Allowed=allowed,Blocker=VeteranHall.DoctrineBlock(choice).ToString(),Reason=reason,Requirement=VeteranHall.DoctrineRequirement(choice)};}).ToArray(),
                    ConstructionAllowed=GameState.ConstructionAllowed,ConstructionCategory=GameState.UIState.currentBuildingCategoryIndex,
                    HallConstructionAvailable=GameState.PlayerActionState.IsAvailable(new PlayerActionCallback(PlayerActionType.ConstructionSelectBuilding,VeteranHall.ObjectId)),
                    Resources=GameState.ResourceState.AsResourcesContainer,Catalog=catalog,NativeResearch=VeteranHallResearch.Describe(),Construction=ConstructionSnapshot,
                    Halls=halls.Select(e=>new{Entity=e.ToString(),Position=Manager.GetComponentData<Translation>(e).Value.ToString(),Renderers=DescribeRenderers(e),InConstruction=Manager.HasComponent<InConstruction>(e),
                        Health=Manager.GetComponentData<Health>(e).currentHealth,Work=Manager.HasComponent<ConstructionWorkCollector>(e)?Manager.GetComponentData<ConstructionWorkCollector>(e).WorkAmountDone:0,
                        WorkRequired=Manager.HasComponent<ConstructionWorkCollector>(e)?Manager.GetComponentData<ConstructionWorkCollector>(e).WorkAmountRequired:0}).ToArray(),
                    SaveName=saveName,SavedPayload=savedPayload,VerifiedSidecar=persisted,LoadedReferences=loadedReferences,
                    Evidence=EvidenceSnapshot,MedicalTarget=healingTarget.ToString(),MedicalBefore=healingBefore,MedicalNow=Manager.Exists(healingTarget)&&Manager.HasComponent<Health>(healingTarget)?Manager.GetComponentData<Health>(healingTarget).currentHealth:0};
            }
            catch(Exception error){return new {Initialized=true,Error=error.ToString()};}
        }
    }

    private static object[] DescribeRenderers(Entity root)
    {
        var list=new List<object>();
        if(!Manager.HasComponent<LinkedEntityGroup>(root))return list.ToArray();
        foreach(var item in Manager.GetBuffer<LinkedEntityGroup>(root))
        {
            if(!Manager.HasComponent<RenderMesh>(item.Value))continue;
            var mesh=Manager.GetSharedComponentData<RenderMesh>(item.Value);
            using var types=Manager.GetComponentTypes(item.Value,Allocator.Temp);
            list.Add(new{Entity=item.Value.ToString(),Mesh=mesh.mesh?.name,Material=mesh.material?.name,Shader=mesh.material?.shader?.name,mesh.subMesh,
                Bounds=mesh.mesh==null?"":mesh.mesh.bounds.ToString(),
                Position=Manager.HasComponent<Translation>(item.Value)?Manager.GetComponentData<Translation>(item.Value).Value.ToString():"",
                Components=types.Select(type=>type.ToString()).ToArray()});
        }
        return list.ToArray();
    }
}
#endif

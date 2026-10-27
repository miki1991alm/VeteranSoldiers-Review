using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Components.RawComponents;
using HarmonyLib;
using Systems;
using UI;
using Unity.Collections;
using Unity.Entities;
using Utility;

namespace VeteranSoldiers
{
    /// <summary>
    /// Hooks only the game's managed save boundaries. A save-local reference mapping plus a
    /// content fingerprint prevents Entity.Index recycling and cross-save assignments.
    /// </summary>
    internal static class VeteranPersistence
    {
        private enum SaveFeedback { Session, Saving, Saved, Restored, SaveFailed, LoadRejected, LoadMissing }
        private static SaveFeedback feedback;
        internal static void ResetFeedback() { feedback = SaveFeedback.Session; }
        internal static string Status
        {
            get
            {
                if (!VeteranRuntime.PersistenceEnabled) return VeteranText.Choose("PERSISTENCE DISABLED · SESSION ONLY", "PERSISTENCIA DESACTIVADA · SOLO ESTA SESIÓN");
                switch (feedback)
                {
                    case SaveFeedback.Saving: return VeteranText.Choose("Saving progression…", "Guardando el progreso…");
                    case SaveFeedback.Saved: return VeteranText.Choose("Progression stored inside this save · save again to keep changes", "Progreso dentro de esta partida · guarda para conservar cambios");
                    case SaveFeedback.Restored: return VeteranText.Choose("Progression restored · save to keep changes", "Progreso restaurado · guarda para conservar cambios");
                    case SaveFeedback.SaveFailed: return VeteranText.Choose("SAVE FAILED · keep an earlier backup; see log", "ERROR AL GUARDAR · conserva un respaldo; consulta el registro");
                    case SaveFeedback.LoadRejected: return VeteranText.Choose("MOD DATA REJECTED · progression not restored; see log", "DATOS RECHAZADOS · progreso sin restaurar; consulta el registro");
                    case SaveFeedback.LoadMissing: return VeteranText.Choose("No saved veteran data · older progress requires the source PC", "Sin datos de veteranía · el progreso antiguo requiere el PC de origen");
                    default: return VeteranText.Choose("Save the game to preserve progression", "Guarda la partida para conservar el progreso");
                }
            }
        }

        private sealed class SaveOperation
        {
            internal SaveLoadSystem? Owner;
            internal readonly List<PersistentVeteran> Records = new List<PersistentVeteran>();
            internal CampaignSnapshot Campaign = new CampaignSnapshot();
            internal string Path = string.Empty;
            internal bool Completed;
            internal bool Success;
            internal DateTime FinishedAt;
            internal DateTime NextAttempt;
        }

        private sealed class LoadOperation
        {
            internal SaveLoadSystem? Owner;
            internal string Path = string.Empty;
            internal string FileHash = string.Empty;
            internal Dictionary<int, PersistentVeteran> Records = new Dictionary<int, PersistentVeteran>();
            internal CampaignSnapshot Campaign = new CampaignSnapshot();
            internal SaveContainer? Container;
            internal bool Verified;
        }

        private static readonly object Gate = new object();
        private static SaveOperation? _save;
        private static LoadOperation? _load;
        private static List<KeyValuePair<Entity, VeteranSnapshot>>? _restore;
        private static World? _restoreWorld;
        private static Dictionary<Entity, ChronicleRecord>? _restoreChronicles;
        private static CampaignSnapshot? _restoreCampaign;
        private static string DirectoryPath { get { return Path.Combine(Paths.ConfigPath, "VeteranSoldiers", "saves"); } }

        internal static void Initialize(Harmony harmony)
        {
            Patch(harmony, typeof(SaveLoadSystem), "SaveUnits", null, nameof(AfterSaveUnits));
            Patch(harmony, typeof(SaveLoadSystem), "CollectSaveData", null, nameof(AfterCollectSaveData));
            Patch(harmony, typeof(SaveLoadSystem), "DoAsyncSave", nameof(BeforeAsyncSave), null);
            Patch(harmony, typeof(SaveLoadSystem), "SaveFinishedHandler", null, nameof(AfterSaveFinished));
            Patch(harmony, typeof(SaveLoadSystem), "ProcessLoad", nameof(BeforeProcessLoad), null);
            Patch(harmony, typeof(SaveLoadSystem), "LoadFinishedHandler", nameof(BeforeLoadFinished), null);
            Patch(harmony, typeof(GameWorldLoaderSystem), "LoadUnits", nameof(BeforeLoadUnits), nameof(AfterLoadUnits));
            Patch(harmony, typeof(GameWorldLoaderSystem), "LoadBuildings", nameof(BeforeLoadBuildings), nameof(AfterLoadBuildings));
            Patch(harmony, typeof(ResearchSystem), "OnStartRunning", null, nameof(AfterResearchStartup));
        }

        private static void Patch(Harmony harmony, Type type, string method, string? prefix, string? postfix)
        {
            var target = AccessTools.Method(type, method);
            if (target == null) throw new MissingMethodException(type.FullName, method);
            harmony.Patch(target,
                prefix == null ? null : new HarmonyMethod(typeof(VeteranPersistence), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(VeteranPersistence), postfix));
        }

        // Called after the game has copied all live components into plain serializable structs.
        private static void AfterSaveUnits(SaveLoadSystem __instance,
            NativeParallelHashMap<Entity, EntityMappingRef> mapping, ref SaveContainer saveContainer)
        {
            try
            {
                VeteranRuntime.SetWorld(__instance.World);
                var operation = new SaveOperation { Owner = __instance };
                var snapshots = new Dictionary<int, VeteranSnapshot>();
                var chronicles = new Dictionary<int, ChronicleRecord>();
                using (NativeArray<Entity> entities = mapping.GetKeyArray(Allocator.Temp))
                {
                    for (int i = 0; i < entities.Length; i++)
                    {
                        Entity entity = entities[i];
                        if (VeteranRuntime.TryGetSnapshot(entity, out VeteranSnapshot snapshot))
                        {
                            snapshots.Add(mapping[entity].referenceId, snapshot);
                            var chronicle = VeteranChronicle.GetOrCreate(entity).Clone();
                            if (__instance.EntityManager.HasComponent<ObjectId>(entity))
                                chronicle.ObjectId = __instance.EntityManager.GetComponentData<ObjectId>(entity).value;
                            chronicles.Add(mapping[entity].referenceId, chronicle);
                        }
                    }
                }
                var batch = SaveCaptureBatch<UnitSaveData>.Prepare(saveContainer.units,
                    unit => snapshots.TryGetValue(unit.referenceId, out VeteranSnapshot snapshot) ?
                        new PersistentVeteran { ReferenceId = unit.referenceId, ObjectId = unit.objectId, Snapshot = snapshot, Chronicle = chronicles[unit.referenceId] } : null,
                    (unit, snapshot) =>
                    {
                        SaveCaptureHealth.Normalize(ref unit.maxHealthMultiplier, ref unit.health, snapshot);
                        return unit;
                    });
                if (VeteranRuntime.PersistenceEnabled)
                {
                    operation.Records.AddRange(batch.Records);
                    operation.Campaign = VeteranChronicle.CaptureCampaign();
                    operation.Campaign.Hall = VeteranHall.Capture(mapping);
                    ChronicleRules.ValidateCampaign(operation.Campaign);
                }
                // Publish only after the entire captured batch and all normalization succeeded.
                // Live combat stats are never changed by save preparation.
                saveContainer.units = batch.Units;
                // Persistence=false still normalizes removable base-game stats, but must
                // never replace an existing sidecar with an empty campaign snapshot.
                lock (Gate) _save = VeteranRuntime.PersistenceEnabled ? operation : null;
                feedback = VeteranRuntime.PersistenceEnabled ? SaveFeedback.Saving : SaveFeedback.Session;
                if (batch.OmittedRecords > 0)
                    VeteranRuntime.Log.LogWarning("Veterancy omitted " + batch.OmittedRecords + " invalid progression records; their safe health factors were removed from the base save.");
            }
            catch (Exception exception)
            {
                lock (Gate) _save = null;
                feedback = SaveFeedback.SaveFailed;
                VeteranRuntime.Log.LogError("Veterancy save capture failed before publishing normalization; base game saving continues with its captured stats. No sidecar will be written. Keep an earlier backup: " + exception);
            }
        }

        private static void AfterCollectSaveData(ref SaveContainer saveContainer)
        {
            // All building queues now exist. The on-disk game save retains vanilla building IDs;
            // only our fingerprint-bound sidecar remembers which exact references were Halls.
            try { VeteranHall.NormalizeSavedBuildings(ref saveContainer); }
            catch (Exception exception)
            {
                lock (Gate) _save = null;
                feedback = SaveFeedback.SaveFailed;
                VeteranRuntime.Log.LogError("Hall save normalization failed: " + exception);
                // Never allow a mod-only building ID to escape into a supposedly removable save.
                throw;
            }
        }

        private static void BeforeAsyncSave(SaveLoadSystem __instance)
        {
            try
            {
                using (EntityQuery query = __instance.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<SaveRequest>()))
                {
                    if (query.CalculateEntityCount() != 1) return;
                    string path = GetSavePath(query.GetSingleton<SaveRequest>().SaveName.Value);
                    lock (Gate)
                    {
                        if (_save != null && ReferenceEquals(_save.Owner, __instance)) _save.Path = path;
                    }
                }
            }
            catch (Exception exception)
            {
                lock (Gate) _save = null;
                feedback = SaveFeedback.SaveFailed;
                VeteranRuntime.Log.LogWarning("Veterancy could not bind the save filename: " + exception.Message);
            }
        }

        // Invoked by the game's worker thread; do not call EntityManager or Unity APIs here.
        private static void AfterSaveFinished(SaveLoadSystem __instance, Exception? e)
        {
            lock (Gate)
            {
                if (_save == null || !ReferenceEquals(_save.Owner, __instance)) return;
                _save.Completed = true;
                _save.Success = e == null;
                _save.FinishedAt = DateTime.UtcNow;
            }
        }

        /// <summary>Call on the main thread. No work is done except after an actual save.</summary>
        internal static void Tick()
        {
            SaveOperation? operation;
            lock (Gate) operation = _save;
            if (!VeteranRuntime.PersistenceEnabled)
            {
                if (operation != null) ClearSave(operation);
                feedback = SaveFeedback.Session;
                return;
            }
            if (operation == null || !operation.Completed) return;
            if (!operation.Success || string.IsNullOrEmpty(operation.Path))
            {
                feedback = SaveFeedback.SaveFailed;
                ClearSave(operation);
                return;
            }
            if (DateTime.UtcNow < operation.NextAttempt) return;
            try
            {
                // The game invokes its completion callback before finally closing its stream.
                // Exclusive sharing ensures we fingerprint only the closed, completed save.
                string digest = PortableSave.Append(operation.Path, operation.Records, operation.Campaign);
                string identity = SaveIdentity(operation.Path);
                string sidecar = PersistenceStore.GetSidecarPath(DirectoryPath, operation.Path);
                try { PersistenceStore.WriteAtomic(sidecar, digest, identity, operation.Records, operation.Campaign); }
                catch (Exception exception)
                {
                    // The authoritative portable snapshot is already durable. A failed local
                    // backup must neither erase it nor cause a second append on the next tick.
                    VeteranRuntime.Log.LogWarning("Portable veterancy saved; local compatibility backup could not be written: " + exception.Message);
                }
                feedback = SaveFeedback.Saved;
                VeteranRuntime.Log.LogInfo("Veterancy saved " + operation.Records.Count + " individual records inside the portable game save with verified fingerprint.");
                ClearSave(operation);
            }
            catch (IOException exception)
            {
                if ((DateTime.UtcNow - operation.FinishedAt).TotalSeconds < 30)
                {
                    operation.NextAttempt = DateTime.UtcNow.AddMilliseconds(100);
                    return;
                }
                VeteranRuntime.Log.LogWarning("Portable veterancy could not be finalized: " + exception.Message);
                feedback = SaveFeedback.SaveFailed;
                ClearSave(operation);
            }
            catch (Exception exception)
            {
                VeteranRuntime.Log.LogError("Portable veterancy write failed; native save prefix was not modified: " + exception);
                feedback = SaveFeedback.SaveFailed;
                ClearSave(operation);
            }
        }

        private static void ClearSave(SaveOperation operation)
        {
            lock (Gate) if (ReferenceEquals(_save, operation)) _save = null;
        }

        private static void BeforeProcessLoad(SaveLoadSystem __instance, bool ____loadStarted)
        {
            if (____loadStarted) return;
            try
            {
                using (EntityQuery query = __instance.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<LoadRequest>()))
                {
                    if (query.CalculateEntityCount() != 1) return;
                    ResetFeedback();
                    lock (Gate) _load = null;
                    if (!VeteranRuntime.PersistenceEnabled) return;
                    string path = GetSavePath(query.GetSingleton<LoadRequest>().NameToLoad.Value);
                    PortableProgress? portable = PortableSave.Read(path, out string digest);
                    if (portable != null)
                    {
                        lock (Gate) _load = new LoadOperation { Owner = __instance, Path = path, FileHash = portable.FileHash, Records = portable.Records, Campaign = portable.Campaign };
                        return;
                    }
                    // Existing 1.0.0 and earlier saves still load from their original local
                    // sidecar. Their next successful save migrates the complete campaign.
                    string sidecar = PersistenceStore.GetSidecarPath(DirectoryPath, path);
                    if (!File.Exists(sidecar)) { feedback = SaveFeedback.LoadMissing; return; }
                    Dictionary<int, PersistentVeteran> records = PersistenceStore.Read(sidecar, digest, SaveIdentity(path), out CampaignSnapshot campaign);
                    lock (Gate) _load = new LoadOperation { Owner = __instance, Path = path, FileHash = digest, Records = records, Campaign = campaign };
                }
            }
            catch (Exception exception)
            {
                lock (Gate) _load = null;
                feedback = SaveFeedback.LoadRejected;
                VeteranRuntime.Log.LogWarning("Veterancy save data rejected; units will start as recruits: " + exception.Message);
            }
        }

        private static void BeforeLoadFinished(SaveLoadSystem __instance, SaveContainer? loadedData, Exception? e)
        {
            // Same background-thread restriction as AfterSaveFinished. This must be a prefix:
            // the original callback publishes _loadFinished, which the main thread can consume
            // immediately. Publish our verification before vanilla makes the container available.
            LoadOperation? operation;
            lock (Gate) operation = _load;
            if (operation == null || !ReferenceEquals(operation.Owner, __instance)) return;
            try
            {
                bool valid = e == null && loadedData != null &&
                    string.Equals(PersistenceStore.HashFile(operation.Path, false), operation.FileHash, StringComparison.Ordinal);
                lock (Gate)
                {
                    if (!ReferenceEquals(_load, operation)) return;
                    operation.Container = valid ? loadedData : null;
                    operation.Verified = valid;
                }
            }
            catch (Exception exception)
            {
                lock (Gate) operation.Verified = false;
                VeteranRuntime.Log.LogWarning("Veterancy load fingerprint could not be verified: " + exception.Message);
            }
        }

        private static void BeforeLoadBuildings(GameWorldLoaderSystem __instance, LoadData ____loadData)
        {
            _restore = null;
            _restoreWorld = null;
            _restoreChronicles = null;
            _restoreCampaign = null;
            try
            {
                SaveFeedback loadFeedback = feedback;
                VeteranRuntime.SetWorld(__instance.World);
                VeteranRuntime.ResetSession();
                if (loadFeedback == SaveFeedback.LoadRejected || loadFeedback == SaveFeedback.LoadMissing) feedback = loadFeedback;
                LoadOperation? operation;
                lock (Gate) operation = _load;
                if (operation == null || !operation.Verified || operation.Container == null || !VeteranRuntime.PersistenceEnabled) return;
                if (____loadData == null || !ReferenceEquals(____loadData.LoadedData, operation.Container))
                    throw new InvalidDataException("Hall load context differs from verified save.");
                VeteranHall.PrepareLoadBuildings(operation.Campaign.Hall, ref ____loadData.LoadedData);
            }
            catch (Exception exception)
            {
                lock (Gate) _load = null;
                // Our failure must not throw out of a Harmony prefix and abort vanilla loading.
                VeteranRuntime.Fail(exception);
            }
        }

        private static void AfterLoadBuildings(GameWorldLoaderSystem __instance, NativeParallelHashMap<int, Entity> mapping, LoadData ____loadData)
        {
            LoadOperation? operation;
            lock (Gate) operation = _load;
            if (operation == null || !operation.Verified || operation.Container == null || !VeteranRuntime.PersistenceEnabled) return;
            try
            {
                if (!ReferenceEquals(____loadData.LoadedData, operation.Container)) throw new InvalidDataException("Hall load context changed.");
                VeteranHall.Restore(operation.Campaign.Hall, mapping);
            }
            catch (Exception exception)
            {
                lock (Gate) _load = null;
                VeteranRuntime.Fail(exception);
            }
        }

        private static void BeforeLoadUnits(GameWorldLoaderSystem __instance)
        {
            // Buildings load first and perform the session reset. Repeating that reset here
            // would erase the Hall doctrine/identity restored from the same verified save.
            try { VeteranRuntime.SetWorld(__instance.World); }
            catch (Exception exception) { lock (Gate) _load = null; VeteranRuntime.Fail(exception); }
        }

        private static void AfterLoadUnits(GameWorldLoaderSystem __instance, NativeParallelHashMap<int, Entity> mapping, LoadData ____loadData)
        {
            LoadOperation? operation;
            lock (Gate) { operation = _load; _load = null; }
            if (operation == null || !operation.Verified || operation.Container == null || !VeteranRuntime.PersistenceEnabled) return;
            try
            {
                if (____loadData == null || !ReferenceEquals(____loadData.LoadedData, operation.Container))
                    throw new InvalidDataException("Load context no longer belongs to the verified save.");
                // Validate the entire set before assigning any experience, including duplicate IDs.
                var savedTypes = new Dictionary<int, int>();
                foreach (UnitSaveData unit in operation.Container.units) savedTypes.Add(unit.referenceId, unit.objectId);
                var assignments = new List<KeyValuePair<Entity, VeteranSnapshot>>();
                var chronicles = new Dictionary<Entity, ChronicleRecord>();
                foreach (PersistentVeteran record in operation.Records.Values)
                {
                    if (!savedTypes.TryGetValue(record.ReferenceId, out int objectId) || objectId != record.ObjectId ||
                        !mapping.TryGetValue(record.ReferenceId, out Entity entity) ||
                        !__instance.EntityManager.Exists(entity) || !__instance.EntityManager.HasComponent<ObjectId>(entity) ||
                        __instance.EntityManager.GetComponentData<ObjectId>(entity).value != record.ObjectId)
                        throw new InvalidDataException("A veteran reference does not map to the exact loaded unit type.");
                    assignments.Add(new KeyValuePair<Entity, VeteranSnapshot>(entity, record.Snapshot));
                    if (record.Chronicle != null) chronicles.Add(entity, record.Chronicle);
                }
                _restore = assignments;
                _restoreWorld = __instance.World;
                _restoreChronicles = chronicles;
                _restoreCampaign = operation.Campaign;
            }
            catch (Exception exception)
            {
                VeteranRuntime.Fail(exception);
                VeteranRuntime.Log.LogWarning("Veterancy restoration rejected: " + exception.Message);
            }
        }

        private static void AfterResearchStartup(ResearchSystem __instance)
        {
            // ResearchSystem.OnStartRunning runs the load-time health/research jobs and plays
            // their ECB back synchronously. Restore AFTER that vanilla adjustment, before combat.
            var assignments = _restore;
            var chronicles = _restoreChronicles;
            var campaign = _restoreCampaign;
            if (assignments == null || !ReferenceEquals(__instance.World, _restoreWorld)) return;
            _restore = null;
            _restoreWorld = null;
            _restoreChronicles = null;
            _restoreCampaign = null;
            if (!VeteranRuntime.PersistenceEnabled) return;
            try
            {
                VeteranRuntime.SetWorld(__instance.World);
                int restored = 0;
                int skipped = 0;
                foreach (KeyValuePair<Entity, VeteranSnapshot> assignment in assignments)
                {
                    // Identity and all references were already validated as one set in AfterLoadUnits.
                    // A deliberate current filter can exclude a saved unit without discarding others.
                    if (!VeteranRuntime.IsEligible(assignment.Key)) { skipped++; continue; }
                    VeteranRuntime.RestoreSnapshot(assignment.Key, assignment.Value);
                    if (chronicles != null && chronicles.TryGetValue(assignment.Key, out var chronicle))
                        VeteranChronicle.Restore(assignment.Key, chronicle);
                    restored++;
                }
                if (campaign != null) VeteranChronicle.RestoreCampaign(campaign);
                feedback = SaveFeedback.Restored;
                VeteranRuntime.Log.LogInfo("Veterancy restored " + restored + " units after load-time research initialization; skipped " + skipped + " currently ineligible units.");
            }
            catch (Exception exception)
            {
                // Undo any assignments already applied, rather than keep a partially restored
                // party. Fail also contains cleanup errors so the base game's load can continue.
                VeteranRuntime.Fail(exception);
                VeteranRuntime.Log.LogError("Veterancy restore failed after research initialization: " + exception);
            }
        }

        private static string GetSavePath(string filename)
        {
            string profile = Path.GetFullPath(Profiles.CurrentProfilePath);
            string path = Path.GetFullPath(Path.Combine(profile, filename));
            string prefix = profile.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Save path escapes the active profile directory.");
            return path;
        }

        private static string SaveIdentity(string path)
        {
            return PersistenceStore.HashText(Path.GetFullPath(path).ToUpperInvariant());
        }
    }
}

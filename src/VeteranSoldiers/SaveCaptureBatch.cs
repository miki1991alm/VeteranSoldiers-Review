#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace VeteranSoldiers;

/// <summary>Prepare a complete serialized copy before publishing any save mutations.</summary>
internal sealed class SaveCaptureBatch<T>
{
    internal T[] Units = Array.Empty<T>();
    internal readonly List<PersistentVeteran> Records = new List<PersistentVeteran>();
    internal int OmittedRecords;

    internal static SaveCaptureBatch<T> Prepare(T[] source, Func<T, PersistentVeteran?> capture,
        Func<T, VeteranSnapshot, T> normalize)
    {
        var result = new SaveCaptureBatch<T>();
        var snapshots = new PersistentVeteran?[source.Length];
        var identities = new HashSet<int>();
        for (int i = 0; i < source.Length; i++)
        {
            var record = capture(source[i]);
            if (record == null) continue;
            // XP or specialization corruption must not retain an otherwise removable health
            // bonus in the base save. Prove normalization data independently first.
            var healthOnly = record.Snapshot;
            healthOnly.Experience = 0;
            healthOnly.Specialization = Specialization.None;
            if (!PersistenceStore.IsValidSnapshot(healthOnly))
                throw new InvalidDataException("Unsafe veteran health snapshot; save normalization was not applied.");
            snapshots[i] = record;
            try { PersistenceStore.Validate(record); }
            catch (InvalidDataException) { result.OmittedRecords++; continue; }
            if (!identities.Add(record.ReferenceId)) throw new InvalidDataException("Duplicate save reference during capture.");
            if (result.Records.Count >= PersistenceStore.MaximumRecords) throw new InvalidDataException("Too many veteran records during capture.");
            result.Records.Add(record);
        }
        // Never mutate the caller's array. A later normalizer failure also discards this copy.
        result.Units = (T[])source.Clone();
        for (int i = 0; i < source.Length; i++)
        {
            var record = snapshots[i];
            if (record != null) result.Units[i] = normalize(source[i], record.Snapshot);
        }
        return result;
    }
}

internal static class SaveCaptureHealth
{
    internal static void Normalize(ref float multiplier, ref float health, VeteranSnapshot snapshot)
    {
        // SaveUnits writes zero for HealthBase.HasHealthMultiplier == false. LoadUnits
        // leaves the prefab/default multiplier untouched for that sentinel; preserve it.
        if (!Progression.Finite(multiplier) || multiplier < 0 || !Progression.Finite(health))
            throw new InvalidDataException("Unsafe serialized health; save normalization was not applied.");
        float normalized = multiplier / snapshot.AppliedHealthFactor;
        if (!Progression.Finite(normalized) || normalized < 0)
            throw new InvalidDataException("Unsafe normalized health multiplier; save normalization was not applied.");
        multiplier = normalized;
        health = Math.Min(health, snapshot.VanillaMaxHealth);
    }
}

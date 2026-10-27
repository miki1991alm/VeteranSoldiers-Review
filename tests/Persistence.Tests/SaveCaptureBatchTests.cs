using System;
using System.IO;
using VeteranSoldiers;

internal static class SaveCaptureBatchTests
{
    private struct Unit
    {
        internal int Id;
        internal float Multiplier;
        internal float Health;
    }
    internal static void Run(Action<bool, string> check)
    {
        var source = new[] {
            new Unit { Id=1, Multiplier=1.2f, Health=115 },
            new Unit { Id=2, Multiplier=1.2f, Health=115 },
            new Unit { Id=3, Multiplier=1.2f, Health=115 }
        };
        var batch = SaveCaptureBatch<Unit>.Prepare(source, unit => Record(unit.Id), Normalize);
        check(batch.Records.Count == 3 && batch.OmittedRecords == 0 && batch.Units[0].Multiplier == 1 && batch.Units[2].Health == 100,
            "complete save batch normalizes all units before publication");
        check(source[0].Multiplier == 1.2f && source[2].Health == 115 && !ReferenceEquals(source, batch.Units),
            "save preparation never mutates the original serialized array");

        batch = SaveCaptureBatch<Unit>.Prepare(source, unit =>
        {
            var record = Record(unit.Id);
            if (unit.Id == 2) record.Snapshot.Experience = double.NaN;
            return record;
        }, Normalize);
        check(batch.OmittedRecords == 1 && batch.Records.Count == 2 && batch.Records[0].ReferenceId == 1 && batch.Records[1].ReferenceId == 3 &&
            batch.Units[0].Multiplier == 1 && batch.Units[1].Multiplier == 1 && batch.Units[2].Multiplier == 1,
            "invalid middle XP omits only its sidecar record while normalizing the entire safe-health batch");
        batch = SaveCaptureBatch<Unit>.Prepare(source, unit =>
        {
            var record = Record(unit.Id);
            if (unit.Id == 2) record.Snapshot.Specialization = (Specialization)99;
            return record;
        }, Normalize);
        check(batch.OmittedRecords == 1 && batch.Units[1].Health == 100,
            "invalid specialization cannot strand a safely removable health bonus in the base save");

        int normalizations = 0;
        bool rejected = false;
        try
        {
            SaveCaptureBatch<Unit>.Prepare(source, unit =>
            {
                var record = Record(unit.Id);
                if (unit.Id == 2) record.Snapshot.VanillaMaxHealth = float.NaN;
                return record;
            }, (unit, snapshot) => { normalizations++; return Normalize(unit, snapshot); });
        }
        catch (InvalidDataException) { rejected = true; }
        check(rejected && normalizations == 0 && source[0].Multiplier == 1.2f && source[2].Health == 115,
            "unsafe health in the middle rejects the whole plan before any normalization, without inventing fallback health");

        rejected = false;
        try
        {
            SaveCaptureBatch<Unit>.Prepare(source, unit => Record(unit.Id), (unit, snapshot) =>
            {
                if (unit.Id == 2) throw new InvalidDataException("Synthetic serialized-health failure.");
                return Normalize(unit, snapshot);
            });
        }
        catch (InvalidDataException) { rejected = true; }
        check(rejected && source[0].Multiplier == 1.2f && source[0].Health == 115 && source[2].Multiplier == 1.2f,
            "a late normalizer failure cannot publish a partially normalized save array");

        var mixed = (Unit[])source.Clone();
        mixed[1].Multiplier = 0;
        mixed[1].Health = 100;
        batch = SaveCaptureBatch<Unit>.Prepare(mixed, unit =>
        {
            var record = Record(unit.Id);
            if (unit.Id == 2) { record.Snapshot.Experience = 0; record.Snapshot.AppliedHealthFactor = 1; record.Snapshot.CurrentHealth = 100; }
            return record;
        }, Normalize);
        check(batch.Records.Count == 3 && batch.OmittedRecords == 0 && batch.Units[0].Multiplier == 1 &&
            batch.Units[1].Multiplier == 0 && batch.Units[1].Health == 100 && batch.Units[2].Multiplier == 1 &&
            mixed[0].Multiplier == 1.2f && mixed[1].Multiplier == 0,
            "real SaveUnits zero sentinel between veterans preserves the whole batch and recruit sidecar record");

        batch = SaveCaptureBatch<Unit>.Prepare(mixed, unit => Record(unit.Id), Normalize);
        check(batch.Records.Count == 3 && batch.Units[1].Multiplier == 0 && batch.Units[1].Health == 100,
            "zero serialized sentinel stays zero even when the independently captured veteran factor is positive");

        foreach (float invalid in new[] { -.1f, float.NaN, float.PositiveInfinity })
        {
            var invalidBatch = (Unit[])source.Clone();
            invalidBatch[1].Multiplier = invalid;
            rejected = false;
            try { SaveCaptureBatch<Unit>.Prepare(invalidBatch, unit => Record(unit.Id), Normalize); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected && invalidBatch[0].Multiplier == 1.2f && invalidBatch[0].Health == 115 && invalidBatch[2].Multiplier == 1.2f,
                "invalid serialized multiplier " + invalid + " rejects publication without modifying earlier units");
        }
    }
    private static PersistentVeteran Record(int id) => new PersistentVeteran {
        ReferenceId=id, ObjectId=68,
        Snapshot=new VeteranSnapshot {Experience=931,AppliedHealthFactor=1.2f,CurrentHealth=115,VanillaMaxHealth=100,Specialization=Specialization.None}
    };
    private static Unit Normalize(Unit unit, VeteranSnapshot snapshot)
    {
        SaveCaptureHealth.Normalize(ref unit.Multiplier, ref unit.Health, snapshot);
        return unit;
    }
}

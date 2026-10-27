using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using VeteranSoldiers;

internal static class Program
{
    private static int _checks;

    private static int Main()
    {
        string directory = Path.Combine(Path.GetFullPath("logs"), "persistence-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string savePath = Path.Combine(directory, "slot.bin");
            File.WriteAllText(savePath, "test-only save bytes, no proprietary content");
            string digest = PersistenceStore.HashFile(savePath, true);
            string identity = PersistenceStore.HashText(Path.GetFullPath(savePath).ToUpperInvariant());
            string sidecar = PersistenceStore.GetSidecarPath(directory, savePath);
            var records = new List<PersistentVeteran>
            {
                Record(7, 12, 302.125, 1.1f, 102.25f, 100),
                Record(29, 12, 0, 1, 81, 100)
            };
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");
            PersistenceStore.WriteAtomic(sidecar, digest, identity, records);
            Dictionary<int, PersistentVeteran> loaded = PersistenceStore.Read(sidecar, digest, identity);
            Check(loaded.Count == 2 && loaded[7].Snapshot.Experience == 302.125 && loaded[29].Snapshot.Experience == 0,
                "two same-type soldiers keep independent XP through locale-independent serialization");
            Check(loaded[7].Snapshot.CurrentHealth == 102.25f && loaded[7].Snapshot.AppliedHealthFactor == 1.1f,
                "health above vanilla maximum is retained in the matching sidecar");

            MustReject(() => PersistenceStore.Read(sidecar, PersistenceStore.HashText("different bytes"), identity),
                "stale sidecar rejected after base save changes");
            MustReject(() => PersistenceStore.Read(sidecar, digest, PersistenceStore.HashText("different slot")),
                "different save identity rejected even with identical contents");

            string oldDocument = File.ReadAllText(sidecar);
            records[0].Snapshot.Specialization = Specialization.Guardian;
            PersistenceStore.WriteAtomic(sidecar, digest, identity, records);
            Check(PersistenceStore.Read(sidecar, digest, identity)[7].Snapshot.Specialization == Specialization.Guardian,
                "schema 2 preserves a personal specialization");
            string specialtyDocument = File.ReadAllText(sidecar);
            var legacy = new XmlDocument(); legacy.LoadXml(specialtyDocument);
            legacy.DocumentElement.SetAttribute("schema", "1");
            legacy.DocumentElement.RemoveChild(legacy.DocumentElement.SelectSingleNode("Campaign"));
            foreach (XmlElement unit in legacy.DocumentElement.SelectNodes("Unit")) unit.RemoveAttribute("specialization");
            File.WriteAllText(sidecar, legacy.OuterXml);
            Check(PersistenceStore.Read(sidecar, digest, identity)[7].Snapshot.Specialization == Specialization.None,
                "schema 1 migrates with no fabricated specialization");
            File.WriteAllText(sidecar, specialtyDocument.Replace("specialization=\"2\"", "specialization=\"99\""));
            MustReject(() => PersistenceStore.Read(sidecar, digest, identity), "unknown specialization rejected");
            File.WriteAllText(sidecar, specialtyDocument.Replace(" specialization=\"2\"", ""));
            MustReject(() => PersistenceStore.Read(sidecar, digest, identity), "schema 2 requires explicit specialization");
            File.WriteAllText(sidecar, oldDocument);
            MustReject(() => PersistenceStore.WriteAtomic(sidecar, digest, identity,
                new List<PersistentVeteran> { Record(1, 12, 40, 1, 50, 100), Record(1, 12, 50, 1, 50, 100) }),
                "duplicate IDs cannot be committed");
            Check(File.ReadAllText(sidecar) == oldDocument, "failed write preserves previous atomic sidecar");
            Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "failed write leaves no temporary payload");

            MustReject(() => PersistenceStore.WriteAtomic(sidecar, digest, identity,
                new List<PersistentVeteran> { Record(1, 12, double.NaN, 1, 50, 100) }), "non-finite XP rejected");
            MustReject(() => PersistenceStore.WriteAtomic(sidecar, digest, identity,
                new List<PersistentVeteran> { Record(1, 12, 1, 0, 50, 100) }), "invalid health factor rejected");
            MustReject(() => PersistenceStore.WriteAtomic(sidecar, digest, identity,
                new List<PersistentVeteran> { Record(1, 12, 1, 1, 0, 100) }), "dead soldier snapshot rejected");

            string duplicate = oldDocument.Replace("referenceId=\"29\"", "referenceId=\"7\"");
            File.WriteAllText(sidecar, duplicate);
            MustReject(() => PersistenceStore.Read(sidecar, digest, identity), "duplicate IDs in input rejected");
            File.WriteAllText(sidecar, "<!DOCTYPE x [<!ENTITY ext SYSTEM 'file:///unread-sensitive-file'>]><x>&ext;</x>");
            MustReject(() => PersistenceStore.Read(sidecar, digest, identity), "DTD and external entity input prohibited");
            File.WriteAllText(sidecar, oldDocument.Replace("schema=\"3\"", "schema=\"99\""));
            MustReject(() => PersistenceStore.Read(sidecar, digest, identity), "unknown schemas rejected");
            File.WriteAllText(sidecar, oldDocument.Substring(0, oldDocument.Length / 2));
            MustReject(() => PersistenceStore.Read(sidecar, digest, identity), "truncated documents rejected");

            PersistenceStore.WriteAtomic(sidecar, digest, identity, new List<PersistentVeteran>());
            Check(PersistenceStore.Read(sidecar, digest, identity).Count == 0, "empty save clears earlier veteran data");
            File.AppendAllText(savePath, "changed");
            Check(digest != PersistenceStore.HashFile(savePath, true), "fingerprint detects changed base save bytes");
            HealthLifecycleTests.Run(Check);
            SaveCaptureBatchTests.Run(Check);
            ChronicleTests.Run(Check, directory, digest, identity);
            PortableSaveTests.Run(Check, directory);
            Console.WriteLine("PASS: " + _checks + " persistence safety checks. These do not validate game runtime hooks.");
            Console.WriteLine("Local evidence: " + directory);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static PersistentVeteran Record(int referenceId, int objectId, double xp, float factor, float health, float vanillaMaximum)
    {
        return new PersistentVeteran
        {
            ReferenceId = referenceId,
            ObjectId = objectId,
            Snapshot = new VeteranSnapshot { Experience = xp, AppliedHealthFactor = factor, CurrentHealth = health, VanillaMaxHealth = vanillaMaximum }
        };
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAILED: " + label);
        _checks++;
        Console.WriteLine("PASS " + label);
    }

    private static void MustReject(Action action, string label)
    {
        try { action(); }
        catch (InvalidDataException) { Check(true, label); return; }
        catch (XmlException) { Check(true, label); return; }
        catch (FormatException) { Check(true, label); return; }
        throw new Exception("FAILED: accepted " + label);
    }
}

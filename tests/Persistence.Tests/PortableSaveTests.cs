using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VeteranSoldiers;

internal static class PortableSaveTests
{
    internal static void Run(Action<bool, string> check, string directory)
    {
        byte[] native = Encoding.UTF8.GetBytes("Synthetic native object bytes; no game data or BinaryFormatter.");
        var records = new List<PersistentVeteran>
        {
            new PersistentVeteran { ReferenceId = 17, ObjectId = -12, Snapshot = new VeteranSnapshot
                { Experience = 931, AppliedHealthFactor = 1.3f, CurrentHealth = 25.875f, VanillaMaxHealth = 20, Specialization = Specialization.Guardian },
                Chronicle = new ChronicleRecord { Name = "Aldric", Favorite = true } },
            new PersistentVeteran { ReferenceId = 42, ObjectId = -12, Snapshot = new VeteranSnapshot
                { Experience = 301, AppliedHealthFactor = 1.1f, CurrentHealth = 20, VanillaMaxHealth = 20, Specialization = Specialization.None },
                Chronicle = new ChronicleRecord { Name = "Borin" } }
        };
        var campaign = new CampaignSnapshot { EffectiveDamage = 12345, ConfirmedKills = 37, Promotions = 9, ObservedDeaths = 1 };
        var fallen = new ChronicleRecord { Name = "Fallen" };
        ChronicleRules.AddEvent(fallen, ChronicleEventKind.Fallen, 0, 0);
        campaign.Memorial.Add(fallen);
        campaign.Hall.ActiveDoctrine = 1; campaign.Hall.UnlockedDoctrines = 3;
        campaign.Hall.PendingDoctrine = 2; campaign.Hall.ResearchProgress = 32.5;
        campaign.Hall.CombatExperience = 600; campaign.Hall.Buildings.Add(new HallBuildingSnapshot { ReferenceId = 19, BaseObjectId = 68 });

        string source = Path.Combine(directory, "Computer A", "Profile A", "slot");
        Directory.CreateDirectory(Path.GetDirectoryName(source)); File.WriteAllBytes(source, native);
        check(PortableSave.Read(source, out var legacyHash) == null && legacyHash == PersistenceStore.HashFile(source, true),
            "unmodified legacy save remains readable with its exact legacy fingerprint");
        string fullHash = PortableSave.Append(source, records, campaign);
        byte[] portableBytes = File.ReadAllBytes(source);
        check(portableBytes.Take(native.Length).SequenceEqual(native), "portable write leaves every native game byte unchanged");
        check(fullHash == PersistenceStore.HashFile(source, true), "portable commit returns the full synchronized file checksum");
        check(Encoding.UTF8.GetString(portableBytes).IndexOf(directory, StringComparison.Ordinal) < 0,
            "portable payload contains no absolute Windows save path");

        string destination = Path.Combine(directory, "Computer B", "Other Windows User", "DifferentProfile", "renamed_slot");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(source, destination);
        var loaded = PortableSave.Read(destination, out var copiedHash);
        check(copiedHash == fullHash && loaded != null && loaded.Records.Count == 2,
            "copying only the save to a different user profile and filename restores portable records without a sidecar");
        check(loaded.Records[17].Snapshot.Experience == 931 && loaded.Records[42].Snapshot.Experience == 301,
            "two same-type soldiers retain different levels by exact save reference across paths");
        check(loaded.Records[17].ObjectId == -12 && loaded.Records[17].Snapshot.Specialization == Specialization.Guardian &&
            loaded.Records[17].Snapshot.AppliedHealthFactor == 1.3f && loaded.Records[17].Snapshot.CurrentHealth == 25.875f,
            "portable record retains object type, specialization and non-stacking health snapshot");
        check(loaded.Records[17].Chronicle.Name == "Aldric" && loaded.Records[17].Chronicle.Favorite &&
            loaded.Records[17].Chronicle.Identity == records[0].Chronicle.Identity,
            "portable chronicle keeps personal name, favorite and unique soldier identity");
        check(loaded.Campaign.EffectiveDamage == 12345 && loaded.Campaign.ConfirmedKills == 37 &&
            loaded.Campaign.Memorial.Single().Identity == fallen.Identity,
            "portable campaign retains counters and memorial identities");
        check(loaded.Campaign.Hall.Buildings.Single().ReferenceId == 19 && loaded.Campaign.Hall.ActiveDoctrine == 1 &&
            loaded.Campaign.Hall.PendingDoctrine == 2 && loaded.Campaign.Hall.ResearchProgress == 32.5,
            "portable campaign retains exact Hall references and active/pending doctrine progress");
        MustReject(() => PortableSave.Append(destination, records, campaign), check, "refuse duplicate trailer instead of growing the save on retries");
        check(File.ReadAllBytes(destination).SequenceEqual(portableBytes), "refused duplicate write preserves the committed save");

        byte[] damaged = (byte[])portableBytes.Clone(); damaged[0] ^= 1;
        RejectBytes(damaged, check, "reject portable XP if native game bytes change");
        damaged = (byte[])portableBytes.Clone(); damaged[native.Length + 10] ^= 1;
        RejectBytes(damaged, check, "reject corrupted portable XML by checksum before parsing");
        int footer = portableBytes.Length - PortableSave.FooterLength;
        foreach (int version in new[] { 0, 2, int.MaxValue })
        {
            damaged = (byte[])portableBytes.Clone(); Array.Copy(BitConverter.GetBytes(version), 0, damaged, footer, 4);
            RejectBytes(damaged, check, "reject unsupported portable container version " + version);
        }
        foreach (int length in new[] { -1, 0, int.MaxValue })
        {
            damaged = (byte[])portableBytes.Clone(); Array.Copy(BitConverter.GetBytes(length), 0, damaged, footer + 12, 4);
            RejectBytes(damaged, check, "bound untrusted portable XML allocation for length " + length);
        }
        damaged = (byte[])portableBytes.Clone(); Array.Copy(BitConverter.GetBytes(long.MaxValue), 0, damaged, footer + 4, 8);
        RejectBytes(damaged, check, "reject overflowing native length in untrusted portable footer");
        RejectBytes(Encoding.ASCII.GetBytes("VS-PORTABLE-SAVE"), check, "recognize and reject a truncated portable footer");

        var invalid = new List<PersistentVeteran> { new PersistentVeteran { ReferenceId = 17, ObjectId = 1,
            Snapshot = new VeteranSnapshot { Experience = double.NaN, AppliedHealthFactor = 1, CurrentHealth = 20, VanillaMaxHealth = 20 } } };
        using (var stream = new MemoryStream())
        {
            stream.Write(native, 0, native.Length);
            MustReject(() => PortableSave.Append(stream, invalid, new CampaignSnapshot()), check, "validate the full snapshot before appending");
            check(stream.ToArray().SequenceEqual(native), "invalid snapshot cannot change even the end of the native save");
        }
        foreach (int successfulBytes in new[] { 0, 10, portableBytes.Length - native.Length - 5 })
        {
            using (var stream = new FailingAppendStream(native, successfulBytes))
            {
                bool failed = false;
                try { PortableSave.Append(stream, records, campaign); } catch (IOException) { failed = true; }
                check(failed && stream.ToArray().SequenceEqual(native), "injected append I/O failure restores exact native bytes after " + successfulBytes + " appended bytes");
            }
        }
        using (var held = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bool locked = false;
            try { PortableSave.Append(source, records, campaign); } catch (IOException) { locked = true; }
            check(locked, "portable writer refuses a slot still held open by another reader");
        }
        using (var stream = new MemoryStream())
        {
            stream.Write(native, 0, native.Length);
            PortableSave.Append(stream, new List<PersistentVeteran>(), campaign);
            check(PortableSave.Read(stream, out _).Campaign.Hall.Buildings.Single().ReferenceId == 19,
                "campaign and Hall progress remain portable even with no surviving veteran units");
        }
        // The legacy backup remains exact-path bound; it is never used to override portable data.
        string local = PersistenceStore.GetSidecarPath(directory, destination);
        PersistenceStore.WriteAtomic(local, PersistenceStore.HashText("stale other save"), PersistenceStore.HashText(destination), records, campaign);
        check(PortableSave.Read(destination, out _).Records[17].Snapshot.Experience == 931,
            "portable progress is independent of an absent or stale destination-computer sidecar");
    }

    private static void RejectBytes(byte[] bytes, Action<bool, string> check, string label)
    {
        using (var stream = new MemoryStream(bytes, false)) MustReject(() => PortableSave.Read(stream, out _), check, label);
    }
    private static void MustReject(Action action, Action<bool, string> check, string label)
    {
        try { action(); }
        catch (InvalidDataException) { check(true, label); return; }
        throw new Exception("FAILED: accepted " + label);
    }
    private sealed class FailingAppendStream : MemoryStream
    {
        private int remaining;
        internal FailingAppendStream(byte[] native, int successfulBytes)
        {
            base.Write(native, 0, native.Length); remaining = successfulBytes;
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            int accepted = Math.Min(remaining, count);
            if (accepted > 0) base.Write(buffer, offset, accepted);
            remaining -= accepted;
            if (accepted != count) throw new IOException("Synthetic disk write failure.");
        }
        public override void WriteByte(byte value) { Write(new[] { value }, 0, 1); }
    }
}

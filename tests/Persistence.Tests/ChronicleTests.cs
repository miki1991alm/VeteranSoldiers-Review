using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using VeteranSoldiers;

internal static class ChronicleTests
{
    internal static void Run(Action<bool, string> check, string directory, string digest, string identity)
    {
        var record = new ChronicleRecord { Name = "Aldric", Favorite = true };
        ChronicleRules.Combat(record, double.NaN, true, true, true);
        check(record.CombatAwards == 0 && record.Medals == ChronicleMedal.None, "invalid combat cannot create medals");
        ChronicleRules.Combat(record, 250, false, true, true);
        check(record.ConfirmedKills == 0 && record.Medals == ChronicleMedal.Blooded, "damage contribution awards merit without inventing a killing blow");
        ChronicleRules.Combat(record, 1750, true, true, false);
        check(record.ConfirmedKills == 1 && record.PowerfulKills == 1 && (record.Medals & ChronicleMedal.Champion) != 0,
            "only confirmed killing credit awards champion");
        for (int i = 1; i <= 25; i++) ChronicleRules.Promote(record, i);
        check(record.History.Count == ChronicleRules.MaximumHistory, "history remains bounded under repeated promotions");
        ChronicleRules.Validate(record);
        check(ChronicleRules.SanitizeName("  A\nB <color=red>\u202e\u0001  ").IndexOf('<') < 0 &&
            ChronicleRules.SanitizeName(new string('x', 100)).Length == 28 && ChronicleRules.SanitizeName("   ") == "",
            "names remove markup/control characters and cap length");
        var clone = record.Clone(); clone.Name = "Other"; clone.History[0].Value = 999;
        check(record.Name == "Aldric" && record.History[0].Value != 999, "save snapshot deeply isolates mutable history");
        var fallen = record.Clone(); fallen.Identity = Guid.NewGuid().ToString("N");
        ChronicleRules.AddEvent(fallen, ChronicleEventKind.Fallen, 0, fallen.HighestLevel);
        var campaign = new CampaignSnapshot { EffectiveDamage = 2000, ConfirmedKills = 1, Promotions = 25, ObservedDeaths = 1 };
        campaign.Memorial.Add(fallen);
        campaign.Hall.ActiveDoctrine = 1; campaign.Hall.PendingDoctrine = 2; campaign.Hall.UnlockedDoctrines = 3;
        campaign.Hall.ResearchProgress = 32.5; campaign.Hall.CombatExperience = 600;
        campaign.Hall.Buildings.Add(new HallBuildingSnapshot { ReferenceId = 888, BaseObjectId = 68 });
        var units = new List<PersistentVeteran> { new PersistentVeteran { ReferenceId = 7, ObjectId = 10,
            Snapshot = new VeteranSnapshot { Experience = 0, AppliedHealthFactor = 1, CurrentHealth = 20, VanillaMaxHealth = 20 }, Chronicle = record } };
        string path = Path.Combine(directory, "chronicle.xml");
        PersistenceStore.WriteAtomic(path, digest, identity, units, campaign);
        string original = File.ReadAllText(path);
        var loaded = PersistenceStore.Read(path, digest, identity, out CampaignSnapshot result);
        check(loaded[7].Chronicle.Name == "Aldric" && loaded[7].Chronicle.Favorite && loaded[7].Snapshot.Experience == 0,
            "named and favorite recruits retain identity with zero experience");
        check(result.Memorial.Count == 1 && result.Hall.Buildings[0].ReferenceId == 888 && result.Hall.ResearchProgress == 32.5,
            "campaign memorial and exact Hall references round-trip with pending research");
        // A different save state must replace future history, never merge it.
        PersistenceStore.WriteAtomic(path, digest, identity, units, new CampaignSnapshot());
        PersistenceStore.Read(path, digest, identity, out var earlier);
        check(earlier.Memorial.Count == 0 && earlier.Hall.ActiveDoctrine == 0 && earlier.Promotions == 0,
            "loading an earlier campaign snapshot cannot retain later deaths or doctrines");
        void Reject(Action action, string label)
        {
            bool rejected = false;
            try { action(); } catch (InvalidDataException) { rejected = true; } catch (XmlException) { rejected = true; }
            check(rejected, label);
        }
        void Malformed(string content, string label)
        {
            File.WriteAllText(path, content);
            Reject(() => PersistenceStore.Read(path, digest, identity), label);
        }
        Malformed(original.Replace(fallen.Identity, record.Identity), "same identity cannot exist as living soldier and memorial entry");
        Malformed(original.Replace("progress=\"32.5\"", "progress=\"NaN\""), "nonfinite doctrine progress rejected");
        Malformed(original.Replace("active=\"1\"", "active=\"4\""), "unknown doctrine rejected");
        Malformed(original.Replace("unlocked=\"3\"", "unlocked=\"0\""), "active and pending doctrines require recorded unlocks");
        Malformed(original.Replace("pending=\"2\"", "pending=\"0\""), "research progress requires a pending doctrine");
        Malformed(original.Replace("referenceId=\"888\" baseObjectId=\"68\" />", "referenceId=\"888\" baseObjectId=\"68\" /><Building referenceId=\"888\" baseObjectId=\"68\" />"),
            "duplicate Hall references rejected");
        Malformed(original.Replace("favorite=\"1\"", "favorite=\"2\""), "invalid favorite state rejected");
        Malformed(original.Replace("name=\"Aldric\"", "name=\"&lt;color&gt;\""), "rich text in persisted names rejected");
        Malformed(original.Replace("medals=\"", "unknown=\"0\" medals=\""), "unknown chronicle attributes rejected");
        Malformed(original.Replace("damage=\"2000\"", "damage=\"0\""), "medals require recorded combat evidence");
        var invalid = record.Clone(); invalid.History.Add(new ChronicleEvent());
        Reject(() => ChronicleRules.Validate(invalid), "oversized history rejected before persistence");
        campaign.Memorial[0].History.RemoveAt(campaign.Memorial[0].History.Count - 1);
        Reject(() => ChronicleRules.ValidateCampaign(campaign), "memorial requires a final observed-death event");
        var legacy = new XmlDocument(); legacy.LoadXml(original); legacy.DocumentElement.SetAttribute("schema", "2");
        legacy.DocumentElement.RemoveChild(legacy.SelectSingleNode("/VeteranSoldiers/Campaign"));
        foreach (XmlElement unit in legacy.SelectNodes("/VeteranSoldiers/Unit")) { unit.RemoveChild(unit.SelectSingleNode("Chronicle")); unit.IsEmpty = true; }
        File.WriteAllText(path, legacy.OuterXml);
        loaded = PersistenceStore.Read(path, digest, identity, out earlier);
        check(loaded[7].Chronicle == null && earlier.Memorial.Count == 0 && earlier.EffectiveDamage == 0,
            "schema 2 migration supplies no fabricated combat career or campaign milestones");
    }
}

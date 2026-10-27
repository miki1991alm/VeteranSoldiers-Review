#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VeteranSoldiers
{
    [Flags]
    internal enum ChronicleMedal { None = 0, Blooded = 1, BattleHardened = 2, Slayer = 4, Champion = 8, SiegeBreaker = 16, Veteran = 32 }
    internal enum ChronicleEventKind { FirstCombat = 1, Promotion = 2, Medal = 3, Fallen = 4, Specialization = 5 }

    internal sealed class ChronicleEvent
    {
        internal ChronicleEventKind Kind;
        internal double Value;
        internal int Level;
        internal ChronicleEvent Clone() { return (ChronicleEvent)MemberwiseClone(); }
    }

    /// <summary>No entity index or location is a persistent identity. The GUID only follows an exact save reference.</summary>
    internal sealed class ChronicleRecord
    {
        internal string Identity = Guid.NewGuid().ToString("N");
        internal string Name = string.Empty;
        internal bool Favorite;
        internal double EffectiveDamage;
        internal int CombatAwards;
        internal int ConfirmedKills;
        internal int PowerfulKills;
        internal int StructureKills;
        internal int HighestLevel;
        internal int ObjectId;
        internal ChronicleMedal Medals;
        internal readonly List<ChronicleEvent> History = new List<ChronicleEvent>();

        internal ChronicleRecord Clone()
        {
            var value = new ChronicleRecord { Identity = Identity, Name = Name, Favorite = Favorite,
                EffectiveDamage = EffectiveDamage, CombatAwards = CombatAwards, ConfirmedKills = ConfirmedKills,
                PowerfulKills = PowerfulKills, StructureKills = StructureKills, HighestLevel = HighestLevel,
                ObjectId = ObjectId, Medals = Medals };
            foreach (var item in History) value.History.Add(item.Clone());
            return value;
        }
    }

    internal sealed class CampaignSnapshot
    {
        internal readonly List<ChronicleRecord> Memorial = new List<ChronicleRecord>();
        internal double EffectiveDamage;
        internal int ConfirmedKills;
        internal int Promotions;
        internal int ObservedDeaths;
        internal HallSnapshot Hall = new HallSnapshot();
    }

    internal sealed class HallBuildingSnapshot
    {
        internal int ReferenceId;
        internal int BaseObjectId;
    }
    internal sealed class HallSnapshot
    {
        internal readonly List<HallBuildingSnapshot> Buildings = new List<HallBuildingSnapshot>();
        internal int ActiveDoctrine;
        internal int PendingDoctrine;
        internal double ResearchProgress;
        internal int UnlockedDoctrines;
        internal double CombatExperience;
    }

    internal static class ChronicleRules
    {
        internal const int MaximumNameLength = 28;
        internal const int MaximumHistory = 12;
        internal const int MaximumMemorial = 256;
        internal const double MaximumDamage = 1e15;
        internal const int MaximumCounter = 1000000000;
        internal const ChronicleMedal AllMedals = ChronicleMedal.Blooded | ChronicleMedal.BattleHardened |
            ChronicleMedal.Slayer | ChronicleMedal.Champion | ChronicleMedal.SiegeBreaker | ChronicleMedal.Veteran;

        internal static string SanitizeName(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            var result = new StringBuilder(MaximumNameLength);
            bool space = false;
            foreach (char value in input!)
            {
                // Restrict names to display text: no TMP markup, control/format characters or invalid UTF-16.
                if (char.IsWhiteSpace(value)) { space = result.Length > 0; continue; }
                if (!char.IsLetterOrDigit(value) && value != '-' && value != '\'' && value != '.') continue;
                if (space && result.Length < MaximumNameLength) result.Append(' ');
                space = false;
                if (result.Length == MaximumNameLength) break;
                result.Append(value);
            }
            return result.ToString().Trim();
        }

        internal static int Increment(int value) { return Math.Min(MaximumCounter, value + 1); }
        internal static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        internal static void AddEvent(ChronicleRecord record, ChronicleEventKind kind, double value = 0, int level = 0)
        {
            if (record.History.Count >= MaximumHistory) record.History.RemoveAt(0);
            record.History.Add(new ChronicleEvent { Kind = kind, Value = value, Level = level });
        }

        internal static void Combat(ChronicleRecord record, double damage, bool confirmedKill, bool powerful, bool structure)
        {
            if (!Finite(damage) || damage <= 0) return;
            if (record.CombatAwards == 0) AddEvent(record, ChronicleEventKind.FirstCombat);
            record.CombatAwards = Increment(record.CombatAwards);
            record.EffectiveDamage = Math.Min(MaximumDamage, record.EffectiveDamage + damage);
            if (confirmedKill)
            {
                record.ConfirmedKills = Increment(record.ConfirmedKills);
                if (powerful) record.PowerfulKills = Increment(record.PowerfulKills);
                if (structure) record.StructureKills = Increment(record.StructureKills);
            }
            Award(record, ChronicleMedal.Blooded, record.EffectiveDamage >= 250);
            Award(record, ChronicleMedal.BattleHardened, record.EffectiveDamage >= 2000);
            Award(record, ChronicleMedal.Slayer, record.ConfirmedKills >= 25);
            Award(record, ChronicleMedal.Champion, record.PowerfulKills >= 1);
            Award(record, ChronicleMedal.SiegeBreaker, record.StructureKills >= 1);
        }

        internal static void Promote(ChronicleRecord record, int level)
        {
            if (level <= record.HighestLevel || level < 0 || level > 100) return;
            record.HighestLevel = level;
            AddEvent(record, ChronicleEventKind.Promotion, 0, level);
            Award(record, ChronicleMedal.Veteran, level >= 2);
        }

        private static void Award(ChronicleRecord record, ChronicleMedal medal, bool earned)
        {
            if (!earned || (record.Medals & medal) != 0) return;
            record.Medals |= medal;
            AddEvent(record, ChronicleEventKind.Medal, (int)medal);
        }

        internal static void Validate(ChronicleRecord record, bool fallen = false)
        {
            if (record == null || record.Identity == null || record.Identity.Length != 32 ||
                !Guid.TryParseExact(record.Identity, "N", out Guid identity) || identity == Guid.Empty || identity.ToString("N") != record.Identity ||
                record.Name != SanitizeName(record.Name) || !Finite(record.EffectiveDamage) ||
                record.EffectiveDamage < 0 || record.EffectiveDamage > MaximumDamage ||
                record.CombatAwards < 0 || record.CombatAwards > MaximumCounter ||
                record.ConfirmedKills < 0 || record.ConfirmedKills > record.CombatAwards ||
                record.PowerfulKills < 0 || record.PowerfulKills > record.ConfirmedKills ||
                record.StructureKills < 0 || record.StructureKills > record.ConfirmedKills ||
                record.HighestLevel < 0 || record.HighestLevel > 100 || (record.Medals & ~AllMedals) != 0 ||
                record.History.Count > MaximumHistory)
                throw new InvalidDataException("Invalid soldier chronicle.");
            int fallenEvents = 0;
            if (((record.Medals & ChronicleMedal.Blooded) != 0 && record.EffectiveDamage < 250) ||
                ((record.Medals & ChronicleMedal.BattleHardened) != 0 && record.EffectiveDamage < 2000) ||
                ((record.Medals & ChronicleMedal.Slayer) != 0 && record.ConfirmedKills < 25) ||
                ((record.Medals & ChronicleMedal.Champion) != 0 && record.PowerfulKills < 1) ||
                ((record.Medals & ChronicleMedal.SiegeBreaker) != 0 && record.StructureKills < 1) ||
                ((record.Medals & ChronicleMedal.Veteran) != 0 && record.HighestLevel < 2))
                throw new InvalidDataException("Medal lacks recorded evidence.");
            foreach (var entry in record.History)
            {
                if (entry == null || !Enum.IsDefined(typeof(ChronicleEventKind), entry.Kind) ||
                    !Finite(entry.Value) || entry.Value < 0 || entry.Value > MaximumDamage || entry.Level < 0 || entry.Level > 100)
                    throw new InvalidDataException("Invalid chronicle event.");
                if ((entry.Kind == ChronicleEventKind.FirstCombat && (entry.Value != 0 || entry.Level != 0)) ||
                    (entry.Kind == ChronicleEventKind.Promotion && (entry.Value != 0 || entry.Level <= 0 || entry.Level > record.HighestLevel)) ||
                    (entry.Kind == ChronicleEventKind.Fallen && (entry.Value != 0 || entry.Level != record.HighestLevel)) ||
                    (entry.Kind == ChronicleEventKind.Specialization && (entry.Value < 1 || entry.Value > 3 || entry.Value != Math.Floor(entry.Value) || entry.Level != 0)) ||
                    (entry.Kind == ChronicleEventKind.Medal && (entry.Level != 0 || entry.Value != Math.Floor(entry.Value) || entry.Value < 1 || entry.Value > 32 ||
                        (((int)entry.Value & ((int)entry.Value - 1)) != 0) || (record.Medals & (ChronicleMedal)(int)entry.Value) == 0)))
                    throw new InvalidDataException("Chronicle event contradicts soldier state.");
                if (entry.Kind == ChronicleEventKind.Fallen) fallenEvents++;
            }
            if ((!fallen && fallenEvents != 0) || (fallen && (fallenEvents != 1 || record.History[record.History.Count - 1].Kind != ChronicleEventKind.Fallen)))
                throw new InvalidDataException("Invalid memorial death evidence.");
        }

        internal static void ValidateCampaign(CampaignSnapshot campaign)
        {
            if (campaign == null || !Finite(campaign.EffectiveDamage) || campaign.EffectiveDamage < 0 || campaign.EffectiveDamage > MaximumDamage ||
                campaign.ConfirmedKills < 0 || campaign.ConfirmedKills > MaximumCounter || campaign.Promotions < 0 || campaign.Promotions > MaximumCounter ||
                campaign.ObservedDeaths < 0 || campaign.ObservedDeaths > MaximumCounter || campaign.Memorial.Count > MaximumMemorial ||
                campaign.Memorial.Count > campaign.ObservedDeaths) throw new InvalidDataException("Invalid campaign chronicle.");
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in campaign.Memorial)
            {
                Validate(record, true);
                if (!identities.Add(record.Identity)) throw new InvalidDataException("Duplicate memorial identity.");
            }
            ValidateHall(campaign.Hall);
        }

        internal static void ValidateHall(HallSnapshot hall)
        {
            if (hall == null || hall.Buildings.Count > 128 || hall.ActiveDoctrine < 0 || hall.ActiveDoctrine > 3 ||
                hall.PendingDoctrine < 0 || hall.PendingDoctrine > 3 || !Finite(hall.ResearchProgress) ||
                hall.ResearchProgress < 0 || hall.ResearchProgress > 60 || hall.UnlockedDoctrines < 0 || hall.UnlockedDoctrines > 7 ||
                !Finite(hall.CombatExperience) || hall.CombatExperience < 0 || hall.CombatExperience > MaximumDamage)
                throw new InvalidDataException("Invalid Hall state.");
            if ((hall.PendingDoctrine == 0 && hall.ResearchProgress != 0) ||
                (hall.ActiveDoctrine > 0 && (hall.UnlockedDoctrines & (1 << (hall.ActiveDoctrine - 1))) == 0) ||
                (hall.PendingDoctrine > 0 && (hall.UnlockedDoctrines & (1 << (hall.PendingDoctrine - 1))) == 0))
                throw new InvalidDataException("Hall doctrine contradicts its unlock or research state.");
            var refs = new HashSet<int>();
            foreach (var building in hall.Buildings)
                if (building == null || building.ReferenceId <= 0 || !refs.Add(building.ReferenceId))
                    throw new InvalidDataException("Duplicate or invalid Hall save reference.");
        }
    }
}

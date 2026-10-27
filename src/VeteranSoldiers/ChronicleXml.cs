#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Xml;

namespace VeteranSoldiers;

/// <summary>Strict, bounded schema 3 extension; contains only data, never CLR type names.</summary>
internal static class ChronicleXml
{
    private static void Attr(XmlWriter writer, string name, object value)
    {
        string text = value is double number ? number.ToString("R", CultureInfo.InvariantCulture) :
            Convert.ToString(value, CultureInfo.InvariantCulture)!;
        writer.WriteAttributeString(name, text);
    }
    internal static void WriteRecord(XmlWriter writer, string element, ChronicleRecord record)
    {
        writer.WriteStartElement(element);
        Attr(writer, "identity", record.Identity); Attr(writer, "name", record.Name); Attr(writer, "favorite", record.Favorite ? 1 : 0);
        Attr(writer, "damage", record.EffectiveDamage); Attr(writer, "combat", record.CombatAwards); Attr(writer, "kills", record.ConfirmedKills);
        Attr(writer, "powerful", record.PowerfulKills); Attr(writer, "structures", record.StructureKills); Attr(writer, "level", record.HighestLevel);
        Attr(writer, "objectId", record.ObjectId); Attr(writer, "medals", (int)record.Medals);
        foreach (var entry in record.History)
        {
            writer.WriteStartElement("Event"); Attr(writer, "kind", (int)entry.Kind); Attr(writer, "value", entry.Value); Attr(writer, "level", entry.Level);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }
    internal static void WriteCampaign(XmlWriter writer, CampaignSnapshot campaign)
    {
        writer.WriteStartElement("Campaign");
        Attr(writer, "damage", campaign.EffectiveDamage); Attr(writer, "kills", campaign.ConfirmedKills);
        Attr(writer, "promotions", campaign.Promotions); Attr(writer, "deaths", campaign.ObservedDeaths);
        foreach (var fallen in campaign.Memorial) WriteRecord(writer, "Fallen", fallen);
        var hall = campaign.Hall;
        writer.WriteStartElement("Hall"); Attr(writer, "active", hall.ActiveDoctrine); Attr(writer, "pending", hall.PendingDoctrine);
        Attr(writer, "progress", hall.ResearchProgress); Attr(writer, "unlocked", hall.UnlockedDoctrines); Attr(writer, "combatXp", hall.CombatExperience);
        foreach (var building in hall.Buildings)
        {
            writer.WriteStartElement("Building"); Attr(writer, "referenceId", building.ReferenceId); Attr(writer, "baseObjectId", building.BaseObjectId);
            writer.WriteEndElement();
        }
        writer.WriteEndElement(); writer.WriteEndElement();
    }
    internal static void Attributes(XmlReader reader, params string[] names)
    {
        if (reader.NamespaceURI.Length != 0 || reader.AttributeCount != names.Length) throw new InvalidDataException("Unexpected chronicle attributes.");
        foreach (var name in names) if (reader.GetAttribute(name) == null) throw new InvalidDataException("Missing chronicle attribute.");
    }
    private static int Int(XmlReader reader, string name) { return int.Parse(reader.GetAttribute(name)!, CultureInfo.InvariantCulture); }
    private static double Number(XmlReader reader, string name) { return double.Parse(reader.GetAttribute(name)!, CultureInfo.InvariantCulture); }
    internal static ChronicleRecord ReadRecord(XmlReader reader, string element, bool fallen)
    {
        Attributes(reader, "identity", "name", "favorite", "damage", "combat", "kills", "powerful", "structures", "level", "objectId", "medals");
        int favorite = Int(reader, "favorite");
        if (favorite != 0 && favorite != 1) throw new InvalidDataException("Invalid favorite state.");
        var record = new ChronicleRecord { Identity = reader.GetAttribute("identity")!, Name = reader.GetAttribute("name")!, Favorite = favorite == 1,
            EffectiveDamage = Number(reader, "damage"), CombatAwards = Int(reader, "combat"), ConfirmedKills = Int(reader, "kills"),
            PowerfulKills = Int(reader, "powerful"), StructureKills = Int(reader, "structures"), HighestLevel = Int(reader, "level"),
            ObjectId = Int(reader, "objectId"), Medals = (ChronicleMedal)Int(reader, "medals") };
        bool empty = reader.IsEmptyElement; reader.ReadStartElement(element);
        if (!empty)
        {
            while (reader.MoveToContent() == XmlNodeType.Element)
            {
                if (reader.Name != "Event" || !reader.IsEmptyElement || record.History.Count >= ChronicleRules.MaximumHistory)
                    throw new InvalidDataException("Unexpected or excessive chronicle history.");
                Attributes(reader, "kind", "value", "level");
                record.History.Add(new ChronicleEvent { Kind = (ChronicleEventKind)Int(reader, "kind"), Value = Number(reader, "value"), Level = Int(reader, "level") });
                reader.ReadStartElement("Event");
            }
            reader.ReadEndElement();
        }
        ChronicleRules.Validate(record, fallen);
        return record;
    }
    internal static CampaignSnapshot ReadCampaign(XmlReader reader)
    {
        Attributes(reader, "damage", "kills", "promotions", "deaths");
        var campaign = new CampaignSnapshot { EffectiveDamage = Number(reader, "damage"), ConfirmedKills = Int(reader, "kills"),
            Promotions = Int(reader, "promotions"), ObservedDeaths = Int(reader, "deaths") };
        if (reader.IsEmptyElement) throw new InvalidDataException("Missing Hall state.");
        reader.ReadStartElement("Campaign");
        bool foundHall = false;
        while (reader.MoveToContent() == XmlNodeType.Element)
        {
            if (reader.Name == "Fallen" && campaign.Memorial.Count < ChronicleRules.MaximumMemorial)
                campaign.Memorial.Add(ReadRecord(reader, "Fallen", true));
            else if (reader.Name == "Hall" && !foundHall) { campaign.Hall = ReadHall(reader); foundHall = true; }
            else throw new InvalidDataException("Unexpected or excessive campaign state.");
        }
        reader.ReadEndElement();
        if (!foundHall) throw new InvalidDataException("Missing Hall state.");
        ChronicleRules.ValidateCampaign(campaign);
        return campaign;
    }
    private static HallSnapshot ReadHall(XmlReader reader)
    {
        Attributes(reader, "active", "pending", "progress", "unlocked", "combatXp");
        var hall = new HallSnapshot { ActiveDoctrine = Int(reader, "active"), PendingDoctrine = Int(reader, "pending"),
            ResearchProgress = Number(reader, "progress"), UnlockedDoctrines = Int(reader, "unlocked"), CombatExperience = Number(reader, "combatXp") };
        bool empty = reader.IsEmptyElement; reader.ReadStartElement("Hall");
        if (!empty)
        {
            while (reader.MoveToContent() == XmlNodeType.Element)
            {
                if (reader.Name != "Building" || !reader.IsEmptyElement || hall.Buildings.Count >= 128)
                    throw new InvalidDataException("Unexpected or excessive Hall building state.");
                Attributes(reader, "referenceId", "baseObjectId");
                hall.Buildings.Add(new HallBuildingSnapshot { ReferenceId = Int(reader, "referenceId"), BaseObjectId = Int(reader, "baseObjectId") });
                reader.ReadStartElement("Building");
            }
            reader.ReadEndElement();
        }
        ChronicleRules.ValidateHall(hall);
        return hall;
    }
}

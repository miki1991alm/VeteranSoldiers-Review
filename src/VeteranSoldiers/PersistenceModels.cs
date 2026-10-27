#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace VeteranSoldiers
{
    /// <summary>Runtime state captured at precisely the same instant as the base-game save.</summary>
    public struct VeteranSnapshot
    {
        public double Experience;
        public float AppliedHealthFactor;
        public float CurrentHealth;
        public float VanillaMaxHealth;
        public Specialization Specialization;
    }

    internal sealed class PersistentVeteran
    {
        public int ReferenceId;
        public int ObjectId;
        public VeteranSnapshot Snapshot;
        public ChronicleRecord? Chronicle;
    }

    /// <summary>
    /// Sidecars contain no ECS index. Reference IDs are only meaningful inside one exact save.
    /// This format intentionally does not use BinaryFormatter or deserialize arbitrary types.
    /// </summary>
    internal static class PersistenceStore
    {
        internal const int SchemaVersion = 3;
        internal const int MaximumRecords = 100000;
        internal const long MaximumDocumentBytes = 32 * 1024 * 1024;

        internal static string GetSidecarPath(string directory, string originalSavePath)
        {
            return Path.Combine(directory, HashText(Path.GetFullPath(originalSavePath).ToUpperInvariant()) + ".xml");
        }

        internal static string HashFile(string path, bool exclusive)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                       exclusive ? FileShare.None : FileShare.ReadWrite))
            using (var sha = SHA256.Create())
                return Hex(sha.ComputeHash(stream));
        }

        internal static string HashText(string value)
        {
            using (var sha = SHA256.Create())
                return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(value)));
        }

        private static string Hex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        internal static void WriteAtomic(string sidecarPath, string saveHash, string identityHash,
            IList<PersistentVeteran> records, CampaignSnapshot? campaign = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sidecarPath)!);
            string temporary = sidecarPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    WriteDocument(stream, saveHash, identityHash, records, campaign);
                    stream.Flush(true);
                }
                if (File.Exists(sidecarPath)) File.Replace(temporary, sidecarPath, null);
                else File.Move(temporary, sidecarPath);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        internal static void WriteDocument(Stream stream, string saveHash, string identityHash,
            IList<PersistentVeteran> records, CampaignSnapshot? campaign = null)
        {
            campaign = campaign ?? new CampaignSnapshot();
            ChronicleRules.ValidateCampaign(campaign);
            if (!IsHash(saveHash) || !IsHash(identityHash)) throw new InvalidDataException("Invalid save fingerprint.");
            if (records.Count > MaximumRecords) throw new InvalidDataException("Too many veteran records.");
            var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false };
            using (XmlWriter writer = XmlWriter.Create(stream, settings))
            {
                writer.WriteStartElement("VeteranSoldiers");
                writer.WriteAttributeString("schema", SchemaVersion.ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("saveSha256", saveHash);
                writer.WriteAttributeString("pathSha256", identityHash);
                var ids = new HashSet<int>();
                var identities = new HashSet<string>(StringComparer.Ordinal);
                foreach (var fallen in campaign.Memorial) identities.Add(fallen.Identity);
                foreach (PersistentVeteran record in records)
                {
                    Validate(record);
                    if (!ids.Add(record.ReferenceId)) throw new InvalidDataException("Duplicate save reference.");
                    writer.WriteStartElement("Unit");
                    writer.WriteAttributeString("referenceId", record.ReferenceId.ToString(CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("objectId", record.ObjectId.ToString(CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("xp", record.Snapshot.Experience.ToString("R", CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("healthFactor", record.Snapshot.AppliedHealthFactor.ToString("R", CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("health", record.Snapshot.CurrentHealth.ToString("R", CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("vanillaMaxHealth", record.Snapshot.VanillaMaxHealth.ToString("R", CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("specialization", ((int)record.Snapshot.Specialization).ToString(CultureInfo.InvariantCulture));
                    if (record.Chronicle != null)
                    {
                        if (!identities.Add(record.Chronicle.Identity)) throw new InvalidDataException("Duplicate soldier identity.");
                        ChronicleXml.WriteRecord(writer, "Chronicle", record.Chronicle);
                    }
                    writer.WriteEndElement();
                }
                ChronicleXml.WriteCampaign(writer, campaign);
                writer.WriteEndElement();
            }
            if (stream.Length > MaximumDocumentBytes) throw new InvalidDataException("Veterancy sidecar exceeds size limit.");
        }

        internal static Dictionary<int, PersistentVeteran> Read(string sidecarPath, string saveHash, string identityHash)
        {
            return Read(sidecarPath, saveHash, identityHash, out _);
        }

        internal static Dictionary<int, PersistentVeteran> Read(string sidecarPath, string saveHash, string identityHash, out CampaignSnapshot campaign)
        {
            using (var stream = new FileStream(sidecarPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                return ReadDocument(stream, saveHash, identityHash, out campaign);
        }

        internal static Dictionary<int, PersistentVeteran> ReadDocument(Stream stream, string saveHash, string identityHash, out CampaignSnapshot campaign)
        {
            campaign = new CampaignSnapshot();
            if (stream.Length - stream.Position > MaximumDocumentBytes)
                throw new InvalidDataException("Veterancy sidecar exceeds size limit.");
            var result = new Dictionary<int, PersistentVeteran>();
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true,
                MaxCharactersInDocument = MaximumDocumentBytes
            };
            using (XmlReader reader = XmlReader.Create(stream, settings))
            {
                reader.MoveToContent();
                ChronicleXml.Attributes(reader, "schema", "saveSha256", "pathSha256");
                string? schema = reader.GetAttribute("schema");
                if (reader.Name != "VeteranSoldiers" || reader.NamespaceURI.Length != 0 || (schema != "1" && schema != "2" && schema != "3") ||
                    !string.Equals(reader.GetAttribute("saveSha256"), saveHash, StringComparison.Ordinal) ||
                    !string.Equals(reader.GetAttribute("pathSha256"), identityHash, StringComparison.Ordinal))
                    throw new InvalidDataException("Sidecar does not match this exact save or schema.");
                bool empty = reader.IsEmptyElement;
                bool foundCampaign = false;
                var identities = new HashSet<string>(StringComparer.Ordinal);
                reader.ReadStartElement("VeteranSoldiers");
                if (!empty)
                {
                    while (reader.MoveToContent() == XmlNodeType.Element)
                    {
                        if (schema == "3" && reader.Name == "Campaign" && !foundCampaign)
                        {
                            campaign = ChronicleXml.ReadCampaign(reader);
                            foundCampaign = true;
                            foreach (var fallen in campaign.Memorial)
                                if (!identities.Add(fallen.Identity)) throw new InvalidDataException("Duplicate soldier identity.");
                            continue;
                        }
                        if (reader.Name != "Unit" || reader.NamespaceURI.Length != 0 || (schema != "3" && !reader.IsEmptyElement))
                            throw new InvalidDataException("Unexpected sidecar element.");
                        if (schema == "1") ChronicleXml.Attributes(reader, "referenceId", "objectId", "xp", "healthFactor", "health", "vanillaMaxHealth");
                        else ChronicleXml.Attributes(reader, "referenceId", "objectId", "xp", "healthFactor", "health", "vanillaMaxHealth", "specialization");
                        var record = new PersistentVeteran
                        {
                            ReferenceId = int.Parse(Required(reader, "referenceId"), CultureInfo.InvariantCulture),
                            ObjectId = int.Parse(Required(reader, "objectId"), CultureInfo.InvariantCulture),
                            Snapshot = new VeteranSnapshot
                            {
                                Experience = double.Parse(Required(reader, "xp"), CultureInfo.InvariantCulture),
                                AppliedHealthFactor = float.Parse(Required(reader, "healthFactor"), CultureInfo.InvariantCulture),
                                CurrentHealth = float.Parse(Required(reader, "health"), CultureInfo.InvariantCulture),
                                VanillaMaxHealth = float.Parse(Required(reader, "vanillaMaxHealth"), CultureInfo.InvariantCulture),
                                Specialization = schema == "1" ? Specialization.None :
                                    (Specialization)int.Parse(Required(reader, "specialization"), CultureInfo.InvariantCulture)
                            }
                        };
                        bool emptyUnit = reader.IsEmptyElement;
                        reader.ReadStartElement("Unit");
                        if (!emptyUnit)
                        {
                            if (reader.MoveToContent() != XmlNodeType.Element || reader.Name != "Chronicle")
                                throw new InvalidDataException("Missing soldier chronicle.");
                            record.Chronicle = ChronicleXml.ReadRecord(reader, "Chronicle", false);
                            if (!identities.Add(record.Chronicle.Identity)) throw new InvalidDataException("Duplicate soldier identity.");
                            reader.ReadEndElement();
                        }
                        Validate(record);
                        if (result.Count >= MaximumRecords || result.ContainsKey(record.ReferenceId))
                            throw new InvalidDataException("Duplicate or excessive veteran records.");
                        result.Add(record.ReferenceId, record);
                    }
                    reader.ReadEndElement();
                }
                if (schema == "3" && !foundCampaign) throw new InvalidDataException("Missing campaign state.");
                if (reader.MoveToContent() != XmlNodeType.None) throw new InvalidDataException("Unexpected trailing sidecar content.");
            }
            return result;
        }

        internal static void Validate(PersistentVeteran record)
        {
            // ObjectId is an opaque game identifier; do not assume all prefab IDs are positive.
            if (record.ReferenceId <= 0 || !IsValidSnapshot(record.Snapshot))
                throw new InvalidDataException("Invalid veteran data.");
            if (record.Chronicle != null) ChronicleRules.Validate(record.Chronicle);
        }

        internal static bool IsValidSnapshot(VeteranSnapshot value)
        {
            return Specializations.Valid(value.Specialization) && !double.IsNaN(value.Experience) && !double.IsInfinity(value.Experience) && value.Experience >= 0 &&
                Finite(value.AppliedHealthFactor) && value.AppliedHealthFactor >= 1 &&
                Finite(value.CurrentHealth) && value.CurrentHealth > 0 &&
                Finite(value.VanillaMaxHealth) && value.VanillaMaxHealth > 0 &&
                (double)value.VanillaMaxHealth * value.AppliedHealthFactor <= float.MaxValue;
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        private static string Required(XmlReader reader, string name)
        {
            string? value = reader.GetAttribute(name);
            if (value == null) throw new InvalidDataException("Missing sidecar attribute: " + name);
            return value;
        }
        private static bool IsHash(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            return true;
        }
    }
}

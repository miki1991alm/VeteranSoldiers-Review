#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VeteranSoldiers;

internal sealed class PortableProgress
{
    internal Dictionary<int, PersistentVeteran> Records = new Dictionary<int, PersistentVeteran>();
    internal CampaignSnapshot Campaign = new CampaignSnapshot();
    internal string FileHash = string.Empty;
}

/// <summary>
/// An optional, bounded trailer after the game's serialized object. The supported game's
/// reader stops at that object's end; its bytes and types are never rewritten here.
/// Binding to the native bytes, rather than a Windows path, makes exact save copies portable.
/// No BinaryFormatter or other polymorphic deserializer is used by the mod.
/// </summary>
internal static class PortableSave
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("VS-PORTABLE-SAVE");
    private static readonly string Identity = PersistenceStore.HashText("VeteranSoldiers portable progress v1");
    internal const int FooterLength = 96; // version + native length + XML length + two SHA-256s + magic
    internal const long MaximumNativeBytes = 256L * 1024 * 1024;

    internal static string Append(string path, IList<PersistentVeteran> records, CampaignSnapshot campaign)
    {
        // The native completion callback precedes stream close. The caller retries this lock
        // for a bounded time; never read or append while the game is still writing the slot.
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            return Append(stream, records, campaign);
    }

    internal static string Append(Stream stream, IList<PersistentVeteran> records, CampaignSnapshot campaign)
    {
        if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek) throw new InvalidDataException("Save stream must be seekable and writable.");
        long nativeLength = stream.Length;
        if (nativeLength <= 0 || nativeLength > MaximumNativeBytes) throw new InvalidDataException("Unsupported native save size.");
        if (HasMagic(stream)) throw new InvalidDataException("Portable progress already exists; refusing to append a second snapshot.");
        byte[] nativeHash = HashPrefix(stream, nativeLength);
        byte[] document;
        using (var xml = new MemoryStream())
        {
            PersistenceStore.WriteDocument(xml, Hex(nativeHash), Identity, records, campaign);
            document = xml.ToArray();
        }
        byte[] documentHash;
        using (var sha = SHA256.Create()) documentHash = sha.ComputeHash(document);
        // Fully serialize and validate before changing any bytes. Only an EOF append follows.
        try
        {
            stream.Position = nativeLength;
            stream.Write(document, 0, document.Length);
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(1);
                writer.Write(nativeLength);
                writer.Write(document.Length);
                writer.Write(nativeHash);
                writer.Write(documentHash);
                writer.Write(Magic);
                writer.Flush();
            }
            Flush(stream);
            return Hex(HashPrefix(stream, stream.Length));
        }
        catch (Exception exception)
        {
            // A normal I/O failure removes only our appended bytes, preserving the complete
            // native save. Power loss cannot be made transactional by an in-process mod.
            try { stream.SetLength(nativeLength); Flush(stream); }
            catch (Exception rollback) { throw new InvalidDataException("Portable write failed and its trailer could not be rolled back; native prefix was not modified.", new AggregateException(exception, rollback)); }
            throw;
        }
    }

    internal static PortableProgress? Read(string path, out string fileHash)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            return Read(stream, out fileHash);
    }

    internal static PortableProgress? Read(Stream stream, out string fileHash)
    {
        if (!stream.CanRead || !stream.CanSeek) throw new InvalidDataException("Save stream must be seekable and readable.");
        if (stream.Length <= 0 || stream.Length > MaximumNativeBytes + PersistenceStore.MaximumDocumentBytes + FooterLength)
            throw new InvalidDataException("Unsupported save size.");
        fileHash = Hex(HashPrefix(stream, stream.Length));
        if (!HasMagic(stream)) return null; // Legacy or ordinary unmodded save.
        if (stream.Length < FooterLength) throw new InvalidDataException("Truncated portable footer.");
        stream.Position = stream.Length - FooterLength;
        int version, documentLength;
        long nativeLength;
        byte[] nativeHash, documentHash;
        using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
        {
            version = reader.ReadInt32();
            nativeLength = reader.ReadInt64();
            documentLength = reader.ReadInt32();
            nativeHash = reader.ReadBytes(32);
            documentHash = reader.ReadBytes(32);
        }
        if (version != 1 || nativeLength <= 0 || nativeLength > MaximumNativeBytes || documentLength <= 0 ||
            documentLength > PersistenceStore.MaximumDocumentBytes ||
            nativeLength != stream.Length - FooterLength - documentLength)
            throw new InvalidDataException("Invalid or unsupported portable save footer.");
        if (!Equal(nativeHash, HashPrefix(stream, nativeLength))) throw new InvalidDataException("Portable progress does not match the native save bytes.");
        stream.Position = nativeLength;
        var document = new byte[documentLength];
        int offset = 0;
        while (offset < document.Length)
        {
            int read = stream.Read(document, offset, document.Length - offset);
            if (read == 0) throw new InvalidDataException("Truncated portable progress.");
            offset += read;
        }
        using (var sha = SHA256.Create())
            if (!Equal(documentHash, sha.ComputeHash(document))) throw new InvalidDataException("Portable progress checksum failed.");
        using (var xml = new MemoryStream(document, false))
        {
            var records = PersistenceStore.ReadDocument(xml, Hex(nativeHash), Identity, out CampaignSnapshot campaign);
            return new PortableProgress { Records = records, Campaign = campaign, FileHash = fileHash };
        }
    }

    private static bool HasMagic(Stream stream)
    {
        if (stream.Length < Magic.Length) return false;
        stream.Position = stream.Length - Magic.Length;
        foreach (byte value in Magic) if (stream.ReadByte() != value) return false;
        return true;
    }

    private static byte[] HashPrefix(Stream stream, long count)
    {
        stream.Position = 0;
        using (var sha = SHA256.Create())
        {
            var buffer = new byte[81920];
            while (count > 0)
            {
                int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (read <= 0) throw new InvalidDataException("Save changed during fingerprint verification.");
                sha.TransformBlock(buffer, 0, read, buffer, 0);
                count -= read;
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return sha.Hash!;
        }
    }

    private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant(); }
    private static bool Equal(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        int difference = 0;
        for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
        return difference == 0;
    }
    private static void Flush(Stream stream)
    {
        if (stream is FileStream file) file.Flush(true);
        else stream.Flush();
    }
}

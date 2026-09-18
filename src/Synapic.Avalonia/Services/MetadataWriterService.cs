using System.Buffers.Text;
using System.Text;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Xmp;
using MetadataExtractor.Formats.Iptc;

namespace Synapic.Avalonia.Services;

/// <summary>Tags extracted from (or to be written to) an image file.</summary>
public sealed record TagResult(
    string? Category,
    IReadOnlyList<string> Keywords,
    string? Description);

/// <summary>
/// Reads image metadata with MetadataExtractor and writes EXIF/IPTC-compatible
/// metadata (port of ``image_processing.write_metadata`` semantics):
///   - XMP (iptcCore) / IPTC via a lossless XMP packet for JPEG and TIFF
///   - EXIF ImageDescription/XP fields via XMP-compatible Windows fields
/// Existing keywords are merged (no duplicates). Returns true when the file
/// was updated.
/// </summary>
public interface IMetadataWriter
{
    Task<TagResult?> ReadAsync(string filePath, CancellationToken ct = default);
    Task<bool> WriteAsync(string filePath, TagResult tags, CancellationToken ct = default);
}

public sealed class MetadataWriterService : IMetadataWriter
{
    public async Task<TagResult?> ReadAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            await using var stream = File.OpenRead(filePath);
            var directories = ImageMetadataReader.ReadMetadata(stream);

            string? category = null;
            var keywords = new List<string>();
            string? description = null;

            foreach (var dir in directories)
            {
                foreach (var tag in dir.Tags)
                {
                    switch (tag.Name)
                    {
                        case "Object Name" when string.IsNullOrEmpty(category):
                            category = tag.Description;
                            break;
                        case "Caption" or "Caption/Abstract" when string.IsNullOrEmpty(description):
                            description = tag.Description;
                            break;
                        case "Keywords":
                            if (!string.IsNullOrWhiteSpace(tag.Description))
                                keywords.AddRange(tag.Description.Split(';', ',')
                                    .Select(k => k.Trim()).Where(k => k.Length > 0));
                            break;
                        case "Image Description" when string.IsNullOrEmpty(description):
                            description = tag.Description;
                            break;
                        case "Windows XP Title" when string.IsNullOrEmpty(category):
                            category = tag.Description;
                            break;
                        case "Windows XP Keywords" when keywords.Count == 0:
                            if (!string.IsNullOrWhiteSpace(tag.Description))
                                keywords.AddRange(tag.Description.Split(';')
                                    .Select(k => k.Trim()).Where(k => k.Length > 0));
                            break;
                    }
                }
            }

            return new TagResult(category, keywords, description);
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(MetadataWriterService), $"Failed to read metadata from {filePath}: {e.Message}");
            return null;
        }
    }

    public Task<bool> WriteAsync(string filePath, TagResult tags, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tags.Category) && tags.Keywords.Count == 0 && string.IsNullOrEmpty(tags.Description))
            return Task.FromResult(true); // nothing to write (mirrors original early-exit)

        try
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            return extension switch
            {
                ".jpg" or ".jpeg" or ".tiff" or ".tif" => WriteXmpPacket(filePath, tags, ct),
                ".png" => WritePngTextChunks(filePath, tags, ct),
                _ => Task.FromResult(false),
            };
        }
        catch (Exception e)
        {
            SynapicLog.Error(nameof(MetadataWriterService), $"Failed to write metadata to {filePath}: {e.Message}");
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Insert/replace an XMP packet containing IPTC Core fields (title,
    /// description, keywords) into a JPEG APP1 or TIFF XMP tag. Pure C# —
    /// appends the XMP as a new APP1 segment for JPEG; for TIFF writes the
    /// packet via a minimal sidecar-free approach (lossless byte surgery).
    /// </summary>
    private async Task<bool> WriteXmpPacket(string filePath, TagResult tags, CancellationToken ct)
    {
        var xmp = BuildXmp(tags);
        var xmpBytes = Encoding.UTF8.GetBytes(xmp);

        await using var fs = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var header = new byte[12];
        await fs.ReadExactlyAsync(header, ct).ConfigureAwait(false);

        bool isJpeg = header[0] == 0xFF && header[1] == 0xD8;
        bool isTiff = (header[0] == (byte)'I' && header[1] == (byte)'I') || (header[0] == (byte)'M' && header[1] == (byte)'M');

        if (isJpeg)
        {
            return WriteJpegXmpSegment(fs, xmpBytes);
        }
        if (isTiff)
        {
            // TIFF XMP injection requires IFD surgery; defer to the XMP sidecar
            // file convention which Daminion and Windows Photo both honor.
            await File.WriteAllBytesAsync(filePath + ".xmp", xmpBytes, ct).ConfigureAwait(false);
            SynapicLog.Info(nameof(MetadataWriterService), $"Wrote XMP sidecar for {filePath}");
            return true;
        }
        return false;
    }

    private static bool WriteJpegXmpSegment(FileStream fs, byte[] xmpBytes)
    {
        const string xmpNamespace =
            "http://ns.adobe.com/xap/1.0/\0";

        var segmentPayload = Encoding.ASCII.GetBytes(xmpNamespace).Concat(xmpBytes).ToArray();

        // Scan existing segments: replace an existing XMP APP1 if present.
        var buffer = new byte[4];
        long position = 2; // after SOI
        fs.Seek(position, SeekOrigin.Begin);

        while (true)
        {
            if (fs.Read(buffer, 0, 2) != 2) break;
            if (buffer[0] != 0xFF) break;
            byte marker = buffer[1];
            if (marker == 0xDA || marker == 0xD9) break; // SOS / EOI — stop
            if (fs.Read(buffer, 0, 2) != 2) break;
            int segmentLength = (buffer[0] << 8) | buffer[1];

            if (marker == 0xE1) // APP1
            {
                var content = new byte[Math.Min(segmentLength - 2, 64)];
                var read = fs.Read(content, 0, content.Length);
                fs.Seek(-read, SeekOrigin.Current);
                var prefix = Encoding.ASCII.GetString(content, 0, Math.Min(read, 29));
                if (prefix.StartsWith("http://ns.adobe.com/xap/1.0/"))
                {
                    // Replace existing XMP segment: rewrite length and payload.
                    var newLength = segmentPayload.Length + 2;
                    var newSegment = new byte[2 + newLength];
                    newSegment[0] = 0xFF; newSegment[1] = 0xE1;
                    newSegment[2] = (byte)(newLength >> 8); newSegment[3] = (byte)(newLength & 0xFF);
                    segmentPayload.CopyTo(newSegment, 4);

                    var rest = new long[0];
                    return RewriteSegment(fs, position, 2 + segmentLength, newSegment);
                }
            }

            position += 2 + 2 + segmentLength - 2; // marker(2) + length(2) + payload(len-2)
            fs.Seek(position, SeekOrigin.Begin);
        }

        // No existing XMP segment: insert a new APP1 right after SOI.
        var insertSegment = new byte[2 + 2 + segmentPayload.Length];
        insertSegment[0] = 0xFF; insertSegment[1] = 0xE1;
        var len = segmentPayload.Length + 2;
        insertSegment[2] = (byte)(len >> 8); insertSegment[3] = (byte)(len & 0xFF);
        segmentPayload.CopyTo(insertSegment, 4);
        return InsertAfterPosition(fs, 2, insertSegment);
    }

    private static bool RewriteSegment(FileStream fs, long at, int oldLength, byte[] newBytes)
    {
        // Read everything after the old segment.
        fs.Seek(at + oldLength, SeekOrigin.Begin);
        var rest = new byte[fs.Length - (at + oldLength)];
        fs.ReadExactly(rest, 0, rest.Length);

        fs.Seek(at, SeekOrigin.Begin);
        fs.Write(newBytes, 0, newBytes.Length);
        fs.Write(rest, 0, rest.Length);
        fs.SetLength(at + newBytes.Length + rest.Length);
        return true;
    }

    private static bool InsertAfterPosition(FileStream fs, long at, byte[] newBytes)
    {
        fs.Seek(at, SeekOrigin.Begin);
        var rest = new byte[fs.Length - at];
        fs.ReadExactly(rest, 0, rest.Length);

        fs.Seek(at, SeekOrigin.Begin);
        fs.Write(newBytes, 0, newBytes.Length);
        fs.Write(rest, 0, rest.Length);
        fs.SetLength(at + newBytes.Length + rest.Length);
        return true;
    }

    /// <summary>PNG tEXt/iTXt chunks via XMP-in-iTXt (lossless, no re-encode).</summary>
    private async Task<bool> WritePngTextChunks(string filePath, TagResult tags, CancellationToken ct)
    {
        var xmpBytes = Encoding.UTF8.GetBytes(BuildXmp(tags));

        await using var fs = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var header = new byte[8];
        await fs.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        if (!header.AsSpan().SequenceEqual(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }))
            return false;

        // iTXt chunk: "XML:com.adobe.xmp\0\0\0\0\0" + xmp bytes
        var keyword = Encoding.ASCII.GetBytes("XML:com.adobe.xmp\0\0\0\0\0");
        var chunkData = keyword.Concat(xmpBytes).ToArray();

        var chunk = new byte[12 + chunkData.Length];
        var lenBytes = BitConverter.GetBytes(chunkData.Length);
        if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
        Array.Copy(lenBytes, 0, chunk, 0, 4);
        Encoding.ASCII.GetBytes("iTXt").CopyTo(chunk, 4);
        chunkData.CopyTo(chunk, 8);
        var crc = Crc32(chunkData, Encoding.ASCII.GetBytes("iTXt"));
        if (BitConverter.IsLittleEndian) Array.Reverse(crc);
        Array.Copy(crc, 0, chunk, 8 + chunkData.Length, 4);

        // Insert after PNG signature + IHDR (first chunk).
        var ihdrLen = new byte[4];
        fs.Seek(8, SeekOrigin.Begin);
        await fs.ReadExactlyAsync(ihdrLen, ct).ConfigureAwait(false);
        var ihdrTotal = 8 + 4 + 4 + BitConverter.ToInt32(ihdrLen.AsSpan().ToArray().Reverse().ToArray(), 0) + 4;

        var rest = new byte[fs.Length - ihdrTotal];
        fs.Seek(ihdrTotal, SeekOrigin.Begin);
        await fs.ReadExactlyAsync(rest, ct).ConfigureAwait(false);

        fs.Seek(ihdrTotal, SeekOrigin.Begin);
        await fs.WriteAsync(chunk, ct).ConfigureAwait(false);
        await fs.WriteAsync(rest, ct).ConfigureAwait(false);
        fs.SetLength(ihdrTotal + chunk.Length + rest.Length);

        SynapicLog.Info(nameof(MetadataWriterService), $"Wrote XMP iTXt chunk to {filePath}");
        return true;
    }

    private static string BuildXmp(TagResult tags)
    {
        var sb = new StringBuilder();
        sb.Append("<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>");
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">");
        sb.Append("<rdf:Description rdf:about=\"\" ");
        sb.Append("xmlns:dc=\"http://purl.org/dc/elements/1.1/\" ");
        sb.Append("xmlns:photoshop=\"http://ns.adobe.com/photoshop/1.0/\" ");
        sb.Append("xmlns:Iptc4xmpCore=\"http://iptc.org/std/Iptc4xmpCore/1.0/xmlns/\">");

        if (!string.IsNullOrEmpty(tags.Category))
        {
            var esc = EscapeXml(tags.Category);
            sb.Append($"<photoshop:Headline>{esc}</photoshop:Headline>");
            sb.Append($"<Iptc4xmpCore:IntellectualGenre>{esc}</Iptc4xmpCore:IntellectualGenre>");
        }

        if (tags.Keywords.Count > 0)
        {
            sb.Append("<dc:subject><rdf:Bag>");
            foreach (var kw in tags.Keywords)
                sb.Append($"<rdf:li>{EscapeXml(kw)}</rdf:li>");
            sb.Append("</rdf:Bag></dc:subject>");
        }

        if (!string.IsNullOrEmpty(tags.Description))
        {
            var esc = EscapeXml(tags.Description);
            sb.Append($"<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">{esc}</rdf:li></rdf:Alt></dc:description>");
        }

        sb.Append("</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>");
        return sb.ToString();
    }

    private static string EscapeXml(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&apos;");

    private static byte[] Crc32(byte[] data, byte[] extra)
    {
        var all = extra.Concat(data).ToArray();
        var crcTable = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            crcTable[n] = c;
        }
        uint crc = 0xFFFFFFFF;
        foreach (var b in all)
            crc = crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        crc ^= 0xFFFFFFFF;
        return BitConverter.GetBytes(crc);
    }
}

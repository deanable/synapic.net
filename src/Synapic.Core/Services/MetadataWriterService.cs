using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MetadataExtractor;
using MetadataExtractor.Formats.Xmp;
using Directory = System.IO.Directory;

using Synapic.Core.Services;
namespace Synapic.Core.Services;

/// <summary>Image tag values. In the legacy write API, null/empty values are unchanged.</summary>
public sealed record TagResult(string? Category, IReadOnlyList<string> Keywords, string? Description);

public enum MetadataPatchMode { Unchanged, Replace, Merge, Clear }

/// <summary>Explicit change to an owned XMP field. Merge is supported only for keywords.</summary>
public sealed record MetadataFieldPatch<T>(MetadataPatchMode Mode, T? Value = default)
{
    public static MetadataFieldPatch<T> Unchanged { get; } = new(MetadataPatchMode.Unchanged);
    public static MetadataFieldPatch<T> Clear { get; } = new(MetadataPatchMode.Clear);
    public static MetadataFieldPatch<T> Replace(T value) => new(MetadataPatchMode.Replace, value);
    public static MetadataFieldPatch<T> Merge(T value) => new(MetadataPatchMode.Merge, value);
}

/// <summary>A patch, not a replacement document: omitted fields and unowned XMP are preserved.</summary>
public sealed record MetadataPatch
{
    public MetadataFieldPatch<string> Category { get; init; } = MetadataFieldPatch<string>.Unchanged;
    public MetadataFieldPatch<IReadOnlyList<string>> Keywords { get; init; } = MetadataFieldPatch<IReadOnlyList<string>>.Unchanged;
    public MetadataFieldPatch<string> Description { get; init; } = MetadataFieldPatch<string>.Unchanged;

    public static MetadataPatch FromTags(TagResult tags) => new()
    {
        Category = string.IsNullOrEmpty(tags.Category) ? MetadataFieldPatch<string>.Unchanged : MetadataFieldPatch<string>.Replace(tags.Category),
        Keywords = tags.Keywords.Count == 0 ? MetadataFieldPatch<IReadOnlyList<string>>.Unchanged : MetadataFieldPatch<IReadOnlyList<string>>.Merge(tags.Keywords),
        Description = string.IsNullOrEmpty(tags.Description) ? MetadataFieldPatch<string>.Unchanged : MetadataFieldPatch<string>.Replace(tags.Description),
    };
}

/// <summary>
/// Reads XMP with IPTC/EXIF fallbacks, including adjacent TIFF .xmp sidecars.
/// Writes lossless JPEG APP1/PNG iTXt patches or TIFF sidecars. Nonempty legacy
/// keywords merge; omitted fields are unchanged. Failure leaves the prior artifact intact;
/// cancellation propagates before commit. No EXIF/IPTC blocks or TIFF pixels are rewritten.
/// </summary>
public interface IMetadataWriter
{
    Task<TagResult?> ReadAsync(string filePath, CancellationToken ct = default);
    Task<bool> WriteAsync(string filePath, TagResult tags, CancellationToken ct = default);
}

internal enum MetadataWriteStage { Staged, Validated, BeforeCommit }

public sealed class MetadataWriterService : IMetadataWriter
{
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Photoshop = "http://ns.adobe.com/photoshop/1.0/";
    private static readonly XNamespace Iptc = "http://iptc.org/std/Iptc4xmpCore/1.0/xmlns/";
    private static readonly byte[] JpegXmpHeader = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
    private static readonly byte[] ExtendedXmpHeader = Encoding.ASCII.GetBytes("http://ns.adobe.com/xmp/extension/\0");
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly byte[] PngXmpHeader = Encoding.ASCII.GetBytes("XML:com.adobe.xmp\0");
    private const int MaxPacketBytes = 16 * 1024 * 1024;
    private readonly Action<MetadataWriteStage, string>? _checkpoint;

    public MetadataWriterService() { }

    // A per-instance fault-injection seam; production always uses actual IO and validation.
    internal MetadataWriterService(Action<MetadataWriteStage, string> checkpoint) => _checkpoint = checkpoint;

    public async Task<TagResult?> ReadAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            await using var source = OpenSource(filePath);
            var fallback = ReadFallback(source);
            source.Position = 0;
            var extension = Path.GetExtension(filePath);
            if (!IsTiffExtension(extension) && !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
                return fallback; // Retain read-only EXIF/IPTC support for other image formats.
            var container = Inspect(source, extension, ct);
            byte[]? packet = container.Packet;
            if (container.IsTiff && File.Exists(filePath + ".xmp"))
                packet = await ReadSidecarAsync(filePath + ".xmp", ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return packet is null ? fallback : ReadTags(ParseXmp(packet), fallback);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(MetadataWriterService), $"Failed to read metadata from {filePath}: {e.Message}");
            return null;
        }
    }

    public Task<bool> WriteAsync(string filePath, TagResult tags, CancellationToken ct = default)
        => PatchAsync(filePath, MetadataPatch.FromTags(tags), ct);

    /// <summary>
    /// Applies an explicit patch through a unique same-directory staging file.
    /// Existing destinations use atomic File.Replace; new sidecars use non-overwriting Move.
    /// Malformed, multiple or extended XMP packets are rejected rather than discarded.
    /// This does not provide multi-file atomicity or a sandbox against concurrent external editors.
    /// </summary>
    public async Task<bool> PatchAsync(string filePath, MetadataPatch patch, CancellationToken ct = default)
    {
        string? staging = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            ValidatePatch(patch);
            if (patch.Category.Mode == MetadataPatchMode.Unchanged && patch.Keywords.Mode == MetadataPatchMode.Unchanged && patch.Description.Mode == MetadataPatchMode.Unchanged)
                return true;
            filePath = Path.GetFullPath(filePath);
            CheckWritableArtifact(filePath);
            await using var source = OpenSource(filePath);
            var container = Inspect(source, Path.GetExtension(filePath), ct);
            var fallback = ReadFallback(source);
            var destination = container.IsTiff ? filePath + ".xmp" : filePath;
            var existed = File.Exists(destination);
            if (existed) CheckWritableArtifact(destination);
            else if (Directory.Exists(destination)) throw new IOException("Metadata destination is a directory.");
            var attributes = existed ? File.GetAttributes(destination) : FileAttributes.Normal;
            var created = existed ? File.GetCreationTimeUtc(destination) : (DateTime?)null;
            UnixFileMode? unixMode = !OperatingSystem.IsWindows() && existed ? File.GetUnixFileMode(destination) : null;

            var packet = container.Packet;
            if (container.IsTiff && existed)
                packet = await ReadSidecarAsync(destination, ct).ConfigureAwait(false);
            var document = packet is null ? NewXmp() : ParseXmp(packet);
            ApplyPatch(document, patch, fallback);
            var updated = Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
            if (updated.Length > MaxPacketBytes) throw new InvalidDataException("XMP packet exceeds the supported size.");
            _ = ParseXmp(updated);
            var replacement = container.IsTiff ? updated : container.IsPng ? PngChunk(updated) : JpegSegment(updated);

            staging = Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                if (container.IsTiff) await output.WriteAsync(replacement, ct).ConfigureAwait(false);
                else await CopyWithReplacementAsync(source, output, container, replacement, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            _checkpoint?.Invoke(MetadataWriteStage.Staged, staging);
            ct.ThrowIfCancellationRequested();
            if (container.IsTiff)
            {
                _ = ParseXmp(await ReadSidecarAsync(staging, ct).ConfigureAwait(false));
                ValidatePixels(filePath, ct);
            }
            else
            {
                using var check = OpenSource(staging);
                var inspected = Inspect(check, Path.GetExtension(filePath), ct);
                if (inspected.Packet is null || !inspected.Packet.AsSpan().SequenceEqual(updated))
                    throw new InvalidDataException("Staged XMP does not match the patch.");
                _ = ParseXmp(inspected.Packet);
                ValidatePixels(staging, ct);
            }
            _checkpoint?.Invoke(MetadataWriteStage.Validated, staging);
            if (created is { } creationTime) File.SetCreationTimeUtc(staging, creationTime);
            if (!OperatingSystem.IsWindows() && unixMode is { } mode) File.SetUnixFileMode(staging, mode);
            File.SetAttributes(staging, attributes);
            _checkpoint?.Invoke(MetadataWriteStage.BeforeCommit, staging);
            ct.ThrowIfCancellationRequested();
            // Close the JPEG/PNG input before replacing it; on Windows its read lease blocks deletion.
            await source.DisposeAsync().ConfigureAwait(false);
            if (existed) File.Replace(staging, destination, destinationBackupFileName: null);
            else File.Move(staging, destination); // Never overwrite a sidecar created by another writer.
            staging = null;
            // No cancellation check after commit: a committed write must be reported as committed.
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            SynapicLog.Error(nameof(MetadataWriterService), $"Failed to write metadata to {filePath}: {e.Message}");
            return false;
        }
        finally
        {
            if (staging is not null)
            {
                try { File.Delete(staging); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    SynapicLog.Warning(nameof(MetadataWriterService), $"Could not remove metadata staging file {staging}: {e.Message}");
                }
            }
        }
    }

    /// <summary>Associated artifacts for future move/delete use cases; does not move or delete them.</summary>
    public static IReadOnlyList<string> AssociatedArtifacts(string imagePath)
        => IsTiffExtension(Path.GetExtension(imagePath)) && File.Exists(imagePath + ".xmp")
            ? [imagePath, imagePath + ".xmp"] : [imagePath];

    private static FileStream OpenSource(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    private static void CheckWritableArtifact(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("Read-only images/sidecars and symbolic links are not writable metadata artifacts.");
    }

    private static void ValidatePixels(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var image = NetVips.Image.NewFromFile(path, access: NetVips.Enums.Access.Sequential, failOn: NetVips.Enums.FailOn.Warning);
        _ = image.Avg(); // Force the lazy decoder to read pixels, not just the header.
        ct.ThrowIfCancellationRequested();
    }

    private static async Task<byte[]> ReadSidecarAsync(string path, CancellationToken ct)
    {
        await using var input = OpenSource(path);
        if (input.Length > MaxPacketBytes) throw new InvalidDataException("XMP sidecar exceeds the supported size.");
        var bytes = new byte[(int)input.Length];
        await input.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return bytes;
    }

    private static XDocument ParseXmp(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxPacketBytes });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        if (document.Descendants(Rdf + "RDF").Count() != 1 || !document.Descendants(Rdf + "Description").Any())
            throw new InvalidDataException("XMP must contain one RDF document with a description.");
        _ = XmpCore.XmpMetaFactory.ParseFromBuffer(bytes); // Also reject structurally invalid XMP/RDF.
        return document;
    }

    private static XDocument NewXmp() => new(new XElement(XName.Get("xmpmeta", "adobe:ns:meta/"),
        new XAttribute(XNamespace.Xmlns + "x", "adobe:ns:meta/"),
        new XElement(Rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", Rdf),
            new XElement(Rdf + "Description", new XAttribute(Rdf + "about", ""),
                new XAttribute(XNamespace.Xmlns + "dc", Dc), new XAttribute(XNamespace.Xmlns + "photoshop", Photoshop),
                new XAttribute(XNamespace.Xmlns + "Iptc4xmpCore", Iptc)))));

    private static IEnumerable<XElement> Descriptions(XDocument doc) => doc.Descendants(Rdf + "RDF").Single().Elements(Rdf + "Description");

    private static string? Scalar(XDocument doc, XName name)
        => Descriptions(doc).Select(d => (string?)d.Attribute(name) ?? d.Element(name)?.Value).FirstOrDefault(v => v is not null);

    private static XElement? Property(XDocument doc, XName name) => Descriptions(doc).Select(d => d.Element(name)).FirstOrDefault(e => e is not null);

    private static bool HasProperty(XDocument doc, XName name) => Descriptions(doc).Any(d => d.Attribute(name) is not null || d.Element(name) is not null);

    private static TagResult ReadTags(XDocument doc, TagResult fallback)
    {
        var category = HasProperty(doc, Photoshop + "Headline") ? Scalar(doc, Photoshop + "Headline") : fallback.Category;
        var descriptionElement = Property(doc, Dc + "description");
        var description = HasProperty(doc, Dc + "description")
            ? descriptionElement?.Descendants(Rdf + "li").FirstOrDefault(e => (string?)e.Attribute(XNamespace.Xml + "lang") == "x-default")?.Value
                ?? descriptionElement?.Descendants(Rdf + "li").FirstOrDefault()?.Value ?? Scalar(doc, Dc + "description")
            : fallback.Description;
        IReadOnlyList<string> keywords = HasProperty(doc, Dc + "subject")
            ? NormalizeKeywords(Property(doc, Dc + "subject")?.Descendants(Rdf + "li").Select(e => e.Value) ?? []) : fallback.Keywords;
        return new TagResult(category, keywords, description);
    }

    private static void ValidatePatch(MetadataPatch patch)
    {
        if (!Enum.IsDefined(patch.Category.Mode) || !Enum.IsDefined(patch.Description.Mode) || !Enum.IsDefined(patch.Keywords.Mode))
            throw new ArgumentException("Unknown metadata patch mode.");
        if (patch.Category.Mode == MetadataPatchMode.Merge || patch.Description.Mode == MetadataPatchMode.Merge)
            throw new ArgumentException("Only keywords support Merge.");
        if (patch.Category.Mode == MetadataPatchMode.Replace && patch.Category.Value is null ||
            patch.Description.Mode == MetadataPatchMode.Replace && patch.Description.Value is null ||
            patch.Keywords.Mode is MetadataPatchMode.Replace or MetadataPatchMode.Merge && patch.Keywords.Value is null)
            throw new ArgumentException("Replace/Merge requires a value.");
    }

    private static void SetProperty(XDocument doc, XName name, XElement replacement)
    {
        var descriptions = Descriptions(doc).ToArray();
        // Refuse to patch multiple RDF subjects: their ownership is ambiguous.
        if (descriptions.Select(d => (string?)d.Attribute(Rdf + "about") ?? "").Distinct().Count() != 1)
            throw new InvalidDataException("Multiple RDF subjects cannot be patched safely.");
        foreach (var d in descriptions)
        {
            d.Attribute(name)?.Remove();
            d.Elements(name).Remove();
        }
        descriptions[0].Add(replacement);
    }

    private static void ApplyPatch(XDocument doc, MetadataPatch patch, TagResult fallback)
    {
        if (patch.Category.Mode != MetadataPatchMode.Unchanged)
        {
            var value = patch.Category.Mode == MetadataPatchMode.Clear ? "" : patch.Category.Value!;
            SetProperty(doc, Photoshop + "Headline", new XElement(Photoshop + "Headline", value));
            SetProperty(doc, Iptc + "IntellectualGenre", new XElement(Iptc + "IntellectualGenre", value));
        }
        if (patch.Keywords.Mode != MetadataPatchMode.Unchanged)
        {
            var values = patch.Keywords.Mode == MetadataPatchMode.Clear ? [] : patch.Keywords.Value!;
            if (patch.Keywords.Mode == MetadataPatchMode.Merge) values = ReadTags(doc, fallback).Keywords.Concat(values).ToArray();
            SetProperty(doc, Dc + "subject", new XElement(Dc + "subject", new XElement(Rdf + "Bag",
                NormalizeKeywords(values).Select(value => new XElement(Rdf + "li", value)))));
        }
        if (patch.Description.Mode != MetadataPatchMode.Unchanged)
        {
            // Preserve translations/qualifiers; change only the x-default alternative.
            var prior = Property(doc, Dc + "description");
            var element = prior is null ? new XElement(Dc + "description") : new XElement(prior);
            var alt = element.Element(Rdf + "Alt");
            if (alt is null)
            {
                if (element.HasElements) throw new InvalidDataException("Unsupported description structure.");
                element.RemoveNodes();
                alt = new XElement(Rdf + "Alt");
                element.Add(alt);
            }
            alt.Elements(Rdf + "li").Where(e => (string?)e.Attribute(XNamespace.Xml + "lang") == "x-default").Remove();
            alt.AddFirst(new XElement(Rdf + "li", new XAttribute(XNamespace.Xml + "lang", "x-default"),
                patch.Description.Mode == MetadataPatchMode.Clear ? "" : patch.Description.Value!));
            SetProperty(doc, Dc + "description", element);
        }
    }

    private static string[] NormalizeKeywords(IEnumerable<string> values)
        => values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.Ordinal).ToArray();

    private static TagResult ReadFallback(Stream source)
    {
        source.Position = 0;
        var directories = ImageMetadataReader.ReadMetadata(source);
        string? category = null, description = null;
        var keywords = new List<string>();
        foreach (var dir in directories)
        {
            if (dir is XmpDirectory xd && xd.XmpMeta is { } xmp)
            {
                category ??= xmp.GetProperty(Photoshop.NamespaceName, "Headline")?.Value;
                description ??= xmp.GetArrayItem(Dc.NamespaceName, "description", 1)?.Value;
                for (var i = 1; i <= xmp.CountArrayItems(Dc.NamespaceName, "subject"); i++)
                    if (xmp.GetArrayItem(Dc.NamespaceName, "subject", i)?.Value is { } value) keywords.Add(value);
            }
            foreach (var tag in dir.Tags)
            {
                switch (tag.Name)
                {
                    case "Object Name" or "Windows XP Title": category ??= tag.Description; break;
                    case "Caption" or "Caption/Abstract" or "Image Description": description ??= tag.Description; break;
                    case "Keywords" or "Windows XP Keywords":
                        if (tag.Description is { } value) keywords.AddRange(value.Split(';', ','));
                        break;
                }
            }
        }
        return new TagResult(category, NormalizeKeywords(keywords), description);
    }

    private sealed record Container(bool IsPng, bool IsTiff, byte[]? Packet, long InsertAt, long RemoveLength);
    private static bool IsTiffExtension(string extension) => extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase);

    private static Container Inspect(Stream input, string extension, CancellationToken ct)
    {
        input.Position = 0;
        if (IsTiffExtension(extension))
        {
            var header = new byte[4];
            input.ReadExactly(header);
            if (!header.AsSpan().SequenceEqual(new byte[] { 73, 73, 42, 0 }) &&
                !header.AsSpan().SequenceEqual(new byte[] { 77, 77, 0, 42 }) &&
                !header.AsSpan().SequenceEqual(new byte[] { 73, 73, 43, 0 }) &&
                !header.AsSpan().SequenceEqual(new byte[] { 77, 77, 0, 43 }))
                throw new InvalidDataException("Invalid TIFF signature.");
            input.Position = 0;
            var dirs = ImageMetadataReader.ReadMetadata(input);
            var packets = dirs.OfType<XmpDirectory>().Where(d => d.XmpMeta is not null).ToArray();
            if (packets.Length > 1) throw new InvalidDataException("Multiple embedded XMP packets are ambiguous.");
            return new Container(false, true, packets.Length == 0 ? null : XmpCore.XmpMetaFactory.SerializeToBuffer(packets[0].XmpMeta!, new XmpCore.Options.SerializeOptions()), 0, 0);
        }
        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase)) return InspectPng(input, ct);
        if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)) return InspectJpeg(input, ct);
        throw new NotSupportedException("Metadata writes support JPEG, PNG and TIFF only.");
    }

    private static Container InspectJpeg(Stream input, CancellationToken ct)
    {
        if (input.ReadByte() != 255 || input.ReadByte() != 216) throw new InvalidDataException("Invalid JPEG signature.");
        byte[]? packet = null;
        long at = 2, remove = 0;
        var lengthBytes = new byte[2];
        while (input.Position < input.Length)
        {
            ct.ThrowIfCancellationRequested();
            var start = input.Position;
            if (input.ReadByte() != 255) throw new InvalidDataException("Invalid JPEG marker.");
            int marker;
            do { marker = input.ReadByte(); } while (marker == 255);
            if (marker is -1 or 0) throw new InvalidDataException("Invalid JPEG marker.");
            if (marker == 217) return new Container(false, false, packet, at, remove);
            if (marker is 1 or >= 208 and <= 215) continue;
            input.ReadExactly(lengthBytes);
            var length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
            if (length < 2 || input.Position + length - 2 > input.Length) throw new InvalidDataException("Invalid JPEG segment length.");
            if (marker == 218) return new Container(false, false, packet, at, remove);
            if (marker == 225)
            {
                var payload = new byte[length - 2];
                input.ReadExactly(payload);
                if (payload.AsSpan().StartsWith(ExtendedXmpHeader)) throw new InvalidDataException("Extended XMP is not supported for safe patching.");
                if (payload.AsSpan().StartsWith(JpegXmpHeader))
                {
                    if (packet is not null) throw new InvalidDataException("Multiple XMP packets cannot be patched safely.");
                    packet = payload[JpegXmpHeader.Length..];
                    at = start;
                    remove = input.Position - start;
                }
            }
            else input.Seek(length - 2, SeekOrigin.Current);
        }
        throw new InvalidDataException("Truncated JPEG.");
    }

    private static Container InspectPng(Stream input, CancellationToken ct)
    {
        var signature = new byte[8];
        input.ReadExactly(signature);
        if (!signature.AsSpan().SequenceEqual(PngSignature)) throw new InvalidDataException("Invalid PNG signature.");
        byte[]? packet = null;
        long at = 0, remove = 0;
        var header = new byte[8];
        var buffer = new byte[65536];
        var crcBytes = new byte[4];
        var first = true;
        while (input.Position < input.Length)
        {
            ct.ThrowIfCancellationRequested();
            var start = input.Position;
            input.ReadExactly(header);
            var size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.ASCII.GetString(header, 4, 4);
            if (size > int.MaxValue || input.Position + size + 4 > input.Length) throw new InvalidDataException("Invalid PNG chunk size.");
            if (first && (type != "IHDR" || size != 13)) throw new InvalidDataException("PNG must start with IHDR.");
            var capture = type == "iTXt";
            if (capture && size > MaxPacketBytes) throw new InvalidDataException("PNG text chunk exceeds the supported size.");
            using var data = capture ? new MemoryStream() : null;
            var crc = UpdateCrc(0xffffffff, header.AsSpan(4));
            long remaining = size;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                var count = (int)Math.Min(buffer.Length, remaining);
                input.ReadExactly(buffer.AsSpan(0, count));
                crc = UpdateCrc(crc, buffer.AsSpan(0, count));
                data?.Write(buffer, 0, count);
                remaining -= count;
            }
            input.ReadExactly(crcBytes);
            if ((crc ^ 0xffffffff) != BinaryPrimitives.ReadUInt32BigEndian(crcBytes)) throw new InvalidDataException("Invalid PNG chunk CRC.");
            if (first) { at = input.Position; first = false; }
            if (data is not null && data.GetBuffer().AsSpan(0, (int)data.Length).StartsWith(PngXmpHeader))
            {
                if (packet is not null) throw new InvalidDataException("Multiple XMP chunks cannot be patched safely.");
                packet = DecodePngXmp(data.ToArray());
                at = start;
                remove = input.Position - start;
            }
            if (type == "IEND")
            {
                if (size != 0 || input.Position != input.Length) throw new InvalidDataException("Invalid PNG end chunk.");
                return new Container(true, false, packet, at, remove);
            }
        }
        throw new InvalidDataException("PNG has no IEND.");
    }

    private static byte[] DecodePngXmp(byte[] payload)
    {
        var pos = PngXmpHeader.Length;
        if (payload.Length < pos + 4 || payload[pos] > 1 || payload[pos + 1] != 0) throw new InvalidDataException("Invalid PNG XMP text header.");
        var compressed = payload[pos] == 1;
        pos += 2;
        for (var i = 0; i < 2; i++)
        {
            var end = Array.IndexOf(payload, (byte)0, pos);
            if (end < 0) throw new InvalidDataException("Invalid PNG XMP text fields.");
            pos = end + 1;
        }
        if (!compressed) return payload[pos..];
        using var input = new MemoryStream(payload, pos, payload.Length - pos);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = zlib.Read(buffer)) > 0)
        {
            if (output.Length + count > MaxPacketBytes) throw new InvalidDataException("Expanded XMP exceeds the supported size.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static byte[] JpegSegment(byte[] packet)
    {
        var length = packet.Length + JpegXmpHeader.Length + 2;
        if (length > ushort.MaxValue) throw new InvalidDataException("XMP exceeds the basic JPEG APP1 size limit; extended XMP is not supported.");
        var segment = new byte[length + 2];
        segment[0] = 255; segment[1] = 225;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)length);
        JpegXmpHeader.CopyTo(segment, 4);
        packet.CopyTo(segment, 4 + JpegXmpHeader.Length);
        return segment;
    }

    private static byte[] PngChunk(byte[] packet)
    {
        var dataLength = PngXmpHeader.Length + 4 + packet.Length;
        var chunk = new byte[12 + dataLength];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)dataLength);
        Encoding.ASCII.GetBytes("iTXt").CopyTo(chunk, 4);
        PngXmpHeader.CopyTo(chunk, 8); // zero-initialized compression/language/translation fields
        packet.CopyTo(chunk, 8 + PngXmpHeader.Length + 4);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + dataLength), UpdateCrc(0xffffffff, chunk.AsSpan(4, dataLength + 4)) ^ 0xffffffff);
        return chunk;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : 0xedb88320 ^ (crc >> 1);
        }
        return crc;
    }

    private static async Task CopyWithReplacementAsync(Stream input, Stream output, Container container, byte[] replacement, CancellationToken ct)
    {
        input.Position = 0;
        var buffer = new byte[65536];
        long remaining = container.InsertAt;
        while (remaining > 0)
        {
            var count = (int)Math.Min(remaining, buffer.Length);
            await input.ReadExactlyAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            remaining -= count;
        }
        await output.WriteAsync(replacement, ct).ConfigureAwait(false);
        input.Position = container.InsertAt + container.RemoveLength;
        await input.CopyToAsync(output, buffer.Length, ct).ConfigureAwait(false);
    }
}

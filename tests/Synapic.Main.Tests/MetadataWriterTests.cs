using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using Synapic.Main.Services;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// Unit-level coverage for the metadata write→read round trip. The e2e test
/// only proves "some metadata appeared" after a real inference run; these
/// tests pin the exact fields so a regression in the XMP packet (write) or the
/// XMP parse (read) fails without needing the sidecar exe.
/// </summary>
public class MetadataWriterTests
{
    private static string? FindSampleImage()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Synapic.Main.Tests", "TestData", "sample.jpg");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string CopySample()
    {
        var sample = FindSampleImage();
        Assert.NotNull(sample);
        var tmp = Path.Combine(Path.GetTempPath(), $"synapic-meta-{Guid.NewGuid():N}.jpg");
        File.Copy(sample!, tmp);
        return tmp;
    }

    [Fact]
    public async Task Jpeg_WriteThenRead_RoundTripsAllFields()
    {
        var path = CopySample();
        try
        {
            var writer = new MetadataWriterService();
            var tags = new TagResult("Nature", new[] { "forest", "tree" }, "A quiet description");

            Assert.True(await writer.WriteAsync(path, tags));

            var read = await writer.ReadAsync(path);
            Assert.NotNull(read);
            Assert.Equal("Nature", read!.Category);
            Assert.Contains("forest", read.Keywords);
            Assert.Contains("tree", read.Keywords);
            Assert.Equal("A quiet description", read.Description);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".tif")]
    public async Task Partial_update_preserves_existing_fields_and_merges_keywords(string extension)
    {
        using var fixture = new MetadataFixture(extension);
        var writer = new MetadataWriterService();
        Assert.True(await writer.WriteAsync(fixture.Path, new TagResult("Existing", ["old"], "Original description")));
        Assert.True(await writer.WriteAsync(fixture.Path, new TagResult(null, ["new", "old"], null)));

        var read = Assert.IsType<TagResult>(await writer.ReadAsync(fixture.Path));
        Assert.Equal("Existing", read.Category);
        Assert.Equal("Original description", read.Description);
        Assert.Equal(new[] { "old", "new" }, read.Keywords);
    }

    [Fact]
    public async Task Oversized_jpeg_packet_fails_without_changing_original()
    {
        using var fixture = new MetadataFixture(".jpg");
        var original = await File.ReadAllBytesAsync(fixture.Path);
        Assert.False(await new MetadataWriterService().WriteAsync(fixture.Path, new TagResult(null, [], new string('x', 70_000))));
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.Path));
    }

    [Fact]
    public async Task Repeated_png_update_does_not_add_xmp_chunks_or_grow_file()
    {
        using var fixture = new MetadataFixture(".png");
        var writer = new MetadataWriterService();
        var tags = new TagResult("Nature", ["tree"], "Description");
        Assert.True(await writer.WriteAsync(fixture.Path, tags));
        var first = await File.ReadAllBytesAsync(fixture.Path);
        Assert.True(await writer.WriteAsync(fixture.Path, tags));
        Assert.Equal(first, await File.ReadAllBytesAsync(fixture.Path));
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".tif")]
    public async Task Patches_preserve_unowned_properties_and_description_translations(string extension)
    {
        using var fixture = new MetadataFixture(extension);
        fixture.SeedXmp();
        var writer = new MetadataWriterService();
        Assert.True(await writer.WriteAsync(fixture.Path, new TagResult("Updated", ["new"], "New default")));
        var doc = fixture.ReadXmp();
        Assert.Equal("Copyright holder", doc.Descendants(MetadataFixture.Dc + "rights").Single().Value);
        Assert.Equal("Creator", doc.Descendants(MetadataFixture.Dc + "creator").Single().Value);
        Assert.Equal("Custom value", doc.Descendants(MetadataFixture.Rdf + "Description").Single().Attribute(XName.Get("value", "urn:synapic:test"))?.Value);
        var translations = doc.Descendants(MetadataFixture.Dc + "description").Single().Descendants(MetadataFixture.Rdf + "li").ToArray();
        Assert.Equal("Traduction", translations.Single(e => (string?)e.Attribute(XNamespace.Xml + "lang") == "fr").Value);
        Assert.Equal("New default", translations.Single(e => (string?)e.Attribute(XNamespace.Xml + "lang") == "x-default").Value);
        fixture.AssertNoStagingFiles();
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".tif")]
    public async Task Explicit_merge_replace_and_clear_have_distinct_semantics(string extension)
    {
        using var fixture = new MetadataFixture(extension);
        fixture.SeedXmp();
        var writer = new MetadataWriterService();
        Assert.True(await writer.PatchAsync(fixture.Path, new MetadataPatch { Keywords = MetadataFieldPatch<IReadOnlyList<string>>.Merge(["new", "old"]) }));
        Assert.Equal(new[] { "old", "new" }, (await writer.ReadAsync(fixture.Path))!.Keywords);
        Assert.True(await writer.PatchAsync(fixture.Path, new MetadataPatch { Keywords = MetadataFieldPatch<IReadOnlyList<string>>.Replace(["only"]) }));
        Assert.Equal(new[] { "only" }, (await writer.ReadAsync(fixture.Path))!.Keywords);
        Assert.True(await writer.PatchAsync(fixture.Path, new MetadataPatch
        {
            Category = MetadataFieldPatch<string>.Clear,
            Keywords = MetadataFieldPatch<IReadOnlyList<string>>.Clear,
            Description = MetadataFieldPatch<string>.Clear,
        }));
        var cleared = Assert.IsType<TagResult>(await writer.ReadAsync(fixture.Path));
        Assert.Equal("", cleared.Category);
        Assert.Equal("", cleared.Description);
        Assert.Empty(cleared.Keywords);
        Assert.Equal("Traduction", fixture.ReadXmp().Descendants(MetadataFixture.Rdf + "li").Single(e => (string?)e.Attribute(XNamespace.Xml + "lang") == "fr").Value);
    }

    public static IEnumerable<object[]> FailureCases()
    {
        foreach (var extension in new[] { ".jpg", ".png", ".tif" })
        foreach (var stage in new[] { 0, 1, 2 })
        foreach (var cancel in new[] { false, true })
            yield return [extension, stage, cancel];
    }

    [Theory]
    [MemberData(nameof(FailureCases))]
    public async Task Failure_or_cancellation_before_commit_preserves_prior_artifacts(string extension, int stage, bool cancel)
    {
        using var fixture = new MetadataFixture(extension);
        fixture.SeedXmp();
        var original = await File.ReadAllBytesAsync(fixture.Path);
        var sidecar = extension == ".tif" ? await File.ReadAllBytesAsync(fixture.Path + ".xmp") : null;
        using var cts = new CancellationTokenSource();
        var writer = new MetadataWriterService((checkpoint, _) =>
        {
            if ((int)checkpoint != stage) return;
            if (cancel) cts.Cancel();
            else throw new IOException("Injected IO failure");
        });
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(fixture.Path, new TagResult("new", ["new"], "new"), cts.Token));
        else
            Assert.False(await writer.WriteAsync(fixture.Path, new TagResult("new", ["new"], "new")));
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.Path));
        if (sidecar is not null) Assert.Equal(sidecar, await File.ReadAllBytesAsync(fixture.Path + ".xmp"));
        fixture.AssertNoStagingFiles();
        fixture.AssertPixelsDecode();
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".tif")]
    public async Task Failed_staging_validation_preserves_original(string extension)
    {
        using var fixture = new MetadataFixture(extension);
        fixture.SeedXmp();
        var original = await File.ReadAllBytesAsync(fixture.Path);
        var sidecar = extension == ".tif" ? await File.ReadAllBytesAsync(fixture.Path + ".xmp") : null;
        var writer = new MetadataWriterService((stage, staging) =>
        {
            if (stage == MetadataWriteStage.Staged) File.WriteAllText(staging, "not an image or XMP");
        });
        Assert.False(await writer.WriteAsync(fixture.Path, new TagResult("new", [], null)));
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.Path));
        if (sidecar is not null) Assert.Equal(sidecar, await File.ReadAllBytesAsync(fixture.Path + ".xmp"));
        fixture.AssertNoStagingFiles();
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".tif")]
    public async Task Malformed_existing_xmp_is_not_discarded(string extension)
    {
        using var fixture = new MetadataFixture(extension);
        fixture.SeedXmp("<broken");
        var original = await File.ReadAllBytesAsync(fixture.Path);
        var sidecar = extension == ".tif" ? await File.ReadAllBytesAsync(fixture.Path + ".xmp") : null;
        Assert.False(await new MetadataWriterService().WriteAsync(fixture.Path, new TagResult("new", [], null)));
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.Path));
        if (sidecar is not null) Assert.Equal(sidecar, await File.ReadAllBytesAsync(fixture.Path + ".xmp"));
        fixture.AssertNoStagingFiles();
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    public async Task Multiple_existing_packets_are_rejected_without_mutation(string extension)
    {
        using var fixture = new MetadataFixture(extension);
        fixture.SeedXmp();
        fixture.SeedXmp();
        var original = await File.ReadAllBytesAsync(fixture.Path);
        Assert.False(await new MetadataWriterService().WriteAsync(fixture.Path, new TagResult("new", [], null)));
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.Path));
        fixture.AssertNoStagingFiles();
    }

    [Fact]
    public async Task Cancelled_read_and_noop_write_propagate_cancellation()
    {
        using var fixture = new MetadataFixture(".jpg");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var writer = new MetadataWriterService();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.ReadAsync(fixture.Path, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(fixture.Path, new TagResult(null, [], null), cts.Token));
    }

    [Fact]
    public async Task Failed_new_sidecar_commit_does_not_overwrite_a_competing_sidecar()
    {
        using var fixture = new MetadataFixture(".tif");
        var original = await File.ReadAllBytesAsync(fixture.Path);
        var writer = new MetadataWriterService((stage, _) =>
        {
            if (stage == MetadataWriteStage.BeforeCommit) File.WriteAllText(fixture.Path + ".xmp", "competing writer");
        });
        Assert.False(await writer.WriteAsync(fixture.Path, new TagResult("new", [], null)));
        Assert.Equal("competing writer", await File.ReadAllTextAsync(fixture.Path + ".xmp"));
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.Path));
        fixture.AssertNoStagingFiles();
    }

    [Fact]
    public async Task Atomic_replace_refusal_retains_original_and_removes_staging()
    {
        using var fixture = new MetadataFixture(".jpg");
        var original = await File.ReadAllBytesAsync(fixture.Path);
        FileStream? blocker = null;
        var writer = new MetadataWriterService((stage, _) =>
        {
            if (stage == MetadataWriteStage.BeforeCommit) blocker = File.Open(fixture.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        });
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Windows sharing semantics provide an actual filesystem replacement refusal.
                Assert.False(await writer.WriteAsync(fixture.Path, new TagResult("new", [], null)));
                Assert.Equal(original, await File.ReadAllBytesAsync(fixture.Path));
            }
            else
            {
                // Unix allows rename of open files; the same operation should commit, not pretend refusal.
                Assert.True(await writer.WriteAsync(fixture.Path, new TagResult("new", [], null)));
                Assert.Equal("new", (await writer.ReadAsync(fixture.Path))!.Category);
            }
            fixture.AssertNoStagingFiles();
        }
        finally { blocker?.Dispose(); }
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    public async Task Invalid_pixel_stream_is_rejected_even_if_headers_parse(string extension)
    {
        using var fixture = new MetadataFixture(extension);
        var bytes = await File.ReadAllBytesAsync(fixture.Path);
        // Keep the signatures but destroy the structural/image content.
        bytes = bytes[..(extension == ".jpg" ? 12 : 33)];
        await File.WriteAllBytesAsync(fixture.Path, bytes);
        Assert.False(await new MetadataWriterService().WriteAsync(fixture.Path, new TagResult("new", [], null)));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.Path));
        fixture.AssertNoStagingFiles();
    }

    [Fact]
    public async Task Existing_read_only_sidecar_and_destination_directory_are_not_replaced()
    {
        using var fixture = new MetadataFixture(".tif");
        fixture.SeedXmp();
        var sidecar = fixture.Path + ".xmp";
        var original = await File.ReadAllBytesAsync(sidecar);
        File.SetAttributes(sidecar, FileAttributes.ReadOnly);
        try { Assert.False(await new MetadataWriterService().WriteAsync(fixture.Path, new TagResult("new", [], null))); }
        finally { File.SetAttributes(sidecar, FileAttributes.Normal); }
        Assert.Equal(original, await File.ReadAllBytesAsync(sidecar));
        File.Delete(sidecar);
        Directory.CreateDirectory(sidecar);
        Assert.False(await new MetadataWriterService().WriteAsync(fixture.Path, new TagResult("new", [], null)));
        Assert.True(Directory.Exists(sidecar));
        fixture.AssertNoStagingFiles();
    }

    [Fact]
    public async Task Tiff_image_is_unchanged_and_sidecar_is_an_associated_artifact()
    {
        using var fixture = new MetadataFixture(".tif");
        var original = await File.ReadAllBytesAsync(fixture.Path);
        Assert.Equal(new[] { fixture.Path }, MetadataWriterService.AssociatedArtifacts(fixture.Path));
        Assert.True(await new MetadataWriterService().WriteAsync(fixture.Path, new TagResult("new", [], null)));
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.Path));
        Assert.Equal(new[] { fixture.Path, fixture.Path + ".xmp" }, MetadataWriterService.AssociatedArtifacts(fixture.Path));
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".tif")]
    public async Task Successful_updates_preserve_pixels_attributes_and_unicode(string extension)
    {
        using var fixture = new MetadataFixture(extension);
        byte[] before;
        using (var image = NetVips.Image.NewFromFile(fixture.Path)) before = image.WriteToMemory<byte>();
        File.SetAttributes(fixture.Path, FileAttributes.Archive);
        var attributes = File.GetAttributes(fixture.Path);
        var writer = new MetadataWriterService();
        Assert.True(await writer.WriteAsync(fixture.Path, new TagResult("Caf\u00e9 & <nature>", ["\u65e5\u672c", "\u00e9t\u00e9"], "Snow \u2603 & <sun>")));
        var read = (await writer.ReadAsync(fixture.Path))!;
        Assert.Equal("Caf\u00e9 & <nature>", read.Category);
        Assert.Equal("Snow \u2603 & <sun>", read.Description);
        Assert.Equal(new[] { "\u65e5\u672c", "\u00e9t\u00e9" }, read.Keywords);
        using var after = NetVips.Image.NewFromFile(fixture.Path);
        Assert.Equal(before, after.WriteToMemory<byte>());
        Assert.Equal(attributes, File.GetAttributes(fixture.Path));
        fixture.AssertNoStagingFiles();
    }

    [Fact]
    public async Task Compressed_png_xmp_is_merged_and_replaced_with_one_chunk()
    {
        using var fixture = new MetadataFixture(".png");
        fixture.SeedXmp(compressed: true);
        Assert.True(await new MetadataWriterService().WriteAsync(fixture.Path, new TagResult(null, ["new"], null)));
        var read = (await new MetadataWriterService().ReadAsync(fixture.Path))!;
        Assert.Equal("Existing", read.Category);
        Assert.Equal(new[] { "old", "new" }, read.Keywords);
        Assert.Equal(1, Encoding.ASCII.GetString(await File.ReadAllBytesAsync(fixture.Path)).Split("XML:com.adobe.xmp").Length - 1);
        fixture.AssertPixelsDecode();
    }

    [Fact]
    public async Task Embedded_tiff_xmp_seeds_new_sidecar_without_changing_image()
    {
        using var fixture = new MetadataFixture(".tif");
        var temporary = System.IO.Path.Combine(fixture.DirectoryPath, "embedded.tif");
        using (var image = NetVips.Image.NewFromFile(fixture.Path))
        using (var embedded = image.Mutate(m => m.Set(NetVips.GValue.BlobType, "xmp-data", Encoding.UTF8.GetBytes(MetadataFixture.SeedPacket))))
            embedded.WriteToFile(temporary);
        File.Move(temporary, fixture.Path, overwrite: true);
        var original = await File.ReadAllBytesAsync(fixture.Path);
        var writer = new MetadataWriterService();
        Assert.Equal("Existing", (await writer.ReadAsync(fixture.Path))!.Category);
        Assert.True(await writer.WriteAsync(fixture.Path, new TagResult(null, ["new"], null)));
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.Path));
        Assert.Equal(new[] { "old", "new" }, (await writer.ReadAsync(fixture.Path))!.Keywords);
        Assert.Equal("Copyright holder", fixture.ReadXmp().Descendants(MetadataFixture.Dc + "rights").Single().Value);
    }

    [Fact]
    public async Task Extended_jpeg_xmp_is_rejected_without_mutation()
    {
        using var fixture = new MetadataFixture(".jpg");
        var original = await File.ReadAllBytesAsync(fixture.Path);
        var payload = Encoding.ASCII.GetBytes("http://ns.adobe.com/xmp/extension/\0unsupported");
        var segment = new byte[payload.Length + 4];
        segment[0] = 255; segment[1] = 225;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(segment, 4);
        var seeded = original[..2].Concat(segment).Concat(original[2..]).ToArray();
        await File.WriteAllBytesAsync(fixture.Path, seeded);
        Assert.False(await new MetadataWriterService().WriteAsync(fixture.Path, new TagResult("new", [], null)));
        Assert.Equal(seeded, await File.ReadAllBytesAsync(fixture.Path));
        fixture.AssertNoStagingFiles();
    }

    [Fact]
    public async Task Invalid_png_crc_is_rejected_without_changes()
    {
        using var fixture = new MetadataFixture(".png");
        var data = await File.ReadAllBytesAsync(fixture.Path);
        data[29] ^= 1; // IHDR CRC, not image contents
        await File.WriteAllBytesAsync(fixture.Path, data);
        Assert.False(await new MetadataWriterService().WriteAsync(fixture.Path, new TagResult("new", [], null)));
        Assert.Equal(data, await File.ReadAllBytesAsync(fixture.Path));
        fixture.AssertNoStagingFiles();
    }

    [Fact]
    public async Task Basic_app1_size_boundary_is_enforced_in_bytes()
    {
        using var fitting = new MetadataFixture(".jpg");
        using var oversized = new MetadataFixture(".jpg");
        var writer = new MetadataWriterService();
        Assert.True(await writer.WriteAsync(fitting.Path, new TagResult(null, [], "x")));
        var overhead = Encoding.UTF8.GetByteCount(fitting.ReadXmp().ToString(SaveOptions.DisableFormatting)) - 1;
        var maxDescription = ushort.MaxValue - 2 - Encoding.ASCII.GetByteCount("http://ns.adobe.com/xap/1.0/\0") - overhead;
        Assert.True(await writer.WriteAsync(fitting.Path, new TagResult(null, [], new string('x', maxDescription))));
        var original = await File.ReadAllBytesAsync(oversized.Path);
        Assert.False(await writer.WriteAsync(oversized.Path, new TagResult(null, [], new string('x', maxDescription + 1))));
        Assert.Equal(original, await File.ReadAllBytesAsync(oversized.Path));
        fitting.AssertPixelsDecode();
    }

    [Fact]
    public async Task Invalid_xml_characters_and_unsupported_patch_modes_fail_without_mutation()
    {
        using var fixture = new MetadataFixture(".jpg");
        var original = await File.ReadAllBytesAsync(fixture.Path);
        var writer = new MetadataWriterService();
        Assert.False(await writer.WriteAsync(fixture.Path, new TagResult("bad\u0001", [], null)));
        Assert.False(await writer.PatchAsync(fixture.Path, new MetadataPatch { Category = MetadataFieldPatch<string>.Merge("bad") }));
        Assert.False(await writer.PatchAsync(fixture.Path, new MetadataPatch { Keywords = new(MetadataPatchMode.Replace) }));
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.Path));
        fixture.AssertNoStagingFiles();
    }

    internal sealed class MetadataFixture : IDisposable
    {
        public string DirectoryPath { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"synapic-meta-{Guid.NewGuid():N}");
        public string Path { get; }

        public MetadataFixture(string extension)
        {
            Directory.CreateDirectory(DirectoryPath);
            Path = System.IO.Path.Combine(DirectoryPath, "image" + extension);
            var sample = FindSampleImage();
            Assert.NotNull(sample);
            if (extension == ".jpg") File.Copy(sample, Path);
            else
            {
                using var image = NetVips.Image.NewFromFile(sample);
                image.WriteToFile(Path);
            }
        }

        internal static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
        internal static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        internal const string SeedPacket = """
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:photoshop="http://ns.adobe.com/photoshop/1.0/" xmlns:test="urn:synapic:test" photoshop:Headline="Existing" test:value="Custom value">
            <dc:subject><rdf:Bag><rdf:li>old</rdf:li></rdf:Bag></dc:subject>
            <dc:description><rdf:Alt><rdf:li xml:lang="x-default">Original description</rdf:li><rdf:li xml:lang="fr">Traduction</rdf:li></rdf:Alt></dc:description>
            <dc:creator><rdf:Seq><rdf:li>Creator</rdf:li></rdf:Seq></dc:creator>
            <dc:rights><rdf:Alt><rdf:li xml:lang="x-default">Copyright holder</rdf:li></rdf:Alt></dc:rights>
            </rdf:Description></rdf:RDF></x:xmpmeta>
            """;

        internal void SeedXmp(string packet = SeedPacket, bool compressed = false)
        {
            var bytes = Encoding.UTF8.GetBytes(packet);
            if (System.IO.Path.GetExtension(Path) == ".tif") { File.WriteAllBytes(Path + ".xmp", bytes); return; }
            var image = File.ReadAllBytes(Path);
            byte[] segment;
            int insertAt;
            if (System.IO.Path.GetExtension(Path) == ".jpg")
            {
                var prefix = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
                segment = new byte[4 + prefix.Length + bytes.Length];
                segment[0] = 255; segment[1] = 225;
                BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(segment.Length - 2));
                prefix.CopyTo(segment, 4);
                bytes.CopyTo(segment, 4 + prefix.Length);
                insertAt = 2;
            }
            else
            {
                var prefix = Encoding.ASCII.GetBytes("XML:com.adobe.xmp\0\0\0\0\0");
                if (compressed)
                {
                    prefix[prefix.Length - 4] = 1;
                    using var output = new MemoryStream();
                    using (var zlib = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionMode.Compress, leaveOpen: true)) zlib.Write(bytes);
                    bytes = output.ToArray();
                }
                segment = new byte[12 + prefix.Length + bytes.Length];
                BinaryPrimitives.WriteUInt32BigEndian(segment, (uint)(prefix.Length + bytes.Length));
                Encoding.ASCII.GetBytes("iTXt").CopyTo(segment, 4);
                prefix.CopyTo(segment, 8);
                bytes.CopyTo(segment, 8 + prefix.Length);
                uint crc = 0xffffffff;
                foreach (var b in segment.AsSpan(4, segment.Length - 8))
                {
                    crc ^= b;
                    for (var bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : 0xedb88320 ^ (crc >> 1);
                }
                BinaryPrimitives.WriteUInt32BigEndian(segment.AsSpan(segment.Length - 4), crc ^ 0xffffffff);
                insertAt = 33;
            }
            File.WriteAllBytes(Path, image[..insertAt].Concat(segment).Concat(image[insertAt..]).ToArray());
        }

        internal XDocument ReadXmp()
        {
            if (System.IO.Path.GetExtension(Path) == ".tif") return XDocument.Load(Path + ".xmp");
            var data = File.ReadAllBytes(Path);
            var marker = Encoding.UTF8.GetBytes("<x:xmpmeta");
            // Use the actual byte packet to test preservation independent of the service reader.
            var start = data.AsSpan().IndexOf(marker);
            Assert.True(start >= 0);
            var endMarker = Encoding.UTF8.GetBytes("</x:xmpmeta>");
            var end = data.AsSpan(start).IndexOf(endMarker) + start + endMarker.Length;
            return XDocument.Parse(Encoding.UTF8.GetString(data[start..end]));
        }

        internal void AssertNoStagingFiles() => Assert.Empty(Directory.GetFiles(DirectoryPath, "*.tmp"));
        internal void AssertPixelsDecode()
        {
            using var image = NetVips.Image.NewFromFile(Path, failOn: NetVips.Enums.FailOn.Warning);
            Assert.True(image.Width > 0 && image.Height > 0);
            Assert.True(double.IsFinite(image.Avg()));
        }

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }

    [Fact]
    public async Task Jpeg_WriteThenRead_KeywordsOnly()
    {
        var path = CopySample();
        try
        {
            var writer = new MetadataWriterService();
            Assert.True(await writer.WriteAsync(path, new TagResult(null, new[] { "alpha", "beta" }, null)));

            var read = await writer.ReadAsync(path);
            Assert.NotNull(read);
            Assert.Contains("alpha", read!.Keywords);
            Assert.Contains("beta", read.Keywords);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

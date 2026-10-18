using System.Numerics;
using Synapic.Main.Services;
using Synapic.Main.Services.Processing;
using Xunit;

namespace Synapic.Main.Tests;

public class DedupServiceTests
{
    /// <summary>The committed sample image, found by walking up from the test
    /// bin directory (same approach as EndToEndRoundTripTests).</summary>
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

    // ── Preview thumbnails ────────────────────────────────────────────

    [Fact]
    public void CreateThumbnail_ScalesToTheBoxAndReturnsAJpeg()
    {
        var sample = FindSampleImage();
        if (sample is null) return; // repo checkout not reachable from the bin

        var bytes = DedupService.CreateThumbnail(sample, maxSize: 128);

        Assert.NotNull(bytes);
        Assert.True(bytes!.Length > 0);
        // JPEG magic — the buffer is what the row decodes straight into a bitmap.
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);

        using var decoded = NetVips.Image.NewFromBuffer(bytes, "")!;
        Assert.True(decoded.Width <= 128, $"thumbnail is {decoded.Width}px wide");
        Assert.True(decoded.Height <= 128, $"thumbnail is {decoded.Height}px tall");
    }

    [Fact]
    public void CreateThumbnail_UnreadableFile_ReturnsNull()
    {
        // Not an image at all: the row just loses its preview, the scan continues.
        Assert.Null(DedupService.CreateThumbnail(Path.Combine(Path.GetTempPath(), "synapic-no-such-file.jpg")));
    }

    /// <summary>
    /// A scan keeps its fingerprints on the result: that is what lets the results
    /// page re-group at another threshold without touching the files again. A
    /// result without them can only offer the setting for the next scan.
    /// </summary>
    [Fact]
    public async Task A_scan_retains_the_fingerprints_for_threshold_retuning()
    {
        var sample = FindSampleImage();
        if (sample is null) return; // repo checkout not reachable from the bin

        var folder = Directory.CreateTempSubdirectory("synapic-retain-");
        try
        {
            var a = Path.Combine(folder.FullName, "a.jpg");
            var b = Path.Combine(folder.FullName, "b.jpg");
            File.Copy(sample, a);
            File.Copy(sample, b);

            var service = new DedupService();
            var result = await service.FindDuplicatesAsync(
                new[] { a, b }, new DedupOptions(HashAlgorithm.PHash, 0.99));

            Assert.Equal(2, result.PerceptualHashes.Count);
            Assert.Contains(a, result.PerceptualHashes.Keys);
            Assert.Contains(b, result.PerceptualHashes.Keys);
            Assert.Empty(result.ExactHashes);
            Assert.Single(result.Groups);

            // The retained hashes are sufficient to re-group at another threshold.
            var regrouped = service.GroupFromHashes(
                result.PerceptualHashes, result.ExactHashes,
                new DedupOptions(HashAlgorithm.PHash, 0.85));
            Assert.Single(regrouped.Groups);
            Assert.Equal(2, regrouped.PerceptualHashes.Count);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void HammingDistance_IdenticalHashes_IsZero()
    {
        // Exposed via grouping behavior: identical hashes always group together.
        var result = DedupTestHarness.Group(new Dictionary<string, ulong>
        {
            ["a.png"] = 0b_1010_1010,
            ["b.png"] = 0b_1010_1010,
        }, 0.90);

        Assert.Single(result);
        Assert.Equal(2, result[0].Items.Length);
    }

    [Fact]
    public void Grouping_RespectsThreshold()
    {
        // 64-bit hashes differing by 6 bits → similarity 0.906 ≥ 0.90 → same group.
        var close = new Dictionary<string, ulong>
        {
            ["a.png"] = 0UL,
            ["b.png"] = 0b_111111UL, // 6 bits
        };
        Assert.Single(DedupTestHarness.Group(close, 0.90));

        // Differing by 7 bits → similarity 0.891 < 0.90 → separate.
        var far = new Dictionary<string, ulong>
        {
            ["a.png"] = 0UL,
            ["b.png"] = 0b_1111111UL, // 7 bits
        };
        Assert.Empty(DedupTestHarness.Group(far, 0.90));
    }

    [Fact]
    public void Grouping_TransitiveChains_JoinViaUnionFind()
    {
        // a≈b and b≈c but a!~c → still one group (connected components).
        var hashes = new Dictionary<string, ulong>
        {
            ["a.png"] = 0UL,
            ["b.png"] = 0b_111111UL,        // 6 bits from a
            ["c.png"] = 0b_111111_111111UL, // 6 bits from b, 12 from a
        };
        var groups = DedupTestHarness.Group(hashes, 0.90);
        Assert.Single(groups);
        Assert.Equal(3, groups[0].Items.Length);
    }

    [Fact]
    public void Grouping_Singletons_AreExcluded()
    {
        var hashes = new Dictionary<string, ulong>
        {
            ["a.png"] = 0UL,
            ["b.png"] = ulong.MaxValue,
        };
        Assert.Empty(DedupTestHarness.Group(hashes, 0.90));
    }

    // ── Server-hash grouping: selectable rule, spaces never mixed ────

    [Fact]
    public void ServerHashes_ExactRule_MergesOnlyIdenticalValues()
    {
        var exact = new Dictionary<string, ulong>
        {
            ["daminion:1"] = 100UL,
            ["daminion:2"] = 100UL,
            ["daminion:3"] = 200UL,
            ["daminion:4"] = 200UL,
        };

        var groups = DedupTestHarness.GroupExact(exact);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Equal("server-hash", g.HashType));
        Assert.All(groups, g => Assert.All(g.SimilarityScores, s => Assert.Equal(1.0, s, 10)));
    }

    [Fact]
    public void ServerHashes_ExactRule_DoesNotBridgeValuesInsideThePerceptualThreshold()
    {
        // 0 and 0b111111 differ by 6 bits → similarity 0.906 ≥ 0.90, so the
        // perceptual path groups them. Exact must not: different content hashes
        // are different files.
        var close = new Dictionary<string, ulong>
        {
            ["daminion:1"] = 0UL,
            ["daminion:2"] = 0b_111111UL,
        };

        Assert.Empty(DedupTestHarness.GroupExact(close));
        Assert.Single(DedupTestHarness.Group(close, 0.90)); // the perceptual path still groups
    }

    [Fact]
    public void ServerHashes_HammingRule_GroupsLikeTheAlgorithmicPath()
    {
        // The server hash is 32 bits wide, so 0.90 is a 3-bit budget, not 6.
        var close = new Dictionary<string, ulong>
        {
            ["daminion:1"] = 0UL,
            ["daminion:2"] = 0b_111UL,    // 3 bits from #1 → inside the threshold
            ["daminion:3"] = 0b_1111UL,   // 4 from #1, but 1 from #2 → joins the chain
        };

        var groups = DedupTestHarness.GroupExactHamming(close);

        Assert.Single(groups);
        Assert.Equal(3, groups[0].Items.Length);
        Assert.Equal("server-hash", groups[0].HashType);
        // Scores are normalized over the hash's own 32 bits, not 64.
        Assert.Equal(1.0 - 4.0 / 32.0, groups[0].SimilarityScores[2], 6);
    }

    [Fact]
    public void ServerHashes_HammingRule_StillExcludesSingletons()
    {
        var hashes = new Dictionary<string, ulong>
        {
            ["daminion:1"] = 0UL,
            ["daminion:2"] = ulong.MaxValue,
        };

        Assert.Empty(DedupTestHarness.GroupExactHamming(hashes));
    }

    [Fact]
    public void ServerHashes_AreNeverComparedWithAlgorithmicHashes()
    {
        // Identical values on purpose: if the two spaces were ever compared,
        // these two items would merge at distance 0 under either rule.
        var perceptual = new Dictionary<string, ulong> { ["file.png"] = 0UL };
        var exact = new Dictionary<string, ulong> { ["daminion:1"] = 0UL };
        var options = new DedupOptions(Threshold: 0.90);

        Assert.Empty(new DedupService().GroupFromHashes(perceptual, exact, options).Groups);
        Assert.Empty(
            new DedupService()
                .GroupFromHashes(perceptual, exact, options with { ServerHashMatch = ServerHashMatchMode.Hamming })
                .Groups);
    }

    [Fact]
    public void ServerHashes_TotalFilesCountsBothSpaces()
    {
        var result = new DedupService().GroupFromHashes(
            new Dictionary<string, ulong> { ["a.png"] = 1UL, ["b.png"] = 2UL },
            new Dictionary<string, ulong> { ["daminion:1"] = 3UL },
            new DedupOptions(Threshold: 0.90));

        Assert.Equal(3, result.TotalFiles);
    }

    // ── Pixel normalisation before hashing (orientation / alpha / cap) ──────

    private static readonly HashAlgorithm[] Algorithms =
        { HashAlgorithm.PHash, HashAlgorithm.DHash, HashAlgorithm.AHash };

    private static int Hamming(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    /// <summary>A colour picture with structure on both axes.</summary>
    private static byte[] RgbScene(int w, int h)
    {
        var px = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 3;
                px[i + 0] = (byte)Math.Clamp(90 + 120.0 * x / w + 30 * Math.Sin(y / 21.0), 0, 255);
                px[i + 1] = (byte)Math.Clamp(70 + 110.0 * y / h, 0, 255);
                px[i + 2] = (byte)Math.Clamp(140 - 90.0 * x / w + 25 * Math.Sin(x / 13.0), 0, 255);
            }
        return px;
    }

    private static string WriteJpeg(byte[] pixels, int w, int h, int bands, string path, int q = 92)
    {
        using var img = NetVips.Image.NewFromMemory(pixels, w, h, bands, NetVips.Enums.BandFormat.Uchar);
        img.Jpegsave(path, q: q);
        return path;
    }

    /// <summary>
    /// Prepend an APP1/EXIF segment carrying just an Orientation tag — how a
    /// phone writes a portrait shot (landscape sensor buffer, tag says
    /// "rotate 90° CW to display").
    /// </summary>
    private static void AddExifOrientation(string path, short orientation)
    {
        var jpeg = File.ReadAllBytes(path);
        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);

        var tiff = new byte[]
        {
            0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,             // "II", 42, IFD0 at 8
            0x01, 0x00,                                                 // one entry
            0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00,             // tag 0x0112, SHORT, count 1
            (byte)(orientation & 0xFF), (byte)(orientation >> 8), 0, 0, // its value
            0x00, 0x00, 0x00, 0x00,                                     // no next IFD
        };

        var payload = new byte[6 + tiff.Length];
        "Exif\0\0"u8.CopyTo(payload);
        tiff.CopyTo(payload, 6);

        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        var length = payload.Length + 2;
        segment[2] = (byte)(length >> 8);
        segment[3] = (byte)(length & 0xFF);
        payload.CopyTo(segment, 4);

        var output = new byte[jpeg.Length + segment.Length];
        jpeg.AsSpan(0, 2).CopyTo(output);                      // SOI stays first
        segment.CopyTo(output, 2);                             // APP1/EXIF
        jpeg.AsSpan(2).CopyTo(output.AsSpan(2 + segment.Length));
        File.WriteAllBytes(path, output);
    }

    /// <summary>
    /// The fingerprint has to describe the picture a person sees. A phone
    /// portrait photo is stored as a landscape buffer plus an EXIF orientation
    /// tag, so hashing the raw buffer scored a copy of the same shot ~0.5
    /// similarity — indistinguishable from an unrelated image.
    /// </summary>
    [Fact]
    public void ComputeHash_AppliesExifOrientation_SoAPhonePortraitMatchesItsUprightCopy()
    {
        var dir = Directory.CreateTempSubdirectory("synapic-orientation-").FullName;
        try
        {
            const int w = 360, h = 640;
            var uprightPath = WriteJpeg(RgbScene(w, h), w, h, 3, Path.Combine(dir, "upright.jpg"));

            // The same shot as the phone stores it: buffer rotated 90° counter-
            // clockwise, EXIF claiming the 90° CW display rotation.
            var phonePath = Path.Combine(dir, "phone.jpg");
            using (var upright = NetVips.Image.NewFromFile(uprightPath))
            {
                using var stored = upright.Rot(NetVips.Enums.Angle.D270);
                stored.Jpegsave(phonePath, q: 92);
            }
            AddExifOrientation(phonePath, 6);

            foreach (var algorithm in Algorithms)
            {
                var opts = new DedupOptions(Algorithm: algorithm);
                var upright = DedupService.ComputeHash(uprightPath, opts);
                var phone = DedupService.ComputeHash(phonePath, opts);

                Assert.NotNull(upright);
                Assert.NotNull(phone);
                var distance = Hamming(upright!.Value, phone!.Value);
                Assert.True(distance <= 6,
                    $"{algorithm}: an EXIF-rotated portrait scored {distance} of 64 bits from its own upright " +
                    "copy (the duplicate threshold allows 6)");
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Alpha is not visual content, and the hidden pixels under a transparent
    /// area are often garbage. libvips keeps the alpha band through
    /// <c>Colourspace(Bw)</c>, so the single-band readers sampled an
    /// interleaved grey/alpha buffer: the same picture scored 31–32 bits apart,
    /// and pHash ran past the end of its 32×32 buffer and produced no hash at
    /// all.
    /// </summary>
    [Fact]
    public void ComputeHash_IgnoresAlpha_SoAnOpaqueRgbaPngHashesLikeItsRgbTwin()
    {
        var dir = Directory.CreateTempSubdirectory("synapic-alpha-").FullName;
        try
        {
            const int w = 400, h = 300;
            var rgb = RgbScene(w, h);
            var rgba = new byte[w * h * 4];
            for (var p = 0; p < w * h; p++)
            {
                rgba[p * 4 + 0] = rgb[p * 3 + 0];
                rgba[p * 4 + 1] = rgb[p * 3 + 1];
                rgba[p * 4 + 2] = rgb[p * 3 + 2];
                rgba[p * 4 + 3] = 255; // fully opaque: the same picture
            }

            var rgbPath = Path.Combine(dir, "rgb.png");
            using (var img = NetVips.Image.NewFromMemory(rgb, w, h, 3, NetVips.Enums.BandFormat.Uchar))
                img.Pngsave(rgbPath);

            var rgbaPath = Path.Combine(dir, "rgba.png");
            using (var img = NetVips.Image.NewFromMemory(rgba, w, h, 4, NetVips.Enums.BandFormat.Uchar))
                img.Pngsave(rgbaPath);

            foreach (var algorithm in Algorithms)
            {
                var opts = new DedupOptions(Algorithm: algorithm);
                var rgbHash = DedupService.ComputeHash(rgbPath, opts);
                var rgbaHash = DedupService.ComputeHash(rgbaPath, opts);

                Assert.NotNull(rgbHash);
                Assert.NotNull(rgbaHash); // used to be null for pHash
                var distance = Hamming(rgbHash!.Value, rgbaHash!.Value);
                Assert.True(distance <= 2,
                    $"{algorithm}: an opaque RGBA PNG scored {distance} of 64 bits from the identical RGB " +
                    "PNG (alpha must not change the fingerprint)");
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// <see cref="DedupOptions.MaxDimension"/> is the documented "resize before
    /// hash". It used to be computed into an unused local while a constant
    /// 64-pixel preparation did the work, so the setting did nothing at all.
    /// </summary>
    [Fact]
    public void ComputeHash_HonoursMaxDimension()
    {
        var dir = Directory.CreateTempSubdirectory("synapic-cap-").FullName;
        try
        {
            // Detailed enough that a 64-pixel working copy really is coarser
            // than a 512-pixel one.
            const int w = 1200, h = 900;
            var detailed = new byte[w * h * 3];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var i = (y * w + x) * 3;
                    var checker = ((x / 3) + (y / 3)) % 2 == 0 ? 45 : 0;
                    detailed[i + 0] = (byte)Math.Clamp(80 + checker + 90.0 * x / w, 0, 255);
                    detailed[i + 1] = (byte)Math.Clamp(70 + checker + 80.0 * y / h, 0, 255);
                    detailed[i + 2] = (byte)Math.Clamp(110 - checker + 20 * Math.Sin(x / 5.0), 0, 255);
                }
            var bigPath = WriteJpeg(detailed, w, h, 3, Path.Combine(dir, "big.jpg"));

            var tiny = DedupService.ComputeHash(bigPath, new DedupOptions(MaxDimension: 64));
            var full = DedupService.ComputeHash(bigPath, new DedupOptions(MaxDimension: 512));
            Assert.NotNull(tiny);
            Assert.NotNull(full);
            Assert.NotEqual(tiny, full);

            // A picture already inside the cap is not resized, so every cap
            // above its longest edge agrees with every other.
            var smallPath = WriteJpeg(RgbScene(100, 80), 100, 80, 3, Path.Combine(dir, "small.jpg"));
            Assert.Equal(
                DedupService.ComputeHash(smallPath, new DedupOptions(MaxDimension: 128)),
                DedupService.ComputeHash(smallPath, new DedupOptions(MaxDimension: 512)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

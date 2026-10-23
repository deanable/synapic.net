using System.IO;
using Synapic.Core.Services.Processing;
using Xunit;

using Synapic.Core.Services;
using Synapic.Core.Services.Daminion;
namespace Synapic.Main.Tests;

/// <summary>
/// Tests for the DedupScanHandler's server-hash shortcut path: when a Daminion
/// item carries a server-computed hashCode, the handler groups it without
/// downloading anything. Items without a server hash are fingerprinted from the
/// server's preview, falling back to the original when the server renders none.
/// </summary>
public class DedupScanHandlerTests
{
    /// <summary>A fake downloader whose two download paths are tracked.
    /// <see cref="PreviewAvailable"/> off (the default) models a server that
    /// renders no preview, which is the fallback path.</summary>
    private sealed class FakeDownloader : IDaminionDownloader
    {
        public int DownloadOriginalCallCount { get; private set; }
        public int DownloadPreviewCallCount { get; private set; }
        public bool PreviewAvailable { get; init; }
        public string TempDir { get; } = Path.Combine(Path.GetTempPath(), $"synapic-dedup-tests-{Guid.NewGuid():N}");

        public async Task<string?> DownloadPreviewAsync(int itemId, int width, int height, CancellationToken ct = default)
        {
            DownloadPreviewCallCount++;
            if (!PreviewAvailable) return null;
            Directory.CreateDirectory(TempDir);
            var path = Path.Combine(TempDir, $"preview_{itemId}");
            await File.WriteAllBytesAsync(path, new byte[] { 7, 7, 7 }, ct);
            return path;
        }

        public async Task<string?> DownloadOriginalAsync(int itemId, CancellationToken ct = default)
        {
            DownloadOriginalCallCount++;
            Directory.CreateDirectory(TempDir);
            var path = Path.Combine(TempDir, $"original_{itemId}");
            await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 }, ct);
            return path;
        }
    }

    /// <summary>A fake downloader that always fails.</summary>
    private sealed class FailedDownloader : IDaminionDownloader
    {
        public Task<string?> DownloadOriginalAsync(int itemId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<string?> DownloadPreviewAsync(int itemId, int width, int height, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    /// <summary>A fake IDedupService whose ComputeHash is tracked.</summary>
    private sealed class FakeDedup : IDedupService
    {
        public int ComputeHashCallCount { get; private set; }

        /// <summary>Models bytes that decode to nothing: the first hash attempt
        /// fails, later ones succeed (a preview that cannot be read, then the
        /// original).</summary>
        public bool FailFirstCall { get; init; }

        public ulong? ComputeHash(string path, DedupOptions opts)
        {
            ComputeHashCallCount++;
            if (FailFirstCall && ComputeHashCallCount == 1) return null;
            return 0xFF_FFFF_FFFF_FFFFUL; // a deterministic fake hash
        }

        Task<DedupResult> IDedupService.FindDuplicatesAsync(IEnumerable<string> imagePaths, DedupOptions opts, IProgress<DedupProgress>? progress, CancellationToken ct)
            => Task.FromResult(new DedupResult { TotalFiles = 0 });

        DedupResult IDedupService.GroupFromHashes(
            IReadOnlyDictionary<string, ulong> perceptualHashes,
            IReadOnlyDictionary<string, ulong> exactHashes,
            DedupOptions opts)
            => new DedupResult { TotalFiles = perceptualHashes.Count + exactHashes.Count };

        Task<bool> IDedupService.ApplyToPathsAsync(IEnumerable<string> paths, DedupAction action, string? moveDestination, CancellationToken ct)
            => Task.FromResult(true);

        Task<bool> IDedupService.ApplyActionsAsync(DedupResult result, DedupAction action, string? moveDestination, CancellationToken ct)
            => Task.FromResult(true);
    }

    /// <summary>A fake IDedupService whose ComputeHash always returns null (unreadable).</summary>
    private sealed class FailingDedup : IDedupService
    {
        public int ComputeHashCallCount { get; private set; }

        public ulong? ComputeHash(string path, DedupOptions opts)
        {
            ComputeHashCallCount++;
            return null; // signal unreadable
        }

        Task<DedupResult> IDedupService.FindDuplicatesAsync(IEnumerable<string> imagePaths, DedupOptions opts, IProgress<DedupProgress>? progress, CancellationToken ct)
            => Task.FromResult(new DedupResult { TotalFiles = 0 });

        DedupResult IDedupService.GroupFromHashes(
            IReadOnlyDictionary<string, ulong> perceptualHashes,
            IReadOnlyDictionary<string, ulong> exactHashes,
            DedupOptions opts)
            => new DedupResult { TotalFiles = perceptualHashes.Count + exactHashes.Count };

        Task<bool> IDedupService.ApplyToPathsAsync(IEnumerable<string> paths, DedupAction action, string? moveDestination, CancellationToken ct)
            => Task.FromResult(true);

        Task<bool> IDedupService.ApplyActionsAsync(DedupResult result, DedupAction action, string? moveDestination, CancellationToken ct)
            => Task.FromResult(true);
    }

    private static readonly DedupOptions Options = new(HashAlgorithm.PHash, 0.90, 512);

    [Fact]
    public async Task ItemsWithServerHash_AreGroupedWithoutDownload()
    {
        var downloader = new FakeDownloader();
        var dedup = new FakeDedup();
        var handler = new DedupScanHandler(downloader, Options, dedup);

        var items = new[]
        {
            new ProcessWorkItem { DaminionId = 1, FileName = "a.jpg", ServerHashCode = 12345 },
            new ProcessWorkItem { DaminionId = 2, FileName = "b.jpg", ServerHashCode = 12345 }, // same hash → duplicate
            new ProcessWorkItem { DaminionId = 3, FileName = "c.jpg", ServerHashCode = 67890 }, // different hash
        };

        foreach (var item in items)
            await handler.ProcessAsync(item, CancellationToken.None);

        // No downloads should have happened — the server hash was used directly.
        Assert.Equal(0, downloader.DownloadOriginalCallCount);
        Assert.Equal(0, dedup.ComputeHashCallCount);

        // All three items land in the server-hash set, none in the perceptual one.
        Assert.Equal(3, handler.ExactHashes.Count);
        Assert.Empty(handler.PerceptualHashes);

        // Two items share hash 12345 → they should be in the same group.
        Assert.Equal(12345UL, handler.ExactHashes["daminion:1"]);
        Assert.Equal(12345UL, handler.ExactHashes["daminion:2"]);
        Assert.Equal(67890UL, handler.ExactHashes["daminion:3"]);

        // The server-hash-grouped counter should reflect all three items.
        Assert.Equal(3, handler.ServerHashGrouped);
        Assert.Equal(0, handler.DownloadFailures);
        Assert.Equal(0, handler.HashFailures);

        // Record metadata: SizeBytes should be 0 (no download), ServerHashCode set.
        Assert.All(handler.Records.Values, r =>
        {
            Assert.Equal(0, r.SizeBytes); // no download occurred
            Assert.True(r.ServerHashCode != 0);
        });
    }

    [Fact]
    public async Task ItemsWithSameServerHash_FormADuplicateGroup()
    {
        var downloader = new FakeDownloader();
        var dedup = new FakeDedup();
        var handler = new DedupScanHandler(downloader, Options, dedup);

        // Five items: three share one server hash, two share another.
        // Items with the same server hash get the same ulong value in Hashes.
        var items = new[]
        {
            new ProcessWorkItem { DaminionId = 1, FileName = "a1.jpg", ServerHashCode = 100 },
            new ProcessWorkItem { DaminionId = 2, FileName = "a2.jpg", ServerHashCode = 100 },
            new ProcessWorkItem { DaminionId = 3, FileName = "a3.jpg", ServerHashCode = 100 },
            new ProcessWorkItem { DaminionId = 4, FileName = "b1.jpg", ServerHashCode = 200 },
            new ProcessWorkItem { DaminionId = 5, FileName = "b2.jpg", ServerHashCode = 200 },
        };

        foreach (var item in items)
            await handler.ProcessAsync(item, CancellationToken.None);

        Assert.Equal(5, handler.ExactHashes.Count);
        Assert.Equal(5, handler.ServerHashGrouped);

        // Items with the same server hash get the same ulong value.
        Assert.Equal(100UL, handler.ExactHashes["daminion:1"]);
        Assert.Equal(100UL, handler.ExactHashes["daminion:2"]);
        Assert.Equal(100UL, handler.ExactHashes["daminion:3"]);
        Assert.Equal(200UL, handler.ExactHashes["daminion:4"]);
        Assert.Equal(200UL, handler.ExactHashes["daminion:5"]);

        // The default (exact) rule must keep 100 and 200 apart even though their
        // values sit inside the 0.90 hamming threshold (5 bits differ): different
        // server hashes mean different files until proven otherwise.
        var result = new DedupService().GroupFromHashes(
            handler.PerceptualHashes, handler.ExactHashes, Options);

        Assert.Equal(2, result.Groups.Count);
        Assert.All(result.Groups, g => Assert.Equal("server-hash", g.HashType));
        Assert.Equal(3, result.Groups.Single(g => g.Items.Contains("daminion:1")).Items.Length);
        Assert.Equal(2, result.Groups.Single(g => g.Items.Contains("daminion:4")).Items.Length);
    }

    [Fact]
    public async Task ServerHashHammingMode_BridgesValuesInsideTheThreshold()
    {
        var handler = new DedupScanHandler(new FakeDownloader(), Options, new FakeDedup());

        await handler.ProcessAsync(
            new ProcessWorkItem { DaminionId = 1, FileName = "a.jpg", ServerHashCode = 100 },
            CancellationToken.None);
        await handler.ProcessAsync(
            new ProcessWorkItem { DaminionId = 2, FileName = "b.jpg", ServerHashCode = 200 },
            CancellationToken.None);

        // 100 ^ 200 = 5 bits. The server hash is 32 bits wide, so 0.90 allows
        // only floor(32 × 0.10) = 3; 0.84 allows 5 and bridges the pair. That is
        // the opt-in rule the user picks when the server hash turns out to be
        // perceptual — measured on the hash's own width, not on 64 bits.
        var hamming = Options with { ServerHashMatch = ServerHashMatchMode.Hamming, Threshold = 0.84 };
        var result = new DedupService().GroupFromHashes(
            handler.PerceptualHashes, handler.ExactHashes, hamming);

        var group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Items.Length);
        Assert.Equal("server-hash", group.HashType);
    }

    /// <summary>
    /// The similarity profile turns the shortcut off. A content hash answers "is
    /// this the same file", so reusing it made every catalog similarity scan an
    /// exact-match scan — the algorithm and threshold applied to no item at all
    /// (a live run reported "500 items, 500 from server hash, 0 hashed").
    /// </summary>
    [Fact]
    public async Task Similar_scan_hashes_pixels_instead_of_reusing_the_server_hash()
    {
        var downloader = new FakeDownloader();
        var dedup = new FakeDedup();
        var handler = new DedupScanHandler(downloader, Options with { UseServerHashes = false }, dedup);

        await handler.ProcessAsync(
            new ProcessWorkItem { DaminionId = 1, FileName = "a.jpg", ServerHashCode = 12345 },
            CancellationToken.None);

        // The item is downloaded and fingerprinted, not filed under its content hash.
        Assert.Equal(1, downloader.DownloadOriginalCallCount);
        Assert.Equal(1, dedup.ComputeHashCallCount);
        Assert.Empty(handler.ExactHashes);
        Assert.Single(handler.PerceptualHashes);
        Assert.Equal(0, handler.ServerHashGrouped);
    }

    /// <summary>
    /// Daminion's hashCode is a signed 32-bit number (every observed value fits
    /// int, about half negative). Casting long → ulong sign-extends, so a
    /// negative value carried 32 bits of 1s that a positive value could never
    /// match: Hamming mode compared the sign padding, and a page of unrelated
    /// items yielded three "similar" groups out of pure coincidence.
    /// </summary>
    [Fact]
    public async Task A_negative_server_hash_is_zero_extended_to_32_bits()
    {
        var handler = new DedupScanHandler(new FakeDownloader(), Options, new FakeDedup());

        await handler.ProcessAsync(
            new ProcessWorkItem { DaminionId = 1, FileName = "a.jpg", ServerHashCode = -1 },
            CancellationToken.None);

        Assert.Equal(0xFFFF_FFFFUL, handler.ExactHashes["daminion:1"]);
        // The record still carries the server's own value for display and logs.
        Assert.Equal(-1, handler.Records["daminion:1"].ServerHashCode);
    }

    /// <summary>
    /// The hamming budget for a 32-bit server hash must be derived from 32 bits.
    /// -1 and 0x7FFFFFFF differ in exactly one bit of 32; the sign-extending cast
    /// put 33 bits between them, so the pair could never group.
    /// </summary>
    [Fact]
    public void Server_hash_hamming_is_measured_over_32_bits()
    {
        var hashes = new Dictionary<string, ulong>
        {
            ["daminion:1"] = 0xFFFF_FFFFUL,          // -1
            ["daminion:2"] = 0x7FFF_FFFFUL,          // int.MaxValue
        };
        var opts = Options with { ServerHashMatch = ServerHashMatchMode.Hamming };

        var result = new DedupService().GroupFromHashes(
            new Dictionary<string, ulong>(), hashes, opts);

        var group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Items.Length);
        // 1 of 32 bits differs, so the pair sits at 0.96875 — not 1 − 33/64 = 0.48.
        Assert.Equal(1.0 - 1.0 / 32.0, group.SimilarityScores[1], 6);
        // The same threshold is a smaller budget on a narrower hash: 10% of 32
        // bits, not 10% of 64.
        Assert.Equal(3, DedupService.MaxHammingDistance(0.90, bits: 32));
        Assert.Equal(6, DedupService.MaxHammingDistance(0.90));
    }

    [Fact]
    public async Task ItemsWithoutServerHash_FallBackToDownloadAndHash()
    {
        var downloader = new FakeDownloader();
        var dedup = new FakeDedup();
        var handler = new DedupScanHandler(downloader, Options, dedup);

        var items = new ProcessWorkItem[]
        {
            new() { DaminionId = 1, FileName = "no-hash.jpg", ServerHashCode = 0 }, // zero = no server hash
        };

        await handler.ProcessAsync(items[0], CancellationToken.None);

        // The fallback path should have downloaded and hashed.
        Assert.Equal(1, downloader.DownloadOriginalCallCount);
        Assert.Equal(1, dedup.ComputeHashCallCount);
        Assert.Equal(0, handler.ServerHashGrouped);
        Assert.Single(handler.PerceptualHashes);
        Assert.Equal(0xFF_FFFF_FFFF_FFFFUL, handler.PerceptualHashes["daminion:1"]);
        Assert.Empty(handler.ExactHashes);
    }

    [Fact]
    public async Task MixedItems_SplitBetweenServerHashAndDownload()
    {
        var downloader = new FakeDownloader();
        var dedup = new FakeDedup();
        var handler = new DedupScanHandler(downloader, Options, dedup);

        var items = new[]
        {
            new ProcessWorkItem { DaminionId = 1, FileName = "hashed.jpg", ServerHashCode = 42 },    // server hash path
            new ProcessWorkItem { DaminionId = 2, FileName = "downloaded.jpg", ServerHashCode = 0 }, // download path
        };

        foreach (var item in items)
            await handler.ProcessAsync(item, CancellationToken.None);

        // The two spaces stay apart: the server-hash item lands in ExactHashes,
        // the downloaded one in PerceptualHashes.
        Assert.Single(handler.ExactHashes);
        Assert.Single(handler.PerceptualHashes);
        Assert.Equal(1, handler.ServerHashGrouped);
        Assert.Equal(1, downloader.DownloadOriginalCallCount);
        Assert.Equal(1, dedup.ComputeHashCallCount);

        Assert.Equal(42UL, handler.ExactHashes["daminion:1"]);
        Assert.Equal(0xFF_FFFF_FFFF_FFFFUL, handler.PerceptualHashes["daminion:2"]);
    }

    [Fact]
    public async Task ServerHashGroupedItems_HaveZeroSizeInRecord()
    {
        var downloader = new FakeDownloader();
        var dedup = new FakeDedup();
        var handler = new DedupScanHandler(downloader, Options, dedup);

        var item = new ProcessWorkItem { DaminionId = 1, FileName = "x.jpg", ServerHashCode = 99 };
        await handler.ProcessAsync(item, CancellationToken.None);

        var record = handler.Records["daminion:1"];
        Assert.Equal(0, record.SizeBytes);
        Assert.Equal(99, record.ServerHashCode);
    }

    [Fact]
    public async Task DownloadFailure_IsCounted_NotThrown()
    {
        var failingHandler = new DedupScanHandler(new FailedDownloader(), Options, new FakeDedup());

        var item = new ProcessWorkItem { DaminionId = 1, FileName = "bad.jpg", ServerHashCode = 0 };
        var outcome = await failingHandler.ProcessAsync(item, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal("no original downloaded", outcome.Status);
        Assert.Equal(1, failingHandler.DownloadFailures);
        Assert.Empty(failingHandler.PerceptualHashes);
        Assert.Empty(failingHandler.ExactHashes);
    }

    [Fact]
    public async Task A_non_Daminion_item_is_skipped_as_a_soft_failure()
    {
        var handler = new DedupScanHandler(new FakeDownloader(), Options, new FakeDedup());

        var outcome = await handler.ProcessAsync(
            new ProcessWorkItem { LocalPath = "local.jpg", FileName = "local.jpg" },
            CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal("not a Daminion item", outcome.Status);
        Assert.Empty(handler.Records);
        Assert.Empty(handler.PerceptualHashes);
        Assert.Empty(handler.ExactHashes);
    }

    [Fact]
    public async Task HashFailure_IsCounted_NotThrown()
    {
        var downloader = new FakeDownloader();
        var handler = new DedupScanHandler(downloader, Options, new FailingDedup());

        var item = new ProcessWorkItem { DaminionId = 1, FileName = "bad-hash.jpg", ServerHashCode = 0 };
        var outcome = await handler.ProcessAsync(item, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal("could not hash", outcome.Status);
        Assert.Equal(0, handler.ServerHashGrouped);
        Assert.Equal(1, handler.HashFailures);
    }

    [Fact]
    public void ServerHashCode_PopulatedInProcessWorkItem_FromDaminionItem()
    {
        // Verify the shape: DaminionItem with hashCode → ProcessWorkItem with ServerHashCode.
        var daminionItem = new Synapic.Core.Services.Daminion.DaminionItem
        {
            Id = 42,
            FileName = "test.jpg",
            HashCode = 12345
        };

        var workItem = new ProcessWorkItem
        {
            DaminionId = daminionItem.Id,
            FileName = daminionItem.FileName ?? "Item 42",
            ServerHashCode = daminionItem.HashCode ?? 0
        };

        Assert.Equal(42, workItem.DaminionId);
        Assert.Equal("test.jpg", workItem.FileName);
        Assert.Equal(12345, workItem.ServerHashCode);
    }

    [Fact]
    public void ServerHashCode_NullInDaminionItem_BecomesZeroInWorkItem()
    {
        var daminionItem = new Synapic.Core.Services.Daminion.DaminionItem
        {
            Id = 42,
            FileName = "test.jpg",
            HashCode = null
        };

        var workItem = new ProcessWorkItem
        {
            DaminionId = daminionItem.Id,
            FileName = daminionItem.FileName ?? "Item 42",
            ServerHashCode = daminionItem.HashCode ?? 0
        };

        Assert.Equal(0, workItem.ServerHashCode);
    }

    [Fact]
    public async Task ServerHashGroupedItem_UsesServerReportedSize()
    {
        // The server-hash path never downloads, so the only size the review UI
        // can offer is the server's own fileSize. Losing it would silently
        // disable the smallest/largest auto-select rules on that path.
        var handler = new DedupScanHandler(new FakeDownloader(), Options, new FakeDedup());

        await handler.ProcessAsync(
            new ProcessWorkItem { DaminionId = 1, FileName = "x.jpg", ServerHashCode = 7, SizeBytes = 2048 },
            CancellationToken.None);

        var record = handler.Records["daminion:1"];
        Assert.Equal(2048, record.SizeBytes);
        Assert.Equal(7, record.ServerHashCode);
        Assert.Equal(0, handler.DownloadFailures);
    }

    // ── Hashing the server's preview instead of the original ───────────────

    /// <summary>An item whose catalog record carries its pixel size.</summary>
    private static ProcessWorkItem Sized(int id, int w = 6000, int h = 4000, long size = 5_000_000) =>
        new()
        {
            DaminionId = id,
            FileName = $"item{id}.jpg",
            ServerHashCode = 0,
            Width = w,
            Height = h,
            SizeBytes = size,
        };

    [Fact]
    public async Task AServerHashedItem_StaysTheCheapestPath_NoPreviewNoOriginal()
    {
        // The preview path must not push a request onto the items the catalog
        // already hashed: those are grouped from the hash with no transfer at all.
        var downloader = new FakeDownloader { PreviewAvailable = true };
        var handler = new DedupScanHandler(downloader, Options, new FakeDedup());

        await handler.ProcessAsync(
            new ProcessWorkItem
            {
                DaminionId = 1, FileName = "a.jpg", ServerHashCode = 4242, Width = 6000, Height = 4000,
            },
            CancellationToken.None);

        Assert.Equal(0, downloader.DownloadPreviewCallCount);
        Assert.Equal(0, downloader.DownloadOriginalCallCount);
        Assert.Equal(0, handler.PreviewHashed);
        Assert.Equal(1, handler.ServerHashGrouped);
    }

    [Fact]
    public async Task ThePreviewIsHashed_NotTheOriginal_WhenTheServerRendersOne()
    {
        var downloader = new FakeDownloader { PreviewAvailable = true };
        var dedup = new FakeDedup();
        var handler = new DedupScanHandler(downloader, Options, dedup);

        var outcome = await handler.ProcessAsync(Sized(1, size: 4_321_000), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(1, downloader.DownloadPreviewCallCount);
        Assert.Equal(0, downloader.DownloadOriginalCallCount);
        Assert.Equal(1, dedup.ComputeHashCallCount);

        var record = handler.Records["daminion:1"];
        // The review list's size column and the smallest/largest rules describe
        // the file the user is keeping, not the preview that was hashed.
        Assert.Equal(4_321_000, record.SizeBytes);
        Assert.Equal(0, handler.PreviewFailures);
        Assert.Equal(0, handler.DownloadFailures);
    }

    [Fact]
    public async Task NoPreview_FallsBackToTheOriginal()
    {
        var downloader = new FakeDownloader { PreviewAvailable = false };
        var handler = new DedupScanHandler(downloader, Options, new FakeDedup());

        var outcome = await handler.ProcessAsync(Sized(1), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(1, downloader.DownloadPreviewCallCount);
        Assert.Equal(1, downloader.DownloadOriginalCallCount);
        Assert.Equal(0, handler.PreviewHashed);
        Assert.Equal(1, handler.PreviewFailures);
        // The downloaded original's own length is what is known on this path.
        Assert.Equal(3, handler.Records["daminion:1"].SizeBytes);
    }

    [Fact]
    public async Task DimensionsUnknown_SkipsThePreviewRequest()
    {
        // A preview request needs a box; without the catalog's dimensions the
        // handler would have to look them up, which is a second request per item.
        var downloader = new FakeDownloader { PreviewAvailable = true };
        var handler = new DedupScanHandler(downloader, Options, new FakeDedup());

        await handler.ProcessAsync(
            new ProcessWorkItem { DaminionId = 1, FileName = "x.jpg" }, CancellationToken.None);

        Assert.Equal(0, downloader.DownloadPreviewCallCount);
        Assert.Equal(1, downloader.DownloadOriginalCallCount);
        Assert.Equal(0, handler.PreviewHashed);
    }

    [Fact]
    public async Task APreviewThatCannotBeDecoded_FallsBackToTheOriginal()
    {
        // Losing the item from the scan because its preview is broken would be
        // worse than the download this path exists to avoid.
        var downloader = new FakeDownloader { PreviewAvailable = true };
        var dedup = new FakeDedup { FailFirstCall = true };
        var handler = new DedupScanHandler(downloader, Options, dedup);

        var outcome = await handler.ProcessAsync(Sized(1), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(1, downloader.DownloadPreviewCallCount);
        Assert.Equal(1, downloader.DownloadOriginalCallCount);
        Assert.Equal(2, dedup.ComputeHashCallCount);
        Assert.Equal(0, handler.PreviewHashed);
        Assert.Equal(0, handler.HashFailures);
    }

    [Fact]
    public async Task AGroupedPreviewItem_HasNoCaptureDate()
    {
        // A preview carries no EXIF, so the handler cannot invent a date; the
        // review list fills it in from the item's metadata for grouped rows.
        var handler = new DedupScanHandler(
            new FakeDownloader { PreviewAvailable = true }, Options, new FakeDedup());

        await handler.ProcessAsync(Sized(1), CancellationToken.None);

        Assert.Null(handler.Records["daminion:1"].DateTakenUtc);
    }
}

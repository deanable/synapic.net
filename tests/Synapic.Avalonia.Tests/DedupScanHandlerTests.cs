using System.IO;
using Synapic.Avalonia.Services.Processing;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// Tests for the DedupScanHandler's server-hash shortcut path: when a Daminion
/// item carries a server-computed hashCode, the handler groups it without
/// downloading the original. Items without a server hash still go through the
/// download-and-algorithmic-hash path.
/// </summary>
public class DedupScanHandlerTests
{
    /// <summary>A fake downloader whose DownloadOriginalAsync is tracked.</summary>
    private sealed class FakeDownloader : IDaminionDownloader
    {
        public int DownloadOriginalCallCount { get; private set; }
        public string TempDir { get; } = Path.Combine(Path.GetTempPath(), $"synapic-dedup-tests-{Guid.NewGuid():N}");

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
    }

    /// <summary>A fake IDedupService whose ComputeHash is tracked.</summary>
    private sealed class FakeDedup : IDedupService
    {
        public int ComputeHashCallCount { get; private set; }

        public ulong? ComputeHash(string path, DedupOptions opts)
        {
            ComputeHashCallCount++;
            return 0xFF_FFFF_FFFF_FFFFUL; // a deterministic fake hash
        }

        Task<DedupResult> IDedupService.FindDuplicatesAsync(IEnumerable<string> imagePaths, DedupOptions opts, IProgress<DedupProgress>? progress, CancellationToken ct)
            => Task.FromResult(new DedupResult { TotalFiles = 0 });

        DedupResult IDedupService.GroupFromHashes(
            IReadOnlyDictionary<string, ulong> perceptualHashes,
            IReadOnlyDictionary<string, ulong> exactHashes,
            DedupOptions opts)
            => new DedupResult { TotalFiles = perceptualHashes.Count + exactHashes.Count };

        Task<bool> IDedupService.ApplyToPathsAsync(IEnumerable<string> paths, DedupAction action, CancellationToken ct)
            => Task.FromResult(true);

        Task<bool> IDedupService.ApplyActionsAsync(DedupResult result, DedupAction action, CancellationToken ct)
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

        Task<bool> IDedupService.ApplyToPathsAsync(IEnumerable<string> paths, DedupAction action, CancellationToken ct)
            => Task.FromResult(true);

        Task<bool> IDedupService.ApplyActionsAsync(DedupResult result, DedupAction action, CancellationToken ct)
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

        var hamming = Options with { ServerHashMatch = ServerHashMatchMode.Hamming };
        var result = new DedupService().GroupFromHashes(
            handler.PerceptualHashes, handler.ExactHashes, hamming);

        // 100 ^ 200 = 5 bits, inside the threshold's 6 — the opt-in rule the user
        // picks when the server hash turns out to be perceptual.
        var group = Assert.Single(result.Groups);
        Assert.Equal(2, group.Items.Length);
        Assert.Equal("server-hash", group.HashType);
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
        var daminionItem = new Services.Daminion.DaminionItem
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
        var daminionItem = new Services.Daminion.DaminionItem
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
}

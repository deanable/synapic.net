using Microsoft.Extensions.DependencyInjection;
using Synapic.Main.Services.Processing;
using Synapic.Main.ViewModels.Steps;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// The last scan's fingerprints survive a restart: written per operation and
/// source, read back into the review list (and its threshold slider) without
/// hashing anything. These tests cover the store itself and the view model's
/// use of it — what is restored, when it is refused, and what invalidates it.
/// </summary>
public class DedupFingerprintRestoreTests
{
    // ── The store ───────────────────────────────────────────────────────────

    private static CachedDedupScan Scan(
        string sourceKey,
        string algorithm = "phash",
        params (string Key, ulong Hash)[] items) =>
        new(
            DedupFingerprintCache.CurrentVersion,
            sourceKey,
            algorithm,
            0.95,
            items.Length,
            items.Select(i => new CachedDedupItem(i.Key, Basename(i.Key), 1234, null, null)).ToArray(),
            items.ToDictionary(i => i.Key, i => i.Hash),
            new Dictionary<string, ulong>());

    /// <summary>The display name the saved scan carries for a row. The tests
    /// deliberately simulate Windows source rows ("C:\photos\a.jpg"), so the
    /// name is split on both separators: in production the save path only ever
    /// names keys from the local file system, but Path.GetFileName is a no-op
    /// for a Windows path on a Linux runner and turned the round-trip test into
    /// an OS coincidence rather than a store contract.</summary>
    private static string Basename(string key)
    {
        var cut = key.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? key : key[(cut + 1)..];
    }

    [Fact]
    public async Task A_saved_scan_round_trips_with_its_hashes_and_rows()
    {
        using var dir = new TempDir();
        var cache = new DedupFingerprintCache(dir.Path);
        var saved = Scan("local|C:\\photos", "phash", ("C:\\photos\\a.jpg", 0UL), ("C:\\photos\\b.jpg", 7UL));

        await cache.SaveAsync("dedup", saved);
        var loaded = await cache.LoadAsync("dedup", "local|C:\\photos");

        Assert.NotNull(loaded);
        Assert.Equal(DedupFingerprintCache.CurrentVersion, loaded!.Version);
        Assert.Equal("local|C:\\photos", loaded.SourceKey);
        Assert.Equal("phash", loaded.Algorithm);
        Assert.Equal(0.95, loaded.Threshold, 6);
        Assert.Equal(2, loaded.TotalFiles);
        Assert.Equal(2, loaded.PerceptualHashes.Count);
        Assert.Equal(7UL, loaded.PerceptualHashes["C:\\photos\\b.jpg"]);
        Assert.Equal("b.jpg", loaded.Items.Single(i => i.Key == "C:\\photos\\b.jpg").FileName);
        Assert.Equal(1234, loaded.Items[0].SizeBytes);
    }

    [Fact]
    public async Task A_cache_belongs_to_one_source_and_is_not_borrowed_by_another()
    {
        using var dir = new TempDir();
        var cache = new DedupFingerprintCache(dir.Path);
        await cache.SaveAsync("dedup", Scan("local|C:\\one", "phash", ("C:\\one\\a.jpg", 1UL)));

        Assert.Null(await cache.LoadAsync("dedup", "local|C:\\two"));
        // The two operations keep their own results even over the same source.
        Assert.Null(await cache.LoadAsync("similar", "local|C:\\one"));
        Assert.NotNull(await cache.LoadAsync("dedup", "local|C:\\one"));
    }

    [Fact]
    public async Task A_corrupt_or_old_cache_is_ignored_rather_than_thrown()
    {
        using var dir = new TempDir();
        var cache = new DedupFingerprintCache(dir.Path);
        await cache.SaveAsync("dedup", Scan("local|C:\\x", "phash", ("C:\\x\\a.jpg", 1UL)));

        // Truncated gzip: what a crash mid-write used to leave behind.
        var file = Assert.Single(Directory.GetFiles(dir.Path, "dedup-*.json.gz"));
        await File.WriteAllBytesAsync(file, new byte[] { 0x1f, 0x8b, 0x08, 0x00, 0xde, 0xad });

        Assert.Null(await cache.LoadAsync("dedup", "local|C:\\x"));

        // A readable file from another format version is ignored too — guessing
        // at its meaning is how a stale result would look current.
        await cache.SaveAsync("dedup", Scan("local|C:\\x", "phash", ("C:\\x\\a.jpg", 1UL)) with
        {
            Version = DedupFingerprintCache.CurrentVersion + 1,
        });
        Assert.Null(await cache.LoadAsync("dedup", "local|C:\\x"));
    }

    [Fact]
    public async Task Deleting_a_source_drops_only_its_cache()
    {
        using var dir = new TempDir();
        var cache = new DedupFingerprintCache(dir.Path);
        await cache.SaveAsync("dedup", Scan("local|C:\\a", "phash", ("C:\\a\\1.jpg", 1UL)));
        await cache.SaveAsync("dedup", Scan("local|C:\\b", "phash", ("C:\\b\\1.jpg", 2UL)));

        cache.Delete("dedup", "local|C:\\a");

        Assert.Null(await cache.LoadAsync("dedup", "local|C:\\a"));
        Assert.NotNull(await cache.LoadAsync("dedup", "local|C:\\b"));
    }

    [Fact]
    public async Task Old_caches_for_one_operation_are_pruned_to_the_newest_eight()
    {
        using var dir = new TempDir();
        var cache = new DedupFingerprintCache(dir.Path);
        // Each save prunes, so the folder never holds more than eight: the file
        // just written always survives, the oldest goes.
        for (var index = 0; index < 11; index++)
            await cache.SaveAsync("dedup", Scan($"local|C:\\folder{index}",
                "phash", ($"C:\\folder{index}\\a.jpg", 1UL)));
        await cache.SaveAsync("similar", Scan("local|C:\\other", "phash", ("C:\\other\\a.jpg", 3UL)));

        Assert.Equal(8, Directory.GetFiles(dir.Path, "dedup-*.json.gz").Length);
        // The newest survives; the oldest is gone.
        Assert.NotNull(await cache.LoadAsync("dedup", "local|C:\\folder10"));
        Assert.Null(await cache.LoadAsync("dedup", "local|C:\\folder0"));
        // Pruning one operation never touches another's file.
        Assert.NotNull(await cache.LoadAsync("similar", "local|C:\\other"));
    }

    // ── The view model ─────────────────────────────────────────────────────

    /// <summary>Two copies of the sample image, the folder that holds them, and
    /// nothing else — the pair groups from their pinned fingerprints.</summary>
    private static (string Folder, string A, string B) TwoCopies()
    {
        var sample = FindSampleImage() ?? throw new InvalidOperationException("TestData/sample.jpg not found");
        var folder = Directory.CreateTempSubdirectory("synapic-restore-").FullName;
        var a = Path.Combine(folder, "a.jpg");
        var b = Path.Combine(folder, "b.jpg");
        File.Copy(sample, a);
        File.Copy(sample, b);
        return (folder, a, b);
    }

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

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task A_restart_reopens_the_last_scan_without_hashing_anything()
    {
        var (folder, a, b) = TwoCopies();
        using var cacheDir = new TempDir();
        try
        {
            var fake = new FakeDedup();
            fake.Hashes[a] = 0UL;
            fake.Hashes[b] = 0b111UL;

            var first = new StepDedupViewModel(
                dedup: fake, fingerprintCache: new DedupFingerprintCache(cacheDir.Path))
            {
                FolderPath = folder,
                Threshold = 0.95,
            };
            await first.ScanCommand.ExecuteAsync(null);
            Assert.Single(first.Groups);
            Assert.Equal(1, fake.Scans);

            // A new instance is the next launch: same folder, same store.
            var second = new StepDedupViewModel(
                dedup: fake, fingerprintCache: new DedupFingerprintCache(cacheDir.Path))
            {
                FolderPath = folder,
            };
            await second.RestoreCompletion;

            Assert.Equal(1, fake.Scans);          // no second scan…
            Assert.Equal(1, fake.Regroups);       // …the saved hashes were re-grouped
            var group = Assert.Single(second.Groups);
            Assert.Equal(2, group.Items.Count);
            Assert.True(second.CanRetune);
            Assert.Contains("from the last scan", second.ScanSummary);
            Assert.All(group.Items, item => Assert.NotNull(item.Thumb)); // reviewable rows
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task Restored_results_retune_from_the_saved_fingerprints()
    {
        var (folder, a, b) = TwoCopies();
        using var cacheDir = new TempDir();
        try
        {
            var fake = new FakeDedup();
            fake.Hashes[a] = 0UL;
            fake.Hashes[b] = 0b111UL;

            var first = new StepDedupViewModel(
                dedup: fake, fingerprintCache: new DedupFingerprintCache(cacheDir.Path))
            {
                FolderPath = folder,
                Threshold = 0.99, // one bit of budget: the pair is three bits apart
            };
            await first.ScanCommand.ExecuteAsync(null);
            Assert.Empty(first.Groups);
            Assert.Equal(1, fake.Scans);

            var second = new StepDedupViewModel(
                dedup: fake, fingerprintCache: new DedupFingerprintCache(cacheDir.Path))
            {
                FolderPath = folder,
                Threshold = 0.99,
            };
            await second.RestoreCompletion;
            Assert.Empty(second.Groups);
            Assert.True(second.CanRetune);         // the slider is live before any scan

            second.Threshold = 0.95;
            await second.RetuneCompletion;

            Assert.Equal(1, fake.Scans);           // still no scan
            Assert.Single(second.Groups);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task Applying_an_action_drops_the_saved_scan()
    {
        var (folder, a, b) = TwoCopies();
        using var cacheDir = new TempDir();
        try
        {
            var fake = new FakeDedup();
            fake.Hashes[a] = 0UL;
            fake.Hashes[b] = 0b111UL;

            var store = new DedupFingerprintCache(cacheDir.Path);
            var first = new StepDedupViewModel(dedup: fake, fingerprintCache: store)
            {
                FolderPath = folder,
                Threshold = 0.95,
            };
            await first.ScanCommand.ExecuteAsync(null);
            Assert.Single(first.Groups);
            Assert.Single(Directory.GetFiles(cacheDir.Path, "dedup-*.json.gz"));

            // Tag is reversible, so it needs no confirmation; one copy is kept and
            // the other is acted on.
            first.Groups[0].Items[1].IsChecked = false;
            await first.ApplyCommand.ExecuteAsync(null);

            Assert.Empty(Directory.GetFiles(cacheDir.Path, "dedup-*.json.gz"));

            // A restart must not offer to re-group files the apply changed.
            var second = new StepDedupViewModel(dedup: fake, fingerprintCache: new DedupFingerprintCache(cacheDir.Path))
            {
                FolderPath = folder,
            };
            await second.RestoreCompletion;
            Assert.Empty(second.Groups);
            Assert.False(second.CanRetune);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task A_saved_scan_is_not_restored_for_a_different_folder()
    {
        var (folder, a, b) = TwoCopies();
        var other = Directory.CreateTempSubdirectory("synapic-restore-other-").FullName;
        using var cacheDir = new TempDir();
        try
        {
            var fake = new FakeDedup();
            fake.Hashes[a] = 0UL;
            fake.Hashes[b] = 0b111UL;

            var first = new StepDedupViewModel(
                dedup: fake, fingerprintCache: new DedupFingerprintCache(cacheDir.Path))
            {
                FolderPath = folder,
                Threshold = 0.95,
            };
            await first.ScanCommand.ExecuteAsync(null);
            Assert.Single(first.Groups);

            var second = new StepDedupViewModel(
                dedup: fake, fingerprintCache: new DedupFingerprintCache(cacheDir.Path))
            {
                FolderPath = other,
            };
            await second.RestoreCompletion;

            Assert.Empty(second.Groups);
            Assert.False(second.CanRetune);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
            Directory.Delete(other, recursive: true);
        }
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task A_saved_scan_from_another_fingerprint_is_not_restored()
    {
        var (folder, a, b) = TwoCopies();
        using var cacheDir = new TempDir();
        try
        {
            // A dHash scan saved for this folder; the form is on pHash.
            var cache = new DedupFingerprintCache(cacheDir.Path);
            await cache.SaveAsync("dedup", new CachedDedupScan(
                DedupFingerprintCache.CurrentVersion,
                "local|" + Path.GetFullPath(folder),
                "dhash",
                0.97,
                2,
                new[]
                {
                    new CachedDedupItem(a, "a.jpg", 1, null, null),
                    new CachedDedupItem(b, "b.jpg", 1, null, null),
                },
                new Dictionary<string, ulong> { [a] = 0UL, [b] = 0b111UL },
                new Dictionary<string, ulong>()));

            var fake = new FakeDedup();
            var vm = new StepDedupViewModel(dedup: fake, fingerprintCache: cache) { FolderPath = folder };
            await vm.RestoreCompletion;

            // Hashes of one fingerprint are meaningless to another: nothing is
            // shown under a slider that would silently mean something else.
            Assert.Empty(vm.Groups);
            Assert.False(vm.CanRetune);
            Assert.Equal(0, fake.Scans);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task Pointing_the_form_at_another_folder_takes_the_slider_away()
    {
        var (folder, a, b) = TwoCopies();
        var other = Directory.CreateTempSubdirectory("synapic-restore-move-").FullName;
        using var cacheDir = new TempDir();
        try
        {
            var fake = new FakeDedup();
            fake.Hashes[a] = 0UL;
            fake.Hashes[b] = 0b111UL;

            var vm = new StepDedupViewModel(
                dedup: fake, fingerprintCache: new DedupFingerprintCache(cacheDir.Path))
            {
                FolderPath = folder,
                Threshold = 0.95,
            };
            await vm.ScanCommand.ExecuteAsync(null);
            Assert.True(vm.CanRetune);

            vm.FolderPath = other;

            // The groups shown still describe the old folder, so re-filtering
            // them as if they were this one would be a lie.
            Assert.False(vm.CanRetune);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
            Directory.Delete(other, recursive: true);
        }
    }

    // ── App wiring ─────────────────────────────────────────────────────────

    /// <summary>
    /// The shipped container is what makes this feature real: the registered
    /// store has to reach both searches through the shell, or nothing a scan
    /// saves would ever be read at launch. Resolving the shell the way App does
    /// (the type is registered, the cache is a singleton) exercises that wiring
    /// instead of a hand-passed constructor argument.
    /// </summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task The_app_container_hands_the_store_to_the_dedup_searches()
    {
        var (folder, a, b) = TwoCopies();
        using var cacheDir = new TempDir();
        try
        {
            var cache = new DedupFingerprintCache(cacheDir.Path);
            await cache.SaveAsync("dedup", new CachedDedupScan(
                DedupFingerprintCache.CurrentVersion,
                "local|" + Path.GetFullPath(folder),
                "phash",
                0.95,
                2,
                new[]
                {
                    new CachedDedupItem(a, "a.jpg", 1, null, null),
                    new CachedDedupItem(b, "b.jpg", 1, null, null),
                },
                new Dictionary<string, ulong> { [a] = 0UL, [b] = 0b111UL },
                new Dictionary<string, ulong>()));

            var session = new Synapic.Main.Models.Session();
            session.Datasource.Type = "local";
            session.Datasource.LocalPath = folder;

            var services = new ServiceCollection();
            services.AddSingleton<Synapic.Main.Services.IInferenceSidecar,
                Synapic.Main.Services.InferenceSidecarService>();
            services.AddSingleton(session);
            services.AddSingleton(cache);
            services.AddSingleton<Synapic.Main.ViewModels.MainWindowViewModel>();
            await using var provider = services.BuildServiceProvider();

            var shell = provider.GetRequiredService<Synapic.Main.ViewModels.MainWindowViewModel>();
            await shell.Operations.Dedup.RestoreCompletion;

            Assert.Single(shell.Operations.Dedup.Groups);
            Assert.True(shell.Operations.Dedup.CanRetune);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private sealed class TempDir : IDisposable
    {
        public TempDir() =>
            Path = Directory.CreateTempSubdirectory("synapic-fingerprint-cache-").FullName;

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Pinned fingerprints and call counters; grouping is the shipped
    /// engine's, so the restore exercises the real rule over the saved hashes.</summary>
    private sealed class FakeDedup : IDedupService
    {
        private readonly DedupService _grouping = new();

        public Dictionary<string, ulong> Hashes { get; } = new();
        public int Scans { get; private set; }
        public int Regroups { get; private set; }

        public Task<DedupResult> FindDuplicatesAsync(
            IEnumerable<string> imagePaths,
            DedupOptions opts,
            IProgress<DedupProgress>? progress = null,
            CancellationToken ct = default)
        {
            Scans++;
            return Task.FromResult(_grouping.GroupFromHashes(
                Hashes, new Dictionary<string, ulong>(), opts));
        }

        public ulong? ComputeHash(string path, DedupOptions opts) => null;

        public DedupResult GroupFromHashes(
            IReadOnlyDictionary<string, ulong> perceptualHashes,
            IReadOnlyDictionary<string, ulong> exactHashes,
            DedupOptions opts)
        {
            Regroups++;
            return _grouping.GroupFromHashes(perceptualHashes, exactHashes, opts);
        }

        public Task<bool> ApplyToPathsAsync(
            IEnumerable<string> paths, DedupAction action,
            string? moveDestination = null, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> ApplyActionsAsync(
            DedupResult result, DedupAction action,
            string? moveDestination = null, CancellationToken ct = default) => Task.FromResult(true);
    }
}

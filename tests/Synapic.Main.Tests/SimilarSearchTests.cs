using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Win32;
using Synapic.Main.Models;
using Synapic.Main.Services;
using Synapic.Main.Services.Processing;
using Synapic.Main.ViewModels;
using Synapic.Main.ViewModels.Operations;
using Synapic.Main.ViewModels.Steps;
using Synapic.Main.Views;
using Synapic.Main.Views.Settings;
using Synapic.Main.Views.Wizard;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// The Similar images operation: the deduplication scan's looser sibling. It is
/// one implementation (<see cref="StepDedupViewModel"/> under
/// <see cref="ImageMatchProfile.Similar"/>) presented as its own operation — its
/// own sidebar row, its own Settings tab, its own help topic and its own stored
/// rules — so what these pin is the part that is genuinely different: the
/// threshold band it groups at, the copy that names it, and the fact that the two
/// searches can never overwrite each other's saved settings.
/// </summary>
public class SimilarSearchTests : IDisposable
{
    /// <summary>An isolated HKCU key path, so the registry tests never touch the
    /// developer's real Software\Synapic\AppData values (same approach as
    /// EngineSettingsStoreTests).</summary>
    private readonly string _keyPath = $@"Software\Synapic\Tests_Similar_{Guid.NewGuid():N}";

    private readonly AppSettingsRegistryStore _store;

    public SimilarSearchTests() => _store = new AppSettingsRegistryStore(_keyPath);

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false); }
        catch { /* the test binary may not have the rights; the key is unique to this run */ }
    }

    // ── The profile: one mechanism, two operations ──────────────────────────

    [Fact]
    public void The_profiles_are_two_operations_over_one_scan()
    {
        var duplicates = new StepDedupViewModel();
        var similar = new StepDedupViewModel(profile: ImageMatchProfile.Similar);

        Assert.Equal(ImageMatchProfile.Duplicates, duplicates.Profile);
        Assert.False(duplicates.IsSimilar);
        Assert.Equal(ImageMatchProfile.Similar, similar.Profile);
        Assert.True(similar.IsSimilar);

        // Distinct identity: the shell routes and persists by these, so a shared
        // key would make the two rows one operation with a second light.
        Assert.Equal(OperationShellViewModel.DedupKey, duplicates.OperationKey);
        Assert.Equal(OperationShellViewModel.SimilarKey, similar.OperationKey);
        Assert.Equal("Deduplication", duplicates.OperationTitle);
        Assert.Equal("Similar images", similar.OperationTitle);

        // And distinct copy, so the similarity view never reads as the duplicate
        // view with the same form.
        Assert.Equal("Scan for duplicates", duplicates.ScanCardTitle);
        Assert.Equal("Search for similar images", similar.ScanCardTitle);
        Assert.Equal("duplicate", duplicates.MatchNoun);
        Assert.Equal("similar", similar.MatchNoun);
        Assert.Equal("duplicates", duplicates.MatchNounPlural);
        Assert.Equal("similar images", similar.MatchNounPlural);
        Assert.NotEqual(duplicates.RunPageHint, similar.RunPageHint);
        Assert.NotEqual(duplicates.ApplyHint, similar.ApplyHint);
        Assert.NotEqual(duplicates.ThresholdHint, similar.ThresholdHint);
    }

    /// <summary>
    /// The two profiles differ in what they compare, not only in how loosely:
    /// the duplicate scan may group the catalog's own content hash (identical
    /// files share it, no download needed), while the similarity search may not
    /// — a content hash cannot say two images look alike. Reusing it made a
    /// catalog similarity scan an exact-match scan, so the algorithm and
    /// threshold never applied to a single image.
    /// </summary>
    [Fact]
    public void Only_the_duplicate_scan_can_use_the_catalog_content_hash()
    {
        var duplicates = new StepDedupViewModel { DatasourceType = "daminion" };
        var similar = new StepDedupViewModel(profile: ImageMatchProfile.Similar) { DatasourceType = "daminion" };

        Assert.True(duplicates.BuildOptions().UseServerHashes);
        Assert.False(similar.BuildOptions().UseServerHashes);

        // And the run page says which rule it used: the duplicate summary names
        // the server-hash rule, the similarity summary does not (it hashes pixels).
        Assert.Contains("server hash", duplicates.ScanSettingsSummary);
        Assert.DoesNotContain("server hash", similar.ScanSettingsSummary);

        // The settings form follows: the rule is offered only where it applies.
        Assert.True(duplicates.ShowsServerHashRule);
        Assert.False(similar.ShowsServerHashRule);
    }

    /// <summary>
    /// A saved Similar scan built only from the catalog's content hashes is the
    /// old path's output. Restoring it would show exact-match groups the search
    /// no longer produces — and make a fixed build look unchanged on the next
    /// launch — so it is dropped instead of re-grouped.
    /// </summary>
    [Fact]
    public void A_similar_result_of_content_hashes_alone_is_not_restored()
    {
        CachedDedupScan Cached(Dictionary<string, ulong> perceptual, Dictionary<string, ulong> exact) =>
            new(Version: 1, SourceKey: "daminion|url|saved_search|8|0|city street", Algorithm: "phash",
                Threshold: 0.85, TotalFiles: 500, Items: Array.Empty<CachedDedupItem>(),
                PerceptualHashes: perceptual, ExactHashes: exact);

        var serverHashesOnly = new Dictionary<string, ulong> { ["daminion:1"] = 4242UL };

        Assert.True(StepDedupViewModel.IsStaleSimilarCache(
            Cached(new Dictionary<string, ulong>(), serverHashesOnly), isSimilar: true));

        // Fingerprint present: a real similarity scan, whatever else it holds.
        Assert.False(StepDedupViewModel.IsStaleSimilarCache(
            Cached(new Dictionary<string, ulong> { ["daminion:1"] = 42UL }, serverHashesOnly), isSimilar: true));

        // A local scan has no server hashes to be stale about.
        Assert.False(StepDedupViewModel.IsStaleSimilarCache(
            Cached(new Dictionary<string, ulong>(), new Dictionary<string, ulong>()), isSimilar: true));

        // And the rule belongs to the similarity profile: the duplicate scan
        // legitimately saves a result of server hashes alone.
        Assert.False(StepDedupViewModel.IsStaleSimilarCache(
            Cached(new Dictionary<string, ulong>(), serverHashesOnly), isSimilar: false));
    }

    // ── The similarity band ────────────────────────────────────────────────

    [Fact]
    public void The_similarity_search_starts_looser_than_the_duplicate_scan()
    {
        var duplicates = new StepDedupViewModel();
        var similar = new StepDedupViewModel(profile: ImageMatchProfile.Similar);

        Assert.Equal(0.90, duplicates.Threshold, 6);
        Assert.Equal(AppSettingsRegistryStore.SimilarThresholdDefault, similar.Threshold, 6);
        Assert.Equal(0.85, similar.Threshold, 6);

        // The threshold is a hamming budget: 0.90 of 64 bits allows six to differ,
        // 0.85 allows ten. That difference is the whole feature.
        Assert.Equal(6, DedupService.MaxHammingDistance(duplicates.BuildOptions().Threshold));
        Assert.Equal(10, DedupService.MaxHammingDistance(similar.BuildOptions().Threshold));
    }

    [Fact]
    public void Images_inside_the_similarity_band_but_outside_the_duplicate_one_group_only_there()
    {
        // 10 bits apart → similarity 0.844: inside the similarity search's band
        // (≤10 bits), outside the duplicate scan's (≤6).
        var hashes = new Dictionary<string, ulong>
        {
            ["a.png"] = 0UL,
            ["b.png"] = 0b_11_1111_1111UL,
        };

        Assert.Empty(DedupTestHarness.Group(hashes, 0.90));

        var similar = DedupTestHarness.Group(hashes, AppSettingsRegistryStore.SimilarThresholdDefault);
        var group = Assert.Single(similar);
        Assert.Equal(2, group.Items.Length);
        Assert.Equal(new[] { "a.png", "b.png" }, group.Items);
        // Scored against the group's representative: 1 − 10/64.
        Assert.Equal(1.0 - 10.0 / 64.0, group.SimilarityScores[1], 6);
    }

    [Fact]
    public void Images_beyond_the_similarity_band_stay_apart()
    {
        // 12 bits apart → similarity 0.8125, below even the loose default. The
        // similarity search widens the band; it does not group everything.
        var hashes = new Dictionary<string, ulong>
        {
            ["a.png"] = 0UL,
            ["b.png"] = 0b_1111_1111_1111UL,
        };

        Assert.Empty(DedupTestHarness.Group(hashes, AppSettingsRegistryStore.SimilarThresholdDefault));
    }

    [Fact]
    public void Similar_pairs_chain_into_one_group_where_the_duplicate_scan_finds_nothing()
    {
        // a≈b (9 bits) and b≈c (9 bits), a and c 18 apart. Union-find joins all
        // three at 0.85 and none at 0.90 — the chained group the help topic warns
        // about, and the reason the group list is read before anything is applied.
        var hashes = new Dictionary<string, ulong>
        {
            ["a.png"] = 0UL,
            ["b.png"] = 0b1_1111_1111UL,                     // 9 bits set
            ["c.png"] = 0b11_1111_1111_1111_1111UL,          // 18 bits: 9 from b
        };

        Assert.Empty(DedupTestHarness.Group(hashes, 0.90));

        var similar = DedupTestHarness.Group(hashes, AppSettingsRegistryStore.SimilarThresholdDefault);
        var group = Assert.Single(similar);
        Assert.Equal(3, group.Items.Length);
        // The chain end is 18 bits from the representative, so its own score is
        // well under the threshold even though it is in the group.
        Assert.Equal(1.0 - 18.0 / 64.0, group.SimilarityScores[2], 6);
    }

    // ── The default threshold belongs to the fingerprint ───────────────────

    [Fact]
    public void Each_fingerprint_carries_its_own_default_threshold()
    {
        // pHash is the only algorithm the shipped thresholds were ever measured
        // for. The other two put *different* pictures far closer together, so at
        // pHash's numbers they merge genuinely different photos into duplicate
        // groups — and duplicates are moved or deleted.
        Assert.Equal(AppSettingsRegistryStore.DedupThresholdDefault,
            StepDedupViewModel.DefaultThresholdFor(ImageMatchProfile.Duplicates, "PHash"), 6);
        Assert.Equal(0.97, StepDedupViewModel.DefaultThresholdFor(ImageMatchProfile.Duplicates, "DHash"), 6);
        Assert.Equal(0.99, StepDedupViewModel.DefaultThresholdFor(ImageMatchProfile.Duplicates, "AHash"), 6);

        Assert.Equal(AppSettingsRegistryStore.SimilarThresholdDefault,
            StepDedupViewModel.DefaultThresholdFor(ImageMatchProfile.Similar, "PHash"), 6);
        Assert.Equal(0.91, StepDedupViewModel.DefaultThresholdFor(ImageMatchProfile.Similar, "DHash"), 6);
        Assert.Equal(0.94, StepDedupViewModel.DefaultThresholdFor(ImageMatchProfile.Similar, "AHash"), 6);

        // An algorithm this build does not know resolves to pHash's default
        // rather than to one of the stricter ones.
        Assert.Equal(AppSettingsRegistryStore.DedupThresholdDefault,
            StepDedupViewModel.DefaultThresholdFor(ImageMatchProfile.Duplicates, "NoSuchAlgorithm"), 6);

        // The calibration unit is the hamming budget, so that is what these
        // numbers have to mean: 2 bits for dHash, 1 for aHash, where pHash gets
        // 6. Coarser fingerprints get tighter budgets, never looser ones.
        Assert.Equal(2, DedupService.MaxHammingDistance(0.97));
        Assert.Equal(1, DedupService.MaxHammingDistance(0.99));
        Assert.Equal(6, DedupService.MaxHammingDistance(0.91));
        Assert.Equal(4, DedupService.MaxHammingDistance(0.94));
    }

    [Fact]
    public void An_untuned_threshold_follows_the_algorithm_and_a_tuned_one_does_not()
    {
        var vm = new StepDedupViewModel();
        Assert.Equal(0.90, vm.Threshold, 6);

        // Still on a shipped default: it follows the fingerprint. Leaving dHash
        // at pHash's 0.90 is the configuration this exists to prevent.
        vm.SelectedAlgorithm = 1; // DHash
        Assert.Equal(0.97, vm.Threshold, 6);
        vm.SelectedAlgorithm = 2; // AHash
        Assert.Equal(0.99, vm.Threshold, 6);
        Assert.Equal(1, DedupService.MaxHammingDistance(vm.BuildOptions().Threshold));

        // Once the number has been moved it is the user's, and switching
        // algorithms must not quietly overwrite the choice.
        vm.Threshold = 0.93;
        vm.SelectedAlgorithm = 0; // PHash
        Assert.Equal(0.93, vm.Threshold, 6);
        vm.SelectedAlgorithm = 1; // back to DHash
        Assert.Equal(0.93, vm.Threshold, 6);
    }

    [Fact]
    public void The_similarity_search_follows_its_own_per_algorithm_defaults()
    {
        var vm = new StepDedupViewModel(profile: ImageMatchProfile.Similar);
        Assert.Equal(AppSettingsRegistryStore.SimilarThresholdDefault, vm.Threshold, 6);

        vm.SelectedAlgorithm = 1; // DHash
        Assert.Equal(0.91, vm.Threshold, 6);
        vm.SelectedAlgorithm = 2; // AHash
        Assert.Equal(0.94, vm.Threshold, 6);
        // The two profiles keep their own numbers for the same algorithm.
        Assert.NotEqual(
            StepDedupViewModel.DefaultThresholdFor(ImageMatchProfile.Duplicates, "AHash"),
            vm.Threshold);
    }

    // ── Stored rules: the two searches cannot overwrite each other ──────────

    [Fact]
    public void Each_search_restores_its_own_saved_rules()
    {
        if (!OperatingSystem.IsWindows()) return; // the store is a no-op elsewhere

        var duplicates = new StepDedupViewModel(appSettingsStore: _store);
        var similar = new StepDedupViewModel(appSettingsStore: _store, profile: ImageMatchProfile.Similar);

        duplicates.SelectedAlgorithm = 2;   // AHash
        duplicates.Threshold = 0.97;
        duplicates.SelectSmallest = true;
        duplicates.MoveDestinationFolder = @"C:\duplicate-target";

        similar.SelectedAlgorithm = 1;      // DHash
        similar.Threshold = 0.72;
        similar.SelectLargest = true;
        similar.MoveDestinationFolder = @"C:\similar-target";

        // A fresh pair — what a relaunch builds — reads the same store back: each
        // form comes up with its own algorithm, threshold, keep-set and destination.
        var reloadedDuplicates = new StepDedupViewModel(appSettingsStore: _store);
        var reloadedSimilar = new StepDedupViewModel(appSettingsStore: _store, profile: ImageMatchProfile.Similar);

        Assert.Equal(2, reloadedDuplicates.SelectedAlgorithm);
        Assert.Equal(0.97, reloadedDuplicates.Threshold, 6);
        Assert.True(reloadedDuplicates.SelectSmallest);
        Assert.False(reloadedDuplicates.SelectLargest);
        Assert.Equal(@"C:\duplicate-target", reloadedDuplicates.MoveDestinationFolder);

        Assert.Equal(1, reloadedSimilar.SelectedAlgorithm);
        Assert.Equal(0.72, reloadedSimilar.Threshold, 6);
        Assert.True(reloadedSimilar.SelectLargest);
        Assert.False(reloadedSimilar.SelectSmallest);
        Assert.Equal(@"C:\similar-target", reloadedSimilar.MoveDestinationFolder);

        // Which is what the scan actually builds its options from: widening the
        // similarity search left the duplicate scan strict.
        Assert.Equal(2, DedupService.MaxHammingDistance(reloadedDuplicates.BuildOptions().Threshold));
        Assert.Equal(18, DedupService.MaxHammingDistance(reloadedSimilar.BuildOptions().Threshold));
    }

    [Fact]
    public void The_two_search_settings_survive_a_fresh_store_read()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Only the similarity form is ever touched: the duplicate half of the
        // store must come back at its defaults rather than inheriting these.
        var similar = new StepDedupViewModel(appSettingsStore: _store, profile: ImageMatchProfile.Similar);
        similar.Threshold = 0.80;
        similar.SelectNewest = true;

        var reloadedDuplicates = new StepDedupViewModel(appSettingsStore: _store);
        var reloadedSimilar = new StepDedupViewModel(appSettingsStore: _store, profile: ImageMatchProfile.Similar);

        Assert.Equal(0.90, reloadedDuplicates.Threshold, 6);
        Assert.False(reloadedDuplicates.SelectNewest);

        Assert.Equal(0.80, reloadedSimilar.Threshold, 6);
        Assert.True(reloadedSimilar.SelectNewest);
    }

    [Fact]
    public void A_store_without_a_saved_threshold_uses_the_saved_algorithms_default()
    {
        if (!OperatingSystem.IsWindows()) return;

        // A registry that names dHash and aHash but carries no threshold — what
        // an older build, or a hand-edited key, leaves behind. The fallback has
        // to be the saved algorithm's own default; falling back to pHash's 0.90
        // would put the duplicate scan four bits looser than dHash supports.
        _store.Save(_store.Defaults with { DedupAlgorithm = "DHash", SimilarAlgorithm = "AHash" });
        using (var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true))
        {
            key!.DeleteValue("DedupThreshold");
            key.DeleteValue("SimilarThreshold");
        }

        var loaded = _store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(0.97, loaded!.DedupThreshold, 6);
        Assert.Equal(0.94, loaded.SimilarThreshold, 6);
    }

    // ── The scan the similarity view actually runs ────────────────────────

    [AvaloniaFact]
    public async Task The_similarity_scan_groups_what_the_duplicate_scan_finds_nothing_in()
    {
        var folder = Directory.CreateTempSubdirectory("synapic-similar-").FullName;
        try
        {
            // Two real files, so the local scan enumerates them; the fingerprints
            // are supplied (10 bits apart) because what is under test here is the
            // profile's rule, not libvips.
            var a = Path.Combine(folder, "a.jpg");
            var b = Path.Combine(folder, "b.jpg");
            File.WriteAllText(a, "not really a jpeg");
            File.WriteAllText(b, "not really a jpeg");

            var hashes = new Dictionary<string, ulong>
            {
                [a] = 0UL,
                [b] = 0b_11_1111_1111UL,
            };

            var similarEngine = new GroupingDedup(hashes);
            var similar = new StepDedupViewModel(
                dedup: similarEngine, profile: ImageMatchProfile.Similar) { FolderPath = folder };

            await similar.ScanCommand.ExecuteAsync(null);

            // The options the scan really built are the profile's: PHash at 0.85.
            Assert.NotNull(similarEngine.Seen);
            Assert.Equal(HashAlgorithm.PHash, similarEngine.Seen!.Algorithm);
            Assert.Equal(AppSettingsRegistryStore.SimilarThresholdDefault, similarEngine.Seen.Threshold, 6);

            var group = Assert.Single(similar.Groups);
            Assert.Equal(2, group.Items.Count);
            Assert.Contains("1 groups", similar.ScanSummary, StringComparison.Ordinal);
            Assert.Contains("similar images", similar.ScanSummary, StringComparison.Ordinal);
            Assert.Contains("1 similar group(s)", similar.SelectionSummary, StringComparison.Ordinal);

            // The same two fingerprints under the duplicate profile: no group, and
            // the summary says so in the duplicate scan's own words.
            var duplicates = new StepDedupViewModel(dedup: new GroupingDedup(hashes)) { FolderPath = folder };

            await duplicates.ScanCommand.ExecuteAsync(null);

            Assert.Empty(duplicates.Groups);
            Assert.Equal("No duplicates found among 2 images — try a lower threshold or another algorithm.",
                duplicates.ScanSummary);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// A dedup engine whose hashes are supplied, so a view-model scan can be driven
    /// end to end: the grouping itself is the real shipped one
    /// (<see cref="DedupService.GroupFromHashes"/>), fed with the options the view
    /// model built — which is exactly what a live scan does after hashing.
    /// </summary>
    private sealed class GroupingDedup : IDedupService
    {
        private readonly Dictionary<string, ulong> _hashes;

        public GroupingDedup(Dictionary<string, ulong> hashes) => _hashes = hashes;

        /// <summary>The options the view model passed to the scan.</summary>
        public DedupOptions? Seen { get; private set; }

        public Task<DedupResult> FindDuplicatesAsync(
            IEnumerable<string> imagePaths,
            DedupOptions opts,
            IProgress<DedupProgress>? progress = null,
            CancellationToken ct = default)
        {
            Seen = opts;
            return Task.FromResult(new DedupService()
                .GroupFromHashes(_hashes, new Dictionary<string, ulong>(), opts));
        }

        public ulong? ComputeHash(string path, DedupOptions opts) =>
            _hashes.TryGetValue(path, out var hash) ? hash : null;

        public DedupResult GroupFromHashes(
            IReadOnlyDictionary<string, ulong> perceptualHashes,
            IReadOnlyDictionary<string, ulong> exactHashes,
            DedupOptions opts) =>
            new DedupService().GroupFromHashes(perceptualHashes, exactHashes, opts);

        public Task<bool> ApplyToPathsAsync(
            IEnumerable<string> paths,
            DedupAction action,
            string? moveDestination = null,
            CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> ApplyActionsAsync(
            DedupResult result,
            DedupAction action,
            string? moveDestination = null,
            CancellationToken ct = default) => Task.FromResult(true);
    }

    // ── The wiring in the real window ──────────────────────────────────────

    /// <summary>
    /// The similarity search has its own Settings tab and its own route, both over
    /// the similar instance — never the dedup one — and the run page it renders
    /// says "similar" where the duplicate page says "duplicate".
    /// </summary>
    [AvaloniaFact]
    public void The_similarity_search_has_its_own_settings_tab_and_run_page()
    {
        var shell = new MainWindowViewModel(
            new FakeSidecar(), new Session(), () => null, null, null, _ => null,
            appSettingsStore: _store);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            // The Settings view's Similar tab hosts the search's own rules, bound
            // to the similar instance.
            var settings = Assert.Single(window.GetVisualDescendants().OfType<SettingsTab>());
            settings.ShowSection("SectionSimilar");
            Dispatcher.UIThread.RunJobs();

            var panel = Assert.Single(window.GetVisualDescendants().OfType<SimilarSettingsPanel>());
            Assert.Same(shell.Operations.Similar, panel.DataContext);

            // No server-hash rule on this tab: the similarity search hashes pixels,
            // so offering the catalog content-hash rule would be a dead control.
            var panelTexts = panel.GetVisualDescendants().OfType<TextBlock>()
                .Select(t => t.Text ?? string.Empty)
                .ToList();
            Assert.DoesNotContain("Server hash:", panelTexts);

            // The row opens the operation, and the run page comes up over that same
            // instance with the similarity wording.
            shell.StartSimilarRouteCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Same(shell.SimilarOperation, shell.Shell.Current);
            Assert.True(shell.IsSimilarModeActive);
            Assert.False(shell.IsDedupModeActive);

            var page = Assert.Single(window.GetVisualDescendants().OfType<StepDedup>());
            Assert.Same(shell.Operations.Similar, page.DataContext);

            var texts = page.GetVisualDescendants().OfType<TextBlock>()
                .Select(t => t.Text ?? string.Empty)
                .ToList();
            Assert.Contains("Search for similar images", texts);
            Assert.DoesNotContain("Scan for duplicates", texts);
        }
        finally
        {
            window.Close();
        }
    }
}

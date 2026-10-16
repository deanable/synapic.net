using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Synapic.Main.Models;
using Synapic.Main.Services;
using Synapic.Main.Services.Processing;
using Synapic.Main.ViewModels;
using Synapic.Main.ViewModels.Operations;
using Synapic.Main.ViewModels.Steps;
using Synapic.Main.Views.Wizard;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// Dedup step behaviour: the local/Daminion source switch, the per-item keep
/// checkboxes, and the oldest/newest/smallest/largest auto-select rules that
/// drive them (checked = kept, unchecked = what Apply acts on).
/// </summary>
public class StepDedupViewModelTests
{
    private static DedupItemViewModel Item(string key, long? size = null, DateTime? date = null) =>
        new(key, System.IO.Path.GetFileName(key)) { SizeBytes = size, DateTaken = date };

    private static DuplicateGroupViewModel Group(params DedupItemViewModel[] items) =>
        new(items, "phash", 0.90);

    // ── Empty scan guidance ────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task Empty_local_folder_shows_actionable_guidance()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"synapic-empty-guidance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var vm = new StepDedupViewModel { FolderPath = folder };

            await vm.ScanCommand.ExecuteAsync(null);

            Assert.Equal("No images found in this source — choose a folder or scope with images, then scan again.",
                vm.ScanSummary);
            Assert.Empty(vm.Groups);

            var view = new StepDedup { DataContext = vm };
            var window = new Window { Content = view, Width = 1000, Height = 700 };
            window.Show();
            try
            {
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Text == vm.ScanSummary && text.IsVisible);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Images_without_duplicates_suggest_relaxing_the_match_rules()
    {
        var folder = Directory.CreateTempSubdirectory("synapic-no-dups-").FullName;
        try
        {
            var image = FindSampleImage() ?? throw new InvalidOperationException("TestData/sample.jpg not found");
            File.Copy(image, Path.Combine(folder, "one.jpg"));
            var vm = new StepDedupViewModel { FolderPath = folder };
            var window = new Window { Content = new StepDedup { DataContext = vm }, Width = 1000, Height = 700 };
            window.Show();
            try
            {
                await vm.ScanCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();

                const string guidance = "No duplicates found among 1 images — try a lower threshold or another algorithm.";
                Assert.Equal(guidance, vm.ScanSummary);
                Assert.Empty(vm.Groups);
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Text == guidance && text.IsVisible);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Unreadable_images_show_no_duplicate_groups_in_the_visible_scan_summary()
    {
        var folder = Directory.CreateTempSubdirectory("synapic-unreadable-dedup-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "broken.jpg"), "not an image");
            // The real dedup service treats an unreadable image as an unhashable
            // item, keeps the scanned-file count, and returns no duplicate groups.
            var vm = new StepDedupViewModel { FolderPath = folder };
            var window = new Window { Content = new StepDedup { DataContext = vm }, Width = 1000, Height = 700 };
            window.Show();
            try
            {
                await vm.ScanCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();

                const string guidance = "No duplicates found among 1 images — try a lower threshold or another algorithm.";
                Assert.Equal(guidance, vm.ScanSummary);
                Assert.Empty(vm.Groups);
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Text == guidance && text.IsVisible);
                Assert.Contains(Synapic.Main.Services.SynapicLog.UiSink.Snapshot(), entry =>
                    entry.RenderedMessage.Contains("Could not hash '" + Path.Combine(folder, "broken.jpg") + "'", StringComparison.Ordinal));
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Scan_failure_reports_the_error_and_resets_scan_busy_state()
    {
        var folder = Directory.CreateTempSubdirectory("synapic-dedup-scan-fail-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "one.jpg"), "dummy image");
            var vm = new StepDedupViewModel(new ThrowingDedupService { Error = new IOException("scan exploded") })
            {
                FolderPath = folder,
            };
            var view = new StepDedup { DataContext = vm };
            var window = new Window { Content = view, Width = 1000, Height = 700 };
            window.Show();
            try
            {
                await vm.ScanCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();

                Assert.Equal("Scan failed: scan exploded", vm.ScanSummary);
                Assert.False(vm.IsScanning);
                Assert.True(vm.ScanCommand.CanExecute(null));
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Text == vm.ScanSummary && text.IsVisible);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Scan_cancellation_reports_cancelled_and_resets_scan_busy_state()
    {
        var folder = Directory.CreateTempSubdirectory("synapic-dedup-scan-cancel-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "one.jpg"), "dummy image");
            var enteredScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var dedup = new ThrowingDedupService
            {
                OnFind = async ct =>
                {
                    enteredScan.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                    return new DedupResult();
                },
            };
            var vm = new StepDedupViewModel(dedup) { FolderPath = folder };
            var view = new StepDedup { DataContext = vm };
            var window = new Window { Content = view, Width = 1000, Height = 700 };
            window.Show();
            try
            {
                var scan = vm.ScanCommand.ExecuteAsync(null);
                await enteredScan.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(vm.IsScanning);
                Assert.True(vm.StopScanCommand.CanExecute(null));

                vm.StopScanCommand.Execute(null);
                await scan;
                Dispatcher.UIThread.RunJobs();

                Assert.Equal("Scan cancelled", vm.ScanSummary);
                Assert.False(vm.IsScanning);
                Assert.True(vm.ScanCommand.CanExecute(null));
                Assert.False(vm.StopScanCommand.CanExecute(null));
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Text == "Scan cancelled" && text.IsVisible);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class ThrowingDedupService : IDedupService
    {
        public Exception? Error { get; init; }
        public Func<CancellationToken, Task<DedupResult>>? OnFind { get; init; }

        public async Task<DedupResult> FindDuplicatesAsync(IEnumerable<string> imagePaths, DedupOptions opts,
            IProgress<DedupProgress>? progress = null, CancellationToken ct = default)
        {
            if (OnFind is not null) return await OnFind(ct);
            throw Error ?? new InvalidOperationException("No scan behavior configured");
        }

        public ulong? ComputeHash(string path, DedupOptions opts) => null;
        public DedupResult GroupFromHashes(IReadOnlyDictionary<string, ulong> perceptualHashes,
            IReadOnlyDictionary<string, ulong> exactHashes, DedupOptions opts) => new();
        public Task<bool> ApplyToPathsAsync(IEnumerable<string> paths, DedupAction action,
            string? moveDestination = null, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ApplyActionsAsync(DedupResult result, DedupAction action,
            string? moveDestination = null, CancellationToken ct = default) => Task.FromResult(true);
    }

    // ── Scan settings ───────────────────────────────────────────────────────

    [Fact]
    public void BuildOptions_CarriesTheServerHashRule()
    {
        var vm = new StepDedupViewModel();

        // Exact is the default: the server hash is only trusted for identical
        // values until a live scan shows it behaves perceptually.
        Assert.Equal(ServerHashMatchMode.Exact, vm.BuildOptions().ServerHashMatch);
        Assert.Equal("Exact", vm.ServerHashMatches[0]);

        vm.SelectedServerHashMatch = 1; // "Hamming"
        Assert.Equal(ServerHashMatchMode.Hamming, vm.BuildOptions().ServerHashMatch);
    }

    [Fact]
    public void Each_selectable_algorithm_maps_to_its_dedup_engine_option()
    {
        var vm = new StepDedupViewModel();
        var expected = new[]
        {
            HashAlgorithm.PHash,
            HashAlgorithm.DHash,
            HashAlgorithm.AHash,
        };

        // Only real fingerprints are offered: the picker used to list a
        // "ColorMoment" choice that silently dispatched to AHash.
        Assert.Equal(new[] { "PHash", "DHash", "AHash" }, vm.Algorithms);
        for (var index = 0; index < expected.Length; index++)
        {
            vm.SelectedAlgorithm = index;
            Assert.Equal(expected[index], vm.BuildOptions().Algorithm);
        }
    }

    [Fact]
    public void BuildOptions_ClampsTheAlgorithmIndexToTheKnownSet()
    {
        var vm = new StepDedupViewModel { SelectedAlgorithm = 99 };

        Assert.Equal(HashAlgorithm.AHash, vm.BuildOptions().Algorithm);
    }

    // ── Auto-select rules ───────────────────────────────────────────────────

    [Fact]
    public void AutoSelect_NoActiveRule_KeepsFirstItemOfEachGroup()
    {
        var vm = new StepDedupViewModel();
        vm.Groups.Add(Group(Item("a.jpg", 10), Item("b.jpg", 20), Item("c.jpg", 30)));

        // Toggle a rule on and back off: with none active the keep-first
        // default (first item checked) is what the recompute restores.
        vm.SelectOldest = true;
        vm.SelectOldest = false;

        Assert.True(vm.Groups[0].Items[0].IsChecked);
        Assert.False(vm.Groups[0].Items[1].IsChecked);
        Assert.False(vm.Groups[0].Items[2].IsChecked);
    }

    [Fact]
    public void AutoSelect_Oldest_ChecksOldestPerGroupByDate()
    {
        var vm = new StepDedupViewModel();
        vm.Groups.Add(Group(
            Item("mid.jpg", date: new DateTime(2021, 1, 1)),
            Item("old.jpg", date: new DateTime(2020, 1, 1)),
            Item("new.jpg", date: new DateTime(2022, 1, 1))));

        vm.SelectOldest = true;

        Assert.True(vm.Groups[0].Items[1].IsChecked);  // 2020
        Assert.False(vm.Groups[0].Items[0].IsChecked);
        Assert.False(vm.Groups[0].Items[2].IsChecked);
    }

    [Fact]
    public void AutoSelect_Newest_ChecksNewestPerGroupByDate()
    {
        var vm = new StepDedupViewModel();
        vm.Groups.Add(Group(
            Item("mid.jpg", date: new DateTime(2021, 1, 1)),
            Item("old.jpg", date: new DateTime(2020, 1, 1)),
            Item("new.jpg", date: new DateTime(2022, 1, 1))));

        vm.SelectNewest = true;

        Assert.False(vm.Groups[0].Items[0].IsChecked);
        Assert.False(vm.Groups[0].Items[1].IsChecked);
        Assert.True(vm.Groups[0].Items[2].IsChecked);  // 2022
    }

    [Fact]
    public void AutoSelect_SmallestAndLargest_UnionTheirPicks()
    {
        var vm = new StepDedupViewModel();
        vm.Groups.Add(Group(
            Item("small.jpg", size: 100),
            Item("mid.jpg", size: 200),
            Item("large.jpg", size: 300)));

        vm.SelectSmallest = true;
        vm.SelectLargest = true;

        Assert.True(vm.Groups[0].Items[0].IsChecked);   // smallest
        Assert.False(vm.Groups[0].Items[1].IsChecked);  // neither
        Assert.True(vm.Groups[0].Items[2].IsChecked);   // largest
    }

    [Fact]
    public void AutoSelect_EachGroupGetsItsOwnPick()
    {
        var vm = new StepDedupViewModel();
        vm.Groups.Add(Group(
            Item("g1-old.jpg", date: new DateTime(2019, 1, 1)),
            Item("g1-new.jpg", date: new DateTime(2023, 1, 1))));
        vm.Groups.Add(Group(
            Item("g2-old.jpg", date: new DateTime(2015, 1, 1)),
            Item("g2-new.jpg", date: new DateTime(2024, 1, 1))));

        vm.SelectOldest = true;

        Assert.True(vm.Groups[0].Items[0].IsChecked);   // 2019 beats 2023
        Assert.False(vm.Groups[0].Items[1].IsChecked);
        Assert.True(vm.Groups[1].Items[0].IsChecked);   // 2015 beats 2024
        Assert.False(vm.Groups[1].Items[1].IsChecked);
    }

    [Fact]
    public void AutoSelect_RuleWithoutData_FallsBackToKeepFirst()
    {
        var vm = new StepDedupViewModel();
        // No dates and no sizes anywhere: no rule can pick anything.
        vm.Groups.Add(Group(Item("a.jpg"), Item("b.jpg")));

        vm.SelectOldest = true;

        Assert.True(vm.Groups[0].Items[0].IsChecked);
        Assert.False(vm.Groups[0].Items[1].IsChecked);
    }

    [Fact]
    public void AutoSelect_SkipsItemsUnknownToTheRule()
    {
        var vm = new StepDedupViewModel();
        // The middle item has no date: the oldest rule must never pick it,
        // even though an unknown value would sort first.
        vm.Groups.Add(Group(
            Item("dated-old.jpg", date: new DateTime(2020, 1, 1)),
            Item("undated.jpg"),
            Item("dated-new.jpg", date: new DateTime(2022, 1, 1))));

        vm.SelectOldest = true;

        Assert.True(vm.Groups[0].Items[0].IsChecked);
        Assert.False(vm.Groups[0].Items[1].IsChecked);
        Assert.False(vm.Groups[0].Items[2].IsChecked);
    }

    [Fact]
    public void AutoSelect_TogglingARule_OverwritesManualChecks()
    {
        var vm = new StepDedupViewModel();
        vm.Groups.Add(Group(
            Item("a.jpg", size: 100),
            Item("b.jpg", size: 300)));

        // Hand tweak: keep both.
        vm.Groups[0].Items[1].IsChecked = true;

        // Toggling a rule recomputes the whole selection ("the automatic part").
        vm.SelectLargest = true;

        Assert.False(vm.Groups[0].Items[0].IsChecked);
        Assert.True(vm.Groups[0].Items[1].IsChecked);
    }

    [Fact]
    public void SelectionSummary_CountsUncheckedItemsAcrossGroups()
    {
        var vm = new StepDedupViewModel();
        vm.Groups.Add(Group(Item("a.jpg", size: 100), Item("b.jpg", size: 300)));
        vm.Groups.Add(Group(Item("c.jpg", size: 100), Item("d.jpg", size: 300)));

        vm.SelectLargest = true;

        // 2 groups × (2 items − 1 kept) = 2 unchecked action targets.
        Assert.Equal(2, vm.SelectedForActionCount);
        Assert.Contains("2 duplicate group(s)", vm.SelectionSummary);
        Assert.Contains("2 selected for the action", vm.SelectionSummary);
    }

    // ── Source switch (same radio pattern as Step 1) ────────────────────────

    [Fact]
    public void SourceRadios_RoundTrip_AndSwitchTheActionList()
    {
        var vm = new StepDedupViewModel();

        Assert.True(vm.IsLocalSelected);
        Assert.False(vm.IsDaminionSelected);
        Assert.Equal(new[] { "Tag", "Move to folder…", "Delete from disk" }, vm.Actions);

        vm.IsDaminionSelected = true;

        Assert.True(vm.IsDaminion);
        Assert.False(vm.IsLocal);
        Assert.False(vm.IsLocalSelected);
        Assert.Equal(new[] { "Delete from catalog" }, vm.Actions);
        Assert.Equal(0, vm.SelectedAction); // clamped back to the new list

        vm.IsLocalSelected = true;
        Assert.Equal(new[] { "Tag", "Move to folder…", "Delete from disk" }, vm.Actions);
    }

    [Fact]
    public void ConfirmAction_DefaultsToNull_SoCatalogDeleteFailsClosed()
    {
        var vm = new StepDedupViewModel();
        Assert.Null(vm.ConfirmAction);

        // A Daminion source without a connection can never apply either:
        vm.IsDaminionSelected = false;
        vm.Groups.Add(Group(Item("a.jpg"), Item("b.jpg"))); // b unchecked → target
        Assert.True(vm.ApplyCommand.CanExecute(null));

        vm.IsDaminionSelected = true;
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void ScanCommand_RequiresAFolderPath_ForTheLocalSource()
    {
        var vm = new StepDedupViewModel();

        Assert.False(vm.ScanCommand.CanExecute(null));

        vm.FolderPath = System.IO.Path.GetTempPath();
        Assert.True(vm.ScanCommand.CanExecute(null));
    }

    // ── Destructive-action gating ──────────────────────────────────────

    /// <summary>One temp file that stays, one that the unchecked row targets.</summary>
    private static (string dir, string dup) MakeDuplicatePair()
    {
        var dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "synapic-dedup-" + Guid.NewGuid().ToString("n"));
        System.IO.Directory.CreateDirectory(dir);
        var dup = System.IO.Path.Combine(dir, "dup.jpg");
        System.IO.File.WriteAllText(dup, "y");
        return (dir, dup);
    }

    private static StepDedupViewModel VmTargeting(string dupPath, int action)
    {
        var vm = new StepDedupViewModel();
        vm.Groups.Add(Group(
            new DedupItemViewModel(System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(dupPath)!, "keep.jpg"), "keep.jpg")
            {
                IsChecked = true,
            },
            new DedupItemViewModel(dupPath, "dup.jpg"))); // unchecked → action target
        vm.SelectedAction = action;
        return vm;
    }

    [Fact]
    public async Task LocalDelete_WithoutConfirmHook_FailsClosed()
    {
        var (dir, dup) = MakeDuplicatePair();
        try
        {
            var vm = VmTargeting(dup, action: 2); // Delete, ConfirmAction null
            Assert.Null(vm.ConfirmAction);
            Assert.True(vm.ApplyCommand.CanExecute(null));

            await vm.ApplyCommand.ExecuteAsync(null);

            Assert.True(System.IO.File.Exists(dup)); // nothing deleted
            Assert.Equal("Delete cancelled", vm.ScanSummary);
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task LocalDelete_Declined_KeepsFiles_Accepted_DeletesThem()
    {
        var (dir, dup) = MakeDuplicatePair();
        try
        {
            var vm = VmTargeting(dup, action: 2);
            var prompts = 0;
            vm.ConfirmAction = message =>
            {
                prompts++;
                Assert.Contains("Permanently delete", message);
                Assert.Contains("cannot be undone", message);
                return Task.FromResult(false);
            };

            await vm.ApplyCommand.ExecuteAsync(null);
            Assert.Equal(1, prompts);
            Assert.True(System.IO.File.Exists(dup)); // declined → untouched
            Assert.Equal("Delete cancelled", vm.ScanSummary);
            Assert.Single(vm.Groups); // declined keeps the review list intact

            vm.ConfirmAction = _ => Task.FromResult(true);
            await vm.ApplyCommand.ExecuteAsync(null);

            Assert.False(System.IO.File.Exists(dup)); // accepted → gone
            Assert.Empty(vm.Groups); // acted-on group left the list
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task LocalTagAndMove_NeverPrompt()
    {
        var vm = new StepDedupViewModel();
        var prompted = false;
        vm.ConfirmAction = _ =>
        {
            prompted = true;
            return Task.FromResult(true);
        };

        // Tag only logs — a nonexistent path is safe and side-effect free.
        vm.Groups.Add(Group(Item("/nonexistent/a.jpg"), Item("/nonexistent/b.jpg")));
        vm.SelectedAction = 0; // Tag
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.False(prompted);

        // Move with a real file: reversible, so it must not prompt either.
        var (dir, dup) = MakeDuplicatePair();
        try
        {
            vm.Groups.Add(Group(
                new DedupItemViewModel(System.IO.Path.Combine(dir, "keep.jpg"), "keep.jpg")
                {
                    IsChecked = true,
                },
                new DedupItemViewModel(dup, "dup.jpg")));
            vm.SelectedAction = 1; // Move
            var dest = System.IO.Path.Combine(dir, "go-here");
            vm.MoveDestinationFolder = dest;
            await vm.ApplyCommand.ExecuteAsync(null);

            Assert.False(prompted);
            Assert.False(System.IO.File.Exists(dup)); // moved to the destination
            Assert.True(System.IO.File.Exists(System.IO.Path.Combine(dest, "dup.jpg")));
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    // ── Preview thumbnails ─────────────────────────────────────────────────

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

    /// <summary>Two byte-identical copies of the sample image — a duplicate
    /// group at any threshold — plus the temp folder that holds them.</summary>
    private static (string Dir, string[] Files) MakeDuplicateImages()
    {
        var sample = FindSampleImage() ?? throw new InvalidOperationException("TestData/sample.jpg not found");
        var dir = Path.Combine(Path.GetTempPath(), $"synapic-thumbs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var files = new[] { "a.jpg", "b.jpg" };
        foreach (var f in files) File.Copy(sample, Path.Combine(dir, f));
        return (dir, files);
    }

    [AvaloniaFact]
    public async Task LocalScan_LoadsAPreviewThumbnail_ForEveryGroupedRow()
    {
        var (dir, _) = MakeDuplicateImages();
        try
        {
            var vm = new StepDedupViewModel { FolderPath = dir };

            await vm.ScanCommand.ExecuteAsync(null);

            Assert.NotEmpty(vm.Groups); // guards the harness: the pair must group
            foreach (var item in vm.Groups.SelectMany(g => g.Items))
            {
                Assert.True(item.HasThumb, $"no preview for {item.DisplayName}");
                Assert.NotNull(item.Thumb);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Previews are native bitmaps: a rescan must detach them from the
    /// old rows (nulled before dispose, so no control keeps a dead source).</summary>
    [AvaloniaFact]
    public async Task Rescan_DetachesThePreviousPreviews()
    {
        var (dir, _) = MakeDuplicateImages();
        var empty = Path.Combine(Path.GetTempPath(), $"synapic-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(empty);
        try
        {
            var vm = new StepDedupViewModel { FolderPath = dir };
            await vm.ScanCommand.ExecuteAsync(null);
            Assert.NotEmpty(vm.Groups);
            var oldRows = vm.Groups.SelectMany(g => g.Items).ToList();
            Assert.All(oldRows, r => Assert.NotNull(r.Thumb));

            vm.FolderPath = empty;
            await vm.ScanCommand.ExecuteAsync(null);

            Assert.Empty(vm.Groups); // empty folder → nothing to review…
            Assert.All(oldRows, r =>
            {
                Assert.Null(r.Thumb); // …and the old previews are gone, not leaked
                Assert.False(r.HasThumb);
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            Directory.Delete(empty, recursive: true);
        }
    }

    // ── View wiring ─────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void DedupView_WiresConfirmHook_AndStep1Scope()
    {
        var host = new OperationShellViewModel(new Session(), new InferenceSidecarService());
        var dedup = new StepDedup { DataContext = host.Dedup };
        Assert.Same(host.Dedup, dedup.DataContext);

        // The view attaches the modal confirm on DataContext change…
        Assert.NotNull(host.Dedup.ConfirmAction);
        // …and the host hands the shared source in for the Daminion scope summary.
        Assert.NotEqual("No datasource step available", host.Dedup.DaminionScopeSummary);
        Assert.Contains("Step 1", host.Dedup.DaminionScopeSummary);
    }

    /// <summary>The Server hash picker exists only for the catalog source and is
    /// bound both ways: it appears with the Daminion source, tracks the VM's
    /// selection, and writes a picked value back (a mistyped binding would leave
    /// it at -1 or drop the write).</summary>
    [AvaloniaFact]
    public void ServerHashPicker_IsDaminionOnly_AndBindsBothWays()
    {
        var vm = new StepDedupViewModel { DatasourceType = "local" };
        // The server-hash picker is one of the scan rules, so it lives on the
        // settings surface (3 · Settings) rather than on the review page.
        var view = new DedupSettingsPanel { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var combo = view.GetVisualDescendants().OfType<ComboBox>()
                .Single(c => c.ItemsSource is string[] items && items.Contains("Hamming"));

            // Local source: there are no server hashes to compare, so the picker
            // stays hidden — and its binding is already resolved to the default.
            Assert.False(combo.IsVisible);
            Assert.Equal(0, combo.SelectedIndex);

            vm.DatasourceType = "daminion";
            Dispatcher.UIThread.RunJobs();
            Assert.True(combo.IsVisible);

            // VM → view.
            vm.SelectedServerHashMatch = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, combo.SelectedIndex);

            // View → VM (Manual = the user picking in the dropdown).
            combo.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, vm.SelectedServerHashMatch);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The image column binds through real compiled XAML: a row with a
    /// preview shows it, a row without one collapses the column instead of
    /// leaving an empty box (a mistyped binding would render neither).</summary>
    [AvaloniaFact]
    public void ItemRows_ShowThePreviewImage_OnlyWhenAThumbExists()
    {
        var sample = FindSampleImage();
        if (sample is null) return; // repo checkout not reachable from the bin

        var withThumb = Item("a.jpg");
        using var bitmap = new global::Avalonia.Media.Imaging.Bitmap(
            new MemoryStream(DedupService.CreateThumbnail(sample) ?? throw new InvalidOperationException("no preview bytes")));
        withThumb.Thumb = bitmap;

        var vm = new StepDedupViewModel();
        vm.Groups.Add(Group(withThumb, Item("b.jpg")));

        var view = new StepDedup { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var images = view.GetVisualDescendants().OfType<Image>().ToList();
            Assert.Equal(2, images.Count);

            Assert.True(images[0].IsVisible);
            Assert.NotNull(images[0].Source);

            Assert.False(images[1].IsVisible);
            Assert.Null(images[1].Source);
        }
        finally
        {
            window.Close();
        }
    }
}

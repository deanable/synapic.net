using Avalonia.Headless.XUnit;
using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.ViewModels;
using Synapic.Avalonia.ViewModels.Steps;
using Synapic.Avalonia.Views.Wizard;
using Xunit;

namespace Synapic.Avalonia.Tests;

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
        Assert.Equal(new[] { "Tag", "Move", "Delete" }, vm.Actions);

        vm.IsDaminionSelected = true;

        Assert.True(vm.IsDaminion);
        Assert.False(vm.IsLocal);
        Assert.False(vm.IsLocalSelected);
        Assert.Equal(new[] { "Delete from catalog" }, vm.Actions);
        Assert.Equal(0, vm.SelectedAction); // clamped back to the new list

        vm.IsLocalSelected = true;
        Assert.Equal(new[] { "Tag", "Move", "Delete" }, vm.Actions);
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
            await vm.ApplyCommand.ExecuteAsync(null);

            Assert.False(prompted);
            Assert.False(System.IO.File.Exists(dup)); // moved into duplicates/
            Assert.True(System.IO.File.Exists(System.IO.Path.Combine(dir, "duplicates", "dup.jpg")));
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    // ── View wiring ─────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void DedupView_WiresConfirmHook_AndStep1Scope()
    {
        var wizard = new WizardViewModel(new Session(), new InferenceSidecarService());
        var dedup = new StepDedup { DataContext = wizard.Dedup };
        Assert.NotNull(dedup);

        // The view attaches the modal confirm on DataContext change…
        Assert.NotNull(wizard.Dedup.ConfirmAction);
        // …and the wizard hands Step 1 in for the Daminion scope summary.
        Assert.NotEqual("No datasource step available", wizard.Dedup.DaminionScopeSummary);
        Assert.Contains("Step 1", wizard.Dedup.DaminionScopeSummary);
    }
}

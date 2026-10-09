using System.IO;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Synapic.Main.Services.Processing;
using Synapic.Main.ViewModels.Steps;
using Synapic.Main.Views.Wizard;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// The dedup Apply busy state (UI-REVIEW.md carried-forward finding #1, ui-design
/// §10 criterion 6): a multi-minute catalog delete must show an indeterminate bar,
/// turn Apply off and keep Stop live for the whole operation — not grey the button
/// silently. The operation is faked at the service seam so the facts can hold it
/// mid-flight and observe the state on both sides of the real await.
/// </summary>
public class ApplyBusyStateTests
{
    private static DedupItemViewModel Item(string key) =>
        new(key, Path.GetFileName(key));

    private static StepDedupViewModel VmWithTargets(IDedupService dedup, int items = 2)
    {
        var vm = new StepDedupViewModel(dedup: dedup);
        vm.Groups.Add(new DuplicateGroupViewModel(
            Enumerable.Range(0, items).Select(i => Item($"dup{i}.jpg")).ToArray(), "phash", 0.90));
        return vm;
    }

    /// <summary>Effective visibility: a control whose own flag is on is still
    /// hidden while an ancestor is collapsed.</summary>
    private static bool EffectivelyVisible(Control control) =>
        control.IsVisible &&
        control.GetVisualAncestors().OfType<Control>().All(ancestor => ancestor.IsVisible);

    /// <summary>
    /// An <see cref="IDedupService"/> whose Apply blocks until the test releases
    /// it (or cancels), so "the operation is in flight" is a state the test owns
    /// rather than a race against a real filesystem call.
    /// </summary>
    private sealed class BlockingDedup : IDedupService
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when Apply has entered the service (the operation started).</summary>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ApplyCalls { get; private set; }

        public bool Cancelled { get; private set; }

        public void Release() => _gate.TrySetResult();

        public async Task<bool> ApplyToPathsAsync(IEnumerable<string> paths, DedupAction action,
            CancellationToken ct = default)
        {
            ApplyCalls++;
            Started.TrySetResult();
            try
            {
                await _gate.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }

            return true;
        }

        public Task<DedupResult> FindDuplicatesAsync(IEnumerable<string> imagePaths, DedupOptions opts,
            IProgress<DedupProgress>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new DedupResult { TotalFiles = 0 });

        public ulong? ComputeHash(string path, DedupOptions opts) => 0;

        public DedupResult GroupFromHashes(
            IReadOnlyDictionary<string, ulong> perceptualHashes,
            IReadOnlyDictionary<string, ulong> exactHashes,
            DedupOptions opts)
            => new() { TotalFiles = 0 };

        public Task<bool> ApplyActionsAsync(DedupResult result, DedupAction action, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    [Fact]
    public async Task IsApplying_WrapsTheRealApplyDuration()
    {
        var dedup = new BlockingDedup();
        var vm = VmWithTargets(dedup);
        Assert.False(vm.IsApplying);

        var run = vm.ApplyCommand.ExecuteAsync(null);
        await dedup.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Mid-flight: busy on, Apply refused, Stop live.
        Assert.True(vm.IsApplying);
        Assert.True(vm.StopApplyCommand.CanExecute(null));

        dedup.Release();
        await run;

        // Settled: the operation ran, and the busy state is down again.
        Assert.False(vm.IsApplying);
        Assert.False(vm.StopApplyCommand.CanExecute(null));
        Assert.Equal(1, dedup.ApplyCalls);
        Assert.Contains("Tag applied to", vm.ScanSummary);
    }

    [Fact]
    public async Task Apply_IsRefused_WhileItIsRunning()
    {
        var dedup = new BlockingDedup();
        var vm = VmWithTargets(dedup);
        Assert.True(vm.ApplyCommand.CanExecute(null));

        var run = vm.ApplyCommand.ExecuteAsync(null);
        await dedup.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The gate the view honors: Avalonia disables a button whose command
        // reports CanExecute false, so while the first batch is in flight a
        // second Apply cannot start (TheApplyCard_ShowsBusyAndStop_WhileApplying
        // pins the disabled button itself). One batch, one call.
        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.Equal(1, dedup.ApplyCalls);
        Assert.True(vm.IsApplying);

        dedup.Release();
        await run;
        Assert.Equal(1, dedup.ApplyCalls);
        // The batch that ran took its group with it, and nothing is in flight.
        Assert.False(vm.IsApplying);
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task StoppingAnApply_ClearsTheBusyState_andCancelsTheOperation()
    {
        var dedup = new BlockingDedup();
        var vm = VmWithTargets(dedup);

        var run = vm.ApplyCommand.ExecuteAsync(null);
        await dedup.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(vm.IsApplying);

        vm.StopApplyCommand.Execute(null);

        // The command may surface the cancellation or swallow it — either way the
        // operation must have been cancelled and the state must not stick busy.
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
            // expected: Stop cancels through the shared token
        }

        Assert.True(dedup.Cancelled);
        Assert.False(vm.IsApplying);
        Assert.False(vm.StopApplyCommand.CanExecute(null));
    }

    [Fact]
    public void Apply_IsNotBusy_BeforeAnOperationStarts()
    {
        var vm = VmWithTargets(new BlockingDedup());
        Assert.False(vm.IsApplying);
        Assert.False(vm.StopApplyCommand.CanExecute(null));
    }

    /// <summary>
    /// The view half of the same contract: while the operation runs the Apply
    /// button is off, a Stop bound to the apply is on screen, and exactly one
    /// indeterminate bar is visible (the scan's is collapsed — Apply is what is
    /// running).
    /// </summary>
    [AvaloniaFact]
    public async Task TheApplyCard_ShowsBusyAndStop_WhileApplying()
    {
        var dedup = new BlockingDedup();
        var vm = VmWithTargets(dedup);
        var window = new Window { Content = new StepDedup { DataContext = vm }, Width = 1000, Height = 700 };
        window.Show();
        try
        {
            var apply = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                b => (b.Content as string) == "Apply");
            Assert.True(apply.IsEnabled);

            var run = vm.ApplyCommand.ExecuteAsync(null);
            await dedup.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Dispatcher.UIThread.RunJobs();

            Assert.False(apply.IsEnabled);
            var stop = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                b => b.Command == vm.StopApplyCommand);
            Assert.True(EffectivelyVisible(stop));
            Assert.True(stop.IsEnabled);
            Assert.Equal(1, window.GetVisualDescendants().OfType<ProgressBar>()
                .Count(p => p.IsIndeterminate && EffectivelyVisible(p)));

            dedup.Release();
            await run;
            Dispatcher.UIThread.RunJobs();

            Assert.True(apply.IsEnabled);
            Assert.False(EffectivelyVisible(stop));
        }
        finally
        {
            window.Close();
        }
    }
}

using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Synapic.Main.Models;
using Synapic.Main.ViewModels;
using Synapic.Main.Views;
using Synapic.Main.Views.Operation;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// Regression: a running batch has to re-arm the shell's navigation lock. The
/// predicate re-checks on click, but the Home entry only redraws when a change
/// notification fires — so the host raises NavigationLockChanged when a run's
/// IsRunning flips, and the shell re-notifies IsNavigationLocked. The wiring
/// used to be missing, so a running batch left Home clickable.
/// </summary>
public class OperationLockWiringTests
{
    [AvaloniaFact]
    public void A_running_batch_locks_navigation_and_unlocks_when_it_stops()
    {
        var vm = new MainWindowViewModel(new FakeSidecar(), new Session(),
            () => null, null, null, _ => null);
        var lockNotices = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.IsNavigationLocked))
                Interlocked.Increment(ref lockNotices);
        };

        Assert.False(vm.IsNavigationLocked);

        vm.Operations.TagRun.IsRunning = true;
        Assert.True(vm.IsNavigationLocked);
        Assert.True(lockNotices > 0, "the shell never heard the run start");

        vm.Operations.TagRun.IsRunning = false;
        Assert.False(vm.IsNavigationLocked);
        Assert.True(lockNotices > 1, "the shell never heard the run stop");
    }

    [AvaloniaFact]
    public void Retry_and_verification_work_also_locks_navigation()
    {
        // R04: the report's Retry/Verify write to the source too, so they belong
        // to the same activity lock as a batch — a second operation must not be
        // opened and started while a retry is in flight.
        var vm = new MainWindowViewModel(new FakeSidecar(), new Session(),
            () => null, null, null, _ => null);

        Assert.False(vm.IsNavigationLocked);
        vm.Operations.TagReport.IsBusy = true;
        Assert.True(vm.IsNavigationLocked);
        vm.Operations.TagReport.IsBusy = false;
        Assert.False(vm.IsNavigationLocked);
    }

    [AvaloniaFact]
    public void Scope_controls_are_locked_while_a_run_is_in_flight()
    {
        var session = new Session();
        session.Datasource.Type = "local";
        session.Datasource.LocalPath = Path.GetTempPath();
        var vm = new MainWindowViewModel(new FakeSidecar { IsRunning = true }, session,
            () => null, null, null, _ => null);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            vm.StartDedupRouteCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var scope = Assert.Single(window.GetVisualDescendants().OfType<ScopeSelectionPanel>());
            Assert.True(scope.IsEnabled);

            vm.Operations.Dedup.IsRunning = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(scope.IsEnabled,
                "the scope described a source the running pass is not using");

            vm.Operations.Dedup.IsRunning = false;
            Dispatcher.UIThread.RunJobs();
            Assert.True(scope.IsEnabled);
        }
        finally
        {
            window.Close();
        }
    }
}

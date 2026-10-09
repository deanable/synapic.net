using Avalonia.Headless.XUnit;
using Synapic.Main.Models;
using Synapic.Main.ViewModels;
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
        var vm = new MainWindowViewModel(new FakeSidecar(), new FakeBuildService(), new Session(),
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
}

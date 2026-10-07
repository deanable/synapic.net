using Avalonia.Headless.XUnit;
using Synapic.Avalonia.Models;
using Synapic.Avalonia.ViewModels;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// Regression: Step 3's IsRunning flips must re-arm the wizard's nav commands
/// (IsNavigationLocked). The predicate re-checks on click, but the buttons
/// only redraw when NotifyCanExecuteChanged fires - the wiring used to be
/// missing, so a running batch showed clickable Next/Back.
/// </summary>
public class WizardProcessingLockWiringTests
{
    [AvaloniaFact]
    public void Step3_IsRunning_refreshes_wizard_navigation()
    {
        var wizard = new WizardViewModel(new Session(), new FakeSidecar());
        var canExecuteChanged = 0;
        wizard.NextCommand.CanExecuteChanged += (_, _) => Interlocked.Increment(ref canExecuteChanged);

        Assert.False(wizard.IsNavigationLocked);

        wizard.Step3.IsRunning = true;
        Assert.True(wizard.IsNavigationLocked);
        Assert.Contains("locked", wizard.NavHintText);
        Assert.False(wizard.NextCommand.CanExecute(null));
        Assert.True(canExecuteChanged > 0);

        wizard.Step3.IsRunning = false;
        Assert.False(wizard.IsNavigationLocked);
        Assert.Equal("", wizard.NavHintText);
        Assert.True(wizard.NextCommand.CanExecute(null));
    }
}

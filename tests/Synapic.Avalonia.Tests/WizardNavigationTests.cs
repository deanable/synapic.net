using Synapic.Avalonia.Models;
using Synapic.Avalonia.ViewModels;
using Xunit;

namespace Synapic.Avalonia.Tests;

public class WizardNavigationTests
{
    private static Session ReadySession()
    {
        var session = new Session();
        session.Datasource.Type = "local";
        session.Datasource.LocalPath = Path.GetTempPath();
        session.Engine.ModelId = "LiquidAI/LFM2.5-VL-450M";
        return session;
    }

    [Fact]
    public void OnStep2_NextAndStart_DisabledUntilAtLeastOneTagField()
    {
        var session = ReadySession();
        var vm = new WizardViewModel(session, new FakeSidecar());

        vm.GoToStep2Command.Execute(null);
        Assert.Equal(1, vm.CurrentStepIndex);
        Assert.True(vm.NextCommand.CanExecute(null));

        // Uncheck all three: the forward action, the Step 3 tab and Start lock.
        vm.Step2.TagKeywords = false;
        vm.Step2.TagCategories = false;
        vm.Step2.TagDescription = false;

        Assert.False(vm.NextCommand.CanExecute(null));
        Assert.False(vm.GoToStep3Command.CanExecute(null));
        Assert.False(vm.Step3.StartCommand.CanExecute(null));

        // Re-checking any one re-enables them.
        vm.Step2.TagDescription = true;
        Assert.True(vm.NextCommand.CanExecute(null));
        Assert.True(vm.GoToStep3Command.CanExecute(null));
        Assert.True(vm.Step3.StartCommand.CanExecute(null));
    }

    [Fact]
    public void OnStep1_Next_IsNotBlockedByTagFields()
    {
        var session = ReadySession();
        session.Engine.TagKeywords = false;
        session.Engine.TagCategories = false;
        session.Engine.TagDescription = false;

        var vm = new WizardViewModel(session, new FakeSidecar());

        Assert.Equal(0, vm.CurrentStepIndex);
        Assert.True(vm.NextCommand.CanExecute(null));
    }
}

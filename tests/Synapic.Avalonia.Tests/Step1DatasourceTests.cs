using Synapic.Avalonia.Models;
using Synapic.Avalonia.ViewModels.Steps;
using Xunit;

namespace Synapic.Avalonia.Tests;

public class Step1DatasourceTests
{
    private static Step1DatasourceViewModel NewViewModel() => new(new Session());

    [Fact]
    public void ConnectCommand_DisabledUntilAllCredentialsEntered()
    {
        var vm = NewViewModel();
        Assert.False(vm.ConnectCommand.CanExecute(null));

        vm.DaminionUrl = "http://dam.local/daminion";
        Assert.False(vm.ConnectCommand.CanExecute(null));
        vm.DaminionUser = "bob";
        Assert.False(vm.ConnectCommand.CanExecute(null));
        vm.DaminionPass = "secret";
        Assert.True(vm.ConnectCommand.CanExecute(null));
    }

    /// <summary>
    /// The button only re-evaluates when CanExecuteChanged fires. Calling
    /// CanExecute directly (as above) passes even when the ViewModel forgets to
    /// notify, so assert the notifications the WPF/Avalonia command machinery
    /// actually depends on.
    /// </summary>
    [Fact]
    public void ConnectCommand_RaisesCanExecuteChanged_AsCredentialsChange()
    {
        var vm = NewViewModel();
        var raised = 0;
        vm.ConnectCommand.CanExecuteChanged += (_, _) => raised++;

        vm.DaminionUrl = "http://dam.local/daminion";
        vm.DaminionUser = "bob";
        vm.DaminionPass = "secret";

        Assert.True(raised >= 3, $"expected a re-query per field, saw {raised}");

        // Clearing a required field must disable the button again.
        vm.DaminionPass = "";
        Assert.False(vm.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public void ConnectCommand_NotificationsAlsoFireWhileConnecting()
    {
        var vm = NewViewModel();
        vm.DaminionUrl = "http://dam.local/daminion";
        vm.DaminionUser = "bob";
        vm.DaminionPass = "secret";
        Assert.True(vm.ConnectCommand.CanExecute(null));

        vm.IsConnecting = true;
        Assert.False(vm.ConnectCommand.CanExecute(null));
    }
}

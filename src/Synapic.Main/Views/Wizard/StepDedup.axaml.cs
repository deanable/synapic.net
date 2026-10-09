using Avalonia.Controls;
using Synapic.Main.ViewModels.Steps;

namespace Synapic.Main.Views.Wizard;

public partial class StepDedup : UserControl
{
    public StepDedup()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireConfirm();
    }

    /// <summary>Route the dedup step's destructive-action confirmation through
    /// the modal dialog (Daminion "delete from catalog"). Re-run whenever the
    /// data context is (re)attached — e.g. headless test hosts set it later.</summary>
    private void WireConfirm()
    {
        if (DataContext is not StepDedupViewModel vm) return;
        vm.ConfirmAction = message =>
            ConfirmDialogWindow.ShowAsync(TopLevel.GetTopLevel(this) as Window, message);
    }
}

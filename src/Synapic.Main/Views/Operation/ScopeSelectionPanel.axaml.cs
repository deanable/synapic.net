using Avalonia.Controls;

namespace Synapic.Main.Views.Operation;

/// <summary>
/// The scope selection the processing views carry (docs/ui-design.md D10): the
/// catalog scope, its filters and the record count, bound to the one shared
/// source view model the Settings view binds too. It carries no commands of its
/// own — the controls bind the view model directly.
/// </summary>
public partial class ScopeSelectionPanel : UserControl
{
    public ScopeSelectionPanel()
    {
        InitializeComponent();
    }
}

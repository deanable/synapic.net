using Avalonia.Controls;

namespace Synapic.Main.Views.Operation;

/// <summary>
/// The shared run bar: one view for the run state every operation inherits from
/// <c>RunStateViewModel</c> (Phase 1). It carries no mode knowledge of its own.
/// </summary>
public partial class RunStateBar : UserControl
{
    public RunStateBar()
    {
        InitializeComponent();
    }
}

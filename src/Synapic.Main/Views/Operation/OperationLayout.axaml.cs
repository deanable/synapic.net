using Avalonia.Controls;

namespace Synapic.Main.Views.Operation;

/// <summary>
/// The shared operation template: the three regions every mode renders through
/// (Data source → Parameters → Output). It hosts the existing panels and pages
/// and holds no mode logic — only per-mode DataTemplates.
/// </summary>
public partial class OperationLayout : UserControl
{
    public OperationLayout()
    {
        InitializeComponent();
    }
}

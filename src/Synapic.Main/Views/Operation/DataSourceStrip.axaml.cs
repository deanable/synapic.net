using Avalonia.Controls;

namespace Synapic.Main.Views.Operation;

/// <summary>
/// Region A of the operation template: the shared data source, bound to the one
/// shell-owned <c>Step1DatasourceViewModel</c> instance (docs/ui-design.md §4.1,
/// D-02/D4). Read-only by design — the source is configured once and every mode
/// shows and runs on the same one — so the code-behind only loads the XAML.
/// </summary>
public partial class DataSourceStrip : UserControl
{
    public DataSourceStrip()
    {
        InitializeComponent();
    }
}

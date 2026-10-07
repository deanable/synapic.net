using Avalonia.Controls;

namespace Synapic.Main.Views.Wizard;

/// <summary>
/// Step 1: the source the run will use, reported read-only (it is chosen on the
/// start screen's source panel), plus the processing limits — how many items to
/// take and how much of each image the model sees. The folder picker and the
/// Daminion connect form live in <see cref="DatasourceSourcePanel"/>.
/// </summary>
public partial class Step1Datasource : UserControl
{
    public Step1Datasource()
    {
        InitializeComponent();
    }
}

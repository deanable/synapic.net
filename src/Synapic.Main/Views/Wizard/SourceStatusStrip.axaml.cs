using Avalonia.Controls;

namespace Synapic.Main.Views.Wizard;

/// <summary>
/// The "what am I working with" profile: one light for the Daminion session and
/// one for the local folder. Hosted by the start screen's source panel and by
/// Step 1's summary, both bound to the same <c>Step1DatasourceViewModel</c>.
/// </summary>
public partial class SourceStatusStrip : UserControl
{
    public SourceStatusStrip()
    {
        InitializeComponent();
    }
}

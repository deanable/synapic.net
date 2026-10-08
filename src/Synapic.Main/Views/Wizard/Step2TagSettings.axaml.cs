using Avalonia.Controls;

namespace Synapic.Main.Views.Wizard;

/// <summary>
/// 3 · Settings: the tagging settings as a summary, with the full form in the
/// settings dialog. It renders the same <see cref="ViewModels.Steps.Step2EngineViewModel"/>
/// the dialog edits, so the summary cannot drift from the settings.
/// </summary>
public partial class Step2TagSettings : UserControl
{
    public Step2TagSettings()
    {
        InitializeComponent();
    }
}

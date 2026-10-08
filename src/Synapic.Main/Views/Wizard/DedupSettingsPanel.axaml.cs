using Avalonia.Controls;

namespace Synapic.Main.Views.Wizard;

/// <summary>
/// The deduplication settings (3 · Settings for the dedup operation), hosted by
/// <see cref="Views.Settings.DedupSettingsDialog"/>. It renders the wizard's own
/// <see cref="ViewModels.Steps.StepDedupViewModel"/>, so the rule chosen here is
/// the rule the scan uses.
/// </summary>
public partial class DedupSettingsPanel : UserControl
{
    public DedupSettingsPanel()
    {
        InitializeComponent();
    }
}

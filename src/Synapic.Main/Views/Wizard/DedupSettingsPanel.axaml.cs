using Avalonia.Controls;

namespace Synapic.Main.Views.Wizard;

/// <summary>
/// The deduplication settings (the dedup operation's Parameters region), hosted
/// by the shared <see cref="Views.Operation.OperationLayout"/>. It renders the
/// wizard's own <see cref="ViewModels.Steps.StepDedupViewModel"/>, so the rule
/// chosen here is the rule the scan uses.
/// </summary>
public partial class DedupSettingsPanel : UserControl
{
    public DedupSettingsPanel()
    {
        InitializeComponent();
    }
}

using Avalonia.Controls;

namespace Synapic.Main.Views.Wizard;

/// <summary>
/// The similarity search's settings (the Similar images operation's Parameters
/// region), hosted by the shared <see cref="Views.Operation.OperationLayout"/> and
/// by the Settings view's Similar tab. It renders the shell's
/// <see cref="ViewModels.Steps.StepDedupViewModel"/> instance for the
/// <see cref="ViewModels.Steps.ImageMatchProfile.Similar"/> profile, so the
/// threshold chosen here is the threshold that search groups at — and never the
/// one the duplicate scan uses.
/// </summary>
public partial class SimilarSettingsPanel : UserControl
{
    public SimilarSettingsPanel()
    {
        InitializeComponent();
    }
}

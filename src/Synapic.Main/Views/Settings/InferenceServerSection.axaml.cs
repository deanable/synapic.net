using Avalonia.Controls;

namespace Synapic.Main.Views.Settings;

/// <summary>
/// The Inference server settings section (docs/ui-design.md §5): status, the
/// Start/Stop/Build controls, the auto-launch toggle, the download progress and
/// the per-platform variant rows. Bound to the shell it operates — this section
/// is the one place that drives the sidecar from the Settings view.
/// </summary>
public partial class InferenceServerSection : UserControl
{
    public InferenceServerSection()
    {
        InitializeComponent();
    }
}

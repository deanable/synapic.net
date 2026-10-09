using Avalonia.Controls;

namespace Synapic.Main.Views.Wizard;

/// <summary>
/// The upscale settings (the upscaling operation's Parameters region), hosted by
/// the shared <see cref="Views.Operation.OperationLayout"/>. It renders the
/// wizard's own <see cref="ViewModels.Steps.StepUpscaleViewModel"/>, so the run
/// page reports exactly the parameters a batch would use.
/// </summary>
public partial class UpscaleSettingsPanel : UserControl
{
    public UpscaleSettingsPanel()
    {
        InitializeComponent();
    }
}

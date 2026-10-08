using Avalonia.Controls;

namespace Synapic.Main.Views.Wizard;

/// <summary>
/// The upscale settings (3 · Settings for the upscaling operation), hosted by
/// <see cref="Views.Settings.UpscaleSettingsDialog"/>. It renders the wizard's
/// own <see cref="ViewModels.Steps.StepUpscaleViewModel"/>, so the run page
/// reports exactly the parameters a batch would use.
/// </summary>
public partial class UpscaleSettingsPanel : UserControl
{
    public UpscaleSettingsPanel()
    {
        InitializeComponent();
    }
}

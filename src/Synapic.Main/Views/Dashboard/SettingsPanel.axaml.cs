using Avalonia.Controls;

namespace Synapic.Main.Views.Dashboard;

/// <summary>
/// The app-wide settings view (docs/ui-design.md §5), rendered by the dashboard's
/// Settings panel. Bound to <see cref="ViewModels.SettingsViewModel"/>; the
/// Inference server section reaches the server it controls through that view
/// model's <c>Shell</c> reference, so no code-behind wiring is needed here.
/// </summary>
public partial class SettingsPanel : UserControl
{
    public SettingsPanel()
    {
        InitializeComponent();
    }
}

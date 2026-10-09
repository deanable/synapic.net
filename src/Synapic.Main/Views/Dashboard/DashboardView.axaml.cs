using Avalonia.Controls;

namespace Synapic.Main.Views.Dashboard;

/// <summary>
/// The app's starting screen (docs/ui-design.md §2.1): four panels — Settings,
/// Tag, Dedup, Upscale — each opening its view. Bound to the shell's own view
/// model, because the panels open the three operations through the same route
/// commands the sidebar uses and read their status from shared shell state.
/// </summary>
public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
    }
}

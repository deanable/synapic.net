using Avalonia;
using Avalonia.Controls;

namespace Synapic.Main.Views.Dashboard;

/// <summary>
/// The app's starting screen (docs/ui-design.md §2.1): four panels — Settings,
/// Tag, Dedup, Upscale — each opening its view. Bound to the shell's own view
/// model, because the panels open the three operations through the same route
/// commands the header uses and read their status from shared shell state.
///
/// It owns the grid's responsive form too (§7/D8): Avalonia has no XAML
/// breakpoint, so this control moves the four panels between the two-column grid
/// and a single reading-order column when its own width crosses
/// <see cref="MinPanelWidth"/>.
/// </summary>
public partial class DashboardView : UserControl
{
    /// <summary>
    /// The width each panel keeps before the grid stacks. ui-design §7 says the
    /// dashboard stacks "below ~1000 px"; measured on the dashboard's own width
    /// that would leave each panel of a two-column grid about 490 px wide, which
    /// the settings form and the operation cards cannot use. The switch is
    /// therefore set where a panel keeps 600 px — twice this, i.e. roughly
    /// 1200 px of dashboard width — and recorded as decision D8 in
    /// docs/ui-design.md. The layout audit pins both forms and asserts this rule
    /// rather than today's geometry.
    /// </summary>
    public const double MinPanelWidth = 600;

    /// <summary>
    /// The form the XAML declares, and the form until a layout pass says
    /// otherwise: two columns. Starting here (rather than at 0 width) keeps the
    /// wide form the default for a control that has not been laid out yet.
    /// </summary>
    private bool _twoColumns = true;

    public DashboardView()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Bounds, not the window's width: the dashboard is what has to fit four
        // panels, and it is the ScrollViewer's content region that gives them
        // their width.
        if (change.Property == BoundsProperty)
            ApplyColumns(change.GetNewValue<Rect>().Width);
    }

    /// <summary>
    /// Lay the four panels out for the width available: two columns while each
    /// keeps <see cref="MinPanelWidth"/>, one full-width column below that.
    /// Row-major reading order either way — Settings, Tag, Dedup, Upscale — so
    /// the dashboard reads the same at every size. Idempotent: a layout pass that
    /// does not change the form does nothing, so measuring cannot loop.
    /// </summary>
    public void ApplyColumns(double width)
    {
        var twoColumns = width >= MinPanelWidth * 2;
        if (twoColumns == _twoColumns) return;
        _twoColumns = twoColumns;

        var panels = new Control[] { SettingsPanel, TagPanel, DedupPanel, UpscalePanel };
        for (var i = 0; i < panels.Length; i++)
        {
            var panel = panels[i];
            Grid.SetRow(panel, twoColumns ? i / 2 : i);
            Grid.SetColumn(panel, twoColumns ? i % 2 : 0);
            Grid.SetColumnSpan(panel, twoColumns ? 1 : 2);
        }
    }
}

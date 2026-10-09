using Avalonia;
using Avalonia.Controls;
using Synapic.Main.ViewModels.Steps;

namespace Synapic.Main.Views.Operation;

/// <summary>
/// The shared operation template: the three regions every mode renders through
/// (Data source → Parameters → Output). It hosts the existing panels and pages
/// and holds no mode logic — only per-mode DataTemplates and the four slots the
/// host fills:
///
/// <list type="bullet">
/// <item><see cref="SharedSource"/> — the one shared source for Region A (D4).</item>
/// <item><see cref="Parameters"/> — the open mode's own settings (Region B).</item>
/// <item><see cref="Run"/> — the open mode's run surface and its controls (Region C).</item>
/// <item><see cref="Report"/> — the open mode's report, when it has one (Region C).</item>
/// </list>
///
/// The slots are Avalonia properties so a mode change really re-renders the
/// regions: the shell writes <c>Shell.Current</c>, the host reads the new mode's
/// content off <c>OperationShellViewModel</c> and sets these, and the bindings
/// follow. (A plain CLR property would be read once and never update, which is
/// how a mode switch could keep showing the previous mode's form.)
/// </summary>
public partial class OperationLayout : UserControl
{
    /// <summary>
    /// The one shell-owned data source (docs/ui-design.md D4), handed to Region A
    /// by the host. The template still knows no mode: it renders whatever source
    /// state the shell passes in, and Region A binds that single instance — which
    /// is what makes the source read identically on every route.
    /// </summary>
    public static readonly StyledProperty<Step1DatasourceViewModel?> SharedSourceProperty =
        AvaloniaProperty.Register<OperationLayout, Step1DatasourceViewModel?>(nameof(SharedSource));

    /// <summary>Region B's content: the open mode's parameters view model.</summary>
    public static readonly StyledProperty<object?> ParametersProperty =
        AvaloniaProperty.Register<OperationLayout, object?>(nameof(Parameters));

    /// <summary>Region C's run content: progress bar plus the mode's run surface.</summary>
    public static readonly StyledProperty<object?> RunProperty =
        AvaloniaProperty.Register<OperationLayout, object?>(nameof(Run));

    /// <summary>Region C's report content, or null for a mode with none.</summary>
    public static readonly StyledProperty<object?> ReportProperty =
        AvaloniaProperty.Register<OperationLayout, object?>(nameof(Report));

    public OperationLayout()
    {
        InitializeComponent();
    }

    public Step1DatasourceViewModel? SharedSource
    {
        get => GetValue(SharedSourceProperty);
        set => SetValue(SharedSourceProperty, value);
    }

    public object? Parameters
    {
        get => GetValue(ParametersProperty);
        set => SetValue(ParametersProperty, value);
    }

    public object? Run
    {
        get => GetValue(RunProperty);
        set => SetValue(RunProperty, value);
    }

    public object? Report
    {
        get => GetValue(ReportProperty);
        set => SetValue(ReportProperty, value);
    }
}

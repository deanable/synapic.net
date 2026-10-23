using Avalonia;
using Avalonia.Controls;
using Synapic.Main.ViewModels.Steps;

namespace Synapic.Main.Views.Operation;

/// <summary>
/// The shared processing template (docs/mock-up/Mockup.svg): what a run works on
/// and what it produced. Configuration lives on the Settings view, so this
/// element hosts only the read-only source summary and the output — it holds no
/// mode logic, only per-operation DataTemplates and the three slots the host
/// fills:
///
/// <list type="bullet">
/// <item><see cref="SharedSource"/> — the read-only source summary.</item>
/// <item><see cref="Run"/> — the open operation's run surface and controls.</item>
/// <item><see cref="Report"/> — the open operation's report, when it has one.</item>
/// </list>
///
/// The slots are Avalonia properties so a view change really re-renders the
/// regions: the shell writes <c>Shell.Current</c>, the host reads the new
/// operation's content off <c>OperationShellViewModel</c> and sets these, and the
/// bindings follow. (A plain CLR property would be read once and never update,
/// which is how a view switch could keep showing the previous operation's run.)
/// </summary>
public partial class OperationLayout : UserControl
{
    /// <summary>
    /// The one shell-owned data source (docs/ui-design.md D4), handed to the
    /// read-only summary by the host. The template still knows no operation: it
    /// renders whatever source state the shell passes in, and the summary has no
    /// inputs — the source is configured on the Settings view.
    /// </summary>
    public static readonly StyledProperty<Step1DatasourceViewModel?> SharedSourceProperty =
        AvaloniaProperty.Register<OperationLayout, Step1DatasourceViewModel?>(nameof(SharedSource));

    /// <summary>The output's run content: progress bar plus the operation's run surface.</summary>
    public static readonly StyledProperty<object?> RunProperty =
        AvaloniaProperty.Register<OperationLayout, object?>(nameof(Run));

    /// <summary>The output's report content, or null for an operation with none.</summary>
    public static readonly StyledProperty<object?> ReportProperty =
        AvaloniaProperty.Register<OperationLayout, object?>(nameof(Report));

    /// <summary>
    /// Whether the scope controls inside the source summary accept edits. The
    /// host clears it while any operation is running: the in-flight run already
    /// snapshotted its selection, so letting the screen move the source mid-pass
    /// would describe a source the run is not using (review R04's source-change
    /// half).
    /// </summary>
    public static readonly StyledProperty<bool> IsSourceEditableProperty =
        AvaloniaProperty.Register<OperationLayout, bool>(nameof(IsSourceEditable), defaultValue: true);

    public OperationLayout()
    {
        InitializeComponent();
    }

    public Step1DatasourceViewModel? SharedSource
    {
        get => GetValue(SharedSourceProperty);
        set => SetValue(SharedSourceProperty, value);
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

    /// <summary>See <see cref="IsSourceEditableProperty"/>.</summary>
    public bool IsSourceEditable
    {
        get => GetValue(IsSourceEditableProperty);
        set => SetValue(IsSourceEditableProperty, value);
    }
}

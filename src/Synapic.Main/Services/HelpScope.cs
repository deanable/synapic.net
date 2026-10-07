using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Synapic.Main.Services;

/// <summary>
/// Per-control help scoping: attach <c>HelpScope.Topic</c> to a control and F1
/// opens that topic while the focus is inside it. The nearest annotated
/// ancestor wins, so a section can carry one topic while a single setting
/// inside it overrides with an anchored deep link such as
/// <c>settings-reference.html#confidence-threshold</c>.
///
/// An annotation that <see cref="HelpService.NormalizeTopic"/> would reject
/// (a path, a non-.html name) is skipped rather than falling back to the home
/// page: a stale annotation degrades to the next scope up, and finally to the
/// unscoped F1 behaviour in
/// <see cref="Synapic.Main.ViewModels.MainWindowViewModel.ContextHelpTopic"/>
/// (the sidecar panel, or the wizard step on screen).
/// </summary>
public sealed class HelpScope
{
    private HelpScope() { }

    /// <summary>The help topic (optionally with a #anchor) for the control's scope.</summary>
    public static readonly AttachedProperty<string?> TopicProperty =
        AvaloniaProperty.RegisterAttached<HelpScope, Control, string?>("Topic");

    /// <summary>Sets the scope's topic; called by XAML.</summary>
    public static void SetTopic(Control element, string? value) => element.SetValue(TopicProperty, value);

    /// <summary>The topic annotated on this control itself, ignoring ancestors.</summary>
    public static string? GetTopic(Control element) => element.GetValue(TopicProperty);

    /// <summary>
    /// The topic of the nearest annotated ancestor of <paramref name="start"/>
    /// (itself included), normalized; null when nothing on the way to the
    /// visual root carries a usable topic.
    /// </summary>
    public static string? Resolve(object? start)
    {
        for (var visual = start as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is not Control control) continue;

            var topic = GetTopic(control);
            if (string.IsNullOrWhiteSpace(topic)) continue;

            var normalized = HelpService.NormalizeTopic(topic);
            if (normalized == topic.Trim()) return normalized;
        }

        return null;
    }
}

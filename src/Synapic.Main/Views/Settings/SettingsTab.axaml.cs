using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Synapic.Main.Views.Settings;

/// <summary>
/// The Settings view (docs/mock-up/Mockup.svg, first sidebar row): the one home
/// for every configuration — the data source and its server connection, the
/// inference server and engine, the per-operation rules, and the app
/// preferences. Bound to the shell, because it composes the shared source, the
/// operation view models and the app-settings view model in one column.
///
/// It also owns the search box: a query filters the column to the configuration
/// sections that mention it and scrolls the first match into view, so the long
/// column stays navigable. An empty query shows every section (the default).
/// </summary>
public partial class SettingsTab : UserControl
{
    public SettingsTab()
    {
        InitializeComponent();
        SettingsSearchBox.TextChanged += (_, _) => ApplyFilter(SettingsSearchBox.Text);
    }

    /// <summary>
    /// Filter the configuration column to the sections that mention
    /// <paramref name="query"/> and scroll the first match into view. An empty
    /// query shows every section again. Returns the number of matching sections.
    /// </summary>
    public int ApplyFilter(string? query)
    {
        var text = (query ?? "").Trim();
        var sections = Sections();

        if (text.Length == 0)
        {
            foreach (var section in sections) section.IsVisible = true;
            SearchStatusText.IsVisible = false;
            UpdateGroupHeaders(show: true);
            return sections.Count;
        }

        var matches = new List<Border>();
        foreach (var section in sections)
        {
            var hit = Matches(section, text);
            section.IsVisible = hit;
            if (hit) matches.Add(section);
        }

        SearchStatusText.Text = matches.Count switch
        {
            0 => $"No settings match \u201c{text}\u201d.",
            1 => $"1 section matches \u201c{text}\u201d.",
            _ => $"{matches.Count} sections match \u201c{text}\u201d.",
        };
        SearchStatusText.IsVisible = true;

        UpdateGroupHeaders(show: false);
        if (matches.Count > 0) ScrollTo(matches[0]);
        return matches.Count;
    }

    /// <summary>The four sections the App preferences heading introduces.</summary>
    private static readonly string[] PreferenceSections =
        { "SectionAppearance", "SectionLogging", "SectionDefaults", "SectionAbout" };

    /// <summary>
    /// Show or hide the App preferences heading with its sections: a query that
    /// names none of them must not leave the heading stranded over empty space.
    /// </summary>
    private void UpdateGroupHeaders(bool show)
    {
        var header = this.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(c => c.Name == "AppPreferencesHeader");
        if (header is null) return;

        header.IsVisible = show || PreferenceSections.Any(name =>
            this.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(b => b.Name == name) is { IsVisible: true });
    }

    /// <summary>The configuration sections, in reading order: the named Borders.</summary>
    private IReadOnlyList<Border> Sections() =>
        this.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Name is not null && b.Name.StartsWith("Section", StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// Does this section mention the query? Its own name counts (so "datasource"
    /// finds SectionDataSource), and so does any text it renders — headings,
    /// hints, field labels, control content.
    /// </summary>
    private static bool Matches(Control section, string query) =>
        NameMatches(section.Name, query) ||
        section.GetVisualDescendants().OfType<TextBlock>().Any(t => Contains(t.Text, query)) ||
        section.GetVisualDescendants().OfType<ContentControl>()
            .Any(c => Contains(c.Content as string, query));

    private static bool Contains(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Name match ignores casing and spaces, so "data source" finds SectionDataSource.</summary>
    private static bool NameMatches(string? name, string query) =>
        !string.IsNullOrEmpty(name) &&
        name.Replace(" ", "").Contains(query.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);

    /// <summary>Scroll the enclosing ScrollViewer so the target sits at the top of the viewport.</summary>
    private static void ScrollTo(Control target)
    {
        var scroll = target.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is null) return;
        if (target.TranslatePoint(new Point(0, 0), scroll) is not { } topLeft) return;
        scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y + topLeft.Y));
    }
}

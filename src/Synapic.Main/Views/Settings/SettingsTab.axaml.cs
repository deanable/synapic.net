using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Synapic.Main.Views.Settings;

/// <summary>
/// The Settings view as eight tabs — Connection, Inference server, Tagging engine,
/// Deduplication, Similar images, Upscaling, Service, App preferences — one concern per tab
/// instead of one long column for all of them (ui-design §5, 2026-10-11 revision:
/// a new user could not see where one concern ended and the next began). The search box
/// above the strip is the fast path: it switches to the tab the query names, and
/// <see cref="ShowSection"/>/<see cref="ShowTab"/> are the entry points tests use
/// to land on a tab by section or tab name.
/// </summary>
public partial class SettingsTab : UserControl
{
    /// <summary>Each tab and the suggestions that name it in the search box.</summary>
    private static readonly (string TabName, string[] Suggestions)[] TabSuggestions =
    {
        ("TabConnection", new[] { "Data source", "Local folder", "Daminion", "Max items", "AI image scale" }),
        ("TabServer", new[] { "Inference server", "Sidecar", "Build", "Download", "Auto-launch" }),
        ("TabEngine", new[] { "Inference engine", "Model", "Device", "Tag fields", "Scoring", "Prompts" }),
        // The shared words (Algorithm, Threshold, Keep-set) deliberately appear on
        // both image-matching tabs: a generic query lands on Deduplication, the
        // first of the two, while "similar" names the similarity tab on its own.
        ("TabDedup", new[] { "Deduplication", "Duplicate", "Duplicates", "Algorithm", "Threshold", "Keep-set" }),
        ("TabSimilar", new[] { "Similar", "Similarity", "Near-match", "Similar threshold", "Algorithm", "Threshold", "Keep-set" }),
        ("TabUpscale", new[] { "Upscaling", "Workflow", "Factor", "Precision", "Output" }),
        ("TabService", new[] { "Service", "Background service", "Watch folders", "Folder monitor", "Queue", "Indexer" }),
        ("TabApp", new[] { "Appearance", "Theme", "Logging", "Defaults", "About" }),
    };

    /// <summary>
    /// The named section cards and the tab that hosts them. The App preferences
    /// tab carries four sections behind one tab entry.
    /// </summary>
    public static readonly (string Section, string Tab)[] SectionToTab =
    {
        ("SectionDataSource", "TabConnection"),
        ("SectionInferenceServer", "TabServer"),
        ("SectionEngine", "TabEngine"),
        ("SectionDedup", "TabDedup"),
        ("SectionSimilar", "TabSimilar"),
        ("SectionUpscale", "TabUpscale"),
        ("SectionService", "TabService"),
        ("SectionWatchFolders", "TabService"),
        ("SectionQueue", "TabService"),
        ("SectionAppearance", "TabApp"),
        ("SectionLogging", "TabApp"),
        ("SectionDefaults", "TabApp"),
        ("SectionAbout", "TabApp"),
    };

    /// <summary>The search box's dropdown: every suggestion names a real control.</summary>
    public static readonly string[] SearchSuggestions =
        TabSuggestions.SelectMany(t => t.Suggestions).ToArray();

    public SettingsTab()
    {
        InitializeComponent();
        SettingsSearchBox.ItemsSource = SearchSuggestions;
        // Watch the Text property rather than TextChanged: a picked suggestion
        // updates Text, and the property is the one value the search reads.
        SettingsSearchBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == AutoCompleteBox.TextProperty) ApplyFilter(SettingsSearchBox.Text);
        };
    }

    /// <summary>
    /// Switch to the tab the query names and report what happened. An empty query
    /// resets the status without switching. Returns the number of matching
    /// suggestions — zero means the query names nothing and no switch happened.
    /// </summary>
    public int ApplyFilter(string? query)
    {
        var text = (query ?? "").Trim();
        if (text.Length == 0)
        {
            SearchStatusText.IsVisible = false;
            return TabSuggestions.Sum(t => t.Suggestions.Length);
        }

        var matches = TabSuggestions
            .Where(t => t.Suggestions.Any(s => s.Contains(text, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var total = matches.Sum(t =>
            t.Suggestions.Count(s => s.Contains(text, StringComparison.OrdinalIgnoreCase)));

        if (total == 0)
        {
            SearchStatusText.Text = $"No settings match \u201c{text}\u201d.";
        }
        else
        {
            ShowTab(matches[0].TabName);
            var label = TabLabel(matches[0].TabName);
            SearchStatusText.Text = total == 1
                ? $"1 setting matches \u201c{text}\u201d \u2014 showing {label}."
                : $"{total} settings match \u201c{text}\u201d \u2014 showing {label}.";
        }
        SearchStatusText.IsVisible = true;
        return total;
    }

    /// <summary>Select the tab that hosts <paramref name="sectionName"/>.</summary>
    public void ShowSection(string sectionName)
    {
        var tab = SectionToTab.FirstOrDefault(kv => kv.Section == sectionName).Tab
            ?? throw new ArgumentException($"Unknown section: {sectionName}", nameof(sectionName));
        ShowTab(tab);
    }

    /// <summary>Select the tab with this name (TabConnection, TabServer, …).</summary>
    public void ShowTab(string tabName)
    {
        var item = this.GetVisualDescendants().OfType<TabItem>()
            .FirstOrDefault(t => t.Name == tabName)
            ?? throw new InvalidOperationException($"No tab named {tabName} in the Settings view");
        item.IsSelected = true;
    }

    private string TabLabel(string tabName) =>
        this.GetVisualDescendants().OfType<TabItem>()
            .First(t => t.Name == tabName).Header as string ?? tabName;
}

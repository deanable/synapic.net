using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Synapic.Main.Models;
using Synapic.Main.Services;
using Synapic.Main.ViewModels;
using Synapic.Main.Views;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// The retired-concept guard (docs/ui-design.md: the sidebar update, §9, D3/D5).
/// The redesign retired four things the app had shipped with — the numbered step
/// headings (<c>1 · Source &amp; model</c> … <c>5 · Results</c>), the wizard and
/// its step chain, the dashboard, and the three modal settings dialogs — and
/// nothing objects to any of them coming back: a numbered heading is only a
/// string, a <c>dashboardPanel</c> class is only an unknown style, a binding to
/// a deleted <c>WizardViewModel</c> member is silently null, and a new
/// <c>Window</c> simply opens. These tests are the objection.
///
/// Two levels, because a concept can reappear at either one: the view sources (a
/// literal label, a style class, a binding path, a view type) and the rendered
/// window (what the user can actually read). The source scan reads the
/// <em>structure</em> of each .axaml — elements, attributes and their values —
/// rather than the raw text, so the comments that explain the retirements, and
/// the <c>Views.Wizard</c> / <c>Views.Dashboard</c> namespaces the surviving
/// panels still live in, are not mistaken for the concepts themselves.
/// </summary>
public partial class RetiredUiConceptTests
{
    /// <summary>The attributes whose value is text the user reads.</summary>
    private static readonly HashSet<string> LabelAttributes = new(StringComparer.Ordinal)
    {
        "Text", "Content", "Header", "Watermark", "ToolTip.Tip", "Title",
    };

    /// <summary>The retired chrome's style classes: the dashboard's panels and
    /// route cards, and the numbered mode rail.</summary>
    private static readonly string[] RetiredChromeClasses = { "dashboardPanel", "routeCard", "navMode" };

    /// <summary>The wizard's navigation members, deleted with it. The shell's
    /// <c>IsNavigationLocked</c> survives on <see cref="MainWindowViewModel"/> and
    /// is deliberately not listed.</summary>
    private static readonly string[] DeletedWizardMembers =
    {
        "CurrentStep", "CurrentStepIndex", "CurrentStepTitle", "NextButtonText", "PrefillDedupSource",
    };

    /// <summary>The settings dialogs retired in favour of the Settings view's
    /// sections (§9, D3).</summary>
    private static readonly string[] RetiredDialogTypes =
    {
        "EngineSettingsDialog", "DedupSettingsDialog", "UpscaleSettingsDialog",
    };

    // ── The sources ────────────────────────────────────────────────────────

    /// <summary>
    /// Every view source, obj/ copies excluded — the same set the help-scope
    /// audit walks.
    /// </summary>
    private static List<string> ViewFiles(string repoRoot)
    {
        var views = Path.Combine(repoRoot, "src", "Synapic.Main", "Views");
        return Directory.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToList();
    }

    private static string Relative(string repoRoot, string file) =>
        Path.GetRelativePath(repoRoot, file).Replace('\\', '/');

    /// <summary>Every attribute in a view source, with its owner element, so a
    /// scan can report where it found something.</summary>
    private static IEnumerable<(XElement Element, XAttribute Attribute)> Attributes(string file)
    {
        var doc = XDocument.Load(file);
        return doc.Descendants().SelectMany(element => element.Attributes().Select(a => (element, a)));
    }

    private static bool IsMarkupExtension(XAttribute attribute) => attribute.Value.StartsWith('{');

    // ── The concepts ───────────────────────────────────────────────────────

    /// <summary>
    /// A numbered step heading: the middot form the retired sidebar and chain
    /// used (<c>4 · Process</c>), a <c>Step 3</c> label, or a numbered heading
    /// with a plain separator. Any numbered label counts as a regression worth a
    /// look, because the design has no numbered chrome left to point at.
    /// </summary>
    [GeneratedRegex(@"^\s*(?:Step\s+\d{1,2}\b|\d{1,2}\s*[·•]\s*\S|\d{1,2}\s*[.\-–—:)]\s+\S)")]
    private static partial Regex NumberedStepHeading();

    /// <summary>The wording the retired chrome carried.</summary>
    [GeneratedRegex(@"\b(?:Wizard|Dashboard|Start Over)\b|←\s*Back|Next\s*→", RegexOptions.IgnoreCase)]
    private static partial Regex RetiredWording();

    /// <summary>The root of a binding path — what a binding would resolve
    /// against, so <c>{Binding CurrentStep.Title}</c> is checked as
    /// <c>CurrentStep</c>.</summary>
    [GeneratedRegex(@"^\{\s*Binding\s+(?:Path\s*=\s*)?([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex BindingRoot();

    // ── What the views say ─────────────────────────────────────────────────

    [Fact]
    public void No_view_shows_a_numbered_step_heading_or_the_retired_wording()
    {
        var repoRoot = InferenceSidecarService.FindRepoRoot();
        if (repoRoot is null) return;   // no source checkout (an installed app): nothing to scan

        var labels = 0;
        var failures = new List<string>();

        foreach (var file in ViewFiles(repoRoot))
        foreach (var (element, attribute) in Attributes(file))
        {
            if (!LabelAttributes.Contains(attribute.Name.LocalName) || IsMarkupExtension(attribute)) continue;

            var text = attribute.Value;
            labels++;
            var where = $"{Relative(repoRoot, file)}: <{element.Name.LocalName} {attribute.Name.LocalName}=\"{text}\">";
            if (NumberedStepHeading().IsMatch(text))
                failures.Add($"{where} reads as a numbered step heading");
            else if (RetiredWording().IsMatch(text))
                failures.Add($"{where} uses wording the redesign retired");
        }

        // A vacuous pass would be worse than a failure: the scan has to be
        // reading the real labels, not an empty file list.
        Assert.True(labels >= 100, $"only {labels} literal labels scanned - the guard is not reading the views");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void No_view_carries_the_retired_chrome_classes_or_a_deleted_wizard_binding()
    {
        var repoRoot = InferenceSidecarService.FindRepoRoot();
        if (repoRoot is null) return;

        var classes = 0;
        var bindings = 0;
        var failures = new List<string>();

        foreach (var file in ViewFiles(repoRoot))
        foreach (var (element, attribute) in Attributes(file))
        {
            var where = $"{Relative(repoRoot, file)}: <{element.Name.LocalName} {attribute.Name.LocalName}=\"{attribute.Value}\">";

            if (attribute.Name.LocalName == "Classes")
            {
                classes++;
                foreach (var retired in RetiredChromeClasses.Where(
                             retired => attribute.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                 .Contains(retired, StringComparer.Ordinal)))
                    failures.Add($"{where} carries the retired style class '{retired}'");
                continue;
            }

            if (!IsMarkupExtension(attribute)) continue;

            bindings++;
            var root = BindingRoot().Match(attribute.Value);
            if (!root.Success) continue;

            var path = root.Groups[1].Value;
            if (DeletedWizardMembers.Contains(path, StringComparer.Ordinal))
                failures.Add($"{where} binds '{path}', a member the wizard took with it");
        }

        Assert.True(classes >= 100, $"only {classes} style-class attributes scanned");
        Assert.True(bindings >= 150, $"only {bindings} bindings scanned");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // ── What the shell builds ──────────────────────────────────────────────

    /// <summary>
    /// The settings surface is a page in the shell, not a window of its own
    /// (§9, D3): every file under Views/Settings is a <see cref="UserControl"/>,
    /// no type in that namespace is a <see cref="Window"/>, and no settings
    /// window type exists anywhere in the assembly for the three retired dialogs
    /// to come back as.
    /// </summary>
    [Fact]
    public void Settings_are_a_page_and_no_settings_dialog_comes_back()
    {
        var repoRoot = InferenceSidecarService.FindRepoRoot();
        if (repoRoot is null) return;

        var settingsFiles = ViewFiles(repoRoot)
            .Where(file => file.Contains($"{Path.DirectorySeparatorChar}Settings{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.True(settingsFiles.Count > 0, "no settings view files found - the guard is not reading the views");
        foreach (var file in settingsFiles)
            Assert.True(XDocument.Load(file).Root!.Name.LocalName == "UserControl",
                $"{Relative(repoRoot, file)} is not a page: its root is not a UserControl");

        var assembly = typeof(MainWindow).Assembly;
        // The app's own types only: the Avalonia XAML compiler emits types whose
        // names carry the resource path ("…:/Views/Wizard/Step2Engine.axaml"),
        // and a folder name is not a concept.
        var types = assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("Synapic.", StringComparison.Ordinal) == true)
            .ToList();
        var windows = types.Where(t => typeof(Window).IsAssignableFrom(t)).ToList();

        // The shell and its three notices (confirm / crash / runtime) are the only
        // windows the redesign left: nothing may be named for configuration, and
        // no window may hide in the settings namespace.
        var settingsWindows = windows
            .Where(t => t.Name.Contains("Setting", StringComparison.OrdinalIgnoreCase) ||
                        t.Name.Contains("Preference", StringComparison.OrdinalIgnoreCase) ||
                        t.Namespace == "Synapic.Main.Views.Settings")
            .Select(t => t.FullName)
            .ToList();
        Assert.True(settingsWindows.Count == 0,
            "the settings surface is a page in the shell, not a window: " + string.Join(", ", settingsWindows));

        foreach (var retired in RetiredDialogTypes)
            Assert.DoesNotContain(types, t => t.Name == retired);
        Assert.DoesNotContain(types, t => t.Name.Contains("Wizard", StringComparison.Ordinal));
        Assert.DoesNotContain(types, t => t.Name is "WizardViewModel" or "DashboardView");
    }

    // ── What the user can read ─────────────────────────────────────────────

    private static MainWindowViewModel Shell()
    {
        var session = new Session();
        session.Datasource.Type = "local";
        session.Datasource.LocalPath = Path.GetTempPath();
        return new MainWindowViewModel(new FakeSidecar(), new FakeBuildService(), session,
            () => null, null, null, _ => null);
    }

    private static bool EffectivelyVisible(Control control) =>
        control.IsVisible &&
        control.GetVisualAncestors().OfType<Control>().All(ancestor => ancestor.IsVisible);

    /// <summary>The run log is a transcript, not UI text: its lines are whatever
    /// the run wrote, and none of the log views is a heading. The shared run bar
    /// owns the run's one log (RunLogList) and the settings drawer owns the
    /// diagnostic one (LogList).</summary>
    private static bool InRunLog(Control control) =>
        control.GetVisualAncestors().OfType<ListBox>()
            .Any(list => list.Name is "LogList" or "RunLogList");

    /// <summary>A path the working directory supplied rather than the app (the
    /// source summary and the config path): a checkout under a folder called
    /// "Dashboard" must not read as a retired concept.</summary>
    private static bool LooksLikeAPath(string text) =>
        text.Contains('/') || text.Contains('\\');

    private static string Describe((string Text, Control Control) pair) =>
        $"\"{pair.Text}\" in {pair.Control.GetType().Name}" +
        (string.IsNullOrEmpty(pair.Control.Name) ? "" : $"#{pair.Control.Name}");

    /// <summary>
    /// The text the user can read in each view, from the real compiled tree —
    /// every visible label and caption, including the strings a view model
    /// supplied, because a numbered heading can come back through a derived one
    /// (the deleted <c>CurrentStepTitle</c> was exactly that) just as easily as
    /// through a literal in a view. A stale <c>dashboardPanel</c> style is in
    /// the same walk: an unknown class is silent in Avalonia.
    /// </summary>
    [AvaloniaFact]
    public void No_view_renders_a_numbered_heading_or_a_retired_style_class()
    {
        var shell = Shell();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            var visited = 0;
            foreach (var (view, enter) in new (string, Action)[]
                     {
                         ("Settings view", () => { }),
                         ("Tagging view", () => shell.StartTaggingRouteCommand.Execute(null)),
                         ("Deduplication view", () => shell.StartDedupRouteCommand.Execute(null)),
                         ("Upscaling view", () => shell.StartUpscaleRouteCommand.Execute(null)),
                     })
            {
                enter();
                Dispatcher.UIThread.RunJobs();
                visited++;

                var controls = window.GetVisualDescendants().OfType<Control>().ToList();

                var texts = controls.OfType<TextBlock>()
                    .Where(t => EffectivelyVisible(t) && !string.IsNullOrWhiteSpace(t.Text) && !InRunLog(t))
                    .Select(t => (Text: t.Text!, Control: (Control)t))
                    .Concat(controls.OfType<Button>()
                        .Where(b => EffectivelyVisible(b) && b.Content is string)
                        .Select(b => (Text: (string)b.Content!, Control: (Control)b)))
                    .ToList();
                Assert.True(texts.Count > 0, $"{view}: no text on screen - the walk is not reading the real tree");

                var headings = texts.Where(pair => NumberedStepHeading().IsMatch(pair.Text))
                    .Select(Describe).ToList();
                Assert.True(headings.Count == 0,
                    $"{view}: numbered step heading on screen: {string.Join(" | ", headings)}");

                var wording = texts
                    .Where(pair => !LooksLikeAPath(pair.Text) && RetiredWording().IsMatch(pair.Text))
                    .Select(Describe).ToList();
                Assert.True(wording.Count == 0,
                    $"{view}: wording the redesign retired on screen: {string.Join(" | ", wording)}");

                var retired = controls
                    .Where(c => c.Classes.Any(RetiredChromeClasses.Contains))
                    .Select(c => $"{c.GetType().Name} (classes: {string.Join(' ', c.Classes)})")
                    .ToList();
                Assert.True(retired.Count == 0,
                    $"{view}: retired chrome on screen: {string.Join(" | ", retired)}");
            }

            Assert.Equal(4, visited);
        }
        finally
        {
            window.Close();
        }
    }
}

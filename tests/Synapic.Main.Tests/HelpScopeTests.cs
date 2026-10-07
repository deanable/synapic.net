using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.ViewModels;
using Synapic.Avalonia.Views;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// The context-aware half of the help system: HelpScope.Topic annotations in
/// the views (what F1 resolves while the focus is inside a control), and the
/// promise that every annotation points at a topic that really exists - the
/// file, its #anchor, and the Synapic.hhp [FILES] entry that puts it in the
/// compiled .chm at all.
/// </summary>
public partial class HelpScopeTests
{
    private const string Exe = "synapic-inference.exe";

    // ── How resolution works ──────────────────────────────────────────────

    [AvaloniaFact]
    public void Resolve_returns_null_when_nothing_in_scope_is_annotated()
    {
        var outer = new StackPanel();
        var inner = new StackPanel();
        var leaf = new Button();
        outer.Children.Add(inner);
        inner.Children.Add(leaf);

        Assert.Null(HelpScope.Resolve(leaf));
        Assert.Null(HelpScope.Resolve((object?)null));
    }

    [AvaloniaFact]
    public void Resolve_walks_up_to_the_nearest_annotated_ancestor()
    {
        var outer = new StackPanel();
        HelpScope.SetTopic(outer, "step1-limits.html");
        var inner = new StackPanel();
        var leaf = new Button();
        outer.Children.Add(inner);
        inner.Children.Add(leaf);

        Assert.Equal("step1-limits.html", HelpScope.Resolve(leaf));
        Assert.Equal("step1-limits.html", HelpScope.Resolve(inner));
        Assert.Equal("step1-limits.html", HelpScope.Resolve(outer));
    }

    [AvaloniaFact]
    public void A_nearest_anchored_annotation_beats_the_coarser_one()
    {
        var outer = new StackPanel();
        HelpScope.SetTopic(outer, "step2-engine.html");
        var setting = new NumericUpDown();
        HelpScope.SetTopic(setting, "settings-reference.html#confidence-threshold");
        outer.Children.Add(setting);

        Assert.Equal("settings-reference.html#confidence-threshold", HelpScope.Resolve(setting));
        Assert.Equal("step2-engine.html", HelpScope.Resolve(outer));
    }

    [AvaloniaFact]
    public void An_annotation_the_topic_filter_rejects_falls_through_to_the_next_scope_up()
    {
        // A stale annotation must degrade to the coarser scope, never silently
        // open the home page: NormalizeTopic maps both of these to Home, which
        // is not what the annotation said, so Resolve keeps walking.
        var outer = new StackPanel();
        HelpScope.SetTopic(outer, "step3-process.html");
        var inner = new StackPanel();
        HelpScope.SetTopic(inner, "notes.txt");
        var leaf = new Button();
        HelpScope.SetTopic(leaf, "sub/page.html");
        outer.Children.Add(inner);
        inner.Children.Add(leaf);

        Assert.Equal("step3-process.html", HelpScope.Resolve(leaf));
    }

    [AvaloniaFact]
    public void An_annotation_is_normalized_before_it_is_returned()
    {
        var panel = new StackPanel();
        HelpScope.SetTopic(panel, "  tips.html  ");

        Assert.Equal("tips.html", HelpScope.Resolve(panel));
    }

    // ── What F1 does with it ──────────────────────────────────────────────

    [AvaloniaFact]
    public async Task F1_opens_the_scope_of_the_focused_control_instead_of_the_fallback_topic()
    {
        // The Help button sits inside the toolbar, which is annotated with the
        // sidecar topic. With a server detected, the unscoped fallback would
        // be the Step 1 topic - so this proves the focus scope really wins.
        var help = new FakeHelpService();
        var vm = NewViewModel(help, Exe);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await vm.DetectServerAsync();

        var helpButton = window.GetVisualDescendants().OfType<Button>()
            .Single(b => (b.Content as string) == "Help");
        Assert.True(helpButton.Focus());
        Assert.Equal("step1-datasource.html", vm.ContextHelpTopic);

        window.KeyPressQwerty(PhysicalKey.F1, RawInputModifiers.None);

        Assert.Equal("first-run-sidecar.html", Assert.Single(help.OpenedTopics));
    }

    [AvaloniaFact]
    public void F1_falls_back_to_the_sidecar_topic_when_no_scope_covers_the_focus()
    {
        // exe: null keeps the setup panel gating, so the fallback is the
        // sidecar topic whether focus sits on the window or on any control
        // whose scope is also the sidecar topic - both must agree here.
        var help = new FakeHelpService();
        var vm = NewViewModel(help, exe: null);
        var window = new MainWindow { DataContext = vm };
        window.Show();

        window.KeyPressQwerty(PhysicalKey.F1, RawInputModifiers.None);

        Assert.Equal(HelpTopics.FirstRunSidecar, Assert.Single(help.OpenedTopics));
    }

    // ── What the annotations point at ─────────────────────────────────────

    [Fact]
    public void Every_HelpScope_annotation_in_the_views_points_at_a_real_topic_and_anchor()
    {
        var repoRoot = InferenceSidecarService.FindRepoRoot();
        if (repoRoot is null) return; // no source checkout (an installed app): nothing to check

        var helpDir = Path.Combine(repoRoot, "docs", HelpService.TopicsFolderName);
        var compiled = File.ReadAllText(Path.Combine(helpDir, "Synapic.hhp"));
        var viewsDir = Path.Combine(repoRoot, "src", "Synapic.Avalonia", "Views");
        var count = 0;

        foreach (var axaml in Directory.EnumerateFiles(viewsDir, "*.axaml", SearchOption.AllDirectories))
        {
            if (axaml.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;

            foreach (Match match in AnnotationRegex().Matches(File.ReadAllText(axaml)))
            {
                count++;
                var topic = match.Groups[1].Value;

                Assert.True(HelpService.NormalizeTopic(topic) == topic.Trim(),
                    $"{axaml}: HelpScope.Topic \"{topic}\" is not a plain topic file name with an optional #anchor");

                var hash = topic.IndexOf('#');
                var file = hash >= 0 ? topic[..hash] : topic;
                Assert.True(File.Exists(Path.Combine(helpDir, file)),
                    $"{axaml}: {file} does not exist in docs/help");

                if (hash >= 0)
                {
                    var body = File.ReadAllText(Path.Combine(helpDir, file));
                    var anchor = topic[(hash + 1)..];
                    var q = (char)34;
                    Assert.True(
                        body.Contains("id=" + q + anchor + q) || body.Contains("name=" + q + anchor + q),
                        $"{axaml}: {file} has no #{anchor} anchor");
                }

                Assert.True(compiled.Contains(file),
                    $"{axaml}: {file} is missing from Synapic.hhp [FILES] - it would be left out of the .chm");
            }
        }

        Assert.True(count >= 20,
            $"expected the views to carry HelpScope annotations, found only {count}");
    }

    [GeneratedRegex("HelpScope\\.Topic=\"([^\"]+)\"")]
    private static partial Regex AnnotationRegex();

    // ── Helpers ───────────────────────────────────────────────────────────

    private static MainWindowViewModel NewViewModel(IHelpService help, string? exe) =>
        new(new FakeSidecar(), new FakeBuildService(), new Session(), () => exe, null, null, _ => exe,
            null, null, help);
}

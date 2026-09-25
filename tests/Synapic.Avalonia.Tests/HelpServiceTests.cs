using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.ViewModels;
using Avalonia.VisualTree;
using Synapic.Avalonia.Views;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// The help system wiring (docs/help): where the app looks for it (the compiled
/// .chm on Windows, the same topics as HTML on every platform, a source checkout
/// as the last resort), how a topic is opened, and which topic the Help button
/// and F1 land on for what the user is looking at.
/// </summary>
public class HelpServiceTests : IDisposable
{
    private const string Exe = "synapic-inference.exe";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "synapic-help-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ── Where the payload is found ─────────────────────────────────────────

    [Fact]
    public void Windows_prefers_the_compiled_chm_and_lands_on_the_requested_topic()
    {
        var app = NewDirectory("app");
        File.WriteAllText(Path.Combine(app, "Synapic.chm"), "compiled help");
        WriteTopic(Path.Combine(app, HelpService.TopicsFolderName), "step2-engine.html");

        var launched = new List<ProcessStartInfo>();
        var help = new HelpService(app, repoRoot: "", launch: launched.Add, isWindows: true);

        Assert.True(help.IsAvailable);
        Assert.True(help.Open("step2-engine.html"));

        var start = Assert.Single(launched);
        Assert.Equal("hh.exe", start.FileName);
        Assert.Equal($"\"ms-its:{Path.Combine(app, "Synapic.chm")}::/step2-engine.html\"", start.Arguments);
        Assert.True(start.UseShellExecute);
    }

    [Fact]
    public void Windows_falls_back_to_the_html_topics_when_there_is_no_chm()
    {
        var app = NewDirectory("app");
        var topics = Path.Combine(app, HelpService.TopicsFolderName);
        WriteTopic(topics, HelpTopics.Home);

        var launched = new List<ProcessStartInfo>();
        var help = new HelpService(app, repoRoot: "", launch: launched.Add, isWindows: true);

        Assert.True(help.Open());

        var start = Assert.Single(launched);
        Assert.Equal(Path.Combine(topics, HelpTopics.Home), start.FileName);
        Assert.Equal("", start.Arguments);
    }

    [Fact]
    public void Other_platforms_never_open_the_chm()
    {
        // macOS and Linux have no .chm viewer: the file can be sitting right
        // there (a Windows publish copied into a shared artifacts directory) and
        // is still ignored in favour of the HTML topics.
        var app = NewDirectory("app");
        File.WriteAllText(Path.Combine(app, "Synapic.chm"), "compiled help");
        var topics = Path.Combine(app, HelpService.TopicsFolderName);
        WriteTopic(topics, "step1-datasource.html");

        var launched = new List<ProcessStartInfo>();
        var help = new HelpService(app, repoRoot: "", launch: launched.Add, isWindows: false);

        Assert.True(help.Open("step1-datasource.html"));

        Assert.Equal(Path.Combine(topics, "step1-datasource.html"), Assert.Single(launched).FileName);
    }

    [Fact]
    public void A_source_checkout_is_the_last_resort()
    {
        // Nothing bundled (a dev build that has not published or packaged yet),
        // but the checkout itself has the topics.
        var app = NewDirectory("app");
        var repo = NewDirectory("repo");
        var topics = Path.Combine(repo, "docs", HelpService.TopicsFolderName);
        WriteTopic(topics, HelpTopics.Home);
        WriteTopic(topics, HelpTopics.Troubleshooting);

        var launched = new List<ProcessStartInfo>();
        var help = new HelpService(app, repo, launched.Add, isWindows: true);

        Assert.True(help.IsAvailable);
        Assert.True(help.Open(HelpTopics.Troubleshooting));
        Assert.Equal(Path.Combine(topics, HelpTopics.Troubleshooting), Assert.Single(launched).FileName);
    }

    [Fact]
    public void The_bundled_copy_wins_over_the_source_checkout()
    {
        var app = NewDirectory("app");
        var repo = NewDirectory("repo");
        WriteTopic(Path.Combine(app, HelpService.TopicsFolderName), HelpTopics.Home);
        WriteTopic(Path.Combine(repo, "docs", HelpService.TopicsFolderName), HelpTopics.Home);

        var launched = new List<ProcessStartInfo>();
        var help = new HelpService(app, repo, launched.Add, isWindows: false);

        help.Open();

        Assert.Equal(Path.Combine(app, HelpService.TopicsFolderName, HelpTopics.Home),
            Assert.Single(launched).FileName);
    }

    [Fact]
    public void No_payload_anywhere_is_reported_rather_than_thrown()
    {
        var launched = new List<ProcessStartInfo>();
        var help = new HelpService(NewDirectory("app"), repoRoot: "", launch: launched.Add, isWindows: true);

        Assert.False(help.IsAvailable);
        Assert.False(help.Open(HelpTopics.Home));
        Assert.Empty(launched);
    }

    [Fact]
    public void A_target_that_will_not_start_falls_through_to_the_next()
    {
        // The realistic case: Windows has the .chm but no hh.exe registered, and
        // the HTML topics beside it still open.
        var app = NewDirectory("app");
        File.WriteAllText(Path.Combine(app, "Synapic.chm"), "compiled help");
        var topics = Path.Combine(app, HelpService.TopicsFolderName);
        WriteTopic(topics, HelpTopics.Home);

        var attempted = new List<ProcessStartInfo>();
        var help = new HelpService(app, repoRoot: "", isWindows: true, launch: psi =>
        {
            attempted.Add(psi);
            if (psi.FileName == "hh.exe") throw new InvalidOperationException("hh.exe not found");
        });

        Assert.True(help.Open());

        Assert.Equal(2, attempted.Count);
        Assert.Equal("hh.exe", attempted[0].FileName);
        Assert.Equal(Path.Combine(topics, HelpTopics.Home), attempted[1].FileName);
    }

    // ── Topic names ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, HelpTopics.Home)]
    [InlineData("", HelpTopics.Home)]
    [InlineData("   ", HelpTopics.Home)]
    [InlineData("../secrets.html", HelpTopics.Home)]
    [InlineData("sub\\page.html", HelpTopics.Home)]
    [InlineData("notes.txt", HelpTopics.Home)]
    [InlineData("  step2-engine.html  ", "step2-engine.html")]
    public void Only_plain_topic_names_survive(string? topic, string expected)
        => Assert.Equal(expected, HelpService.NormalizeTopic(topic));

    [Fact]
    public void Every_topic_the_app_names_is_in_the_help_sources_and_in_the_chm()
    {
        var repoRoot = InferenceSidecarService.FindRepoRoot();
        if (repoRoot is null) return; // no source checkout (an installed app): nothing to check

        var helpDir = Path.Combine(repoRoot, "docs", HelpService.TopicsFolderName);
        var compiled = CompiledFiles(File.ReadAllText(Path.Combine(helpDir, "Synapic.hhp")));

        foreach (var topic in TopicsTheAppCanName())
        {
            Assert.True(File.Exists(Path.Combine(helpDir, topic)),
                $"{topic} is named by the app but is not in docs/help - a renamed topic needs HelpTopics updated.");
            Assert.Contains(topic, compiled);
        }
    }

    // ── What the window opens ──────────────────────────────────────────────

    [AvaloniaFact]
    public void The_help_button_opens_the_home_page()
    {
        var help = new FakeHelpService();
        var vm = NewViewModel(help, Exe);

        vm.OpenHelpCommand.Execute(null);

        Assert.Equal(new[] { HelpTopics.Home }, help.OpenedTopics);
    }

    [AvaloniaFact]
    public async Task F1_opens_the_topic_for_the_step_on_screen()
    {
        var help = new FakeHelpService();
        var vm = NewViewModel(help, Exe);
        await vm.DetectServerAsync(); // finds the executable, so the sidecar panel is not gating

        var steps = new (ObservableObject Step, string Topic)[]
        {
            (vm.Wizard.Step1, "step1-datasource.html"),
            (vm.Wizard.Step2, "step2-engine.html"),
            (vm.Wizard.Step3, "step3-process.html"),
            (vm.Wizard.Step4, "step4-results.html"),
            (vm.Wizard.Dedup, "dedup.html"),
        };

        foreach (var (step, topic) in steps)
        {
            vm.Wizard.CurrentStep = step;
            Assert.Equal(topic, vm.ContextHelpTopic);

            vm.OpenContextHelpCommand.Execute(null);
            Assert.Equal(topic, help.OpenedTopics[^1]);
        }

        Assert.Equal(steps.Length, help.OpenedTopics.Count);
    }

    [AvaloniaFact]
    public async Task F1_opens_the_sidecar_topic_while_that_panel_is_gating()
    {
        var help = new FakeHelpService();
        var vm = NewViewModel(help, exe: null);
        await vm.DetectServerAsync();

        Assert.True(vm.IsSidecarRequired);
        Assert.Equal(HelpTopics.FirstRunSidecar, vm.ContextHelpTopic);

        vm.OpenContextHelpCommand.Execute(null);

        Assert.Equal(HelpTopics.FirstRunSidecar, Assert.Single(help.OpenedTopics));
    }

    [AvaloniaFact]
    public void F1_on_the_real_window_reaches_the_command()
    {
        // The shortcut itself lives in MainWindow.axaml's KeyBindings, and a
        // binding there has to resolve against the window's DataContext - so this
        // drives the real window with a real key press instead of trusting the
        // view model alone.
        var help = new FakeHelpService();
        var window = new MainWindow { DataContext = NewViewModel(help, exe: null) };
        window.Show();

        window.KeyPressQwerty(PhysicalKey.F1, RawInputModifiers.None);

        Assert.Equal(HelpTopics.FirstRunSidecar, Assert.Single(help.OpenedTopics));
    }

    [AvaloniaFact]
    public void The_toolbar_help_button_is_bound()
    {
        var window = new MainWindow { DataContext = NewViewModel(new FakeHelpService(), exe: null) };
        window.Show();

        var button = window.GetVisualDescendants().OfType<Button>()
            .SingleOrDefault(b => (b.Content as string) == "Help");

        // A failed {Binding} in XAML leaves Command null and looks fine until
        // someone clicks the button.
        Assert.NotNull(button);
        Assert.NotNull(button!.Command);
        Assert.True(button.Command!.CanExecute(null));
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static MainWindowViewModel NewViewModel(IHelpService help, string? exe) =>
        new(new FakeSidecar(), new FakeBuildService(), new Session(), () => exe, null, null, _ => exe,
            null, null, help);

    /// <summary>Every topic name the app can pass to IHelpService.Open.</summary>
    private static IEnumerable<string> TopicsTheAppCanName()
    {
        yield return HelpTopics.Home;
        yield return HelpTopics.FirstRunSidecar;
        yield return HelpTopics.Troubleshooting;
        for (var step = 0; step <= 4; step++) yield return HelpTopics.ForStepIndex(step);
    }

    /// <summary>The [FILES] entries of the .hhp - the list that decides what is compiled into the .chm.</summary>
    private static List<string> CompiledFiles(string hhp)
    {
        var files = new List<string>();
        var inFiles = false;

        foreach (var raw in hhp.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inFiles = line.Equals("[FILES]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (inFiles && line.Length > 0 && !line.StartsWith(';'))
                files.Add(line);
        }

        return files;
    }

    private string NewDirectory(string name) =>
        Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private void WriteTopic(string directory, string fileName, string body = "<html></html>")
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), body);
    }
}

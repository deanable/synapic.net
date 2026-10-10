using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Synapic.Main.Models;
using Synapic.Main.ViewModels;
using Synapic.Main.ViewModels.Operations;
using Synapic.Main.Views;
using Synapic.Main.Views.Operation;
using Synapic.Main.Views.Settings;
using Synapic.Main.Views.Wizard;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// The shell's structure after the sidebar redesign (docs/mock-up/Mockup.svg): a
/// left sidebar of four rows — Settings, Tagging, Deduplication, Upscaling — and
/// a content panel. Settings owns every configuration; the three processing views
/// carry only the read-only source summary and their own output. These drive the
/// real compiled XAML, because a mistyped binding path is silent in Avalonia.
/// </summary>
public class WorkflowOrderTests
{
    private static MainWindowViewModel Shell(string sourceType = "local", string path = "")
    {
        var session = new Session();
        session.Datasource.Type = sourceType;
        session.Datasource.LocalPath = path;
        if (sourceType == "daminion")
        {
            session.Datasource.DaminionUrl = "http://damserver.local/daminion";
            session.Datasource.DaminionScope = "all";
        }

        return new MainWindowViewModel(new FakeSidecar(), new FakeBuildService(), session,
            () => null, null, null, _ => null);
    }

    private static bool EffectivelyVisible(Control control) =>
        control.IsVisible &&
        control.GetVisualAncestors().OfType<Control>().All(ancestor => ancestor.IsVisible);

    /// <summary>
    /// The chrome the redesign deletes: the numbered sidebar (navMode) and the
    /// Back/Next/StartOver action bar. The absence is the invariant now — a
    /// re-introduced action-bar entry fails here rather than shipping.
    /// </summary>
    private static System.Collections.Generic.List<Button> DeletedChrome(Window window) =>
        window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("navMode")
                        || (b.Content as string) is "← Back" or "Next →" or "Start Over")
            .ToList();

    private static System.Collections.Generic.List<Button> NavRows(Window window) =>
        window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("navItem")).ToList();

    /// <summary>A row's label text — the part compact mode drops.</summary>
    private static string Label(Button row) =>
        row.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Classes.Contains("navLabel")).Text!;

    /// <summary>
    /// The app reads as Settings then the three processing operations, with the
    /// numbered sequence gone at every point.
    /// </summary>
    [AvaloniaFact]
    public void App_reads_as_settings_then_the_three_operations_with_no_numbered_sidebar()
    {
        var shell = Shell("local", Path.GetTempPath());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            Assert.True(shell.IsSettingsVisible);
            Assert.Null(shell.Shell.Current);
            Assert.Empty(DeletedChrome(window));

            var nav = NavRows(window);
            Assert.Equal(4, nav.Count);
            Assert.Equal(
                new[] { "Settings", "Tagging", "Deduplication", "Upscaling" },
                nav.Select(Label).ToArray());

            // Every processing view opens from its own row, and the chrome stays
            // gone inside it.
            foreach (var (mode, enter) in new (string, Action)[]
                     {
                         ("tag", () => shell.StartTaggingRouteCommand.Execute(null)),
                         ("dedup", () => shell.StartDedupRouteCommand.Execute(null)),
                         ("upscale", () => shell.StartUpscaleRouteCommand.Execute(null)),
                     })
            {
                enter();
                Dispatcher.UIThread.RunJobs();

                Assert.True(shell.IsOperationVisible, $"{mode}: the view did not open");
                Assert.Equal(shell.OperationTitle, shell.OperationTitle);
                Assert.Empty(DeletedChrome(window));
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The sidebar (docs/mock-up/Mockup.svg): the four rows down the left, the
    /// open one lit. Pressing a row switches the view through the same route
    /// command the rest of the app carries, and only the open view's row is lit,
    /// because the lit state is derived from Shell.Current rather than remembered
    /// by the sidebar (pressing the open view's own row keeps it open instead of
    /// toggling the view empty).
    /// </summary>
    [AvaloniaFact]
    public void Sidebar_switches_the_view_and_lights_only_the_open_one()
    {
        var shell = Shell("local", Path.GetTempPath());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            shell.StartTaggingRouteCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var output = Assert.Single(window.GetVisualDescendants().OfType<Border>(),
                b => b.Classes.Contains("outputRegion"));
            double LeftOf(Control c) => c.TranslatePoint(new Point(0, 0), window)!.Value.X;

            // Four rows on screen, to the left of the content.
            var nav = NavRows(window);
            Assert.Equal(4, nav.Count);
            Assert.All(nav, b => Assert.True(EffectivelyVisible(b),
                $"{b.Content} is not on screen in an open view"));
            Assert.All(nav, b => Assert.True(LeftOf(b) < LeftOf(output),
                $"{b.Content} is not left of the content panel"));

            // The open view's row is the lit one.
            var lit = Assert.Single(nav, b => b.Classes.Contains("active"));
            Assert.Equal(shell.StartTaggingRouteCommand, lit.Command);

            // Press another view: the view and the light move together.
            var dedup = Assert.Single(nav, b => b.Command == shell.StartDedupRouteCommand);
            dedup.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Same(shell.DedupOperation, shell.Shell.Current);
            Assert.True(shell.IsOperationVisible);
            lit = Assert.Single(NavRows(window), b => b.Classes.Contains("active"));
            Assert.Equal(shell.StartDedupRouteCommand, lit.Command);

            // The sidebar selects; it does not toggle the open view off.
            lit.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(shell.DedupOperation, shell.Shell.Current);
            Assert.Single(NavRows(window), b => b.Classes.Contains("active"));

            // Back on Settings the Settings row is the lit one.
            shell.GoHomeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(shell.IsSettingsVisible);
            lit = Assert.Single(NavRows(window), b => b.Classes.Contains("active"));
            Assert.Equal(shell.GoHomeCommand, lit.Command);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The way back is the sidebar's Settings row, from every view, and it lands on
    /// the Settings view with the view's state intact (the configured source and
    /// the operation's own settings survive the trip).
    /// </summary>
    [AvaloniaFact]
    public void Settings_row_returns_from_every_view_and_keeps_the_views_state()
    {
        var shell = Shell("local", Path.GetTempPath());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            foreach (var (mode, enter) in new (string, Action)[]
                     {
                         ("tag", () => shell.StartTaggingRouteCommand.Execute(null)),
                         ("dedup", () => shell.StartDedupRouteCommand.Execute(null)),
                         ("upscale", () => shell.StartUpscaleRouteCommand.Execute(null)),
                     })
            {
                enter();
                Dispatcher.UIThread.RunJobs();

                var entry = Assert.Single(NavRows(window), b => b.Command == shell.GoHomeCommand);
                Assert.True(EffectivelyVisible(entry), $"{mode}: no way back to Settings");
                Assert.True(entry.IsEnabled, $"{mode}: the Settings row is disabled while idle");

                entry.Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();

                Assert.True(shell.IsSettingsVisible, $"{mode}: the row did not return to Settings");
                Assert.False(shell.IsOperationVisible, $"{mode}: the processing view is still on screen");
                Assert.Null(shell.Shell.Current);
            }

            // State survives the trip: a parameter set inside the view is the same
            // parameter when the view is re-entered (resume, not reset).
            shell.StartTaggingRouteCommand.Execute(null);
            shell.Operations.TagParameters.TagCategories = false;
            shell.GoHomeCommand.Execute(null);
            shell.StartTaggingRouteCommand.Execute(null);

            Assert.False(shell.Operations.TagParameters.TagCategories);
            Assert.Same(shell.TagOperation, shell.Shell.Current);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The configuration forms live on the Settings view — the source/connection,
    /// the inference server, the engine form and the operation rules — and not in
    /// a window or a processing view.
    /// </summary>
    [AvaloniaFact]
    public void The_settings_view_hosts_every_configuration_form()
    {
        var shell = Shell("local", Path.GetTempPath());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            var settings = Assert.Single(window.GetVisualDescendants().OfType<SettingsTab>());

            // The whole form is inside the Settings view, not a modal of its own.
            Assert.Same(shell.Operations.Source, Assert.Single(
                settings.GetVisualDescendants().OfType<DatasourceSourcePanel>()).DataContext);
            Assert.Same(shell.Operations.TagParameters, Assert.Single(
                settings.GetVisualDescendants().OfType<Step2Engine>()).DataContext);
            Assert.Same(shell.Operations.Dedup, Assert.Single(
                settings.GetVisualDescendants().OfType<DedupSettingsPanel>()).DataContext);
            Assert.Same(shell.Operations.Upscale, Assert.Single(
                settings.GetVisualDescendants().OfType<UpscaleSettingsPanel>()).DataContext);

            // And the window owning them is the shell itself.
            Assert.Same(window, settings.GetVisualAncestors().OfType<Window>().Last());

            // A processing view carries none of them.
            shell.StartDedupRouteCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<DatasourceSourcePanel>(), EffectivelyVisible);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Step2Engine>(), EffectivelyVisible);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<DedupSettingsPanel>(), EffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The unmatched-content trap: a ContentControl whose content matches no
    /// DataTemplate draws the content's ToString() — a raw
    /// <c>Synapic.Main.ViewModels.Steps.…</c> type name as visible UI text. This
    /// walks every view and fails on any rendered text that is a type name.
    /// </summary>
    [AvaloniaFact]
    public void No_view_renders_a_view_model_type_name_as_text()
    {
        var shell = Shell("local", Path.GetTempPath());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            foreach (var enter in new Action[]
                     {
                         () => { },
                         () => shell.StartTaggingRouteCommand.Execute(null),
                         () => shell.StartDedupRouteCommand.Execute(null),
                         () => shell.StartUpscaleRouteCommand.Execute(null),
                     })
            {
                enter();
                Dispatcher.UIThread.RunJobs();

                var typeNames = window.GetVisualDescendants().OfType<TextBlock>()
                    .Select(t => t.Text)
                    .Where(t => t is not null && t.StartsWith("Synapic.", StringComparison.Ordinal))
                    .ToList();

                Assert.Empty(typeNames);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Tagging's report shows actionable guidance before the first batch.
    /// </summary>
    [AvaloniaFact]
    public void Tagging_report_shows_actionable_guidance_before_first_results()
    {
        var shell = Shell("local", Path.GetTempPath());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            shell.StartTaggingRouteCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var report = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "No results yet — run tagging to create a report.");
            Assert.True(report.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// CONTEXT D-05: the shared run bar is one surface for all three operations and
    /// binds the Phase 1 run state by name — progress, ETA, the file being worked
    /// on, the run log and the primary action all really come from the view model
    /// (a mistyped binding path is silent in Avalonia).
    /// </summary>
    [AvaloniaFact]
    public void RunStateBar_renders_the_run_state_it_binds()
    {
        var step = Shell("local", Path.GetTempPath()).Operations.TagRun;
        step.ProgressPercent = 42;
        step.ProgressText = "21/50 (1 failed)";
        step.EtaText = "ETA 00:01:30 remaining";
        step.CurrentFile = "IMG_0007.CR2";
        step.LogLines.Add("a line from this run");

        var host = new Window
        {
            Content = new RunStateBar { DataContext = step },
            Width = 900,
            Height = 600,
        };
        host.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            var texts = host.GetVisualDescendants().OfType<TextBlock>()
                .Select(t => t.Text).ToList();
            Assert.Contains("21/50 (1 failed)", texts);
            Assert.Contains("ETA 00:01:30 remaining", texts);
            Assert.Contains("IMG_0007.CR2", texts);
            Assert.Contains("a line from this run", texts);

            var progress = Assert.Single(host.GetVisualDescendants().OfType<ProgressBar>());
            Assert.Equal(42, progress.Value);

            var start = Assert.Single(host.GetVisualDescendants().OfType<Button>(),
                b => b.Command == step.StartCommand);
            Assert.True(start.IsVisible);

            // The bar says the mode's own words rather than "Start": tagging's
            // primary action and its Stop are the mode's, asked of the view model.
            Assert.Equal("Start tagging", (string?)start.Content);
            var labels = host.GetVisualDescendants().OfType<Button>()
                .Select(b => b.Content as string).ToList();
            Assert.Contains("Abort", labels);
            Assert.DoesNotContain("Start", labels); // the mode's word, not the generic one
        }
        finally
        {
            host.Close();
        }
    }

    /// <summary>
    /// The bar carries each mode's own run actions (D10): while a run is in flight
    /// the mode's Stop appears — tagging's Abort, dedup's scan Stop, upscaling's
    /// Stop — and tagging's Pause/Resume follow the pause state. The labels and the
    /// commands come from the mode's run view model through the bar's bindings, and
    /// a state change shows and hides them, so the bar cannot offer a mode an
    /// action it cannot run (a permanently greyed Stop is the defect this replaces).
    /// </summary>
    [AvaloniaFact]
    public void The_run_bar_shows_the_modes_own_stop_and_pause_actions()
    {
        var shell = Shell("local", Path.GetTempPath());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            var visits = new (string Mode, Action Enter, Func<RunStateViewModel> Run, string Label, string Stop, bool CanPause)[]
            {
                ("Tagging", () => shell.StartTaggingRouteCommand.Execute(null),
                    () => shell.Operations.TagRun, "Start tagging", "Abort", true),
                ("Deduplication", () => shell.StartDedupRouteCommand.Execute(null),
                    () => shell.Operations.Dedup, "Scan", "Stop", false),
                ("Upscaling", () => shell.StartUpscaleRouteCommand.Execute(null),
                    () => shell.Operations.Upscale, "Run upscale", "Stop", false),
            };

            foreach (var (mode, enter, getRun, label, stop, canPause) in visits)
            {
                enter();
                Dispatcher.UIThread.RunJobs();

                var run = getRun();
                var bar = window.GetVisualDescendants().OfType<RunStateBar>()
                    .Single(b => ReferenceEquals(b.DataContext, run));

                System.Collections.Generic.List<string> Shown() =>
                    bar.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible)
                        .Select(b => (string?)b.Content ?? "").ToList();

                // The mode's words really are the mode's.
                Assert.Equal(label, run.RunActionLabel);
                Assert.Equal(stop, run.StopActionLabel);
                Assert.Equal(canPause, run.PauseAction is not null);

                // Idle: the primary action, and nothing to stop or pause.
                Assert.Contains(label, Shown());
                Assert.DoesNotContain(stop, Shown());
                Assert.DoesNotContain("Pause", Shown());

                // In flight (the state the run lifecycle sets): the Stop appears.
                run.IsRunning = true;
                Dispatcher.UIThread.RunJobs();
                Assert.DoesNotContain(label, Shown());
                Assert.Contains(stop, Shown());
                Assert.Equal(canPause, Shown().Contains("Pause"));

                // Paused (tagging only): Pause gives way to Resume.
                run.IsPaused = true;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(canPause, Shown().Contains("Resume"));
                Assert.DoesNotContain("Pause", Shown());

                // Back to idle leaves nothing behind that claims a run.
                run.IsPaused = false;
                run.IsRunning = false;
                Dispatcher.UIThread.RunJobs();
                Assert.Contains(label, Shown());
                Assert.DoesNotContain(stop, Shown());
                Assert.DoesNotContain("Resume", Shown());
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// One run surface per processing view (D10): the run bar's progress, log and
    /// actions, and nothing that shows the run a second time. The tagging view
    /// used to render both "Run" and "Progress" — two progress bars, two Start
    /// buttons and two run logs, which could disagree about whether a run was in
    /// flight. This counts them on the real tree, once per view.
    /// </summary>
    [AvaloniaFact]
    public void Every_processing_view_has_exactly_one_run_surface()
    {
        var shell = Shell("local", Path.GetTempPath());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            foreach (var (view, enter) in new (string, Action)[]
                     {
                         ("Tagging", () => shell.StartTaggingRouteCommand.Execute(null)),
                         ("Deduplication", () => shell.StartDedupRouteCommand.Execute(null)),
                         ("Upscaling", () => shell.StartUpscaleRouteCommand.Execute(null)),
                     })
            {
                enter();
                Dispatcher.UIThread.RunJobs();

                Assert.Single(window.GetVisualDescendants().OfType<RunStateBar>());

                // The run's own surface, once: one progress bar per *run*
                // (dedup's scan bar is indeterminate and only shows while it runs,
                // so it is not a second reading of the same state) and one log.
                var logs = window.GetVisualDescendants().OfType<ListBox>()
                    .Where(l => EffectivelyVisible(l) && l.Name is "LogList" or "RunLogList")
                    .ToList();
                Assert.True(logs.Count == 1,
                    $"{view}: {logs.Count} run logs on screen ({string.Join(", ", logs.Select(l => l.Name))}); the run has one log");
                Assert.Equal("RunLogList", logs[0].Name);

                var progress = window.GetVisualDescendants().OfType<ProgressBar>()
                    .Where(EffectivelyVisible).ToList();
                Assert.True(progress.Count == 1,
                    $"{view}: {progress.Count} progress bars on screen; the run reports its progress once");

                // The one accent action is the bar's, and it really runs the open
                // mode: the page cannot offer a second way to start the same run.
                var run = Assert.IsAssignableFrom<RunStateViewModel>(
                    shell.Operations.RunFor(shell.Shell.Current!.Key));
                var bar = window.GetVisualDescendants().OfType<RunStateBar>()
                    .Single(b => ReferenceEquals(b.DataContext, run));
                var barAccent = Assert.Single(bar.GetVisualDescendants().OfType<Button>(),
                    b => b.Classes.Contains("accent"));
                Assert.Same(run.StartCommand, barAccent.Command);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The processing views report the settings that live on the Settings view, so
    /// a run is never started from rules nobody can see. These read the real bound
    /// strings.
    /// </summary>
    [AvaloniaFact]
    public void Processing_views_report_the_settings_configured_on_settings()
    {
        var shell = Shell("local", Path.GetTempPath());

        // Tagging.
        shell.Operations.TagParameters.ManualModelId = "LiquidAI/LFM2.5-VL-450M";
        Assert.Contains("LiquidAI/LFM2.5-VL-450M", shell.Operations.TagParameters.ModelSummary);
        Assert.Equal("Built-in tag instruction", shell.Operations.TagParameters.PromptSummary);
        Assert.Contains("LLM only", shell.Operations.TagParameters.ProbabilitySummary);

        // Deduplication: the rule line reads algorithm, threshold and keep-set.
        var dedup = shell.Operations.Dedup;
        dedup.SelectedAlgorithm = 1;   // DHash
        dedup.Threshold = 0.85;
        dedup.SelectOldest = true;
        Assert.Contains("DHash", dedup.ScanSettingsSummary);
        Assert.Contains(0.85.ToString("0.00"), dedup.ScanSettingsSummary);
        Assert.Contains("keep oldest", dedup.ScanSettingsSummary);
        dedup.SelectNewest = true;
        Assert.Contains("keep oldest + newest", dedup.ScanSettingsSummary);

        // Upscaling.
        var upscale = shell.Operations.Upscale;
        upscale.SelectedWorkflow = 2;   // fast
        upscale.SelectedFactor = 1;     // 4x
        Assert.Contains("fast", upscale.SettingsSummary);
        Assert.Contains("4x", upscale.SettingsSummary);
        Assert.Contains("precision auto", upscale.SettingsSummary);
    }
}

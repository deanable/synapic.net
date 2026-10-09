using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Synapic.Main.Models;
using Synapic.Main.ViewModels;
using Synapic.Main.ViewModels.Operations;
using Synapic.Main.Views;
using Synapic.Main.Views.Operation;
using Synapic.Main.Views.Wizard;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// The shell's structure after the navigation collapse (ui-design §6, decision
/// D5): the dashboard is the entry point, an operation opens straight onto the
/// three-region template, and the old numbered sequence (1 source &amp; model,
/// 2 operation type, 3 settings, then the operation's steps) exists nowhere —
/// neither as a sidebar nor as an action bar. These drive the real compiled
/// XAML, because a mistyped binding path is silent in Avalonia.
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

    /// <summary>
    /// The chrome D-01 deletes: the numbered sidebar (navItem/navMode) and the
    /// Back/Next/StartOver action bar. The absence is the invariant now — a
    /// re-introduced sidebar entry fails here rather than shipping.
    /// </summary>
    private static List<Button> DeletedChrome(Window window) =>
        window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("navItem")
                        || b.Classes.Contains("navMode")
                        || (b.Content as string) is "← Back" or "Next →" or "Start Over")
            .ToList();

    private static List<Button> RouteCards(Window window) =>
        window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("routeCard") && EffectivelyVisible(b))
            .ToList();

    private static bool EffectivelyVisible(Control control) =>
        control.IsVisible &&
        control.GetVisualAncestors().OfType<Control>().All(ancestor => ancestor.IsVisible);

    /// <summary>
    /// The dashboard reads as Settings plus the three operations, and the
    /// numbered sidebar that used to spell out "1 · Source &amp; model →
    /// 2 · Operation type → the steps of the chosen operation" is gone from the
    /// window at every point in the flow (ui-design §6, D5).
    /// </summary>
    [AvaloniaFact]
    public void Dashboard_reads_as_settings_then_the_three_operations_with_no_sidebar()
    {
        var shell = Shell("local", Path.GetTempPath());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            // On the dashboard: the Settings panel on screen, no operation open.
            Assert.True(shell.IsDashboardVisible);
            Assert.Null(shell.Shell.Current);
            Assert.Empty(DeletedChrome(window));

            var cards = RouteCards(window);
            Assert.Equal(3, cards.Count);

            // The Settings panel is one of the dashboard's panels, and the
            // operations are the other three: Tags, Dedup, Upscale.
            var panels = window.GetVisualDescendants().OfType<Control>()
                .Where(c => c.Classes.Contains("dashboardPanel")).ToList();
            Assert.Equal(4, panels.Count);
            Assert.Single(panels, p => p.Name == "SettingsPanel");
            Assert.Single(panels, p => p.Name == "TagPanel");
            Assert.Single(panels, p => p.Name == "DedupPanel");
            Assert.Single(panels, p => p.Name == "UpscalePanel");

            // Every mode still opens from its own panel, and the sidebar stays
            // absent inside the operation too — the template is the whole body.
            foreach (var (mode, enter) in new (string, Action)[]
                     {
                         ("tag", () => shell.StartTaggingRouteCommand.Execute(null)),
                         ("dedup", () => shell.StartDedupRouteCommand.Execute(null)),
                         ("upscale", () => shell.StartUpscaleRouteCommand.Execute(null)),
                     })
            {
                enter();
                Dispatcher.UIThread.RunJobs();

                Assert.True(shell.IsOperationVisible, $"{mode}: the mode did not open");
                Assert.Equal($"Dashboard / {shell.OperationTitle}", shell.Breadcrumb);
                Assert.Empty(DeletedChrome(window));
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The way back is the header's Dashboard entry, from every mode, and it
    /// lands on the dashboard with the mode's state intact (ui-design §6.2: the
    /// dashboard resumes where you left off — the configured source and the
    /// mode's own parameters survive the trip; there is no step position to
    /// survive, because there is no chain).
    /// </summary>
    [AvaloniaFact]
    public void Dashboard_entry_returns_from_every_mode_and_keeps_the_modes_state()
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

                var entry = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                    b => b.Command == shell.GoHomeCommand);
                Assert.True(EffectivelyVisible(entry), $"{mode}: no way back to the dashboard");
                Assert.True(entry.IsEnabled, $"{mode}: the dashboard entry is disabled while idle");

                entry.Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();

                Assert.True(shell.IsDashboardVisible, $"{mode}: the entry did not return to the dashboard");
                Assert.False(shell.IsOperationVisible, $"{mode}: the operation is still on screen");
                Assert.Null(shell.Shell.Current);   // D-03: the dashboard means nothing is open
            }

            // State survives the trip: a parameter set inside the mode is the
            // same parameter when the mode is re-entered (resume, not reset).
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
    /// 3 · Settings is app-wide now (ui-design §5/§6.1, D3): the shell's Settings
    /// entry point lands on the dashboard's Settings panel and never builds a
    /// window, because every operation's own parameters live inline in the
    /// Parameters region of its route (pinned by the two facts below).
    /// </summary>
    [AvaloniaFact]
    public void Settings_entry_point_opens_the_settings_panel_and_never_a_window()
    {
        var shell = Shell("local", Path.GetTempPath());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            var buttons = window.GetVisualDescendants().OfType<Button>().ToList();

            // On the dashboard exactly one Settings entry is on screen — the
            // header shortcut — and it is wired (a broken binding leaves Command
            // null, which looks fine until somebody clicks it).
            var entry = Assert.Single(buttons.Where(EffectivelyVisible),
                b => b.Command == shell.SettingsCommand);
            Assert.NotNull(entry.Command);
            Assert.True(entry.Command!.CanExecute(null));

            // From inside an operation the shortcut goes to the same place: the
            // dashboard's Settings panel — app-wide settings, one home.
            shell.StartDedupRouteCommand.Execute(null);
            Assert.True(shell.IsOperationVisible);

            shell.SettingsCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(shell.IsDashboardVisible);
            Assert.Null(shell.Shell.Current);
            var settingsPanel = Assert.Single(window.GetVisualDescendants().OfType<Control>(),
                c => c.Name == "SettingsPanel");
            Assert.True(EffectivelyVisible(settingsPanel));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// ui-design §10 criterion 4 (with D-02): each operation's parameters are
    /// reachable inline in the Parameters region of its own route — the region
    /// really holds that mode's form, bound to the mode's own view model, with no
    /// window of its own. Entering the mode is enough to see it: the mode has no
    /// settings step to walk through first (D5).
    /// </summary>
    [AvaloniaFact]
    public void Every_route_renders_its_parameters_inline_in_the_parameters_region()
    {
        // Tagging: the whole engine form (model list, device, tag fields, scoring
        // and prompts) is the Parameters region.
        var tagging = Shell("local", Path.GetTempPath());
        var taggingWindow = new MainWindow { DataContext = tagging };
        taggingWindow.Show();
        try
        {
            tagging.StartTaggingRouteCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var form = Assert.Single(taggingWindow.GetVisualDescendants().OfType<Step2Engine>());
            Assert.Same(tagging.Operations.TagParameters, form.DataContext);
            Assert.Contains(form, ParametersRegion(taggingWindow).GetVisualDescendants()
                .OfType<Control>());
            Assert.Contains(form.GetVisualDescendants().OfType<CheckBox>(),
                c => c.Name == "TagCategoriesBox");
        }
        finally
        {
            taggingWindow.Close();
        }

        // Deduplication: the scan rules and keep-set rules panel.
        var dedup = Shell("daminion", Path.GetTempPath());
        var dedupWindow = new MainWindow { DataContext = dedup };
        dedupWindow.Show();
        try
        {
            dedup.StartDedupRouteCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var panel = Assert.Single(dedupWindow.GetVisualDescendants().OfType<DedupSettingsPanel>());
            Assert.Same(dedup.Operations.Dedup, panel.DataContext);
            Assert.Contains(panel, ParametersRegion(dedupWindow).GetVisualDescendants()
                .OfType<Control>());
            Assert.NotEmpty(panel.GetVisualDescendants().OfType<NumericUpDown>());
        }
        finally
        {
            dedupWindow.Close();
        }

        // Upscaling: the workflow, factor, precision and output panel.
        var upscale = Shell("daminion", Path.GetTempPath());
        var upscaleWindow = new MainWindow { DataContext = upscale };
        upscaleWindow.Show();
        try
        {
            upscale.StartUpscaleRouteCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var panel = Assert.Single(upscaleWindow.GetVisualDescendants().OfType<UpscaleSettingsPanel>());
            Assert.Same(upscale.Operations.Upscale, panel.DataContext);
            Assert.Contains(panel, ParametersRegion(upscaleWindow).GetVisualDescendants()
                .OfType<Control>());
            Assert.Equal(4, panel.GetVisualDescendants().OfType<ComboBox>().Count());
        }
        finally
        {
            upscaleWindow.Close();
        }
    }

    private static Border ParametersRegion(Window window) =>
        Assert.Single(window.GetVisualDescendants().OfType<Border>(),
            b => b.Classes.Contains("parametersRegion"));

    /// <summary>
    /// The model picker belongs to the operation that loads a model (ui-design §8:
    /// the model picker lands in Tag), not to the dashboard: the app-wide Settings
    /// panel carries no model choice, and the tagging Parameters region is the one
    /// place a model is chosen — over the engine's own view model, so the model
    /// chosen there is the model a run loads. (The compact picker that used to sit
    /// on step 1 duplicated this form and is gone.)
    /// </summary>
    [AvaloniaFact]
    public void Tagging_model_picker_lives_in_the_parameters_region_not_the_dashboard()
    {
        var shell = Shell("local", Path.GetTempPath());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            // Dashboard: no model list anywhere — the model is an operation
            // parameter, and the Settings panel is app-wide settings only.
            var settingsPanel = Assert.Single(window.GetVisualDescendants().OfType<Control>(),
                c => c.Name == "SettingsPanel");
            Assert.Empty(settingsPanel.GetVisualDescendants().OfType<ListBox>());

            shell.StartTaggingRouteCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            // The tagging Parameters region hosts it: a model list bound to the
            // one engine view model (the same view model the run loads from).
            var form = Assert.Single(window.GetVisualDescendants().OfType<Step2Engine>());
            Assert.Same(shell.Operations.TagParameters, form.DataContext);

            var list = Assert.Single(form.GetVisualDescendants().OfType<ListBox>());
            Assert.Same(shell.Operations.TagParameters.LocalModels, list.ItemsSource);
            Assert.Contains(list, ParametersRegion(window).GetVisualDescendants().OfType<Control>());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The engine form has exactly one home: the Parameters region. The run
    /// region reports the run and never re-asks for the settings — no tag-field
    /// checkbox, no model list, no prompt box — so the form is never the only
    /// view of the state it describes nor a second editable copy of it.
    /// </summary>
    [AvaloniaFact]
    public void The_engine_form_has_one_editable_home_in_the_parameters_region()
    {
        var shell = Shell("local", Path.GetTempPath());
        shell.Operations.TagParameters.TagCategories = false;
        shell.StartTaggingRouteCommand.Execute(null);

        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            // Editable in Parameters…
            var form = Assert.Single(window.GetVisualDescendants().OfType<Step2Engine>());
            Assert.Same(shell.Operations.TagParameters, form.DataContext);
            Assert.Contains(form.GetVisualDescendants().OfType<CheckBox>(),
                c => c.Name == "TagCategoriesBox");

            // …and the run region shows no second copy of that form.
            var run = Assert.Single(window.GetVisualDescendants().OfType<Step3Process>());
            Assert.Same(shell.Operations.TagRun, run.DataContext);
            Assert.Empty(run.GetVisualDescendants().OfType<CheckBox>());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// ui-design §10 criterion 2 — one layout, three modes: walking Tag, Dedup and
    /// Upscale finds the same three regions in the same order, Data source →
    /// Parameters → Output, and the source Region A renders is the one shared
    /// instance on every route.
    /// </summary>
    [AvaloniaFact]
    public void All_three_modes_render_the_same_three_regions_in_order()
    {
        var shell = Shell("local", Path.GetTempPath());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            foreach (var (mode, enter) in new (string, Action)[]
                     {
                         ("Tag", () => shell.StartTaggingRouteCommand.Execute(null)),
                         ("Dedup", () => shell.StartDedupRouteCommand.Execute(null)),
                         ("Upscale", () => shell.StartUpscaleRouteCommand.Execute(null)),
                     })
            {
                enter();
                // The route just became visible: let the template realize its
                // regions before reading the tree.
                Dispatcher.UIThread.RunJobs();

                var tree = window.GetVisualDescendants().ToList();
                var dataSource = Assert.Single(tree.OfType<Border>(),
                    b => b.Classes.Contains("dataSourceRegion"));
                var parameters = Assert.Single(tree.OfType<Border>(),
                    b => b.Classes.Contains("parametersRegion"));
                var output = Assert.Single(tree.OfType<Border>(),
                    b => b.Classes.Contains("outputRegion"));

                // Order, not mere existence (criterion 2 says in order, on every route).
                Assert.True(tree.IndexOf(dataSource) < tree.IndexOf(parameters),
                    $"{mode}: the data source region is not before the parameters region");
                Assert.True(tree.IndexOf(parameters) < tree.IndexOf(output),
                    $"{mode}: the parameters region is not before the output region");

                // Region A is the one shared source on every mode, not a per-mode copy.
                Assert.Same(shell.Operations.Source, Assert.Single(
                    window.GetVisualDescendants().OfType<DatasourceSourcePanel>()).DataContext);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The unmatched-content trap: a ContentControl whose content matches no
    /// DataTemplate draws the content's ToString() — a raw
    /// <c>Synapic.Main.ViewModels.Steps.…</c> type name as visible UI text. The
    /// Parameters region and the run-bar host legitimately stay empty for content
    /// that has no view yet, so this walks all three modes and fails on any
    /// rendered text that is a type name. A mode that opens with content and no
    /// template fails here instead of shipping.
    /// </summary>
    [AvaloniaFact]
    public void No_mode_renders_a_view_model_type_name_as_text()
    {
        var shell = Shell("local", Path.GetTempPath());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            foreach (var (mode, enter) in new (string, Action)[]
                     {
                         ("Tag", () => shell.StartTaggingRouteCommand.Execute(null)),
                         ("Dedup", () => shell.StartDedupRouteCommand.Execute(null)),
                         ("Upscale", () => shell.StartUpscaleRouteCommand.Execute(null)),
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
    /// CONTEXT D-05: the shared run bar is one surface for all three modes and
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

            // The one accent action of the region is this step's own Start command,
            // and it is offered while the run is idle.
            var start = Assert.Single(host.GetVisualDescendants().OfType<Button>(),
                b => b.Command == step.StartCommand);
            Assert.True(start.IsVisible);
        }
        finally
        {
            host.Close();
        }
    }

    /// <summary>
    /// The run surfaces report the settings that moved into Parameters, so a run
    /// is never started from rules nobody can see. These read the real bound
    /// strings.
    /// </summary>
    [AvaloniaFact]
    public void Run_pages_report_the_settings_that_moved_to_the_parameters_region()
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

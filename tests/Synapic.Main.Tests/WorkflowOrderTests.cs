using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Synapic.Main.Models;
using Synapic.Main.ViewModels;
using Synapic.Main.ViewModels.Steps;
using Synapic.Main.Views;
using Synapic.Main.Views.Operation;
using Synapic.Main.Views.Wizard;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// The workflow as a systematic sequence, which is the whole point of the
/// redesign: 1 source &amp; model, 2 operation type, 3 the operation's settings
/// (on a dialog), then the steps that operation runs. These drive the real
/// compiled XAML — a mistyped binding path is silent in Avalonia.
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

    private static List<string> SidebarEntries(Window window) =>
        window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("navItem") || b.Classes.Contains("navMode"))
            .Where(EffectivelyVisible)
            .Select(b => b.Content as string ?? "")
            .ToList();

    private static bool EffectivelyVisible(Control control) =>
        control.IsVisible &&
        control.GetVisualAncestors().OfType<Control>().All(ancestor => ancestor.IsVisible);

    [AvaloniaFact]
    public void Sidebar_reads_as_the_numbered_setup_then_the_steps_of_the_chosen_operation()
    {
        var shell = Shell("local", Path.GetTempPath());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            // On the start screen the setup pipeline is on screen (as the page
            // itself), and no operation has been chosen yet.
            Assert.Equal(new[] { "2 · Operation type" }, SidebarEntries(window));

            shell.StartTaggingRouteCommand.Execute(null);
            Assert.Equal(new[]
            {
                "1 · Source & model",
                "2 · Operation type",
                "3 · Settings…",
                "🏷  Tagging",
                "4 · Process",
                "5 · Results",
                "🧹  Dedup",
                "✨  Upscale",
            }, SidebarEntries(window));

            shell.StartDedupRouteCommand.Execute(null);
            Assert.Equal(new[]
            {
                "1 · Source & model",
                "2 · Operation type",
                "3 · Settings…",
                "🏷  Tagging",
                "🧹  Dedup",
                "4 · Deduplication",
                "✨  Upscale",
            }, SidebarEntries(window));

            shell.StartUpscaleRouteCommand.Execute(null);
            Assert.Equal(new[]
            {
                "1 · Source & model",
                "2 · Operation type",
                "3 · Settings…",
                "🏷  Tagging",
                "🧹  Dedup",
                "✨  Upscale",
                "4 · Upscaling",
            }, SidebarEntries(window));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// 2 · Operation type is the way back to the dashboard, from every step of
    /// the operation on screen — the dashboard is one click away wherever the
    /// user is, and the entry is marked as the one they are on.
    /// </summary>
    [AvaloniaFact]
    public void Operation_entry_returns_to_the_dashboard_from_any_step()
    {
        var shell = Shell("local", Path.GetTempPath());
        var steps = new (string Name, int Index, Action<WizardViewModel> Go)[]
        {
            ("step 1 source & model", 0, _ => { }),
            ("step 2 settings", 1, w => w.GoToStep2Command.Execute(null)),
            ("step 3 process", 2, w => w.GoToStep3Command.Execute(null)),
            ("step 4 results", 3, w => w.GoToStep4Command.Execute(null)),
        };

        foreach (var (name, index, go) in steps)
        {
            shell.StartTaggingRouteCommand.Execute(null);
            go(shell.Wizard);
            Assert.Equal(index, shell.Wizard.CurrentStepIndex);   // the fact really left the source step

            shell.GoHomeCommand.Execute(null);

            Assert.True(shell.IsHomeVisible, $"{name}: the operation entry did not return to the dashboard");
            Assert.False(shell.IsWizardVisible, $"{name}: the wizard is still on screen after the operation entry");
            Assert.True(shell.IsNavHomeActive, $"{name}: the dashboard entry is not marked active");
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
            Assert.True(shell.IsWizardVisible);

            shell.SettingsCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(shell.IsHomeVisible);
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
    /// window of its own. These are the three forms the retired dialogs hosted.
    /// </summary>
    [AvaloniaFact]
    public async Task Every_route_renders_its_parameters_inline_in_the_parameters_region()
    {
        // Tagging: the whole engine form (model list, device, tag fields, scoring
        // and prompts) is the Parameters region.
        var tagging = Shell("local", Path.GetTempPath());
        var taggingWindow = new MainWindow { DataContext = tagging };
        taggingWindow.Show();
        try
        {
            tagging.StartTaggingRouteCommand.Execute(null);
            tagging.Wizard.GoToStep2Command.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var form = Assert.Single(taggingWindow.GetVisualDescendants().OfType<Step2Engine>());
            Assert.Same(tagging.Wizard.Step2, form.DataContext);
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
            await dedup.Wizard.NextCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            var panel = Assert.Single(dedupWindow.GetVisualDescendants().OfType<DedupSettingsPanel>());
            Assert.Same(dedup.Wizard.Dedup, panel.DataContext);
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
            await upscale.Wizard.NextCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            var panel = Assert.Single(upscaleWindow.GetVisualDescendants().OfType<UpscaleSettingsPanel>());
            Assert.Same(upscale.Wizard.Upscale, panel.DataContext);
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
    /// 1 · Source &amp; model renders the model picker over the engine's own view
    /// model: the start screen and step 1 both do, so a model chosen anywhere is
    /// the model a run loads.
    /// </summary>
    [AvaloniaFact]
    public void Source_and_model_step_renders_the_model_picker_over_the_engine_view_model()
    {
        var shell = Shell("local", Path.GetTempPath());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            // Start screen: the picker is inside the "1 · Source & model" card.
            var onHome = window.GetVisualDescendants().OfType<EngineModelPicker>().ToList();
            Assert.Single(onHome);
            Assert.Same(shell.Wizard.Step2, onHome[0].DataContext);

            shell.StartTaggingRouteCommand.Execute(null);
            var onStep1 = window.GetVisualDescendants().OfType<EngineModelPicker>().ToList();
            Assert.Single(onStep1);
            Assert.Same(shell.Wizard.Step2, onStep1[0].DataContext);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The settings page reports them back instead of re-asking: it carries no
    /// engine form of its own (no tag-field checkbox, no text box), while the same
    /// view model's form is one region up in Parameters — so the summary is never
    /// the only view of the state it describes.
    /// </summary>
    [AvaloniaFact]
    public void Tagging_settings_page_summarizes_the_form_that_lives_in_the_parameters_region()
    {
        var shell = Shell("local", Path.GetTempPath());
        shell.Wizard.Step2.TagCategories = false;
        shell.StartTaggingRouteCommand.Execute(null);
        shell.Wizard.GoToStep2Command.Execute(null);
        Assert.Same(shell.Wizard.Step2, shell.Wizard.CurrentStep);

        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            var page = Assert.Single(window.GetVisualDescendants().OfType<Step2TagSettings>());
            Assert.Same(shell.Wizard.Step2, page.DataContext);
            Assert.Empty(page.GetVisualDescendants().OfType<CheckBox>());
            Assert.Empty(page.GetVisualDescendants().OfType<TextBox>());

            var summary = shell.Wizard.Step2.TagFieldSummary;
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text == summary);

            // That same state is editable in the Parameters region of this window.
            var form = Assert.Single(window.GetVisualDescendants().OfType<Step2Engine>());
            Assert.Contains(form.GetVisualDescendants().OfType<CheckBox>(),
                c => c.Name == "TagCategoriesBox");
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
                Assert.Same(shell.Wizard.Step1, Assert.Single(
                    window.GetVisualDescendants().OfType<DataSourceStrip>()).DataContext);
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
    /// Parameters region and the run-bar host legitimately stay empty for the
    /// steps that have no panel yet, so this walks every step of all three routes
    /// and fails on any rendered text that is a type name. A future step view
    /// model that lands without a template fails here instead of shipping.
    /// </summary>
    [AvaloniaFact]
    public async Task No_step_renders_a_view_model_type_name_as_text()
    {
        var shell = Shell("local", Path.GetTempPath());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            var visits = new List<(string Step, Func<Task> Go)>
            {
                ("Tag step 1", () => Task.CompletedTask),
                ("Tag step 2", () => { shell.Wizard.GoToStep2Command.Execute(null); return Task.CompletedTask; }),
                ("Tag step 3", () => { shell.Wizard.GoToStep3Command.Execute(null); return Task.CompletedTask; }),
                ("Dedup step 1", () => { shell.StartDedupRouteCommand.Execute(null); return Task.CompletedTask; }),
                ("Dedup step 2", () => shell.Wizard.NextCommand.ExecuteAsync(null)),
                ("Upscale step 1", () => { shell.StartUpscaleRouteCommand.Execute(null); return Task.CompletedTask; }),
                ("Upscale step 2", () => shell.Wizard.NextCommand.ExecuteAsync(null)),
            };

            shell.StartTaggingRouteCommand.Execute(null);
            foreach (var (step, go) in visits)
            {
                await go();
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
        var step = Shell("local", Path.GetTempPath()).Wizard.Step3;
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
    /// The pages that lost their settings report them back, so a run is never
    /// started from rules nobody can see. These read the real bound strings.
    /// </summary>
    [AvaloniaFact]
    public void Run_pages_report_the_settings_that_moved_to_the_parameters_region()
    {
        var shell = Shell("local", Path.GetTempPath());

        // Tagging.
        shell.Wizard.Step2.ManualModelId = "LiquidAI/LFM2.5-VL-450M";
        Assert.Contains("LiquidAI/LFM2.5-VL-450M", shell.Wizard.Step2.ModelSummary);
        Assert.Equal("Built-in tag instruction", shell.Wizard.Step2.PromptSummary);
        Assert.Contains("LLM only", shell.Wizard.Step2.ProbabilitySummary);

        // Deduplication: the rule line reads algorithm, threshold and keep-set.
        var dedup = shell.Wizard.Dedup;
        dedup.SelectedAlgorithm = 1;   // DHash
        dedup.Threshold = 0.85;
        dedup.SelectOldest = true;
        Assert.Contains("DHash", dedup.ScanSettingsSummary);
        Assert.Contains(0.85.ToString("0.00"), dedup.ScanSettingsSummary);
        Assert.Contains("keep oldest", dedup.ScanSettingsSummary);
        dedup.SelectNewest = true;
        Assert.Contains("keep oldest + newest", dedup.ScanSettingsSummary);

        // Upscaling.
        var upscale = shell.Wizard.Upscale;
        upscale.SelectedWorkflow = 2;   // fast
        upscale.SelectedFactor = 1;     // 4x
        Assert.Contains("fast", upscale.SettingsSummary);
        Assert.Contains("4x", upscale.SettingsSummary);
        Assert.Contains("precision auto", upscale.SettingsSummary);
    }
}

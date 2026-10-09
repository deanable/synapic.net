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
using Synapic.Main.Views.Settings;
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
    /// 2 · Operation type is the start screen, and one click enters an operation
    /// at its first step; the three cards are the chooser the user asked for.
    /// </summary>
    [AvaloniaFact]
    public void Operation_entry_returns_to_the_chooser_from_any_step()
    {
        var shell = Shell("local", Path.GetTempPath());
        shell.StartTaggingRouteCommand.Execute(null);
        shell.Wizard.GoToStep3Command.Execute(null);
        Assert.Equal(2, shell.Wizard.CurrentStepIndex);

        shell.GoHomeCommand.Execute(null);

        Assert.True(shell.IsHomeVisible);
        Assert.False(shell.IsWizardVisible);
        Assert.True(shell.IsNavHomeActive);
    }

    /// <summary>
    /// 3 · Settings is per operation: the shell opens the dialog that owns the
    /// settings of the operation on screen, over that operation's own view model.
    /// </summary>
    [AvaloniaFact]
    public void Settings_opens_the_dialog_of_the_operation_on_screen()
    {
        var shell = Shell("local", Path.GetTempPath());

        // Nothing chosen yet: the chooser defaults to the tagging settings.
        var onHome = shell.CreateSettingsDialog();
        Assert.IsType<EngineSettingsDialog>(onHome);
        Assert.Same(shell.Wizard.Step2, onHome.DataContext);
        onHome.Close();

        shell.StartTaggingRouteCommand.Execute(null);
        var tagging = shell.CreateSettingsDialog();
        Assert.IsType<EngineSettingsDialog>(tagging);
        Assert.Same(shell.Wizard.Step2, tagging.DataContext);
        tagging.Close();

        shell.StartDedupRouteCommand.Execute(null);
        var dedup = shell.CreateSettingsDialog();
        Assert.IsType<DedupSettingsDialog>(dedup);
        Assert.Same(shell.Wizard.Dedup, dedup.DataContext);
        dedup.Close();

        shell.StartUpscaleRouteCommand.Execute(null);
        var upscale = shell.CreateSettingsDialog();
        Assert.IsType<UpscaleSettingsDialog>(upscale);
        Assert.Same(shell.Wizard.Upscale, upscale.DataContext);
        upscale.Close();
    }

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
    /// Settings live on the dialog, and the pages report them back instead of
    /// re-asking: the tagging settings page carries no engine form (no tag-field
    /// checkbox, no model list), while the dialog does.
    /// </summary>
    [AvaloniaFact]
    public void Tagging_settings_page_is_a_summary_and_the_dialog_holds_the_form()
    {
        var shell = Shell("local", Path.GetTempPath());
        shell.Wizard.Step2.TagCategories = false;

        var page = new Step2TagSettings { DataContext = shell.Wizard.Step2 };
        var host = new Window { Content = page, Width = 1000, Height = 700 };
        host.Show();
        try
        {
            Assert.Empty(page.GetVisualDescendants().OfType<CheckBox>());
            Assert.Empty(page.GetVisualDescendants().OfType<TextBox>());

            var summary = shell.Wizard.Step2.TagFieldSummary;
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text == summary);
        }
        finally
        {
            host.Close();
        }

        var dialog = new EngineSettingsDialog { DataContext = shell.Wizard.Step2 };
        dialog.Show();
        try
        {
            Assert.NotEmpty(dialog.GetVisualDescendants().OfType<CheckBox>());
            Assert.NotNull(dialog.GetVisualDescendants().OfType<CheckBox>()
                .SingleOrDefault(c => c.Name == "TagCategoriesBox"));
        }
        finally
        {
            dialog.Close();
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
    public void Run_pages_report_the_settings_that_moved_to_the_dialog()
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

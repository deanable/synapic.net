using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Synapic.Main.Models;
using Synapic.Main.ViewModels;
using Synapic.Main.ViewModels.Operations;
using Synapic.Main.ViewModels.Steps;
using Synapic.Main.Views;
using Xunit;

using Synapic.Core.Services;
namespace Synapic.Main.Tests;

/// <summary>
/// The tagging run's own gate (docs/ui-design.md D6): the action button carries
/// the gate, so a tag-field selection that cannot produce a run disables Start.
/// The wizard's half of this gate (a blocked Next) is gone with the step chain.
/// </summary>
public class OperationGatingTests
{
    private static Session ReadySession()
    {
        var session = new Session();
        session.Datasource.Type = "local";
        session.Datasource.LocalPath = Path.GetTempPath();
        session.Engine.ModelId = "LiquidAI/LFM2.5-VL-450M";
        return session;
    }

    [Fact]
    public void Tagging_run_stays_disabled_until_at_least_one_tag_field_is_on()
    {
        var session = ReadySession();
        var host = new OperationShellViewModel(session, new FakeSidecar { IsRunning = true });

        Assert.True(host.TagRun.StartCommand.CanExecute(null));

        // Uncheck all three: there is nothing to tag, so the run locks — and the
        // locked button names the setting rather than leaving the user to guess.
        host.TagParameters.TagKeywords = false;
        host.TagParameters.TagCategories = false;
        host.TagParameters.TagDescription = false;
        Assert.False(host.TagRun.StartCommand.CanExecute(null));
        Assert.Equal("Select at least one tag field in the tagging settings", host.TagRun.StartBlockedReason);

        // Re-checking any one re-enables it, and the reason goes away with it.
        host.TagParameters.TagDescription = true;
        Assert.True(host.TagRun.StartCommand.CanExecute(null));
        Assert.Null(host.TagRun.StartBlockedReason);
    }

    [Fact]
    public void Tagging_run_stays_disabled_until_the_source_exists_and_the_server_runs()
    {
        var session = ReadySession();
        session.Datasource.LocalPath = Path.Combine(Path.GetTempPath(), $"synapic-missing-{Guid.NewGuid():N}");
        var sidecar = new FakeSidecar { IsRunning = true };
        var host = new OperationShellViewModel(session, sidecar);

        // A typed path that does not resolve is not a source: the batch would
        // fail on its first fetch, so Start stays off and says so.
        Assert.False(host.TagRun.StartCommand.CanExecute(null));
        Assert.Equal("Choose an existing folder in Settings", host.TagRun.StartBlockedReason);

        host.Source.LocalPath = Path.GetTempPath();
        Assert.True(host.TagRun.StartCommand.CanExecute(null));

        // Inference needs a live server; a stopped one disables the run rather
        // than failing every item once it starts.
        sidecar.IsRunning = false;
        sidecar.RaiseStatus(Synapic.Core.Services.SidecarStatus.Stopped);
        Assert.False(host.TagRun.StartCommand.CanExecute(null));
        Assert.Equal("Start the inference server in Settings", host.TagRun.StartBlockedReason);

        sidecar.IsRunning = true;
        sidecar.RaiseStatus(Synapic.Core.Services.SidecarStatus.Ready);
        Assert.True(host.TagRun.StartCommand.CanExecute(null));
        Assert.Null(host.TagRun.StartBlockedReason);
    }

    [Fact]
    public void Tagging_gate_names_the_daminion_connection_and_the_missing_model()
    {
        // A catalog source that is selected but not connected: "pick a scope"
        // would be wrong, because there is nothing to pick one from yet.
        var catalog = new Session();
        catalog.Datasource.Type = "daminion";
        var catalogSource = new Step1DatasourceViewModel(catalog);
        var catalogRun = new Step3ProcessViewModel(catalog, new FakeSidecar { IsRunning = true }, catalogSource);

        Assert.False(catalogRun.StartCommand.CanExecute(null));
        Assert.Equal("Connect to the Daminion server in Settings, then pick a scope", catalogRun.StartBlockedReason);

        // A usable local source with no model: the batch has nowhere to send the
        // items, so Start stays off and names the setting that is missing.
        var noModel = ReadySession();
        noModel.Engine.ModelId = "";
        var localSource = new Step1DatasourceViewModel(noModel);
        var localRun = new Step3ProcessViewModel(noModel, new FakeSidecar { IsRunning = true }, localSource);

        Assert.False(localRun.StartCommand.CanExecute(null));
        Assert.Equal("Choose a model in the tagging settings", localRun.StartBlockedReason);
    }

    [Fact]
    public void Opening_the_tagging_mode_is_never_blocked_by_the_tag_fields()
    {
        // Clearing every tag field leaves the mode open and its parameters on
        // screen: entering is free (D6) — the gate belongs to the run, and the run
        // is what reports it.
        var host = new OperationShellViewModel(ReadySession(), new FakeSidecar());

        host.TagParameters.TagKeywords = false;
        host.TagParameters.TagCategories = false;
        host.TagParameters.TagDescription = false;

        Assert.Same(host.TagParameters, host.ParametersFor(OperationShellViewModel.TagKey));
        Assert.False(host.TagRun.StartCommand.CanExecute(null));
        Assert.False(host.IsRunning);
    }

    [Fact]
    public void Matching_scan_waits_for_an_existing_folder_and_no_apply()
    {
        var host = new OperationShellViewModel(ReadySession(), new FakeSidecar { IsRunning = true });
        var scan = host.Dedup;

        Assert.True(scan.ScanCommand.CanExecute(null));
        Assert.Null(scan.StartBlockedReason);

        // A folder that no longer exists is not a source the scan can read.
        host.Source.LocalPath = Path.Combine(Path.GetTempPath(), $"synapic-gone-{Guid.NewGuid():N}");
        Assert.False(scan.ScanCommand.CanExecute(null));
        Assert.Contains("no longer exists", scan.StartBlockedReason);

        host.Source.LocalPath = Path.GetTempPath();
        Assert.True(scan.ScanCommand.CanExecute(null));

        // Applying rewrites the rows the scan would rebuild: no second pass.
        scan.IsApplying = true;
        Assert.False(scan.ScanCommand.CanExecute(null));
        Assert.Equal("An apply is in progress", scan.StartBlockedReason);
        scan.IsApplying = false;
        Assert.True(scan.ScanCommand.CanExecute(null));
    }

    [Fact]
    public void A_pass_in_any_operation_locks_every_other_one_out()
    {
        var host = new OperationShellViewModel(ReadySession(), new FakeSidecar { IsRunning = true });
        Assert.False(host.IsRunning);

        // A matching scan is a run too: the shell has to lock navigation for it,
        // or a second operation could be opened and started over the same source.
        host.Dedup.IsRunning = true;
        Assert.True(host.IsRunning);
        host.Dedup.IsRunning = false;

        host.Similar.IsApplying = true;
        Assert.True(host.IsRunning);
        host.Similar.IsApplying = false;

        host.Upscale.IsRunning = true;
        Assert.True(host.IsRunning);
        host.Upscale.IsRunning = false;
        Assert.False(host.IsRunning);
    }

    [AvaloniaFact]
    public void A_disabled_action_button_says_which_setting_is_missing()
    {
        var session = ReadySession();
        session.Datasource.LocalPath = "";
        var vm = new MainWindowViewModel(
            new FakeSidecar { IsRunning = true }, session, () => null, null, null, _ => null);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            vm.StartTaggingRouteCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            // The gate lives on the run bar: the reason is rendered right beside
            // the greyed Start, so the button is never a dead end.
            Assert.Contains("Choose an existing folder in Settings", VisibleTexts(window));

            vm.Operations.Source.LocalPath = Path.GetTempPath();
            Dispatcher.UIThread.RunJobs();

            // The source is in place, so the hint disappears with the gate.
            Assert.DoesNotContain("Choose an existing folder in Settings", VisibleTexts(window));
            Assert.True(vm.Operations.TagRun.StartCommand.CanExecute(null));
        }
        finally
        {
            window.Close();
        }
    }

    private static List<string?> VisibleTexts(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsVisible)
            .Select(t => t.Text)
            .ToList();
}

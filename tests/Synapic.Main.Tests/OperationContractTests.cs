using System.ComponentModel;
using Synapic.Main.Models;
using Synapic.Main.Services;
using Synapic.Main.ViewModels.Operations;
using Synapic.Main.ViewModels.Steps;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// Pins the Phase 1 operation contract (docs/ui-refactor-plan.md §3, CONTEXT
/// D-04/D-05): the exact members the shared operation template binds, the rule
/// that no mode owns a datasource, and the thin-adapter rule that every adapter
/// delegates to the step view model it wraps instead of copying its state.
/// </summary>
public class OperationContractTests
{
    private static readonly string[] ContractMembers =
    {
        "Description",
        "HelpTopic",
        "IsRunEnabled",
        "Key",
        "ParametersSummary",
        "Report",
        "Run",
        "RunDisabledReason",
        "Title",
    };

    private static (TagOperationViewModel Tag, DedupOperationViewModel Dedup, UpscaleOperationViewModel Upscale) Adapters()
    {
        var session = new Session();
        var step1 = new Step1DatasourceViewModel(session);
        var step3 = new Step3ProcessViewModel(session, new FakeSidecar(), step1);
        return (
            new TagOperationViewModel(step3),
            new DedupOperationViewModel(new StepDedupViewModel()),
            new UpscaleOperationViewModel(new StepUpscaleViewModel()));
    }

    // ── The contract shape (D-04) ───────────────────────────────────────────

    [Fact]
    public void The_contract_exposes_exactly_the_nine_template_members()
    {
        var members = typeof(IOperationViewModel).GetProperties()
            .Select(p => p.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ContractMembers, members);
        Assert.True(typeof(INotifyPropertyChanged).IsAssignableFrom(typeof(IOperationViewModel)));
    }

    [Fact]
    public void The_contract_carries_no_datasource_member()
    {
        // Region A is shell-owned: the datasource is picked once by the template,
        // so no mode may smuggle its own folder/drive/source path in through the
        // contract and drift back into three private source models.
        var members = typeof(IOperationViewModel).GetProperties().Select(p => p.Name);

        Assert.DoesNotContain(members, name =>
            name.Contains("Folder", StringComparison.Ordinal) ||
            name.Contains("Drive", StringComparison.Ordinal) ||
            name.Contains("SourcePath", StringComparison.Ordinal));
    }

    // ── The three adapters (D-05) ──────────────────────────────────────────

    [Fact]
    public void Three_sealed_adapters_carry_the_dashboard_keys_and_titles()
    {
        var (tag, dedup, upscale) = Adapters();

        Assert.Equal("tag", tag.Key);
        Assert.Equal("dedup", dedup.Key);
        Assert.Equal("upscale", upscale.Key);

        Assert.Equal("Tagging", tag.Title);
        Assert.Equal("Deduplication", dedup.Title);
        Assert.Equal("Upscaling", upscale.Title);

        Assert.True(typeof(IOperationViewModel).IsAssignableFrom(tag.GetType()));
        Assert.True(typeof(IOperationViewModel).IsAssignableFrom(dedup.GetType()));
        Assert.True(typeof(IOperationViewModel).IsAssignableFrom(upscale.GetType()));
        Assert.True(tag.GetType().IsSealed);
        Assert.True(dedup.GetType().IsSealed);
        Assert.True(upscale.GetType().IsSealed);

        Assert.All(new[] { tag.Description, dedup.Description, upscale.Description },
            description => Assert.False(string.IsNullOrWhiteSpace(description)));
    }

    [Fact]
    public void Run_is_the_wrapped_modes_own_command()
    {
        var session = new Session();
        var step1 = new Step1DatasourceViewModel(session);
        var step3 = new Step3ProcessViewModel(session, new FakeSidecar(), step1);
        var dedup = new StepDedupViewModel();
        var upscale = new StepUpscaleViewModel();

        // Delegation, not re-implementation: the adapter hands out the very command
        // the mode's own button invokes (tagging's batch, dedup's scan, upscaling's run).
        Assert.Same(step3.StartCommand, new TagOperationViewModel(step3).Run);
        Assert.Same(dedup.ScanCommand, new DedupOperationViewModel(dedup).Run);
        Assert.Same(upscale.StartCommand, new UpscaleOperationViewModel(upscale).Run);
    }

    [Fact]
    public void Run_enabled_mirrors_the_wrapped_commands_can_execute()
    {
        var session = new Session();
        var step1 = new Step1DatasourceViewModel(session);
        var step3 = new Step3ProcessViewModel(session, new FakeSidecar(), step1);
        var dedup = new StepDedupViewModel();
        var upscale = new StepUpscaleViewModel();

        var cases = new (IOperationViewModel Adapter, bool CanExecute)[]
        {
            (new TagOperationViewModel(step3), step3.StartCommand.CanExecute(null)),
            (new DedupOperationViewModel(dedup), dedup.ScanCommand.CanExecute(null)),
            (new UpscaleOperationViewModel(upscale), upscale.StartCommand.CanExecute(null)),
        };

        foreach (var (adapter, canExecute) in cases)
        {
            Assert.Equal(canExecute, adapter.IsRunEnabled);
            // A disabled run always says why; an enabled one never nags.
            Assert.Equal(adapter.IsRunEnabled, adapter.RunDisabledReason is null);
        }
    }

    [Fact]
    public void Source_gated_modes_start_disabled_with_a_reason()
    {
        // Neither dedup nor upscaling has a source yet in the default state, so
        // the template must show them as not-ready — with text, not a dead button.
        var (_, dedup, upscale) = Adapters();

        Assert.False(dedup.IsRunEnabled);
        Assert.False(string.IsNullOrWhiteSpace(dedup.RunDisabledReason));

        Assert.False(upscale.IsRunEnabled);
        Assert.False(string.IsNullOrWhiteSpace(upscale.RunDisabledReason));
    }

    [Fact]
    public void Adapters_re_raise_their_members_when_the_wrapped_mode_changes()
    {
        var dedup = new StepDedupViewModel();
        var adapter = new DedupOperationViewModel(dedup);
        var raised = new List<string>();
        adapter.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        Assert.False(adapter.IsRunEnabled);

        // Picking a folder flips the scan gate: the wrapped view model's own change
        // has to reach the template without it polling.
        dedup.FolderPath = Path.GetTempPath();

        Assert.True(adapter.IsRunEnabled);
        Assert.Null(adapter.RunDisabledReason);
        Assert.Contains(nameof(IOperationViewModel.IsRunEnabled), raised);

        // And a settings change reaches the parameters read-back.
        raised.Clear();
        dedup.SelectedAlgorithm = 1; // "DHash"

        Assert.Contains(nameof(IOperationViewModel.ParametersSummary), raised);
        Assert.Equal(dedup.ScanSettingsSummary, adapter.ParametersSummary);
    }

    // ── Delegated read-backs ────────────────────────────────────────────────

    [Fact]
    public void Summary_help_topic_and_report_come_from_the_wrapped_mode()
    {
        var session = new Session();
        var step1 = new Step1DatasourceViewModel(session);
        var step3 = new Step3ProcessViewModel(session, new FakeSidecar(), step1);
        var dedup = new StepDedupViewModel();
        var upscale = new StepUpscaleViewModel();

        var tag = new TagOperationViewModel(step3);
        var dedupAdapter = new DedupOperationViewModel(dedup);
        var upscaleAdapter = new UpscaleOperationViewModel(upscale);

        Assert.Equal(dedup.ScanSettingsSummary, dedupAdapter.ParametersSummary);
        Assert.Equal(upscale.SettingsSummary, upscaleAdapter.ParametersSummary);
        Assert.Contains("confidence", tag.ParametersSummary, StringComparison.Ordinal);

        // Topics come from HelpTopics, the app's single list of topic names, so a
        // renamed topic cannot silently become a blank help page.
        Assert.Equal(HelpTopics.ForStepIndex(2), tag.HelpTopic);
        Assert.Equal(HelpTopics.ForStepIndex(4), dedupAdapter.HelpTopic);
        Assert.Equal(HelpTopics.Upscale, upscaleAdapter.HelpTopic);

        // Nothing has run in any mode yet: no mode claims a result surface.
        Assert.Null(tag.Report);
        Assert.Null(dedupAdapter.Report);
        Assert.Null(upscaleAdapter.Report);
    }
}

using Synapic.Main.Models;
using Synapic.Main.ViewModels.Operations;
using Xunit;

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
        var host = new OperationShellViewModel(session, new FakeSidecar());

        Assert.True(host.TagRun.StartCommand.CanExecute(null));

        // Uncheck all three: there is nothing to tag, so the run locks.
        host.TagParameters.TagKeywords = false;
        host.TagParameters.TagCategories = false;
        host.TagParameters.TagDescription = false;
        Assert.False(host.TagRun.StartCommand.CanExecute(null));

        // Re-checking any one re-enables it.
        host.TagParameters.TagDescription = true;
        Assert.True(host.TagRun.StartCommand.CanExecute(null));
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
}

using Synapic.Main.Models;
using Synapic.Main.Services.Processing;
using Synapic.Main.ViewModels.Steps;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// Step 3 ETA math (port of processing.py progress_callback): average seconds
/// per completed item × remaining items, available from the first completion.
/// </summary>
public class ProcessingEtaTests
{
    [Fact]
    public void NoEstimateBeforeFirstItemCompletes()
    {
        var (eta, perItem) = WorkflowRunner.EstimateProgress(TimeSpan.FromSeconds(5), 0, 10);
        Assert.Null(eta);
        Assert.Null(perItem);
    }

    [Fact]
    public void EstimateMatchesPythonFormula()
    {
        var (eta, perItem) = WorkflowRunner.EstimateProgress(TimeSpan.FromSeconds(10), 2, 12);
        Assert.Equal(TimeSpan.FromSeconds(5), perItem);
        Assert.Equal(TimeSpan.FromSeconds(50), eta);
    }

    [Fact]
    public void EstimateIsZeroOnceComplete()
    {
        var (eta, perItem) = WorkflowRunner.EstimateProgress(TimeSpan.FromSeconds(20), 4, 4);
        Assert.Equal(TimeSpan.Zero, eta);
        Assert.Equal(TimeSpan.FromSeconds(5), perItem);
    }

    [Fact]
    public void ProcessAllFlowsFromStep1IntoTheSelection()
    {
        var session = new Session();
        var vm = new Step1DatasourceViewModel(session);

        Assert.False(vm.ProcessAll);
        vm.ProcessAll = true;

        Assert.True(session.Datasource.ProcessAll);
        Assert.True(session.Datasource.ToSelection(null).ProcessAll);
    }
}

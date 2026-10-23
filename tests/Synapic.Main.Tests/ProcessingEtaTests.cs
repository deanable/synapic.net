using Synapic.Main.Models;
using Synapic.Core.Services.Processing;
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
    public void Catalog_page_eta_is_average_page_time_times_pages_left()
    {
        // 1,000 records walked in two 500-record pages over 10 s: 5 s a page.
        // The scope holds 2,123 raw records, so 1,123 records (3 pages) remain.
        var (eta, perPage) = WorkflowRunner.EstimateCatalogFetchProgress(
            TimeSpan.FromSeconds(10), pages: 2, scanned: 1000, total: 2123);

        Assert.Equal(TimeSpan.FromSeconds(5), perPage);
        Assert.Equal(TimeSpan.FromSeconds(15), eta);
    }

    [Fact]
    public void Catalog_page_eta_needs_a_scope_total()
    {
        var (eta, perPage) = WorkflowRunner.EstimateCatalogFetchProgress(
            TimeSpan.FromSeconds(4), pages: 2, scanned: 1000, total: null);

        Assert.Null(eta);
        Assert.Equal(TimeSpan.FromSeconds(2), perPage);
    }

    [Fact]
    public void Catalog_page_eta_is_unavailable_before_the_first_page()
    {
        var (eta, perPage) = WorkflowRunner.EstimateCatalogFetchProgress(
            TimeSpan.FromSeconds(3), pages: 0, scanned: 0, total: 500);

        Assert.Null(eta);
        Assert.Null(perPage);
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

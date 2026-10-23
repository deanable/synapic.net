using Synapic.Main.Models;
using Synapic.Main.Services;
using Synapic.Core.Services.Processing;
using Synapic.Main.ViewModels.Steps;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Synapic.Main.Tests;

public sealed class Step4ResultsBehaviorTests
{
    [AvaloniaFact]
    public async Task Verify_with_connected_catalog_and_no_Daminion_targets_reports_empty_state()
    {
        using var server = new DaminionEmptyTagResultTests.EmptyCatalogServer();
        var session = new Session();
        session.Datasource.Type = "daminion";
        session.Results.Add(Result("local-only.jpg", ProcessStatus.Success));
        var source = new Step1DatasourceViewModel(session)
        {
            DaminionUrl = $"http://127.0.0.1:{server.Port}",
            DaminionUser = "test",
            DaminionPass = "test",
        };
        await source.ConnectCommand.ExecuteAsync(null);
        Assert.True(source.IsDaminionConnected, source.ConnectionMessage);
        var vm = new Step4ResultsViewModel(session, source,
            new Step3ProcessViewModel(session, new FakeSidecar(), source));
        vm.Refresh();
        var reportView = new Synapic.Main.Views.Wizard.Step4Results { DataContext = vm };
        var window = new Window { Content = reportView, Width = 1100, Height = 700 };
        window.Show();
        try
        {
            await vm.VerifyDaminionCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("No Daminion-backed results to verify", vm.Summary);
            Assert.Single(vm.Results);
            Assert.Contains(reportView.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == vm.Summary && text.IsVisible);
            Assert.False(vm.IsBusy);
        }
        finally
        {
            window.Close();
            source.DisconnectCommand.Execute(null);
        }
    }

    [Fact]
    public void Verify_with_no_daminion_results_reports_that_nothing_can_be_verified()
    {
        var session = new Session();
        var source = new Step1DatasourceViewModel(session);
        var vm = new Step4ResultsViewModel(session, source,
            new Step3ProcessViewModel(session, new FakeSidecar(), source));
        vm.Refresh();

        Assert.Contains("No results yet", vm.Summary);
        Assert.False(vm.VerifyDaminionCommand.CanExecute(null));
    }

    [Fact]
    public async Task Export_csv_writes_escaped_report_rows_to_the_requested_path()
    {
        var session = new Session();
        session.Results.Add(new ProcessItemResult(
            "one, \"nice\".jpg", "Success", "Cat: Nature", "Nature", ["tree", "forest"],
            "Line one\nLine two", new Dictionary<string, double> { ["tree"] = 0.875 },
            new ScoringResultDto("high", true)));
        var path = Path.Combine(Path.GetTempPath(), $"synapic-results-{Guid.NewGuid():N}.csv");
        var source = new Step1DatasourceViewModel(session);
        var vm = new Step4ResultsViewModel(session, source,
            new Step3ProcessViewModel(session, new FakeSidecar(), source), () => path);
        vm.Refresh();

        try
        {
            await vm.ExportCsvCommand.ExecuteAsync(null);

            var csv = await File.ReadAllTextAsync(path);
            Assert.StartsWith("filename,status,tags,category,keywords,description,probabilities,scoring_tier", csv);
            Assert.Contains("\"one, \"\"nice\"\".jpg\"", csv);
            Assert.Contains("\"Line one\nLine two\"", csv);
            Assert.Contains("tree", csv);
            Assert.Contains("high", csv);
            Assert.Contains(path, vm.Summary);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// A clean report has nothing to retry. The action is disabled — not offered
    /// and then ignored — and the summary stays the baseline, so no run ever reads
    /// as if a retry had happened when none could.
    /// </summary>
    [Fact]
    public void Retry_with_nothing_to_retry_stays_disabled_and_says_nothing_about_retrying()
    {
        var session = new Session();
        session.Results.Add(Result("tagged.jpg", ProcessStatus.Success));
        var source = new Step1DatasourceViewModel(session);
        var vm = new Step4ResultsViewModel(session, source,
            new Step3ProcessViewModel(session, new FakeSidecar(), source));
        vm.Refresh();

        Assert.False(vm.RetryFailedCommand.CanExecute(null));
        Assert.Equal("1 succeeded (0 verified), 0 failed, 1 total", vm.Summary);
        Assert.DoesNotContain("Retried", vm.Summary);
        Assert.DoesNotContain("Retrying", vm.Summary);
    }

    [Fact]
    public async Task Retry_with_empty_source_reports_zero_retried_and_removes_stale_failure_rows()
    {
        var folder = Directory.CreateTempSubdirectory("synapic-retry-empty-").FullName;
        var session = new Session();
        session.Datasource.Type = "local";
        session.Datasource.LocalPath = folder;
        session.Results.Add(Result("missing-from-source.jpg", ProcessStatus.WriteFailed));
        var source = new Step1DatasourceViewModel(session);
        var vm = new Step4ResultsViewModel(session, source, new Step3ProcessViewModel(session, new FakeSidecar(), source));
        vm.Refresh();

        try
        {
            await vm.RetryFailedCommand.ExecuteAsync(null);

            Assert.Equal("Retried 0 item(s) after 1 old entries", vm.Summary);
            Assert.Empty(session.Results);
            Assert.Empty(vm.Results);
            Assert.False(vm.RetryFailedCommand.CanExecute(null));
            Assert.False(vm.IsBusy);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Retry_cancellation_reports_cancelled_and_preserves_the_existing_row()
    {
        var folder = Directory.CreateTempSubdirectory("synapic-retry-cancel-").FullName;
        var enteredTag = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        File.Copy(FindSampleImage() ?? throw new InvalidOperationException("TestData/sample.jpg not found"),
            Path.Combine(folder, "failed.jpg"));
        var session = new Session();
        session.Datasource.Type = "local";
        session.Datasource.LocalPath = folder;
        var existing = Result("failed.jpg", ProcessStatus.WriteFailed);
        session.Results.Add(existing);
        var source = new Step1DatasourceViewModel(session);
        var sidecar = new CountingTagSidecar
        {
            OnTag = async (_, ct) =>
            {
                enteredTag.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return new Synapic.Shared.Contracts.TagResponse();
            },
        };
        var vm = new Step4ResultsViewModel(session, source, new Step3ProcessViewModel(session, sidecar, source));
        vm.Refresh();
        var report = new Synapic.Main.Views.Wizard.Step4Results { DataContext = vm };
        var window = new Window { Content = report, Width = 1100, Height = 700 };
        window.Show();

        try
        {
            var retry = vm.RetryFailedCommand.ExecuteAsync(null);
            await enteredTag.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsBusy);
            Assert.True(vm.IsRetrying);
            Assert.False(vm.VerifyDaminionCommand.CanExecute(null));
            Assert.False(report.GetVisualDescendants().OfType<Button>()
                .Single(b => (string?)b.Content == "Verify Daminion Writes").IsEnabled);
            Assert.True(report.FindControl<Button>("CancelRetryButton")!.IsVisible);
            Assert.True(report.FindControl<Button>("CancelRetryButton")!.IsEnabled);

            report.FindControl<Button>("CancelRetryButton")!.Command!.Execute(null);
            await retry;

            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Retry cancelled", vm.Summary);
            Assert.Same(existing, Assert.Single(session.Results));
            Assert.False(vm.IsRetrying);
            Assert.False(vm.IsBusy);
            Assert.False(report.FindControl<Button>("CancelRetryButton")!.IsVisible);
            Assert.True(report.GetVisualDescendants().OfType<Button>()
                .Single(b => (string?)b.Content == "Retry Failed").IsEnabled);
            Assert.True(vm.RetryFailedCommand.CanExecute(null));
            Assert.True(report.GetVisualDescendants().OfType<Button>()
                .Single(b => (string?)b.Content == "Verify Daminion Writes").IsEnabled);
        }
        finally
        {
            window.Close();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Retry_when_source_fetch_fails_preserves_old_rows_and_reports_the_failure()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"synapic-retry-missing-{Guid.NewGuid():N}");
        var session = new Session();
        session.Datasource.Type = "local";
        session.Datasource.LocalPath = missing;
        session.Results.Add(Result("failed.jpg", ProcessStatus.WriteFailed));
        var source = new Step1DatasourceViewModel(session);
        var vm = new Step4ResultsViewModel(session, source, new Step3ProcessViewModel(session, new FakeSidecar(), source));
        vm.Refresh();

        await vm.RetryFailedCommand.ExecuteAsync(null);

        Assert.Equal("Retry failed: Folder not found: " + missing, vm.Summary);
        Assert.Single(session.Results);
        Assert.Equal(ProcessStatus.WriteFailed, session.Results[0].Status);
        Assert.Equal(ProcessStatus.WriteFailed, Assert.Single(vm.Results).Status);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Retry_failed_reprocesses_only_failed_files_and_replaces_their_report_rows()
    {
        var folder = Directory.CreateTempSubdirectory("synapic-retry-results-").FullName;
        var sample = FindSampleImage() ?? throw new InvalidOperationException("TestData/sample.jpg not found");
        var failedPath = Path.Combine(folder, "failed.jpg");
        var successPath = Path.Combine(folder, "already-ok.jpg");
        File.Copy(sample, failedPath);
        File.Copy(sample, successPath);
        var session = new Session();
        session.Datasource.Type = "local";
        session.Datasource.LocalPath = folder;
        session.Results.Add(Result("failed.jpg", ProcessStatus.WriteFailed));
        var untouched = Result("already-ok.jpg", ProcessStatus.Success);
        session.Results.Add(untouched);
        var source = new Step1DatasourceViewModel(session);
        var sidecar = new CountingTagSidecar();
        var vm = new Step4ResultsViewModel(session, source, new Step3ProcessViewModel(session, sidecar, source));
        vm.Refresh();

        try
        {
            Assert.True(vm.RetryFailedCommand.CanExecute(null));
            await vm.RetryFailedCommand.ExecuteAsync(null);

            Assert.Equal(1, sidecar.TagCalls);
            Assert.Equal(2, session.Results.Count);
            Assert.Contains(session.Results, result => result.FileName == "failed.jpg" && result.Status == ProcessStatus.Success);
            Assert.Same(untouched, session.Results.Single(result => result.FileName == "already-ok.jpg"));
            Assert.Contains("Retried 1 item(s)", vm.Summary);
            Assert.False(vm.RetryFailedCommand.CanExecute(null));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static ProcessItemResult Result(string name, string status, int? daminionId = null) =>
        new(name, status, "Cat: Nature", "Nature", ["tree"], "A forest at dawn", null, null, daminionId);

    private static string? FindSampleImage()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && directory is not null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "Synapic.Main.Tests", "TestData", "sample.jpg");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private sealed class CountingTagSidecar : FixedTagSidecar
    {
        public int TagCalls { get; private set; }
        public Func<Synapic.Shared.Contracts.TagRequest, CancellationToken, Task<Synapic.Shared.Contracts.TagResponse>>? OnTag { get; init; }

        public override Task<Synapic.Shared.Contracts.TagResponse> TagAsync(
            Synapic.Shared.Contracts.TagRequest request,
            CancellationToken ct = default)
        {
            TagCalls++;
            return OnTag is { } handler ? handler(request, ct) : base.TagAsync(request, ct);
        }
    }
}

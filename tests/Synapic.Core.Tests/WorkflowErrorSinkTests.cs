using Synapic.Core.Services.Processing;
using Xunit;

namespace Synapic.Core.Tests;

public sealed class WorkflowErrorSinkTests
{
    private static readonly WorkflowDefinition Definition = new("test", "Test", MaxDegreeOfParallelism: 1);

    private sealed class Handler : IWorkflowItemHandler
    {
        private readonly Func<ProcessWorkItem, Task<WorkflowItemOutcome>> _process;

        public Handler(Func<ProcessWorkItem, Task<WorkflowItemOutcome>>? process = null) =>
            _process = process ?? (_ => Task.FromResult(WorkflowItemOutcome.Ok));

        public Task<WorkflowItemOutcome> ProcessAsync(ProcessWorkItem item, CancellationToken ct) => _process(item);
    }

    [Fact]
    public async Task A_throwing_item_is_counted_failed_and_recorded_once()
    {
        var errors = new List<WorkflowItemError>();
        var runner = new WorkflowRunner();
        var items = new[]
        {
            new ProcessWorkItem { LocalPath = @"C:\photos\a.jpg", FileName = "a.jpg" },
            new ProcessWorkItem { LocalPath = @"C:\photos\b.jpg", FileName = "b.jpg" },
        };

        var summary = await runner.RunItemsAsync(
            Definition, items,
            new Handler(item => item.FileName == "a.jpg"
                ? throw new IOException("The file is being used by another process.")
                : Task.FromResult(WorkflowItemOutcome.Ok)),
            errors: errors);

        Assert.Equal(2, summary.Total);
        Assert.Equal(1, summary.Failed);
        var error = Assert.Single(errors);
        Assert.Equal("a.jpg", error.FileName);
        Assert.Equal(@"C:\photos\a.jpg", error.LocalPath);
        Assert.Null(error.DaminionId);
        Assert.Equal("IOException", error.ExceptionType);
        Assert.Contains("being used", error.Message);
    }

    [Fact]
    public async Task A_clean_run_leaves_the_error_list_empty()
    {
        var errors = new List<WorkflowItemError>();

        await new WorkflowRunner().RunItemsAsync(
            Definition,
            new[] { new ProcessWorkItem { LocalPath = @"C:\a.jpg", FileName = "a.jpg" } },
            new Handler(),
            errors: errors);

        Assert.Empty(errors);
    }

    [Fact]
    public async Task A_catalog_failure_carries_the_item_id_instead_of_a_path()
    {
        var errors = new List<WorkflowItemError>();

        await new WorkflowRunner().RunItemsAsync(
            Definition,
            new[] { new ProcessWorkItem { DaminionId = 42, FileName = "catalog.jpg" } },
            new Handler(_ => throw new ArgumentException("cannot decode the image")),
            errors: errors);

        var error = Assert.Single(errors);
        Assert.Equal(42, error.DaminionId);
        Assert.Null(error.LocalPath);
        Assert.Equal("ArgumentException", error.ExceptionType);
    }

    [Fact]
    public async Task Omitting_the_error_list_keeps_the_old_behaviour()
    {
        var summary = await new WorkflowRunner().RunItemsAsync(
            Definition,
            new[] { new ProcessWorkItem { LocalPath = @"C:\a.jpg", FileName = "a.jpg" } },
            new Handler(_ => throw new IOException("boom")));

        Assert.Equal(1, summary.Failed);
    }
}

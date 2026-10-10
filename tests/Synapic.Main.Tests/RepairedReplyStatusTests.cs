using System.Text.Json;
using Synapic.Main.Models;
using Synapic.Main.Services;
using Synapic.Main.Services.Processing;
using Synapic.Main.ViewModels.Steps;
using Synapic.Shared;
using Synapic.Shared.Contracts;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// Sidecar fake whose replies carry the repair reasons the JSON hunt reported.
/// The host turns a non-empty <see cref="TagResponse.ReplyRepairs"/> into a
/// distinct per-item status, so empty here means "the reply parsed as it came".
/// </summary>
internal sealed class RepairReportingSidecar : IInferenceSidecar
{
    public RepairReportingSidecar(params string[]? replyRepairs) => ReplyRepairs = replyRepairs;

    /// <summary>Whether the sidecar had to ask the model again for this item.</summary>
    public bool ReplyRetried { get; init; }

    /// <summary>
    /// Exactly what goes on the wire. An older sidecar build omits the member
    /// altogether, which deserializes to null — <see cref="MissingField"/> is how
    /// a test reproduces that without an older binary.
    /// </summary>
    public string[]? ReplyRepairs { get; }

    /// <summary>A sidecar built before the field existed: no member at all.</summary>
    public static RepairReportingSidecar MissingField { get; } = new((string[]?)null);

    public SidecarStatus CurrentStatus => SidecarStatus.Ready;
    public int SidecarPort => 0;
    public bool IsRunning => true;

#pragma warning disable CS0067 // events unused by this fake
    public event EventHandler<SidecarStatusChangedEventArgs>? StatusChanged;
    public event Action<string>? LogReceived;
#pragma warning restore CS0067

    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(TimeSpan? gracefulTimeout = null) => Task.CompletedTask;

    public Task<TagResponse> TagAsync(TagRequest request, CancellationToken ct = default)
        => Task.FromResult(new TagResponse
        {
            Category = "Nature",
            Keywords = ["tree", "forest"],
            Description = "A forest at dawn",
            ReplyRepairs = ReplyRepairs,
            ReplyRetried = ReplyRetried,
        });

    public Task<ModelInfo[]> ListModelsAsync(CancellationToken ct = default) => Task.FromResult(Array.Empty<ModelInfo>());
    public Task DownloadModelAsync(string modelId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<HealthResponse> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new HealthResponse());
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// An item whose model reply had to be put back together before it could be read
/// reports itself as repaired: as a distinct status in the results grid, as a
/// line in the run log, and as its own count in the report summary. Otherwise a
/// batch whose model was mangling its own JSON looked exactly like a clean one —
/// the tags were written, so nothing complained.
/// </summary>
public class RepairedReplyStatusTests
{
    private static DatasourceSelection LocalSelection(string dir) => new()
    {
        IsDaminion = false,
        LocalPath = dir,
        LocalRecursive = false,
    };

    private static Task<(ProcessItemResult Result, List<string> Log)> RunOneItemAsync(params string[] replyRepairs)
        => RunOneItemWithAsync(new RepairReportingSidecar(replyRepairs));

    private static async Task<(ProcessItemResult Result, List<string> Log)> RunOneItemWithAsync(RepairReportingSidecar sidecar)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"synapic-repaired-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "img1.jpg"), new byte[] { 1 });
        try
        {
            var orchestrator = new ProcessingOrchestrator(
                sidecar, new CapturingMetadataWriter(), maxDegreeOfParallelism: 1);
            var results = new List<ProcessItemResult>();
            var log = new List<string>();
            var template = new TagRequest
            {
                ImagePath = "",
                ModelId = "LiquidAI/LFM2.5-VL-450M",
                Task = "image-text-to-text",
            };

            await orchestrator.RunAsync(
                LocalSelection(dir), template, new Progress<ProcessProgress>(),
                line => { lock (log) log.Add(line); return Task.CompletedTask; },
                CancellationToken.None, results);

            return (Assert.Single(results), log);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Repaired_reply_gets_its_own_status_and_a_run_log_line()
    {
        var (result, log) = await RunOneItemAsync("missing member separator");

        Assert.Equal(ProcessStatus.SuccessRepaired, result.Status);
        Assert.Equal("Success (repaired)", result.Status);

        // The tags are real — the repair is what made them readable — so the item
        // is a success, just not an ordinary one.
        Assert.Equal("Nature", result.Category);
        Assert.Equal(new[] { "tree", "forest" }, result.Keywords);

        Assert.Contains(log, line => line.Contains("Reply repaired before reading", StringComparison.Ordinal)
            && line.Contains("missing member separator", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Clean_reply_stays_a_plain_success()
    {
        var (result, log) = await RunOneItemAsync();

        Assert.Equal(ProcessStatus.Success, result.Status);
        Assert.DoesNotContain(log, line => line.Contains("repaired", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_sidecar_too_old_to_send_the_field_reads_as_no_repairs()
    {
        // The app and the sidecar are separate binaries and the older one wins
        // whenever the app is installed over it: a /tag body with no
        // reply_repairs member is what that looks like on the wire, and it used
        // to deserialize to null and crash every item of the batch with a
        // NullReferenceException before any tags were written.
        const string oldSidecarBody =
            """{"category":"Nature","keywords":["tree"],"description":"A forest","inference_ms":42}""";

        var response = JsonSerializer.Deserialize(oldSidecarBody, SynapicJsonContext.Default.TagResponse);

        Assert.NotNull(response);
        Assert.Null(response!.ReplyRepairs);
    }

    [Fact]
    public async Task A_sidecar_that_sends_no_member_treats_the_item_as_clean()
    {
        // Same thing through the code that reads it, end to end: absent
        // information has to be an ordinary clean item, not an exception.
        var (result, log) = await RunOneItemWithAsync(RepairReportingSidecar.MissingField);

        Assert.Equal(ProcessStatus.Success, result.Status);
        Assert.Equal("Nature", result.Category);
        Assert.DoesNotContain(log, line => line.Contains("repaired", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_retried_reply_says_so_in_the_run_log()
    {
        // Asking the model again is the sidecar's business, but a batch that
        // needed a second ask for every image has to be visible here — otherwise
        // it looks exactly like a clean run at double the model cost.
        var (result, log) = await RunOneItemWithAsync(
            new RepairReportingSidecar { ReplyRetried = true });

        Assert.Equal(ProcessStatus.Success, result.Status);
        Assert.Contains(log, line => line.Contains("asked the model again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_reply_that_was_not_retried_says_nothing_about_it()
    {
        var (_, log) = await RunOneItemAsync();

        Assert.DoesNotContain(log, line => line.Contains("asked the model again", StringComparison.Ordinal));
    }

    [Fact]
    public void An_absent_retry_flag_reads_as_not_retried()
    {
        // Same backward-compatibility rule as the repair list: a sidecar that
        // predates the field sends no member, and an absent bool member reads
        // as false rather than crashing the item.
        const string oldSidecarBody =
            """{"category":"Nature","keywords":["tree"],"description":"A forest"}""";

        var response = JsonSerializer.Deserialize(oldSidecarBody, SynapicJsonContext.Default.TagResponse);

        Assert.NotNull(response);
        Assert.False(response!.ReplyRetried);
    }

    [Fact]
    public void A_repair_is_not_something_to_retry()
    {
        // Retry Failed is driven by these predicates: a repaired item was tagged
        // and written, so it must not be picked up for another attempt (which
        // would rewrite the same metadata and could report it as failed later).
        Assert.False(ProcessStatus.NeedsRetry("Success"));
        Assert.False(ProcessStatus.NeedsRetry("Success (repaired)"));
        Assert.False(ProcessStatus.NeedsRetry("Verified"));
        Assert.False(ProcessStatus.NeedsRetry("Verified (repaired)"));
        Assert.True(ProcessStatus.NeedsRetry("Write Failed"));

        Assert.True(ProcessStatus.WasRepaired("Success (repaired)"));
        Assert.True(ProcessStatus.WasRepaired("Verified (repaired)"));
        Assert.False(ProcessStatus.WasRepaired("Success"));
        Assert.False(ProcessStatus.WasRepaired("Write Failed"));
    }

    [Fact]
    public void Verification_keeps_the_repaired_marker()
    {
        // Verify replaces the status, and losing the marker there would hide a
        // shaky batch behind the very action taken to trust it.
        Assert.Equal("Verified (repaired)", ProcessStatus.VerifiedFor("Success (repaired)"));
        Assert.Equal("Verified", ProcessStatus.VerifiedFor("Success"));
        Assert.True(ProcessStatus.IsVerified(ProcessStatus.VerifiedFor("Success (repaired)")));
    }

    [Fact]
    public void Report_summary_names_the_repaired_count()
    {
        var (vm, _) = ReportWith(
            ("a.jpg", ProcessStatus.Success),
            ("b.jpg", ProcessStatus.SuccessRepaired),
            ("c.jpg", ProcessStatus.WriteFailed));

        Assert.Contains("2 succeeded (0 verified, 1 repaired), 1 failed, 3 total", vm.Summary);
        Assert.True(vm.RetryFailedCommand.CanExecute(null));

        var clean = ReportWith(
            ("a.jpg", ProcessStatus.Success),
            ("b.jpg", ProcessStatus.SuccessRepaired)).Vm;
        Assert.False(clean.RetryFailedCommand.CanExecute(null));
        Assert.Contains("2 succeeded (0 verified, 1 repaired), 0 failed, 2 total", clean.Summary);
    }

    private static (Step4ResultsViewModel Vm, Session Session) ReportWith(params (string File, string Status)[] rows)
    {
        var session = new Session();
        foreach (var (file, status) in rows)
        {
            session.Results.Add(new ProcessItemResult(
                file, status, "Cat: Nature", "Nature", ["tree"], "A forest", null, null));
        }

        var step1 = new Step1DatasourceViewModel(session);
        var step3 = new Step3ProcessViewModel(session, new FakeSidecar(), step1);
        var vm = new Step4ResultsViewModel(session, step1, step3);
        vm.Refresh();

        return (vm, session);
    }
}

using Synapic.Core.Services.Processing;
using Synapic.Indexer.Pipeline;
using Xunit;

namespace Synapic.Indexer.Tests;

/// <summary>
/// The failure table from docs/windows-indexer.md §8, one row per case. The
/// classifier is the only place retry-vs-permanent is decided, so a regression
/// here turns into either a retry storm or a lost job.
/// </summary>
public sealed class JobOutcomeClassifierTests
{
    private static WorkflowItemError Error(
        string exceptionType,
        string message,
        int? daminionId = null) =>
        new("a.jpg", daminionId is null ? @"C:\photos\a.jpg" : null, daminionId, "Failed", message, exceptionType);

    [Fact]
    public void A_missing_error_is_transient_not_a_silent_drop()
    {
        var disposition = JobOutcomeClassifier.Classify(null, attempts: 1, sidecarRunning: true);

        Assert.Equal(JobDispositionKind.Transient, disposition.Kind);
        Assert.Equal(TimeSpan.FromSeconds(60), disposition.Delay);
        Assert.Contains("no result", disposition.Reason);
    }

    [Theory]
    [InlineData("IOException", "The process cannot access the file because it is being used by another process.")]
    [InlineData("UnauthorizedAccessException", "Access to the path 'C:\\photos\\a.jpg' is denied.")]
    public void A_locked_file_retries_after_a_minute(string type, string message)
    {
        var disposition = JobOutcomeClassifier.Classify(Error(type, message), attempts: 1, sidecarRunning: true);

        Assert.Equal(JobDispositionKind.Transient, disposition.Kind);
        Assert.Equal(TimeSpan.FromSeconds(60), disposition.Delay);
        Assert.Contains("in use", disposition.Reason);
    }

    [Theory]
    [InlineData("ArgumentException", "The image could not be decoded.")]
    [InlineData("NotSupportedException", "Unsupported image format: .heic")]
    [InlineData("InferenceApiException", "Sidecar API error 422: Failed to decode image: cannot identify image file")]
    public void A_corrupt_image_fails_permanently_on_the_first_attempt(string type, string message)
    {
        var disposition = JobOutcomeClassifier.Classify(Error(type, message), attempts: 1, sidecarRunning: true);

        Assert.Equal(JobDispositionKind.Failed, disposition.Kind);
        Assert.Contains("could not be decoded", disposition.Reason);
    }

    [Theory]
    [InlineData("DaminionNetworkException", "Connection refused")]
    [InlineData("DaminionRateLimitException", "429 too many requests")]
    [InlineData("HttpRequestException", "The request timed out after 30s")]
    public void An_unreachable_catalog_pauses_the_run_for_five_minutes(string type, string message)
    {
        var disposition = JobOutcomeClassifier.Classify(
            Error(type, message, daminionId: 77), attempts: 1, sidecarRunning: true);

        Assert.Equal(JobDispositionKind.PauseRun, disposition.Kind);
        Assert.Equal(TimeSpan.FromMinutes(5), disposition.Delay);
    }

    [Fact]
    public void A_catalog_authentication_failure_stops_the_batch_instead_of_retrying()
    {
        var disposition = JobOutcomeClassifier.Classify(
            Error("DaminionAuthenticationException", "401 Unauthorized"), attempts: 1, sidecarRunning: true);

        Assert.Equal(JobDispositionKind.Failed, disposition.Kind);
        Assert.Contains("authentication", disposition.Reason);
        Assert.DoesNotContain("password", disposition.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_dead_sidecar_with_a_local_http_error_is_a_restart_not_a_catalog_outage()
    {
        // This is the mid-batch kill shape: the in-flight POST fails with
        // HttpRequestException once the process is gone. It must not pause the
        // run for five minutes (only catalog items pause).
        var disposition = JobOutcomeClassifier.Classify(
            Error("HttpRequestException", "An error occurred while sending the request."),
            attempts: 1, sidecarRunning: false);

        Assert.Equal(JobDispositionKind.Transient, disposition.Kind);
        Assert.Equal(TimeSpan.FromSeconds(60), disposition.Delay);
        Assert.Contains("not running", disposition.Reason);
    }

    [Fact]
    public void A_live_sidecar_that_does_not_answer_a_local_item_is_transient()
    {
        var disposition = JobOutcomeClassifier.Classify(
            Error("TaskCanceledException", "The operation was canceled."),
            attempts: 1, sidecarRunning: true);

        Assert.Equal(JobDispositionKind.Transient, disposition.Kind);
        Assert.Equal(TimeSpan.FromSeconds(60), disposition.Delay);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void An_unknown_failure_retries_until_the_attempt_cap(int attempts)
    {
        var disposition = JobOutcomeClassifier.Classify(
            Error("InvalidOperationException", "something unexpected"), attempts, sidecarRunning: true);

        Assert.Equal(JobDispositionKind.Transient, disposition.Kind);
        Assert.Equal(TimeSpan.FromSeconds(60), disposition.Delay);
    }

    [Fact]
    public void An_unknown_failure_becomes_permanent_on_the_third_attempt()
    {
        var disposition = JobOutcomeClassifier.Classify(
            Error("InvalidOperationException", "something unexpected"), attempts: 3, sidecarRunning: true);

        Assert.Equal(JobDispositionKind.Failed, disposition.Kind);
        Assert.Contains("3 attempts", disposition.Reason);
    }
}

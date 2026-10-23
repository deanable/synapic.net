using Synapic.Core.Services.Processing;

namespace Synapic.Indexer.Pipeline;

/// <summary>What should happen to a job after an attempt.</summary>
public enum JobDispositionKind
{
    Completed,
    Transient,
    PauseRun,
    Failed,
}

/// <summary>A decision plus when to try again (transient/pause) and why it was made.</summary>
public sealed record JobDisposition(JobDispositionKind Kind, TimeSpan Delay, string Reason)
{
    public static JobDisposition Completed(string reason = "completed") =>
        new(JobDispositionKind.Completed, TimeSpan.Zero, reason);

    public static JobDisposition Failed(string reason) =>
        new(JobDispositionKind.Failed, TimeSpan.Zero, reason);

    public static JobDisposition Transient(string reason, TimeSpan delay) =>
        new(JobDispositionKind.Transient, delay, reason);

    public static JobDisposition Pause(string reason, TimeSpan delay) =>
        new(JobDispositionKind.PauseRun, delay, reason);
}

/// <summary>
/// The spec's §8 failure table, as code. One place decides retry vs permanent, so
/// a new catch block cannot invent a second policy and start a retry storm.
/// </summary>
public static class JobOutcomeClassifier
{
    /// <summary>Spec constant: a locked file is retried after a minute.</summary>
    public static readonly TimeSpan LockRetryDelay = TimeSpan.FromSeconds(60);

    /// <summary>Spec constant: an unreachable catalog pauses the run for five minutes.</summary>
    public static readonly TimeSpan CatalogPauseDelay = TimeSpan.FromMinutes(5);

    /// <summary>Attempts after which an unknown failure is treated as permanent.</summary>
    public const int MaxTransientAttempts = 3;

    /// <summary>
    /// Classify a failed item. <paramref name="attempts"/> is the attempt count of
    /// the row that just failed, so a third unknown failure becomes permanent
    /// instead of looping forever.
    /// </summary>
    public static JobDisposition Classify(WorkflowItemError? error, int attempts, bool sidecarRunning)
    {
        if (error is null)
        {
            return JobDisposition.Transient(
                "no result was reported for this item", LockRetryDelay);
        }

        var type = error.ExceptionType;
        var message = error.Message ?? "";
        var isCatalogItem = error.DaminionId is not null || Mentions(type, "Daminion");

        if (Mentions(type, "DaminionAuthentication"))
            return JobDisposition.Failed($"catalog authentication failed: {message}");

        if (Mentions(type, "DaminionNetwork") || Mentions(type, "DaminionRateLimit") ||
            (isCatalogItem && IsTimeoutLike(type, message)))
        {
            return JobDisposition.Pause($"catalog unreachable: {message}", CatalogPauseDelay);
        }

        if (Mentions(type, "Daminion"))
            return JobDisposition.Failed($"catalog rejected the item: {message}");

        // A catalog item's image comes from the server, so "the download failed"
        // means the catalog stopped answering — the spec's outage row, not a
        // locked local file. DaminionApiClient returns null (and the handler
        // throws IOException) for transport errors, which is why this is keyed on
        // the message of a catalog item.
        if (isCatalogItem && Contains(message, "download") &&
            type is "IOException" or "DaminionException")
            return JobDisposition.Pause($"the catalog could not supply the image: {message}", CatalogPauseDelay);

        // The sidecar's own process state outranks the exception shape: a local
        // HttpRequestException after a crash reads exactly like a dead endpoint,
        // but it is a restart, not a catalog outage. Checked before the timeout
        // branch so a killed sidecar never pauses the run for five minutes.
        if (!sidecarRunning)
        {
            // The process died mid-item. Whether that is retryable depends on the
            // recovery step, so treat it as transient for now.
            return JobDisposition.Transient($"the inference sidecar is not running: {message}", LockRetryDelay);
        }

        if (IsTimeoutLike(type, message))
            return JobDisposition.Transient($"the inference sidecar did not answer: {message}", LockRetryDelay);

        if (IsDecodeFailure(type, message))
            return JobDisposition.Failed($"the image could not be decoded: {message}");

        if (IsLockFailure(type, message))
            return JobDisposition.Transient($"the file is in use: {message}", LockRetryDelay);

        return attempts >= MaxTransientAttempts
            ? JobDisposition.Failed($"gave up after {attempts} attempts: {message}")
            : JobDisposition.Transient($"{type}: {message}", LockRetryDelay);
    }

    private static bool IsTimeoutLike(string type, string message) =>
        type is "HttpRequestException" or "TaskCanceledException" or "TimeoutException" ||
        Contains(message, "timed out") || Contains(message, "timeout");

    // InferenceApiException covers the sidecar rejecting the file: a corrupt or
    // unsupported image answers 4xx with a decode detail, not a .NET decode type.
    private static bool IsDecodeFailure(string type, string message) =>
        type is "ArgumentException" or "NotSupportedException" or "InvalidOperationException"
            or "InferenceApiException" &&
        (Contains(message, "decode") || Contains(message, "corrupt") || Contains(message, "image") ||
         Contains(message, "invalid") || Contains(message, "unsupported") || Contains(message, "format"));

    private static bool IsLockFailure(string type, string message) =>
        type is "IOException" or "UnauthorizedAccessException" &&
        (Contains(message, "being used") || Contains(message, "lock") || Contains(message, "sharing") ||
         Contains(message, "access") || Contains(message, "denied"));

    private static bool Mentions(string? value, string fragment) =>
        value?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true;

    private static bool Contains(string? value, string fragment) =>
        value?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true;
}

namespace Synapic.Main.Services.Processing;

/// <summary>
/// The per-item status vocabulary of the results report (<see cref="ProcessItemResult.Status"/>).
///
/// Two things are being reported at once: whether the metadata write landed
/// (Success / Write Failed) and whether the sidecar had to repair the model's
/// reply before it could be read at all (<c>(repaired)</c>). The repaired
/// variants are full statuses rather than a separate flag so the grid, the CSV
/// and the summary line all show them without extra plumbing — but every
/// comparison against a status has to go through this class, because the
/// repaired statuses are not the plain strings they extend.
/// </summary>
public static class ProcessStatus
{
    public const string Success = "Success";
    public const string SuccessRepaired = "Success (repaired)";
    public const string WriteFailed = "Write Failed";
    public const string Verified = "Verified";
    public const string VerifiedRepaired = "Verified (repaired)";

    private const string RepairedSuffix = " (repaired)";

    /// <summary>True when the sidecar had to rewrite the reply before reading it.</summary>
    public static bool WasRepaired(string status)
        => status.EndsWith(RepairedSuffix, StringComparison.Ordinal);

    /// <summary>
    /// True when the item does not need another attempt: the tags were written
    /// (with or without a repair, before or after verification).
    /// </summary>
    public static bool IsSuccess(string status)
        => status is Success or SuccessRepaired or Verified or VerifiedRepaired;

    /// <summary>True once a Daminion write has been verified against the server.</summary>
    public static bool IsVerified(string status)
        => status is Verified or VerifiedRepaired;

    /// <summary>True when the metadata write did not land and a retry may help.</summary>
    public static bool NeedsRetry(string status) => !IsSuccess(status);

    /// <summary>The verified form that keeps the repaired marker.</summary>
    public static string VerifiedFor(string status)
        => WasRepaired(status) ? VerifiedRepaired : Verified;
}

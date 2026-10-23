using System.Globalization;
using System.Security.Cryptography;

namespace Synapic.Indexer.Detection;

/// <summary>
/// Reads a file's content hash. An interface, not a static method, so tests can
/// count reads and prove that an unchanged file costs one stat and no bytes
/// (SCAN-02), and so a future NAS-friendly hasher can be swapped in.
/// </summary>
public interface IFileHasher
{
    /// <summary>
    /// SHA-256 hex (lowercase), or null when the file vanished or cannot be read.
    /// Cancellation propagates; filesystem races do not.
    /// </summary>
    Task<string?> TryHashAsync(string path, CancellationToken ct);
}

/// <summary>
/// The real hasher. Opens with <see cref="FileShare.ReadWrite"/> and
/// <see cref="FileShare.Delete"/>: a status read must not block the writer that
/// is about to finish, and a file replaced mid-hash is simply hashed again on
/// the next scan.
/// </summary>
public sealed class Sha256FileHasher : IFileHasher
{
    private const int BufferSize = 64 * 1024;

    public async Task<string?> TryHashAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>Hash strings that are not SHA-256 of local bytes.</summary>
public static class ContentFingerprint
{
    /// <summary>
    /// A catalog item's server-side content hash. Prefixed so it can never be
    /// mistaken for, or collide with, a local SHA-256.
    /// </summary>
    public static string ServerHash(long hash) =>
        "srv:" + hash.ToString(CultureInfo.InvariantCulture);
}

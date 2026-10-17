using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synapic.Main.Services.Processing;

/// <summary>One scanned item as stored in the fingerprint cache: enough to
/// rebuild its review row without touching the file or the catalog.</summary>
/// <param name="Key">Stable identity — a local path, or "daminion:{id}".</param>
/// <param name="FileName">Display name (the catalog's file name for a Daminion item).</param>
/// <param name="SizeBytes">Server/file size; 0 when unknown.</param>
/// <param name="DateTakenUtc">Best known capture/creation date, UTC.</param>
/// <param name="DaminionId">Catalog id, so restored rows can be acted on after reconnecting.</param>
public sealed record CachedDedupItem(
    string Key,
    string FileName,
    long SizeBytes,
    DateTime? DateTakenUtc,
    int? DaminionId);

/// <summary>
/// A completed scan's fingerprints plus the row metadata needed to show its
/// groups again. Persisting this is what arms the results page's threshold
/// slider on the next launch without rehashing anything.
/// </summary>
/// <param name="Version">Cache format version; a mismatch is ignored, never guessed at.</param>
/// <param name="SourceKey">The source the fingerprints describe (see the view model's key).</param>
/// <param name="Algorithm">The algorithm that produced the hashes ("phash" …); hashes of one algorithm are meaningless to another.</param>
/// <param name="Threshold">The threshold the scan grouped at, for the log and a sanity read.</param>
/// <param name="TotalFiles">How many files the scan covered (may exceed the hash count when some were unreadable).</param>
/// <param name="Items">One row per hashed item.</param>
/// <param name="PerceptualHashes">Algorithmic fingerprints keyed like <paramref name="Items"/>.</param>
/// <param name="ExactHashes">Server content hashes, when the source is Daminion.</param>
public sealed record CachedDedupScan(
    int Version,
    string SourceKey,
    string Algorithm,
    double Threshold,
    int TotalFiles,
    CachedDedupItem[] Items,
    Dictionary<string, ulong> PerceptualHashes,
    Dictionary<string, ulong> ExactHashes);

/// <summary>
/// On-disk store for the last scan per operation profile and source, so a
/// restart can rebuild the review list and its live threshold slider without a
/// rescan. One gzipped JSON file per profile/source; the source key is hashed
/// into the file name so no path or query string becomes a filename.
/// </summary>
/// <remarks>
/// The cache is disposable state: a missing, truncated, old-version or
/// algorithm-mismatched file is ignored (and logged), never repaired. Writes
/// are atomic (temp file then move), so a crash mid-save cannot leave a file
/// that deserializes into a partial result set. The newest eight files per
/// profile are kept; the rest are pruned on save.
/// </remarks>
public sealed class DedupFingerprintCache
{
    public const int CurrentVersion = 1;

    /// <summary>Refuse to persist beyond this many items — a cache that large
    /// costs more to load than a rescan of the same folder.</summary>
    public const int MaxItems = 250_000;

    private const int KeepPerProfile = 8;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _directory;

    public DedupFingerprintCache(string? directory = null) => _directory = directory ?? DefaultDirectory;

    /// <summary>
    /// %LOCALAPPDATA%\Synapic\fingerprints on Windows, the platform's local
    /// data directory elsewhere, then beside the app if neither is available.
    /// </summary>
    public static string DefaultDirectory
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrEmpty(local)
                ? Path.Combine(AppContext.BaseDirectory, "fingerprints")
                : Path.Combine(local, "Synapic", "fingerprints");
        }
    }

    public string DirectoryPath => _directory;

    /// <summary>Write one scan atomically. Best effort at the call site: a failed
    /// save only costs the next launch a rescan.</summary>
    public async Task SaveAsync(string profileKey, CachedDedupScan scan, CancellationToken ct = default)
    {
        if (scan.Items.Length > MaxItems)
            throw new InvalidOperationException(
                $"refusing to cache {scan.Items.Length:N0} items (limit {MaxItems:N0})");

        System.IO.Directory.CreateDirectory(_directory);
        var path = PathFor(profileKey, scan.SourceKey);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, useAsync: true))
            await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            {
                await JsonSerializer.SerializeAsync(gzip, scan, JsonOptions, ct).ConfigureAwait(false);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            throw;
        }

        Prune(profileKey, keepPath: path);
    }

    /// <summary>Read the cached scan for a profile/source, or null when there is
    /// none, it is unreadable, it belongs to another source or its algorithm or
    /// format version no longer applies.</summary>
    public async Task<CachedDedupScan?> LoadAsync(
        string profileKey, string sourceKey, CancellationToken ct = default)
    {
        var path = PathFor(profileKey, sourceKey);
        if (!File.Exists(path)) return null;

        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, useAsync: true);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var scan = await JsonSerializer.DeserializeAsync<CachedDedupScan>(gzip, JsonOptions, ct)
                .ConfigureAwait(false);
            if (scan is null) return null;
            if (scan.Version != CurrentVersion || !string.Equals(scan.SourceKey, sourceKey, StringComparison.Ordinal))
            {
                SynapicLog.Info(nameof(DedupFingerprintCache),
                    $"Ignoring fingerprint cache {Path.GetFileName(path)}: version {scan.Version} / another source");
                return null;
            }
            return scan;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                      or InvalidDataException or NotSupportedException)
        {
            SynapicLog.Warning(nameof(DedupFingerprintCache),
                $"Fingerprint cache {Path.GetFileName(path)} could not be read ({e.Message}); a rescan will replace it");
            return null;
        }
    }

    /// <summary>Drop a source's cache (after Apply changed the files it described).</summary>
    public void Delete(string profileKey, string sourceKey)
    {
        var path = PathFor(profileKey, sourceKey);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                SynapicLog.Info(nameof(DedupFingerprintCache), $"Fingerprint cache removed: {Path.GetFileName(path)}");
            }
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(DedupFingerprintCache),
                $"Could not remove fingerprint cache {Path.GetFileName(path)}: {e.Message}");
        }
    }

    private string PathFor(string profileKey, string sourceKey)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceKey))).ToLowerInvariant();
        return Path.Combine(_directory, $"{profileKey}-{hash}.json.gz");
    }

    /// <summary>Keep the newest few caches for this profile so scanning many
    /// folders over time does not accumulate megabytes forever.</summary>
    private void Prune(string profileKey, string keepPath)
    {
        try
        {
            var files = new DirectoryInfo(_directory)
                .GetFiles($"{profileKey}-*.json.gz")
                // The file just written is the one future launches want, even if
                // the clock says another file is newer (a copied/restored cache).
                .OrderByDescending(f => string.Equals(f.FullName, keepPath, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(f => f.LastWriteTimeUtc)
                .ThenBy(f => f.Name, StringComparer.Ordinal)
                .ToArray();
            foreach (var stale in files.Skip(KeepPerProfile)) stale.Delete();
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(DedupFingerprintCache), $"Could not prune old fingerprint caches: {e.Message}");
        }
    }
}

namespace Synapic.Indexer.Detection;

/// <summary>
/// Canonical queue keys. The key is the identity of a piece of work: the queue's
/// unique index on it is what makes enqueueing idempotent (SCAN-07), and every
/// comparison is case-insensitive because Windows paths are.
/// </summary>
public static class PathKey
{
    public const string LocalPrefix = "local|";
    public const string DaminionPrefix = "daminion|";

    /// <summary><c>local|C:\Photos\a.jpg</c> for a valid path; throws only for a path the caller owns.</summary>
    public static string ForLocal(string path)
    {
        if (!TryForLocal(path, out var key))
            throw new ArgumentException($"'{path}' is not a usable file path.", nameof(path));
        return key;
    }

    /// <summary>
    /// Canonical key for a path that came from the OS (a watcher callback, a scan).
    /// Returns false instead of throwing: a path that cannot be canonicalized must
    /// skip that file, never end a scan or kill a watcher thread.
    /// </summary>
    public static bool TryForLocal(string? path, out string key)
    {
        key = "";
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            var full = Path.GetFullPath(path);
            // Keep a drive root ("C:\") intact; trim the separator everywhere else.
            var canonical = full.Length > 3
                ? Path.TrimEndingDirectorySeparator(full)
                : full;
            if (canonical.Length == 0) return false;

            key = LocalPrefix + canonical;
            return true;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException
                                      or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static string ForDaminion(int id) => DaminionPrefix + id;
}

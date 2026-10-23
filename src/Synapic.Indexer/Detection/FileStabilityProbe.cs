namespace Synapic.Indexer.Detection;

/// <summary>
/// Answers "is this file finished being written?" — the gate between a watcher
/// event and a queue row (SCAN-03).
/// </summary>
public interface IFileStabilityProbe
{
    /// <summary>
    /// True only when the file can be opened with no sharing at all. A locked or
    /// missing file returns false with a short reason; this never throws for an
    /// ordinary filesystem race.
    /// </summary>
    bool TryOpenExclusive(string path, out string? reason);
}

/// <summary>
/// The real probe: <see cref="FileShare.None"/>. A writer still holding the file
/// makes this fail, which is exactly the answer wanted — a half-written TIFF must
/// not be handed to the tagger.
/// </summary>
public sealed class FileStabilityProbe : IFileStabilityProbe
{
    public bool TryOpenExclusive(string path, out string? reason)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.None, bufferSize: 1, FileOptions.None);
            reason = null;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
                                      or ArgumentException)
        {
            reason = e.GetType().Name;
            return false;
        }
    }
}

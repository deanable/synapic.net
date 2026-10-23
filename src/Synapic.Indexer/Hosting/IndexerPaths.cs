namespace Synapic.Indexer.Hosting;

/// <summary>
/// Where the service keeps its files under <c>%ProgramData%\Synapic</c>
/// (spec §5.1, §8.1). The root is a constructor value so tests can use a temp
/// folder instead of the machine's ProgramData.
/// </summary>
public sealed class IndexerPaths
{
    public IndexerPaths(string root) => Root = root;

    /// <summary><c>%ProgramData%\Synapic</c>.</summary>
    public static IndexerPaths Default => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Synapic"));

    public string Root { get; }
    public string LogsDirectory => Path.Combine(Root, "Logs");
    public string StateDirectory => Path.Combine(Root, "state");
    public string ConfigFilePath => Path.Combine(Root, "indexer-config.json");
    public string SidecarPidFile => Path.Combine(StateDirectory, "sidecar.pid");

    /// <summary>The durable job queue (spec §5.1): <c>state\indexer.db</c>.</summary>
    public string QueueFilePath => Path.Combine(StateDirectory, "indexer.db");

    /// <summary>
    /// Creates the three directories the service writes to. The installer sets
    /// their ACLs (05-04); this only ensures they exist.
    /// </summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(StateDirectory);
    }
}

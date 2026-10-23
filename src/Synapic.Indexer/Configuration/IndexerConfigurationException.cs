namespace Synapic.Indexer.Configuration;

/// <summary>
/// A configuration value that is well-formed enough to load but cannot be used —
/// a password that will not decrypt, a scope this build cannot query. Thrown
/// before the host is built, so the service exits with a message an operator can
/// act on instead of starting and failing every scan.
/// </summary>
public sealed class IndexerConfigurationException : Exception
{
    public IndexerConfigurationException(string setting, string problem)
        : base($"{setting} {problem}")
    {
        Setting = setting;
    }

    /// <summary>The dotted config path, e.g. <c>datasource.daminion.encryptedPassword</c>.</summary>
    public string Setting { get; }
}

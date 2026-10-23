namespace Synapic.Indexer.Configuration;

/// <summary>
/// The indexer's configuration, matching <c>docs/windows-indexer.md</c> §5.2
/// exactly (schema version 1). One typed shape for both sources: the registry
/// supplies machine defaults, the ProgramData JSON file overrides individual
/// values on top of them.
/// </summary>
public sealed class IndexerConfig
{
    public int Version { get; set; } = 1;
    public ServiceSettings Service { get; set; } = new();
    public ScheduleSettings Schedule { get; set; } = new();
    public EngineSettings Engine { get; set; } = new();
    public DatasourceSettings Datasource { get; set; } = new();
    public TaggingSettings Tagging { get; set; } = new();

    /// <summary>The spec's defaults, before either source is read.</summary>
    public static IndexerConfig CreateDefault() => new();
}

public sealed class ServiceSettings
{
    public bool AutoStart { get; set; } = true;
    public string LogLevel { get; set; } = "Information";
    public int MaxConcurrentItems { get; set; } = 4;

    /// <summary>Minutes of no work before the sidecar is stopped; 0 means never.</summary>
    public int IdleSidecarTimeoutMinutes { get; set; } = 30;
}

public sealed class ScheduleSettings
{
    /// <summary>Interval, Watch or IntervalAndWatch.</summary>
    public string Mode { get; set; } = "IntervalAndWatch";
    public int IntervalMinutes { get; set; } = 15;
    public List<string> WatchPaths { get; set; } = new();
    public bool WatchRecursive { get; set; } = true;
}

public sealed class EngineSettings
{
    public string Device { get; set; } = "cpu";
    public string ModelId { get; set; } = "";
    public double ConfidenceThreshold { get; set; } = 0.3;

    /// <summary>both or threshold (the sidecar's probability mode).</summary>
    public string ProbabilityMode { get; set; } = "both";
    public double ProbabilityThreshold { get; set; } = 0.5;
    public string? UserPrompt { get; set; }
}

public sealed class DatasourceSettings
{
    /// <summary>local or daminion.</summary>
    public string Type { get; set; } = "local";
    public DaminionSettings Daminion { get; set; } = new();
}

public sealed class DaminionSettings
{
    public string ServerUrl { get; set; } = "";
    public int CatalogId { get; set; }
    public string Username { get; set; } = "";

    /// <summary>DPAPI LocalMachine ciphertext; never the plaintext (D-11-07).</summary>
    public string? EncryptedPassword { get; set; }

    /// <summary>all, catalog, saved-search or collection.</summary>
    public string Scope { get; set; } = "all";

    /// <summary>Fields a record must be missing to count as untagged.</summary>
    public List<string> UntaggedFields { get; set; } = new();
}

public sealed class TaggingSettings
{
    public bool WriteCategory { get; set; } = true;
    public bool WriteKeywords { get; set; } = true;
    public bool WriteDescription { get; set; } = true;
    public bool OverwriteExistingTags { get; set; }
}

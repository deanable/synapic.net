using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synapic.Avalonia.Services;

/// <summary>
/// Local-only, opt-in usage telemetry (plan P6.1, spec §11 Q5). When
/// <c>ui.telemetryEnabled</c> is true in config.json, counters are recorded to
/// <c>synapic-usage.json</c> next to the log: batches processed, items and
/// failures, model usage, dedup runs, and app launches — plus the app version
/// and OS family so a support engineer can read usage from a user-shared
/// file. <b>No network access, ever.</b> Disabled by default; when disabled,
/// <see cref="TelemetryService"/> is a cheap no-op.
/// </summary>
public sealed class TelemetryService
{
    /// <summary>
    /// Process-wide instance for view models. Defaults to a disabled no-op so
    /// tests and pre-startup code never touch disk; App startup replaces it
    /// with the config-driven instance.
    /// </summary>
    public static TelemetryService Shared { get; set; } = new(enabled: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly bool _enabled;
    private readonly Func<DateTime> _now;
    private readonly object _writeLock = new();
    private UsageData _data;

    /// <summary>Record an application launch (called once per run at startup).</summary>
    public void RecordAppLaunch() => Update(d => d.Launches++);

    /// <summary>Record a finished tagging batch (Step 3).</summary>
    public void RecordBatch(int itemsProcessed, int itemsFailed, string? modelId, double durationSeconds)
        => Update(d =>
        {
            d.Batches++;
            d.ItemsProcessed += Math.Max(0, itemsProcessed);
            d.ItemsFailed += Math.Max(0, itemsFailed);
            d.TotalInferenceSeconds += Math.Max(0, durationSeconds);
            if (!string.IsNullOrEmpty(modelId))
            {
                d.ModelUsage.TryGetValue(modelId, out var count);
                d.ModelUsage[modelId] = count + 1;
            }
        });

    /// <summary>Record a dedup run (Step D).</summary>
    public void RecordDedup(int duplicatesFound) => Update(d =>
    {
        d.DedupRuns++;
        d.DuplicatesFound += Math.Max(0, duplicatesFound);
    });

    /// <summary>Record a metadata writeback (Step 4).</summary>
    public void RecordMetadataWrite(int count) => Update(d => d.MetadataWrites += Math.Max(0, count));

    public TelemetryService(
        bool enabled,
        string? filePath = null,
        Func<DateTime>? now = null,
        string? appVersion = null,
        string? osFamily = null)
    {
        _enabled = enabled;
        _filePath = filePath ?? Path.Combine(
            SynapicLog.LogDirectory.Length > 0
                ? SynapicLog.LogDirectory
                : AppContext.BaseDirectory,
            "synapic-usage.json");
        _now = now ?? (() => DateTime.UtcNow);
        _data = Load();
        if (!string.IsNullOrEmpty(appVersion)) _data.AppVersion = appVersion;
        if (!string.IsNullOrEmpty(osFamily)) _data.OsFamily = osFamily;
    }

    public bool Enabled => _enabled;

    /// <summary>Current counters (a copy — for the UI or tests).</summary>
    public UsageData Snapshot() { lock (_writeLock) { return Clone(_data); } }

    private void Update(Action<UsageData> mutate)
    {
        if (!_enabled) return;
        try
        {
            lock (_writeLock)
            {
                mutate(_data);
                _data.LastUpdatedUtc = _now();
                Write();
            }
        }
        catch (Exception e)
        {
            // Telemetry must never break the app; swallow and continue.
            SynapicLog.Warning(nameof(TelemetryService), $"Telemetry write failed: {e.Message}");
        }
    }

    private void Write()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(_data, JsonOptions));
    }

    private UsageData Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new UsageData();
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<UsageData>(json, JsonOptions) ?? new UsageData();
        }
        catch (Exception)
        {
            // Corrupt usage file: start fresh, never block startup.
            return new UsageData();
        }
    }

    private static UsageData Clone(UsageData d) => new()
    {
        Launches = d.Launches,
        Batches = d.Batches,
        ItemsProcessed = d.ItemsProcessed,
        ItemsFailed = d.ItemsFailed,
        TotalInferenceSeconds = d.TotalInferenceSeconds,
        DedupRuns = d.DedupRuns,
        DuplicatesFound = d.DuplicatesFound,
        MetadataWrites = d.MetadataWrites,
        LastUpdatedUtc = d.LastUpdatedUtc,
        AppVersion = d.AppVersion,
        OsFamily = d.OsFamily,
        ModelUsage = new Dictionary<string, int>(d.ModelUsage),
    };
}

/// <summary>Aggregate usage counters (local-only file, user-shareable).</summary>
public sealed class UsageData
{
    [JsonPropertyName("launches")]
    public int Launches { get; set; }

    [JsonPropertyName("batches")]
    public int Batches { get; set; }

    [JsonPropertyName("itemsProcessed")]
    public int ItemsProcessed { get; set; }

    [JsonPropertyName("itemsFailed")]
    public int ItemsFailed { get; set; }

    [JsonPropertyName("totalInferenceSeconds")]
    public double TotalInferenceSeconds { get; set; }

    [JsonPropertyName("dedupRuns")]
    public int DedupRuns { get; set; }

    [JsonPropertyName("duplicatesFound")]
    public int DuplicatesFound { get; set; }

    [JsonPropertyName("metadataWrites")]
    public int MetadataWrites { get; set; }

    [JsonPropertyName("lastUpdatedUtc")]
    public DateTime? LastUpdatedUtc { get; set; }

    /// <summary>App version at last write (informational, filled by App startup).</summary>
    [JsonPropertyName("appVersion")]
    public string? AppVersion { get; set; }

    /// <summary>OS family (Win/Linux/macOS — no version, no hardware info).</summary>
    [JsonPropertyName("osFamily")]
    public string? OsFamily { get; set; }

    [JsonPropertyName("modelUsage")]
    public Dictionary<string, int> ModelUsage { get; set; } = new(StringComparer.Ordinal);
}

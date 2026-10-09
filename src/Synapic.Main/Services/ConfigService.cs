using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Synapic.Shared;

namespace Synapic.Main.Services;

/// <summary>UI/application settings persisted to %APPDATA%/Synapic/config.json (spec §6.1).</summary>
public sealed class AppConfig
{
    public int Version { get; set; } = 2;

    public DatasourceSettings Datasource { get; set; } = new();
    public EngineSettings Engine { get; set; } = new();
    public ProcessingSettings Processing { get; set; } = new();
    public UiSettings Ui { get; set; } = new();

    public static string DefaultDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appData, "Synapic");
            }
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var baseDir = string.IsNullOrEmpty(configHome)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : configHome;
            return Path.Combine(baseDir, "Synapic");
        }
    }

    public static string DefaultFilePath => Path.Combine(DefaultDirectory, "config.json");
}

public sealed class DatasourceSettings
{
    public string Type { get; set; } = "local";
    public string LocalPath { get; set; } = "";
    public bool LocalRecursive { get; set; }
    public DaminionSettings Daminion { get; set; } = new();
}

public sealed class DaminionSettings
{
    public string ServerUrl { get; set; } = "";
    public string Username { get; set; } = "";
    // Password is intentionally NOT persisted in plain config (entered per session).
    public string Scope { get; set; } = "all";
    public string SavedSearchId { get; set; } = "";
    public string CollectionId { get; set; } = "";
    public string SearchTerm { get; set; } = "";
    public bool UntaggedKeywords { get; set; }
    public bool UntaggedCategories { get; set; }
    public bool UntaggedDescription { get; set; }
    public string StatusFilter { get; set; } = "all";
    public int MaxItems { get; set; } = 100;
}

public sealed class EngineSettings
{
    public string ModelId { get; set; } = "LiquidAI/LFM2.5-VL-450M";
    public string Task { get; set; } = "image-text-to-text";
    public string Device { get; set; } = "cpu";
    public double ConfidenceThreshold { get; set; } = 0.3;
    public string ProbabilityMode { get; set; } = "both";
    public double ProbabilityThreshold { get; set; } = 0.5;
    public string[] ProbabilityCandidates { get; set; } = Array.Empty<string>();
    public string SystemPrompt { get; set; } = "";

    /// <summary>
    /// Custom tag instruction. Empty means the sidecar's built-in instruction,
    /// which is why a blank value is the safe thing to persist rather than a copy
    /// of the built-in wording that would freeze at today's version of it.
    /// </summary>
    public string UserPrompt { get; set; } = "";

    public bool EmbeddingRescueEnabled { get; set; }
}

public sealed class ProcessingSettings
{
    public int MaxItems { get; set; }
    public bool AutoPaginate { get; set; } = true;
    public int ResizeScale { get; set; } = 100;
    public bool UseThumbnailOverride { get; set; }
}

public sealed class UiSettings
{
    public string Theme { get; set; } = "system";
    public string LogLevel { get; set; } = "info";
    // Server lifecycle (user requirement): start with the app, stop with the
    // app. Set to false only for manual Start/Stop usage.
    public bool AutoLaunchSidecar { get; set; } = true;
    public bool TelemetryEnabled { get; set; }

    /// <summary>
    /// Diagnostics drawer (docs/ui-design.md §5 Logging, D7): shows the in-app
    /// log view in the shell. Off by default - the design keeps no
    /// always-visible log strip; run logs live in an operation's Output region
    /// and this is where the full feed is switched on.
    /// </summary>
    public bool ShowDiagnostics { get; set; }

    /// <summary>Default device for a session with no saved engine state ("" = leave the shipped default).</summary>
    public string DefaultDevice { get; set; } = "";

    /// <summary>Default tagging confidence for a new session (0 = leave the shipped default).</summary>
    public double DefaultConfidenceThreshold { get; set; }

    /// <summary>Default max items for a new session (0 = leave the shipped default).</summary>
    public int DefaultMaxItems { get; set; }
}

/// <summary>
/// Loads/saves the JSON app config. Save merges into the on-disk document:
/// keys this app version does not model (hand edits, forward-compatible
/// fields from a newer build) survive every save, and the write lands
/// atomically (temp file + move) so a crash mid-save cannot corrupt it.
/// </summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;

    public ConfigService(string? filePath = null)
    {
        _filePath = filePath ?? AppConfig.DefaultFilePath;
    }

    public string FilePath => _filePath;

    public AppConfig Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new AppConfig();
            var json = File.ReadAllText(_filePath);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
            return cfg ?? new AppConfig();
        }
        catch (Exception e)
        {
            SynapicLog.Warning("ConfigService", $"Failed to load config ({e.Message}); using defaults");
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Merge into the on-disk document rather than replacing it, so
            // unknown fields survive the round-trip (the typed model would
            // otherwise silently drop them on every save).
            var root = ReadRawRoot(_filePath);
            ReplaceSection(root, nameof(AppConfig.Datasource), config.Datasource);
            ReplaceSection(root, nameof(AppConfig.Engine), config.Engine);
            ReplaceSection(root, nameof(AppConfig.Processing), config.Processing);
            ReplaceSection(root, nameof(AppConfig.Ui), config.Ui);
            root[nameof(AppConfig.Version)] = config.Version;

            // Atomic write: a temp file in the same directory, then a single
            // move. A crash mid-write leaves a stray .tmp, never a corrupt
            // config, and readers never see a half-written file.
            var tmpPath = _filePath + ".tmp";
            try
            {
                File.WriteAllText(tmpPath, JsonSerializer.Serialize(root, Options));
                File.Move(tmpPath, _filePath, overwrite: true);
            }
            finally
            {
                try { File.Delete(tmpPath); }
                catch (IOException) { /* move already consumed it / best effort */ }
            }
        }
        catch (Exception e)
        {
            SynapicLog.Error("ConfigService", $"Failed to save config: {e.Message}");
        }
    }

    /// <summary>On-disk document of the config file as a JsonObject (empty when missing/unreadable).</summary>
    private static JsonObject ReadRawRoot(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return new JsonObject();
            return JsonNode.Parse(File.ReadAllText(filePath)) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject(); // nothing parseable: nothing to preserve
        }
    }

    /// <summary>
    /// Replace one known top-level section with its typed value, then copy
    /// back any keys the typed model does not have (hand edits or fields a
    /// newer app version added below a known section) so they survive.
    /// </summary>
    private static void ReplaceSection(JsonObject root, string name, object section)
    {
        var fresh = JsonSerializer.SerializeToNode(section, Options) is JsonObject freshObject
            ? freshObject
            : new JsonObject();
        if (root[name] is JsonObject previous)
            CopyUnknownKeys(previous, fresh);
        root[name] = fresh;
    }

    /// <summary>Recursively copy object keys absent from <paramref name="fresh"/> out of <paramref name="previous"/>.</summary>
    private static void CopyUnknownKeys(JsonObject previous, JsonObject fresh)
    {
        foreach (var (key, value) in previous)
        {
            if (fresh.ContainsKey(key)) continue;
            fresh[key] = value?.DeepClone();
        }
    }
}

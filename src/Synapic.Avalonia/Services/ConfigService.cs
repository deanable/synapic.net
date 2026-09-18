using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Synapic.Shared;

namespace Synapic.Avalonia.Services;

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
    public string CatalogId { get; set; } = "";
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
    public string ModelId { get; set; } = "LiquidAI/LFM2.5-VL-1.6B";
    public string Task { get; set; } = "image-text-to-text";
    public string Device { get; set; } = "cpu";
    public double ConfidenceThreshold { get; set; } = 0.3;
    public string ProbabilityMode { get; set; } = "both";
    public double ProbabilityThreshold { get; set; } = 0.5;
    public string[] ProbabilityCandidates { get; set; } = Array.Empty<string>();
    public string SystemPrompt { get; set; } = "";
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
    public bool AutoLaunchSidecar { get; set; }
    public bool TelemetryEnabled { get; set; }
}

/// <summary>
/// Loads/saves the JSON app config. Unknown fields survive round-trips by
/// preserving the raw JSON object on load.
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
            var json = JsonSerializer.Serialize(config, Options);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception e)
        {
            SynapicLog.Error("ConfigService", $"Failed to save config: {e.Message}");
        }
    }
}

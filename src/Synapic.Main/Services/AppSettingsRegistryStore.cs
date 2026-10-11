using Microsoft.Win32;
using System.IO;
using System.Text.Json;

namespace Synapic.Main.Services;

/// <summary>
/// One snapshot of every general app setting: appearance, logging, defaults,
/// dedup rules, upscaling parameters and the dedup move destination. Persisted
/// as a flat value map under HKCU\Software\Synapic\AppData.
///
/// Everything the app has to remember now lives in the registry (user request):
/// the Daminion connection (~\Synapic\Daminion), the engine settings
/// (~\Synapic\Engine) and these general settings here. The old
/// %APPDATA%\Synapic\config.json is imported once into this store and kept as
/// a backup — the app reads it no longer.
/// </summary>
public sealed record AppSettingsState(
    // Appearance / logging
    string Theme,
    string LogLevel,
    bool ShowDiagnostics,
    bool AutoLaunchSidecar,
    bool TelemetryEnabled,

    // Defaults for a new session
    string DefaultDevice,
    double DefaultConfidenceThreshold,
    int DefaultMaxItems,

    // Dedup scan + keep-set + action destination
    string DedupAlgorithm,          // algorithm name ("PHash"…)
    double DedupThreshold,
    string DedupServerHashMatch,    // "Exact" | "Hamming"
    bool DedupSelectOldest,
    bool DedupSelectNewest,
    bool DedupSelectSmallest,
    bool DedupSelectLargest,
    int DedupSelectedAction,
    string DedupMoveDestination,

    // Upscaling parameters
    int UpscaleWorkflow,            // dropdown index
    int UpscaleFactor,              // dropdown index
    int UpscalePrecision,           // dropdown index
    int UpscaleOutputFormat,        // dropdown index
    int UpscaleJpegQuality,
    double UpscaleDenoiseStrength,
    double UpscaleSharpenAmount,
    bool UpscaleOverwriteExisting,

    // Data source (local) — mirrors the Daminion store's role for folders
    string LocalPath,
    bool LocalRecursive,

    // Processing limits
    int MaxItems,
    bool ProcessAll,
    int ResizeScale,
    bool UseThumbnailOverride);

/// <summary>
/// Registry store for the general app settings. Flat string/dword values, no
/// DPAPI needed (no secrets here). On load, a one-time migration imports
/// %APPDATA%\Synapic\config.json when the registry key is empty and the file
/// exists — so an update from a JSON-era build carries every setting across.
/// On non-Windows the store is a no-op (the JSON path stays the fallback there,
/// matching DaminionConnectionStore/EngineSettingsStore).
/// </summary>
public sealed class AppSettingsRegistryStore
{
    private const string KeyPath = @"Software\Synapic\AppData";
    private const string MigratedValue = "_MigratedFromJson";

    private readonly string? _keyPathOverride;
    private readonly AppConfig _typedConfig; // shape + defaults for the import

    public AppSettingsRegistryStore(string? keyPathOverride = null)
    {
        _keyPathOverride = keyPathOverride;
        _typedConfig = new AppConfig();
    }

    private string EffectiveKeyPath => _keyPathOverride ?? KeyPath;

    private static string V(string name) => name;

    /// <summary>Load the saved settings; null when nothing has been saved yet.</summary>
    public AppSettingsState? Load()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(EffectiveKeyPath);
            if (key is null) return null;
            if (key.GetValue(MigratedValue) is not int flag || flag == 0)
            {
                // Registry key present but never populated by this store: treat
                // as empty so the JSON import still runs.
                MigrateFromJsonIfPresent(key);
            }

            string? S(string name) => key.GetValue(name) as string;
            int I(string name, int fallback) => key.GetValue(name) is int v ? v : fallback;
            double D(string name, double fallback) =>
                key.GetValue(name) is string s &&
                double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                    out var d)
                    ? d
                    : fallback;
            bool B(string name) => key.GetValue(name) is int v && v != 0;

            if (S("Theme") is null) return null;   // nothing meaningful saved

            return new AppSettingsState(
                Theme: S("Theme") ?? _typedConfig.Ui.Theme,
                LogLevel: S("LogLevel") ?? _typedConfig.Ui.LogLevel,
                ShowDiagnostics: B("ShowDiagnostics"),
                AutoLaunchSidecar: I("AutoLaunchSidecar", 1) != 0,
                TelemetryEnabled: B("TelemetryEnabled"),
                DefaultDevice: S("DefaultDevice") ?? "",
                DefaultConfidenceThreshold: D("DefaultConfidenceThreshold", 0),
                DefaultMaxItems: I("DefaultMaxItems", 0),
                DedupAlgorithm: S("DedupAlgorithm") ?? "PHash",
                DedupThreshold: D("DedupThreshold", 0.90),
                DedupServerHashMatch: S("DedupServerHashMatch") ?? "Exact",
                DedupSelectOldest: B("DedupSelectOldest"),
                DedupSelectNewest: B("DedupSelectNewest"),
                DedupSelectSmallest: B("DedupSelectSmallest"),
                DedupSelectLargest: B("DedupSelectLargest"),
                DedupSelectedAction: I("DedupSelectedAction", 0),
                DedupMoveDestination: S("DedupMoveDestination") ?? "",
                UpscaleWorkflow: I("UpscaleWorkflow", 0),
                UpscaleFactor: I("UpscaleFactor", 0),
                UpscalePrecision: I("UpscalePrecision", 0),
                UpscaleOutputFormat: I("UpscaleOutputFormat", 0),
                UpscaleJpegQuality: I("UpscaleJpegQuality", 95),
                UpscaleDenoiseStrength: D("UpscaleDenoiseStrength", 1.0),
                UpscaleSharpenAmount: D("UpscaleSharpenAmount", 0),
                UpscaleOverwriteExisting: I("UpscaleOverwriteExisting", 1) != 0,
                LocalPath: S("LocalPath") ?? "",
                LocalRecursive: B("LocalRecursive"),
                MaxItems: I("MaxItems", 100),
                ProcessAll: B("ProcessAll"),
                ResizeScale: I("ResizeScale", 100),
                UseThumbnailOverride: B("UseThumbnailOverride"));
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(AppSettingsRegistryStore),
                $"Failed to load app settings from registry: {e.Message}");
            return null;
        }
    }

    /// <summary>Save the whole state (all values written each time).</summary>
    public void Save(AppSettingsState state)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(EffectiveKeyPath, writable: true);

            void S(string name, string value) => key.SetValue(name, value, RegistryValueKind.String);
            void I(string name, int value) => key.SetValue(name, value, RegistryValueKind.DWord);
            void D(string name, double value) => key.SetValue(
                name, value.ToString(System.Globalization.CultureInfo.InvariantCulture), RegistryValueKind.String);
            void B(string name, bool value) => I(name, value ? 1 : 0);

            S("Theme", state.Theme);
            S("LogLevel", state.LogLevel);
            B("ShowDiagnostics", state.ShowDiagnostics);
            I("AutoLaunchSidecar", state.AutoLaunchSidecar ? 1 : 0);
            B("TelemetryEnabled", state.TelemetryEnabled);
            S("DefaultDevice", state.DefaultDevice);
            D("DefaultConfidenceThreshold", state.DefaultConfidenceThreshold);
            I("DefaultMaxItems", state.DefaultMaxItems);
            S("DedupAlgorithm", state.DedupAlgorithm);
            D("DedupThreshold", state.DedupThreshold);
            S("DedupServerHashMatch", state.DedupServerHashMatch);
            B("DedupSelectOldest", state.DedupSelectOldest);
            B("DedupSelectNewest", state.DedupSelectNewest);
            B("DedupSelectSmallest", state.DedupSelectSmallest);
            B("DedupSelectLargest", state.DedupSelectLargest);
            I("DedupSelectedAction", state.DedupSelectedAction);
            S("DedupMoveDestination", state.DedupMoveDestination);
            I("UpscaleWorkflow", state.UpscaleWorkflow);
            I("UpscaleFactor", state.UpscaleFactor);
            I("UpscalePrecision", state.UpscalePrecision);
            I("UpscaleOutputFormat", state.UpscaleOutputFormat);
            I("UpscaleJpegQuality", state.UpscaleJpegQuality);
            D("UpscaleDenoiseStrength", state.UpscaleDenoiseStrength);
            D("UpscaleSharpenAmount", state.UpscaleSharpenAmount);
            I("UpscaleOverwriteExisting", state.UpscaleOverwriteExisting ? 1 : 0);
            S("LocalPath", state.LocalPath);
            B("LocalRecursive", state.LocalRecursive);
            I("MaxItems", state.MaxItems);
            B("ProcessAll", state.ProcessAll);
            I("ResizeScale", state.ResizeScale);
            B("UseThumbnailOverride", state.UseThumbnailOverride);
            key.SetValue(MigratedValue, 1, RegistryValueKind.DWord);

            SynapicLog.Info(nameof(AppSettingsRegistryStore),
                $"Saved app settings to registry ({EffectiveKeyPath})");
        }
        catch (Exception e)
        {
            SynapicLog.Error(nameof(AppSettingsRegistryStore),
                $"Failed to save app settings to registry: {e.Message}");
        }
    }

    /// <summary>
    /// One-time import: when the registry holds no usable settings and the old
    /// %APPDATA%\Synapic\config.json exists, map every field it knows into the
    /// state record and write it. The workflow settings (dedup/upscale) and the
    /// move destination never lived in the old format, so they just keep their
    /// defaults. The JSON file is left in place as a backup.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void MigrateFromJsonIfPresent(RegistryKey key)
    {
        try
        {
            // Already-saved values win; migration only fills an empty store.
            if (key.GetValue("Theme") is not null) return;
            if (!File.Exists(AppConfig.DefaultFilePath)) return;

            var json = File.ReadAllText(AppConfig.DefaultFilePath);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (cfg is null) return;

            var imported = new AppSettingsState(
                Theme: string.IsNullOrWhiteSpace(cfg.Ui.Theme) ? _typedConfig.Ui.Theme : cfg.Ui.Theme,
                LogLevel: string.IsNullOrWhiteSpace(cfg.Ui.LogLevel) ? _typedConfig.Ui.LogLevel : cfg.Ui.LogLevel,
                ShowDiagnostics: cfg.Ui.ShowDiagnostics,
                AutoLaunchSidecar: cfg.Ui.AutoLaunchSidecar,
                TelemetryEnabled: cfg.Ui.TelemetryEnabled,
                DefaultDevice: cfg.Ui.DefaultDevice,
                DefaultConfidenceThreshold: cfg.Ui.DefaultConfidenceThreshold,
                DefaultMaxItems: cfg.Ui.DefaultMaxItems,
                DedupAlgorithm: "PHash",
                DedupThreshold: 0.90,
                DedupServerHashMatch: "Exact",
                DedupSelectOldest: false,
                DedupSelectNewest: false,
                DedupSelectSmallest: false,
                DedupSelectLargest: false,
                DedupSelectedAction: 0,
                DedupMoveDestination: "",
                UpscaleWorkflow: 0,
                UpscaleFactor: 0,
                UpscalePrecision: 0,
                UpscaleOutputFormat: 0,
                UpscaleJpegQuality: 95,
                UpscaleDenoiseStrength: 1.0,
                UpscaleSharpenAmount: 0,
                UpscaleOverwriteExisting: true,
                LocalPath: cfg.Datasource.LocalPath,
                LocalRecursive: cfg.Datasource.LocalRecursive,
                MaxItems: cfg.Processing.MaxItems > 0 ? cfg.Processing.MaxItems : 100,
                ProcessAll: false,
                ResizeScale: cfg.Processing.ResizeScale > 0 ? cfg.Processing.ResizeScale : 100,
                UseThumbnailOverride: cfg.Processing.UseThumbnailOverride);
            Save(imported);
            SynapicLog.Info(nameof(AppSettingsRegistryStore),
                "Imported legacy config.json into the registry (one-time migration; the JSON file was kept as a backup)");
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(AppSettingsRegistryStore),
                $"Legacy JSON import skipped: {e.Message}");
        }
    }
}

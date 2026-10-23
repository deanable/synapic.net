using Microsoft.Win32;
using System.IO;
using System.Text.Json;

using Synapic.Core.Services;
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

    /// <summary>First-run automation: fetch the suited sidecar variant when
    /// none exists, then start the server. Off restores ask-first.</summary>
    bool AutoInstallSidecar,

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

    // Similar-image search scan + keep-set + action destination. A second,
    // independent set: the similarity search is its own operation with its own
    // tab, so widening one search must never widen the other.
    string SimilarAlgorithm,        // algorithm name ("PHash"…)
    double SimilarThreshold,
    string SimilarServerHashMatch,  // "Exact" | "Hamming"
    bool SimilarSelectOldest,
    bool SimilarSelectNewest,
    bool SimilarSelectSmallest,
    bool SimilarSelectLargest,
    int SimilarSelectedAction,
    string SimilarMoveDestination,

    // Upscaling parameters
    int UpscaleWorkflow,            // dropdown index
    int UpscaleFactor,              // dropdown index
    int UpscalePrecision,           // dropdown index
    int UpscaleOutputFormat,        // dropdown index
    int UpscaleJpegQuality,
    double UpscaleDenoiseStrength,
    double UpscaleSharpenAmount,
    bool UpscaleOverwriteExisting,

    // Data source: which one the next launch starts on, and the local folder
    // it remembers. The Daminion half (server, credentials, scope, filters)
    // lives in DaminionConnectionStore; this only records that the catalog was
    // the active source, so the launch sequence reconnects instead of falling
    // back to the folder tab.
    string DatasourceType,          // "local" | "daminion"
    string LocalPath,
    bool LocalRecursive,

    // Processing limits
    int MaxItems,
    bool ProcessAll,
    int ResizeScale,
    bool UseThumbnailOverride)
{
    /// <summary>
    /// The stored source in the session's own shape, so launch restores it with
    /// the same call the old config.json path used
    /// (<see cref="Synapic.Main.Models.DatasourceState.ApplyStoredSource"/>).
    /// Only the identity of the source: its scope and limits are the forms'
    /// own settings.
    /// </summary>
    public DatasourceSettings ToDatasourceSettings() => new()
    {
        Type = DatasourceType == "daminion" ? "daminion" : "local",
        LocalPath = LocalPath ?? "",
        LocalRecursive = LocalRecursive,
    };
}

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

    /// <summary>
    /// The similarity search starts looser than the duplicate scan: 0.84 lets
    /// ten of the 64 hash bits differ, which is the band a re-encode, a crop or a
    /// light edit lands in, where 0.90 would still call them different pictures.
    /// Public so the settings panel and its tests name one default.
    /// </summary>
    public const double SimilarThresholdDefault = 0.84;

    /// <summary>The duplicate scan's starting threshold for pHash (0.90: at most
    /// six of the 64 bits differ).</summary>
    public const double DedupThresholdDefault = 0.90;

    // The thresholds are not one scale. The three fingerprints differ hugely in
    // how far apart they put two *different* pictures: measured on 22 real
    // photographs (Windows wallpapers), scored over 220 same-photo pairs
    // (re-encode, resize, brightness, crop) and 462 different-photo pairs, the
    // nearest pair of different pictures sat 10 bits apart under pHash, 3 under
    // dHash and 1 under aHash. A threshold that is safe for pHash therefore is
    // not safe for the others: dHash at pHash's 0.90 merges 0.87% of different
    // pairs into duplicate groups, aHash 1.73%, and aHash reached zero false
    // merges at no budget below 1.0 at all. Each number below is the loosest
    // setting that measured no false merges, or — for aHash, whose measured
    // zero-error budget was 0 bits — the tightest useful one.
    //
    // The budget is floored (see DedupService.MaxHammingDistance), so a default
    // written as the measured percentage would lose a bit to the floor. The
    // numbers below are chosen to keep the intended budget: 0.96 → 2 bits,
    // 0.98 → 1 bit, 0.90 → 6, 0.84 → 10, 0.93 → 4.

    /// <summary>The starting threshold for <paramref name="algorithm"/>, which is
    /// the one the duplicate scan should use when the profile has no saved
    /// value: it depends on the fingerprint, so a store that knows only the
    /// selected algorithm still resolves the right default.</summary>
    public static double DedupThresholdDefaultFor(string? algorithm) => algorithm switch
    {
        "DHash" => 0.96, //  2 bits:  0% of different pairs merged, 78.2% caught
        "AHash" => 0.98, //  1 bit:   tightest useful; first false merge at 4 bits
        _ => DedupThresholdDefault,
    };

    /// <summary>The similarity search's starting threshold for
    /// <paramref name="algorithm"/>. Its own numbers are looser than the duplicate
    /// scan's throughout — a false group is a browsing annoyance there, not a
    /// moved or deleted file.</summary>
    public static double SimilarThresholdDefaultFor(string? algorithm) => algorithm switch
    {
        "DHash" => 0.90, //  6 bits: 0.87% of different pairs merged, 91.8% caught
        "AHash" => 0.93, //  4 bits: 1.30% merged, 96.8% caught
        _ => SimilarThresholdDefault,
    };

    private readonly string? _keyPathOverride;
    private readonly AppConfig _typedConfig; // shape + defaults for the import

    public AppSettingsRegistryStore(string? keyPathOverride = null)
    {
        _keyPathOverride = keyPathOverride;
        _typedConfig = new AppConfig();
    }

    private string EffectiveKeyPath => _keyPathOverride ?? KeyPath;

    /// <summary>
    /// A complete state whose values are the shipped app defaults — the same
    /// state a first save would have produced. The load-then-modify writers
    /// use it when the registry has nothing yet, so a step or the settings view
    /// never has to carry its own positional copy of every default.
    /// </summary>
    public AppSettingsState Defaults => new AppSettingsState(
        Theme: _typedConfig.Ui.Theme,
        LogLevel: _typedConfig.Ui.LogLevel,
        ShowDiagnostics: _typedConfig.Ui.ShowDiagnostics,
        AutoLaunchSidecar: _typedConfig.Ui.AutoLaunchSidecar,
        AutoInstallSidecar: _typedConfig.Ui.AutoInstallSidecar,
        TelemetryEnabled: _typedConfig.Ui.TelemetryEnabled,
        DefaultDevice: _typedConfig.Ui.DefaultDevice,
        DefaultConfidenceThreshold: _typedConfig.Ui.DefaultConfidenceThreshold,
        DefaultMaxItems: _typedConfig.Ui.DefaultMaxItems,
        DedupAlgorithm: "PHash",
        DedupThreshold: DedupThresholdDefault,
        DedupServerHashMatch: "Exact",
        DedupSelectOldest: false,
        DedupSelectNewest: false,
        DedupSelectSmallest: false,
        DedupSelectLargest: false,
        DedupSelectedAction: 0,
        DedupMoveDestination: "",
        SimilarAlgorithm: "PHash",
        SimilarThreshold: SimilarThresholdDefault,
        SimilarServerHashMatch: "Exact",
        SimilarSelectOldest: false,
        SimilarSelectNewest: false,
        SimilarSelectSmallest: false,
        SimilarSelectLargest: false,
        SimilarSelectedAction: 0,
        SimilarMoveDestination: "",
        UpscaleWorkflow: 0,
        UpscaleFactor: 0,
        UpscalePrecision: 0,
        UpscaleOutputFormat: 0,
        UpscaleJpegQuality: 95,
        UpscaleDenoiseStrength: 1.0,
        UpscaleSharpenAmount: 0,
        UpscaleOverwriteExisting: false,
        DatasourceType: "local",
        LocalPath: "",
        LocalRecursive: false,
        MaxItems: 100,
        ProcessAll: false,
        ResizeScale: 100,
        UseThumbnailOverride: false);

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
                AutoInstallSidecar: I("AutoInstallSidecar", 1) != 0,
                TelemetryEnabled: B("TelemetryEnabled"),
                DefaultDevice: S("DefaultDevice") ?? "",
                DefaultConfidenceThreshold: D("DefaultConfidenceThreshold", 0),
                DefaultMaxItems: I("DefaultMaxItems", 0),
                DedupAlgorithm: S("DedupAlgorithm") ?? "PHash",
                // An absent threshold takes the default of the *saved* algorithm,
                // not pHash's: this fallback fires for a profile that was never
                // touched, and the fingerprints are not on a common scale.
                DedupThreshold: D("DedupThreshold", DedupThresholdDefaultFor(S("DedupAlgorithm"))),
                DedupServerHashMatch: S("DedupServerHashMatch") ?? "Exact",
                DedupSelectOldest: B("DedupSelectOldest"),
                DedupSelectNewest: B("DedupSelectNewest"),
                DedupSelectSmallest: B("DedupSelectSmallest"),
                DedupSelectLargest: B("DedupSelectLargest"),
                DedupSelectedAction: I("DedupSelectedAction", 0),
                DedupMoveDestination: S("DedupMoveDestination") ?? "",
                SimilarAlgorithm: S("SimilarAlgorithm") ?? "PHash",
                SimilarThreshold: D("SimilarThreshold", SimilarThresholdDefaultFor(S("SimilarAlgorithm"))),
                SimilarServerHashMatch: S("SimilarServerHashMatch") ?? "Exact",
                SimilarSelectOldest: B("SimilarSelectOldest"),
                SimilarSelectNewest: B("SimilarSelectNewest"),
                SimilarSelectSmallest: B("SimilarSelectSmallest"),
                SimilarSelectLargest: B("SimilarSelectLargest"),
                SimilarSelectedAction: I("SimilarSelectedAction", 0),
                SimilarMoveDestination: S("SimilarMoveDestination") ?? "",
                UpscaleWorkflow: I("UpscaleWorkflow", 0),
                UpscaleFactor: I("UpscaleFactor", 0),
                UpscalePrecision: I("UpscalePrecision", 0),
                UpscaleOutputFormat: I("UpscaleOutputFormat", 0),
                UpscaleJpegQuality: I("UpscaleJpegQuality", 95),
                UpscaleDenoiseStrength: D("UpscaleDenoiseStrength", 1.0),
                UpscaleSharpenAmount: D("UpscaleSharpenAmount", 0),
                UpscaleOverwriteExisting: I("UpscaleOverwriteExisting", 1) != 0,
                DatasourceType: S("DatasourceType") == "daminion" ? "daminion" : "local",
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
            I("AutoInstallSidecar", state.AutoInstallSidecar ? 1 : 0);
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
            S("SimilarAlgorithm", state.SimilarAlgorithm);
            D("SimilarThreshold", state.SimilarThreshold);
            S("SimilarServerHashMatch", state.SimilarServerHashMatch);
            B("SimilarSelectOldest", state.SimilarSelectOldest);
            B("SimilarSelectNewest", state.SimilarSelectNewest);
            B("SimilarSelectSmallest", state.SimilarSelectSmallest);
            B("SimilarSelectLargest", state.SimilarSelectLargest);
            I("SimilarSelectedAction", state.SimilarSelectedAction);
            S("SimilarMoveDestination", state.SimilarMoveDestination);
            I("UpscaleWorkflow", state.UpscaleWorkflow);
            I("UpscaleFactor", state.UpscaleFactor);
            I("UpscalePrecision", state.UpscalePrecision);
            I("UpscaleOutputFormat", state.UpscaleOutputFormat);
            I("UpscaleJpegQuality", state.UpscaleJpegQuality);
            D("UpscaleDenoiseStrength", state.UpscaleDenoiseStrength);
            D("UpscaleSharpenAmount", state.UpscaleSharpenAmount);
            I("UpscaleOverwriteExisting", state.UpscaleOverwriteExisting ? 1 : 0);
            S("DatasourceType", state.DatasourceType);
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
                AutoInstallSidecar: cfg.Ui.AutoInstallSidecar,
                TelemetryEnabled: cfg.Ui.TelemetryEnabled,
                DefaultDevice: cfg.Ui.DefaultDevice,
                DefaultConfidenceThreshold: cfg.Ui.DefaultConfidenceThreshold,
                DefaultMaxItems: cfg.Ui.DefaultMaxItems,
                DedupAlgorithm: "PHash",
                DedupThreshold: DedupThresholdDefault,
                DedupServerHashMatch: "Exact",
                DedupSelectOldest: false,
                DedupSelectNewest: false,
                DedupSelectSmallest: false,
                DedupSelectLargest: false,
                DedupSelectedAction: 0,
                DedupMoveDestination: "",
                SimilarAlgorithm: "PHash",
                SimilarThreshold: SimilarThresholdDefault,
                SimilarServerHashMatch: "Exact",
                SimilarSelectOldest: false,
                SimilarSelectNewest: false,
                SimilarSelectSmallest: false,
                SimilarSelectLargest: false,
                SimilarSelectedAction: 0,
                SimilarMoveDestination: "",
                UpscaleWorkflow: 0,
                UpscaleFactor: 0,
                UpscalePrecision: 0,
                UpscaleOutputFormat: 0,
                UpscaleJpegQuality: 95,
                UpscaleDenoiseStrength: 1.0,
                UpscaleSharpenAmount: 0,
                UpscaleOverwriteExisting: false,
                DatasourceType: cfg.Datasource.Type == "daminion" ? "daminion" : "local",
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

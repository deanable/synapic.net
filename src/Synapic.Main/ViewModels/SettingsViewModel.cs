using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Main.Models;
using Synapic.Main.Services;

namespace Synapic.Main.ViewModels;

/// <summary>One choice in a settings selector: what it reads, and what it stores.</summary>
public sealed record SettingOption(string Label, string Value);

/// <summary>
/// The app-wide settings view (docs/ui-design.md §5, decision D3): Inference
/// server, Appearance, Logging, Defaults and About, in reading order.
///
/// This is the app-wide half of the settings split §5 draws — per-operation
/// tunables live inline in the operation template's Parameters region, so
/// nothing is configured in two places, and the Source section that used to sit
/// on this panel moved to the shared Data source region of every operation (it
/// is one source for every mode, not an app preference).
///
/// §5 allows a sectioned nav beside the content or a single scrolling column;
/// this is the column, deliberately: all five sections are in the tree at once,
/// so the layout audit measures every one of them at every window size (a tab
/// control would realize only the selected section).
///
/// Every setting here applies immediately and persists through
/// <see cref="ConfigService"/>: the theme switches the running app, the log
/// level switches the live UI sink, the diagnostics toggle shows or hides the
/// shell's log view, and the Defaults section is what a session with no saved
/// engine state starts from.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    /// <summary>Where the About section's repository link points.</summary>
    public const string RepositoryUrl = "https://github.com/deanable/Synapic.NET";

    /// <summary>Theme choices, in the order the selector shows them (§5 Appearance).</summary>
    public static readonly IReadOnlyList<string> ThemeOptions = new[] { "System", "Light", "Dark" };

    /// <summary>UI log levels, in the order the selector shows them (§5 Logging).</summary>
    public static readonly IReadOnlyList<string> LogLevelOptions =
        new[] { "Verbose", "Debug", "Information", "Warning", "Error" };

    /// <summary>Default devices a new session may start on (§5 Defaults).</summary>
    public static readonly IReadOnlyList<SettingOption> DefaultDeviceOptions = new[]
    {
        new SettingOption("CPU", "cpu"),
        new SettingOption("CUDA", "cuda"),
        new SettingOption("MPS", "mps"),
    };

    private readonly Session _session;
    private readonly Func<ConfigService?> _configProvider;
    private readonly Action<string> _openExternal;

    public SettingsViewModel(
        Session session,
        Func<ConfigService?>? configProvider = null,
        Action<string>? openExternal = null)
    {
        _session = session;
        _configProvider = configProvider ?? (() => null);
        _openExternal = openExternal ?? OpenWithShell;

        // Loaded through the field initializers' defaults when there is no config
        // (a test, or an install that has never saved one): the panel then shows
        // exactly what the app is running with, not what it wishes it ran with.
        var config = _configProvider();
        var cfg = config?.Load();
        if (cfg is not null)
        {
            _themeIndex = IndexOf(ThemeOptions, cfg.Ui.Theme, ThemeText);
            _logLevelIndex = LogLevelIndexOf(cfg.Ui.LogLevel);
            _showDiagnostics = cfg.Ui.ShowDiagnostics;
            _autoLaunchSidecar = cfg.Ui.AutoLaunchSidecar;
            _defaultDeviceIndex = DefaultDeviceIndexOf(cfg.Ui.DefaultDevice);
            _defaultConfidenceThreshold = cfg.Ui.DefaultConfidenceThreshold > 0
                ? cfg.Ui.DefaultConfidenceThreshold
                : _session.Engine.ConfidenceThreshold;
            _defaultMaxItems = cfg.Ui.DefaultMaxItems > 0 ? cfg.Ui.DefaultMaxItems : _session.Datasource.MaxItems;
        }
        else
        {
            _defaultConfidenceThreshold = _session.Engine.ConfidenceThreshold;
            _defaultMaxItems = _session.Datasource.MaxItems;
        }

        VersionText = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        ConfigFilePath = config?.FilePath ?? "";
    }

    /// <summary>
    /// The shell, so the Inference server section can reach the server it
    /// configures (status, Start/Stop/Build, the per-platform variant rows and
    /// the fallback warning). Set by <see cref="MainWindowViewModel"/>; null in
    /// isolated tests, where the section simply renders empty.
    /// </summary>
    public MainWindowViewModel? Shell { get; set; }

    public IReadOnlyList<string> ThemeOptionList => ThemeOptions;
    public IReadOnlyList<string> LogLevelOptionList => LogLevelOptions;
    public IReadOnlyList<SettingOption> DefaultDeviceOptionList => DefaultDeviceOptions;

    /// <summary>About: the running build's version.</summary>
    public string VersionText { get; }

    /// <summary>About: where config.json lives, so a support question has an answer.</summary>
    public string ConfigFilePath { get; }

    /// <summary>Logging/About: where this run's log file is (empty before Initialize).</summary>
    public string LogFilePath => SynapicLog.LogFilePath;

    // ── Appearance ────────────────────────────────────────────────────────

    [ObservableProperty]
    private int _themeIndex;

    public string ThemeText => ThemeOptions[Math.Clamp(ThemeIndex, 0, ThemeOptions.Count - 1)];

    partial void OnThemeIndexChanged(int value)
    {
        if (value < 0 || value >= ThemeOptions.Count) return;   // a selector with no selection yet
        var theme = ThemeText.ToLowerInvariant();
        ApplyTheme(theme);
        SynapicLog.Info(nameof(SettingsViewModel), $"Appearance: theme set to {theme}");
        Persist();
    }

    /// <summary>
    /// Applies a persisted theme name to a running app (no-op for an unknown
    /// name). Static so startup can apply the saved theme before any view model
    /// exists: the first frame is then already in the right theme.
    /// </summary>
    public static void ApplyTheme(string? theme, Application? app = null)
    {
        var target = app ?? Application.Current;
        if (target is null) return;   // a headless unit test with no application
        target.RequestedThemeVariant = theme?.Trim().ToLowerInvariant() switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    // ── Logging ───────────────────────────────────────────────────────────

    [ObservableProperty]
    private int _logLevelIndex = LogLevelIndexOf("info");

    public string LogLevelText => LogLevelOptions[Math.Clamp(LogLevelIndex, 0, LogLevelOptions.Count - 1)];

    partial void OnLogLevelIndexChanged(int value)
    {
        if (value < 0 || value >= LogLevelOptions.Count) return;   // a selector with no selection yet
        SynapicLog.SetMinimumLevel(LogLevelText.ToLowerInvariant());
        SynapicLog.Info(nameof(SettingsViewModel), $"Logging: UI log level set to {LogLevelText.ToLowerInvariant()}");
        Persist();
    }

    /// <summary>
    /// Diagnostics drawer (D7): the shell shows its log view only while this is
    /// on. Off by default - run logs live in an operation's Output region.
    /// </summary>
    [ObservableProperty]
    private bool _showDiagnostics;

    partial void OnShowDiagnosticsChanged(bool value)
    {
        SynapicLog.Info(nameof(SettingsViewModel), $"Logging: diagnostics view {(value ? "shown" : "hidden")}");
        Persist();
    }

    /// <summary>The live log file, in the file manager (its folder).</summary>
    [RelayCommand]
    private void OpenLogFolder()
    {
        var target = SynapicLog.LogFilePath.Length > 0 ? SynapicLog.LogFilePath : SynapicLog.LogDirectory;
        if (target.Length > 0) _openExternal(target);
    }

    // ── Inference server (the shell's lifecycle toggle) ───────────────────

    /// <summary>
    /// Start the sidecar with the app and stop it with the app (the shipping
    /// behaviour). Off means manual Start/Stop only; the shell reads this at
    /// launch, so a change applies to the next run.
    /// </summary>
    [ObservableProperty]
    private bool _autoLaunchSidecar = true;

    partial void OnAutoLaunchSidecarChanged(bool value)
    {
        SynapicLog.Info(nameof(SettingsViewModel),
            $"Inference server: auto-launch {(value ? "on" : "off")} (applies to the next launch)");
        Persist();
    }

    // ── Defaults (a session with no saved engine state) ───────────────────

    [ObservableProperty]
    private int _defaultDeviceIndex;

    public SettingOption DefaultDeviceOption =>
        DefaultDeviceOptions[Math.Clamp(DefaultDeviceIndex, 0, DefaultDeviceOptions.Count - 1)];

    public string DefaultDeviceText => DefaultDeviceOption.Label;

    partial void OnDefaultDeviceIndexChanged(int value)
    {
        if (value < 0 || value >= DefaultDeviceOptions.Count) return;   // a selector with no selection yet
        OnPropertyChanged(nameof(DefaultDeviceText));
        Persist();
    }

    [ObservableProperty]
    private double _defaultConfidenceThreshold = 0.3;

    partial void OnDefaultConfidenceThresholdChanged(double value)
    {
        // A control that has not been given a value yet offers NaN/0; neither is
        // a default anyone chose, and writing one would erase the stored value.
        if (double.IsNaN(value) || value <= 0 || value > 1) return;
        Persist();
    }

    [ObservableProperty]
    private int _defaultMaxItems = 100;

    partial void OnDefaultMaxItemsChanged(int value)
    {
        if (value <= 0) return;
        Persist();
    }

    /// <summary>
    /// Applies the Defaults section to a session that has no saved engine state
    /// (first run, or after the stored engine settings were cleared). The shell
    /// calls this once, before the step view models hydrate, so the form opens
    /// on these values rather than on the shipped ones.
    /// </summary>
    public void ApplyDefaultsToNewSession()
    {
        if (DefaultDeviceOption.Value.Length > 0) _session.Engine.Device = DefaultDeviceOption.Value;
        if (DefaultConfidenceThreshold > 0) _session.Engine.ConfidenceThreshold = DefaultConfidenceThreshold;
        if (DefaultMaxItems > 0) _session.Datasource.MaxItems = DefaultMaxItems;

        SynapicLog.Info(nameof(SettingsViewModel),
            $"New session defaults applied: device={_session.Engine.Device}, " +
            $"confidence={_session.Engine.ConfidenceThreshold:0.00}, max items={_session.Datasource.MaxItems}");
    }

    // ── About ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private void OpenRepository() => _openExternal(RepositoryUrl);

    // ── Persistence ───────────────────────────────────────────────────────

    /// <summary>
    /// Writes the app-wide settings back to config.json. It loads the document
    /// first and mutates only the fields this view owns, so a save from here can
    /// never revert the source or the engine parameters the session owns - the
    /// two writers touch disjoint fields.
    /// </summary>
    private void Persist()
    {
        var config = _configProvider();
        if (config is null) return;
        try
        {
            var cfg = config.Load();
            cfg.Ui.Theme = ThemeText.ToLowerInvariant();
            cfg.Ui.LogLevel = LogLevelText.ToLowerInvariant();
            cfg.Ui.ShowDiagnostics = ShowDiagnostics;
            cfg.Ui.AutoLaunchSidecar = AutoLaunchSidecar;
            cfg.Ui.DefaultDevice = DefaultDeviceOption.Value;
            cfg.Ui.DefaultConfidenceThreshold = DefaultConfidenceThreshold;
            cfg.Ui.DefaultMaxItems = DefaultMaxItems;
            config.Save(cfg);
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(SettingsViewModel), $"Failed to persist app settings: {e.Message}");
        }
    }

    private static void OpenWithShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(SettingsViewModel), $"Could not open {target}: {e.Message}");
        }
    }

    private static int IndexOf(IReadOnlyList<string> options, string? value, string fallback)
    {
        var wanted = (value ?? "").Trim().ToLowerInvariant();
        for (var i = 0; i < options.Count; i++)
            if (string.Equals(options[i], wanted, StringComparison.OrdinalIgnoreCase)) return i;
        for (var i = 0; i < options.Count; i++)
            if (string.Equals(options[i], fallback, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    private static int LogLevelIndexOf(string? level) => (level ?? "").Trim().ToLowerInvariant() switch
    {
        "verbose" => 0,
        "debug" => 1,
        "warning" or "warn" => 3,
        "error" or "fatal" => 4,
        _ => 2,   // information (and the "info" shorthand): the app's default level
    };

    private static int DefaultDeviceIndexOf(string? device)
    {
        var wanted = (device ?? "").Trim().ToLowerInvariant();
        for (var i = 0; i < DefaultDeviceOptions.Count; i++)
            if (string.Equals(DefaultDeviceOptions[i].Value, wanted, StringComparison.Ordinal)) return i;
        return 0;
    }
}

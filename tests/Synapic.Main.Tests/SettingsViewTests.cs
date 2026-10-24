using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Synapic.Main.Models;
using Synapic.Main.Services;
using Synapic.Main.ViewModels;
using Synapic.Main.Views;
using Synapic.Main.Views.Dashboard;
using Synapic.Main.Views.Settings;
using Synapic.Main.Views.Wizard;
using Xunit;

using Synapic.Core.Services;
namespace Synapic.Main.Tests;

/// <summary>
/// The app-wide settings view (docs/ui-design.md §5, decision D3): Inference
/// server, Appearance, Logging, Defaults and About, rendered by the dashboard's
/// Settings panel over the real compiled window. These pin what the panel
/// contains, that each setting reaches the thing it configures (the running
/// theme, the live log level, the shell's diagnostics view, a new session), and
/// that a change survives a save/load of config.json.
/// </summary>
public class SettingsViewTests
{
    /// <summary>A config file of this test's own, so nothing touches the real one.</summary>
    private sealed class TempConfig : IDisposable
    {
        public TempConfig()
        {
            Directory = Path.Combine(Path.GetTempPath(), "synapic-settings-" + Guid.NewGuid().ToString("N"));
            Service = new ConfigService(Path.Combine(Directory, "config.json"));
        }

        public string Directory { get; }
        public ConfigService Service { get; }

        public void Dispose()
        {
            try { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
            catch (IOException) { /* best effort */ }
        }
    }

    private static MainWindowViewModel NewShell(ConfigService? config = null, Session? session = null) =>
        new(new FakeSidecar(), session ?? new Session(), () => null, null, null, _ => null,
            configService: config);

    private static MainWindow NewWindow(MainWindowViewModel shell)
    {
        var window = new MainWindow { DataContext = shell };
        window.Show();
        return window;
    }

    private static bool EffectivelyVisible(Control control) =>
        control.IsVisible &&
        control.GetVisualAncestors().OfType<Control>().All(ancestor => ancestor.IsVisible);

    /// <summary>The settings view itself, found by its type inside the window.</summary>
    /// <summary>Switch tab and wait for the layout pass that materializes the
    /// selected tab's content — a TabItem's children are not yet in the visual
    /// tree right after selection; the dispatcher has to run.</summary>
    private static void ShowTabAndWait(SettingsTab settings, string tabName)
    {
        settings.ShowTab(tabName);
        Dispatcher.UIThread.RunJobs();
    }

    private static SettingsPanel Panel(Window window)
    {
        // The app preferences are behind the App tab now; land on it first.
        var settings = Assert.Single(window.GetVisualDescendants().OfType<SettingsTab>());
        ShowTabAndWait(settings, "TabApp");
        return Assert.Single(window.GetVisualDescendants().OfType<SettingsPanel>());
    }

    /// <summary>
    /// §5's app-preference half is four sections — Appearance, Logging, Defaults
    /// and About — in one scrolling column, composed into the Settings view. It
    /// carries no source form and no engine form: those have their own sections
    /// beside it on the Settings view, so nothing here is configured twice.
    /// </summary>
    [AvaloniaFact]
    public void Settings_panel_is_the_four_section_app_preferences_view()
    {
        var shell = NewShell();
        var window = NewWindow(shell);
        try
        {
            Assert.True(shell.IsSettingsVisible, "the Settings view is where the app opens");

            var panel = Panel(window);
            var sections = panel.GetVisualDescendants().OfType<Border>()
                .Where(b => b.Classes.Contains("settingsSection"))
                .Select(b => b.Name)
                .ToList();

            Assert.Equal(
                new[] { "SectionAppearance", "SectionLogging", "SectionDefaults", "SectionAbout" },
                sections);

            Assert.All(sections, name => Assert.True(
                EffectivelyVisible(panel.GetVisualDescendants().OfType<Border>().First(b => b.Name == name)),
                $"{name} is not on screen on the Settings view"));

            // No source form, no engine form: each has exactly one home — the
            // Settings view's Data source and Inference engine sections — and it
            // is not this panel.
            Assert.Empty(panel.GetVisualDescendants().OfType<DatasourceSourcePanel>());
            Assert.Empty(panel.GetVisualDescendants().OfType<Step2Engine>());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// §5 Appearance: the theme switch takes effect on the running app (not only
    /// on the next launch) and is written to config.json.
    /// </summary>
    [AvaloniaFact]
    public void Appearance_theme_applies_to_the_running_app_and_persists()
    {
        using var temp = new TempConfig();
        var shell = NewShell(temp.Service);
        try
        {
            var settings = shell.AppSettings;
            Assert.Equal(0, settings.ThemeIndex);            // system, the shipped default

            settings.ThemeIndex = 2;                          // dark
            Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
            Assert.Equal("dark", temp.Service.Load().Ui.Theme);

            settings.ThemeIndex = 1;                          // light
            Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);

            // …and a freshly built view model reads it back, so a restart lands
            // on the theme the user chose.
            var reloaded = new SettingsViewModel(new Session(), () => temp.Service);
            Assert.Equal("light", reloaded.ThemeText.ToLowerInvariant());
        }
        finally
        {
            SettingsViewModel.ApplyTheme("system");           // do not leak a theme into other tests
        }
    }

    /// <summary>
    /// §5 Logging: the level applies to the live UI sink immediately (the file
    /// log keeps everything) and is written to config.json.
    /// </summary>
    [AvaloniaFact]
    public void Logging_level_applies_to_the_running_logger_and_persists()
    {
        using var temp = new TempConfig();
        try
        {
            var settings = NewShell(temp.Service).AppSettings;

            settings.LogLevelIndex = 1;                       // Debug
            Assert.Equal("debug", SynapicLog.CurrentMinimumLevel);
            Assert.Equal("debug", temp.Service.Load().Ui.LogLevel);

            settings.LogLevelIndex = 3;                       // Warning
            Assert.Equal("warning", SynapicLog.CurrentMinimumLevel);
        }
        finally
        {
            SynapicLog.SetMinimumLevel("information");        // do not leak a level into other tests
        }
    }

    /// <summary>
    /// §5 Logging + D7: the in-app log view is the diagnostics drawer. It is off
    /// by default (no always-visible log strip), and the toggle really shows and
    /// hides it in the shell.
    /// </summary>
    [AvaloniaFact]
    public void Diagnostics_toggle_shows_and_hides_the_shell_log_view()
    {
        using var temp = new TempConfig();
        var shell = NewShell(temp.Service);
        var window = NewWindow(shell);
        try
        {
            var log = Assert.Single(window.GetVisualDescendants().OfType<ListBox>(), l => l.Name == "LogList");

            Assert.False(shell.IsDiagnosticsVisible);
            Assert.False(EffectivelyVisible(log), "the log strip is on screen before Settings → Logging enables it");

            shell.AppSettings.ShowDiagnostics = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(shell.IsDiagnosticsVisible);
            Assert.True(EffectivelyVisible(log));

            shell.AppSettings.ShowDiagnostics = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(EffectivelyVisible(log));

            // The choice survives a restart: the shell reads it from config.
            shell.AppSettings.ShowDiagnostics = true;
            Assert.True(temp.Service.Load().Ui.ShowDiagnostics);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// §5 Defaults: what a session that has no saved engine state starts from.
    /// The shell applies the section before the step view models are built, so
    /// the engine form opens on the defaults rather than on the shipped ones.
    /// </summary>
    [AvaloniaFact]
    public void Defaults_seed_a_session_that_has_no_saved_engine_state()
    {
        using var temp = new TempConfig();
        var config = temp.Service.Load();
        config.Ui.DefaultDevice = "cuda";
        config.Ui.DefaultConfidenceThreshold = 0.6;
        config.Ui.DefaultMaxItems = 250;
        temp.Service.Save(config);

        var session = new Session();
        var shell = NewShell(temp.Service, session);

        // The device default lands on the session, and then the engine form's own
        // rule applies: a device this machine cannot offer is corrected to one it
        // can, so a CUDA default on a CPU-only machine ends up on the CPU (that
        // correction is the engine form's, and it is what a run would use).
        Assert.Equal(ComputeAvailability.Detect().Cuda ? "cuda" : "cpu", session.Engine.Device);
        Assert.Equal(0.6, session.Engine.ConfidenceThreshold);
        Assert.Equal(250, session.Datasource.MaxItems);

        // The panel reports the same values, so the file and the form agree.
        Assert.Equal("CUDA", shell.AppSettings.DefaultDeviceText);
        Assert.Equal(0.6, shell.AppSettings.DefaultConfidenceThreshold);
        Assert.Equal(250, shell.AppSettings.DefaultMaxItems);
    }

    /// <summary>
    /// The other direction: with nothing configured at all, Auto is what
    /// applies — the session opens on the device this machine can run (CUDA
    /// when an NVIDIA driver is present, CPU otherwise; the R20 fix's user
    /// requirement), while everything else keeps the shipped values. The panel
    /// reports the Auto choice, so file and form agree.
    /// </summary>
    [AvaloniaFact]
    public void A_settings_file_without_defaults_autoselects_the_appropriate_device()
    {
        using var temp = new TempConfig();
        var session = new Session();
        var shippedRest = (session.Engine.ConfidenceThreshold, session.Datasource.MaxItems);

        var shell = NewShell(temp.Service, session);

        Assert.Equal(
            ComputeAvailability.Detect().Cuda ? "cuda" : "cpu",
            session.Engine.Device);
        Assert.Equal("Auto (recommended)", shell.AppSettings.DefaultDeviceText);

        // Everything but the device stays on the shipped values.
        Assert.Equal(shippedRest, (session.Engine.ConfidenceThreshold, session.Datasource.MaxItems));
        Assert.Equal(shippedRest.Item1, shell.AppSettings.DefaultConfidenceThreshold);
        Assert.Equal(shippedRest.Item2, shell.AppSettings.DefaultMaxItems);
    }

    /// <summary>
    /// §5 Inference server: the server is operated from here — status, the
    /// Start/Stop pair, the auto-launch toggle and one row per buildable variant
    /// (the shell keeps only the status indicator, §6.1/D7).
    /// </summary>
    [AvaloniaFact]
    public async Task Inference_server_section_operates_the_server_through_the_shell()
    {
        using var temp = new TempConfig();
        var shell = NewShell(temp.Service);
        await shell.DetectServerAsync();     // creates the per-platform variant rows
        Assert.NotEmpty(shell.SidecarVariants);

        var window = NewWindow(shell);
        try
        {
            // The inference server is behind its own tab now.
            ShowTabAndWait(Assert.Single(window.GetVisualDescendants().OfType<SettingsTab>()), "TabServer");
            var section = Assert.Single(window.GetVisualDescendants().OfType<InferenceServerSection>());
            var buttons = section.GetVisualDescendants().OfType<Button>().ToList();

            Assert.Single(buttons, b => b.Command == shell.StartServerCommand);
            Assert.Single(buttons, b => b.Command == shell.StopServerCommand);

            // One realized row per variant, rendered from the shell's own rows
            // (not a copy), and each row's Download command is wired - a broken
            // binding leaves Command null and looks fine until it is clicked.
            var items = Assert.Single(section.GetVisualDescendants().OfType<ItemsControl>());
            Assert.Same(shell.SidecarVariants, items.ItemsSource);
            var rendered = section.GetVisualDescendants().OfType<Button>()
                .Select(b => b.DataContext).OfType<SidecarVariantViewModel>()
                .Distinct().ToList();
            Assert.Equal(shell.SidecarVariants.Count, rendered.Count);
            Assert.All(shell.SidecarVariants, v => Assert.NotNull(v.DownloadCommand));

            // The auto-launch toggle is the persisted opt-out (the shell's
            // blocking banner points here rather than offering a second copy).
            var autoLaunch = Assert.Single(section.GetVisualDescendants().OfType<CheckBox>(),
                c => (c.Content as string)?.StartsWith("Start the server") == true);
            Assert.True(autoLaunch.IsChecked);
            shell.AppSettings.AutoLaunchSidecar = false;
            Assert.False(temp.Service.Load().Ui.AutoLaunchSidecar);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The Settings view has a search box: a query filters the column to the
    /// sections that mention it and jumps to the first match; an empty query shows
    /// every section again, and a query that matches nothing says so.
    /// </summary>
    [AvaloniaFact]
    public void Settings_search_filters_to_matching_sections_and_jumps_to_the_first()
    {
        var shell = NewShell();
        var window = NewWindow(shell);
        try
        {
            var settings = Assert.Single(window.GetVisualDescendants().OfType<SettingsTab>());
            var box = Assert.Single(settings.GetVisualDescendants().OfType<AutoCompleteBox>(),
                t => t.Name == "SettingsSearchBox");
            Assert.Same(SettingsTab.SearchSuggestions, box.ItemsSource);

            var tabs = settings.GetVisualDescendants().OfType<TabItem>().ToList();
            Assert.Equal(8, tabs.Count);
            var selected = () => tabs.Single(t => t.IsSelected).Name!;
            // The Connection tab is where the app opens.
            Assert.Equal("TabConnection", selected());

            var status = Assert.Single(settings.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Name == "SearchStatusText");
            Assert.False(status.IsVisible);

            // Typing goes through the box's own text field, as a user's keys do:
            // AutoCompleteBox.Text is driven by that field, so setting Text
            // directly does not stick.
            var typed = Assert.Single(box.GetVisualDescendants().OfType<TextBox>());
            typed.Text = "dedup";
            Dispatcher.UIThread.RunJobs();

            // The query switched to the tab the settings live on, and the status
            // says where it went.
            Assert.Equal("TabDedup", selected());
            Assert.True(status.IsVisible);
            Assert.Contains("TabDedup" == selected() ? "Dedup" : "", status.Text!);

            // Picking a suggestion from the dropdown switches the same way.
            box.SelectedItem = "Theme";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("TabApp", selected());

            // A query that names nothing switches nowhere and says so.
            ShowTabThenQuery(settings, "TabEngine", typed, "zzzznotathing");
            Assert.Equal("TabEngine", selected());
            Assert.Contains("No settings match", status.Text);

            // Clearing the box resets the status but keeps the tab the user is on.
            typed.Text = "";
            Dispatcher.UIThread.RunJobs();
            Assert.False(status.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Land on a tab, then set a query the box will not match.</summary>
    private static void ShowTabThenQuery(SettingsTab settings, string tab, TextBox typed, string query)
    {
        ShowTabAndWait(settings, tab);
        Dispatcher.UIThread.RunJobs();
        typed.Text = query;
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// The search dropdown offers only names that really find something: every
    /// suggestion, once picked, must match at least one section of the column.
    /// A suggestion that matches nothing would open the dropdown and then say
    /// "No settings match".
    /// </summary>
    [AvaloniaFact]
    public void Every_search_suggestion_finds_at_least_one_section()
    {
        var shell = NewShell();
        var window = NewWindow(shell);
        try
        {
            var settings = Assert.Single(window.GetVisualDescendants().OfType<SettingsTab>());
            Assert.NotEmpty(SettingsTab.SearchSuggestions);

            foreach (var suggestion in SettingsTab.SearchSuggestions)
            {
                Assert.True(settings.ApplyFilter(suggestion) > 0,
                    $"suggestion \u201c{suggestion}\u201d matches no settings section");
            }

            settings.ApplyFilter("");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>§5 About: what build this is, and the two links out.</summary>
    [AvaloniaFact]
    public void About_reports_the_version_and_the_links()
    {
        var shell = NewShell();
        var window = NewWindow(shell);
        try
        {
            var about = Panel(window).GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SectionAbout");
            Assert.False(string.IsNullOrWhiteSpace(shell.AppSettings.VersionText));
            var texts = about.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            Assert.Contains(texts, t => t.Contains(shell.AppSettings.VersionText));
            Assert.Single(about.GetVisualDescendants().OfType<Button>(), b => b.Command == shell.OpenHelpCommand);
            Assert.Single(about.GetVisualDescendants().OfType<Button>(),
                b => b.Command == shell.AppSettings.OpenRepositoryCommand);
            Assert.Equal("https://github.com/deanable/Synapic.NET", SettingsViewModel.RepositoryUrl);
        }
        finally
        {
            window.Close();
        }
    }
}

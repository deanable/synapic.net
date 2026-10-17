using Avalonia.Headless.XUnit;
using Synapic.Main.Models;
using Synapic.Main.Services;
using Synapic.Main.ViewModels;
using Synapic.Main.ViewModels.Steps;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// Which source the start screen opens on has to survive a restart: a user who
/// closed the app connected to a Daminion server must come back on the catalog
/// (and be reconnected), one who worked in a folder must come back on that
/// folder. Launch reads the source from <see cref="AppSettingsRegistryStore"/>,
/// so these tests drive the real store through an isolated HKCU key.
/// Skipped on non-Windows, where the store is a no-op by design (same as
/// DaminionConnectionStoreTests).
/// </summary>
public class SourceSettingsPersistenceTests : IDisposable
{
    private readonly string _keyPath =
        $@"Software\Synapic\SourceTests_{Guid.NewGuid():N}";

    private readonly AppSettingsRegistryStore _store;

    public SourceSettingsPersistenceTests() => _store = new AppSettingsRegistryStore(_keyPath);

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows()) return;
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);
    }

    /// <summary>The count is irrelevant here; keep the step from scanning on a folder that vanishes.</summary>
    private static Func<string, bool, CancellationToken, Task<int>> NoCount =>
        (_, _, _) => Task.FromResult(0);

    private static Step1DatasourceViewModel Reopen(AppSettingsRegistryStore store)
    {
        // What App does at launch: the session takes its source from the store,
        // then the source step hydrates from the session.
        var session = new Session();
        session.Datasource.ApplyStoredSource(
            (store.Load() ?? store.Defaults).ToDatasourceSettings());
        return new Step1DatasourceViewModel(session, countLocalImages: NoCount, appSettingsStore: store);
    }

    [Fact]
    public void Store_round_trips_the_active_source_and_its_folder()
    {
        if (!OperatingSystem.IsWindows()) return; // registry is Windows-only

        var folder = Path.GetTempPath();
        _store.Save(_store.Defaults with
        {
            DatasourceType = "daminion",
            LocalPath = folder,
            LocalRecursive = true,
        });

        var restored = _store.Load();
        Assert.NotNull(restored);
        Assert.Equal("daminion", restored.DatasourceType);

        var session = new Session();
        session.Datasource.ApplyStoredSource(restored.ToDatasourceSettings());
        Assert.Equal("daminion", session.Datasource.Type);
        Assert.Equal(folder, session.Datasource.LocalPath);
        Assert.True(session.Datasource.LocalRecursive);
    }

    [Fact]
    public void Missing_source_value_falls_back_to_the_folder_tab()
    {
        if (!OperatingSystem.IsWindows()) return;

        _store.Save(_store.Defaults with { DatasourceType = "daminion" });
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(_keyPath, writable: true))
        {
            Assert.NotNull(key);
            key!.DeleteValue("DatasourceType", throwOnMissingValue: false);
        }

        var restored = _store.Load();
        Assert.NotNull(restored);
        Assert.Equal("local", restored.DatasourceType);
    }

    [AvaloniaFact]
    public void Choosing_a_folder_is_remembered_for_the_next_launch()
    {
        if (!OperatingSystem.IsWindows()) return;

        var folder = Path.Combine(Path.GetTempPath(), "synapic-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var vm = new Step1DatasourceViewModel(
                new Session(), countLocalImages: NoCount, appSettingsStore: _store);
            vm.DatasourceType = "local";
            vm.LocalPath = folder;
            vm.LocalRecursive = true;

            var reopened = Reopen(_store);

            Assert.Equal("local", reopened.DatasourceType);
            Assert.True(reopened.IsLocal);
            Assert.Equal(folder, reopened.LocalPath);
            Assert.True(reopened.LocalRecursive);
            Assert.True(reopened.HasUsableSource); // the workflows are ready, no re-pick
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public void Choosing_the_catalog_is_remembered_for_the_next_launch()
    {
        if (!OperatingSystem.IsWindows()) return;

        var vm = new Step1DatasourceViewModel(
            new Session(), countLocalImages: NoCount, appSettingsStore: _store);
        vm.DatasourceType = "daminion";

        var reopened = Reopen(_store);

        // The catalog tab comes back; the launch sequence then reconnects with
        // the credentials DaminionConnectionStore already keeps.
        Assert.Equal("daminion", reopened.DatasourceType);
        Assert.True(reopened.IsDaminion);
    }

    [AvaloniaFact]
    public void Switching_back_to_the_folder_tab_wins_the_next_launch()
    {
        if (!OperatingSystem.IsWindows()) return;

        var vm = new Step1DatasourceViewModel(
            new Session(), countLocalImages: NoCount, appSettingsStore: _store);
        vm.DatasourceType = "daminion";   // last session ended on the catalog…
        vm.DatasourceType = "local";      // …but the user went back to a folder

        Assert.Equal("local", Reopen(_store).DatasourceType);
    }

    /// <summary>
    /// The wiring that matters: the shell's shared source step writes through
    /// the store App passes it, not only into the session. A store the step never
    /// receives would leave every restart on the folder tab again.
    /// </summary>
    [AvaloniaFact]
    public void Shell_wiring_persists_the_source_through_the_registry_store()
    {
        if (!OperatingSystem.IsWindows()) return;

        var shell = new MainWindowViewModel(
            new FakeSidecar(), new Session(),
            () => "x.exe", null, null, _ => null,
            appSettingsStore: _store);

        shell.Operations.Source.DatasourceType = "daminion";

        var stored = _store.Load();
        Assert.NotNull(stored);
        Assert.Equal("daminion", stored.DatasourceType);
    }
}

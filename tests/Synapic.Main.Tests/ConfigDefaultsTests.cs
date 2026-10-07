using System;
using System.IO;
using Synapic.Avalonia.Services;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// Locks the shipped server-lifecycle default (README §2): the sidecar is
/// auto-launched with the app, and `ui.autoLaunchSidecar=false` is the
/// documented opt-out to manual Start/Stop. The spec text originally described
/// a manual default and drifted from the implementation once, so the contract
/// is pinned here rather than only in prose.
/// </summary>
public class ConfigDefaultsTests
{
    [Fact]
    public void AutoLaunchSidecar_DefaultsToTrue()
    {
        Assert.True(new UiSettings().AutoLaunchSidecar);
        Assert.True(new AppConfig().Ui.AutoLaunchSidecar);
    }

    /// <summary>
    /// A config file written before the setting existed (or hand-edited without
    /// it) must resume the default, not silently fall back to manual launch.
    /// </summary>
    [Fact]
    public void MissingSetting_FallsBackToAutoLaunch()
    {
        var path = TempConfigPath();
        try
        {
            File.WriteAllText(path, "{\"Ui\":{\"Theme\":\"dark\",\"LogLevel\":\"info\"}}");

            var loaded = new ConfigService(path).Load();

            Assert.Equal("dark", loaded.Ui.Theme);
            Assert.True(loaded.Ui.AutoLaunchSidecar);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExplicitOptOut_RoundTrips()
    {
        var path = TempConfigPath();
        try
        {
            var configService = new ConfigService(path);
            var config = configService.Load();
            config.Ui.AutoLaunchSidecar = false;
            configService.Save(config);

            Assert.False(new ConfigService(path).Load().Ui.AutoLaunchSidecar);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TempConfigPath() =>
        Path.Combine(Path.GetTempPath(), $"synapic-config-default-{Guid.NewGuid():N}.json");
}

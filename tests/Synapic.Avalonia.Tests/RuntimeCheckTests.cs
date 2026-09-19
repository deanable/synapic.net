using Synapic.Avalonia.Services;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// Startup runtime-check logic: the app targets the .NET 10 Desktop Runtime,
/// so detection must find it (registry or dotnet CLI) and the minimum-version
/// gate must accept 10.x and reject older/absent runtimes.
/// </summary>
public class RuntimeCheckTests
{
    [Fact]
    public void Installed_desktop_runtime_is_detected_and_satisfies_minimum()
    {
        var service = new DotNetRuntimeCheckService();
        var version = service.GetInstalledDesktopRuntimeVersion();

        Assert.NotNull(version);
        Assert.True(
            DotNetRuntimeCheckService.Satisfies(version),
            $"expected an installed .NET 10+ Desktop Runtime, found {version}");
    }

    [Theory]
    [InlineData("10.0.0", true)]
    [InlineData("10.0.12", true)]
    [InlineData("11.0.5", true)]
    [InlineData("9.0.9", false)]
    [InlineData("8.0.21", false)]
    public void Satisfies_applies_minimum_version_gate(string version, bool expected)
    {
        Assert.Equal(expected, DotNetRuntimeCheckService.Satisfies(Version.Parse(version)));
    }

    [Fact]
    public void Satisfies_rejects_missing_runtime()
    {
        Assert.False(DotNetRuntimeCheckService.Satisfies(null));
    }

    /// <summary>
    /// No-op path of the startup flow: on a machine with the runtime (or a
    /// non-Windows platform, where the check skips) the flow must log and
    /// return without attempting any install.
    /// </summary>
    [Fact]
    public async Task EnsureRuntime_noops_when_runtime_present_or_platform_skips()
    {
        var logs = new List<string>();
        await new DotNetRuntimeCheckService().EnsureRuntimeAsync(logs.Add);

        Assert.Contains(logs, l =>
            l.Contains("detected", StringComparison.OrdinalIgnoreCase) ||   // Windows with runtime
            l.Contains("skipped", StringComparison.OrdinalIgnoreCase));     // non-Windows
        Assert.DoesNotContain(logs, l => l.Contains("Downloading"));
        Assert.DoesNotContain(logs, l => l.Contains("silent install", StringComparison.OrdinalIgnoreCase));
    }
}

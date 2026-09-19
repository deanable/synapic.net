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
}

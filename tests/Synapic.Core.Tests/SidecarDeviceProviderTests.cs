using Synapic.Core.Services;
using Xunit;

namespace Synapic.Core.Tests;

/// <summary>
/// CORE-03: the sidecar host is driven by <see cref="ISidecarDeviceProvider"/>,
/// not by the desktop's Session, so a headless host can launch it.
/// </summary>
public sealed class SidecarDeviceProviderTests
{
    [Fact]
    public void Device_provider_supplies_the_launch_device_without_a_session()
    {
        var service = new InferenceSidecarService(
            options: new SidecarLaunchOptions
            {
                DeviceProvider = new StaticSidecarDeviceProvider("cuda"),
            });

        Assert.Equal("cuda", service.DeviceSelection);
    }

    [Fact]
    public void No_provider_defaults_to_cpu()
    {
        var service = new InferenceSidecarService();

        Assert.Equal("cpu", service.DeviceSelection);
    }

    [Fact]
    public void Unknown_devices_normalize_to_cpu()
    {
        var service = new InferenceSidecarService(
            options: new SidecarLaunchOptions
            {
                DeviceProvider = new StaticSidecarDeviceProvider("directml"),
            });

        Assert.Equal("cpu", service.DeviceSelection);
    }

    [Fact]
    public async Task Dispose_leaves_no_running_process()
    {
        var service = new InferenceSidecarService();

        await service.DisposeAsync();

        Assert.False(service.IsRunning);
    }
}

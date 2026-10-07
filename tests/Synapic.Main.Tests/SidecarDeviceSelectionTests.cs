using Microsoft.Extensions.DependencyInjection;
using Synapic.Main.Models;
using Synapic.Main.Services;
using Synapic.Main.ViewModels;
using Synapic.Main.ViewModels.Steps;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// The device selected in Step 2 is what decides which sidecar variant runs and
/// which device the server's /tag pipeline is built on. Both used to be lost:
/// the launcher always took the plain (CPU) executable and nothing ever told the
/// server which device was asked for, so selecting CUDA still tagged on the CPU.
/// </summary>
public class SidecarDeviceSelectionTests
{
    [Theory]
    [InlineData(null, "cpu")]
    [InlineData("", "cpu")]
    [InlineData(" CPU ", "cpu")]
    [InlineData("CUDA", "cuda")]
    [InlineData("mps", "mps")]
    // Config.json is user-editable and the value reaches a command line, so
    // anything unrecognized has to land on the safe device.
    [InlineData("cuda --port=1", "cpu")]
    [InlineData("gpu", "cpu")]
    public void Device_names_are_whitelisted(string? requested, string expected)
        => Assert.Equal(expected, InferenceSidecarService.NormalizeDevice(requested));

    [Fact]
    public async Task The_service_gets_the_session_device_through_dependency_injection()
    {
        // App.axaml.cs registers the session and the service and lets the
        // container build it; a construction that quietly ignored the session
        // would put the app back on the CPU default.
        var services = new ServiceCollection();
        var session = new Session();
        session.Engine.Device = "cuda";
        services.AddSingleton(session);
        services.AddSingleton<IInferenceSidecar, InferenceSidecarService>();
        // The service is IAsyncDisposable only, so the container is too.
        await using var provider = services.BuildServiceProvider();

        var sidecar = Assert.IsType<InferenceSidecarService>(provider.GetRequiredService<IInferenceSidecar>());

        Assert.Equal("cuda", sidecar.DeviceSelection);
    }

    [Fact]
    public void The_device_travels_in_the_environment_not_the_command_line()
    {
        // A sidecar built before it knew the device would refuse to start on an
        // unknown argument, and the app must be able to launch any build it
        // finds. The Python side pins the same name (config.DEVICE_ENV_VAR).
        Assert.Equal("SYNAPIC_DEVICE", InferenceSidecarService.DeviceEnvVar);
    }

    [Fact]
    public void Cuda_prefers_the_cuda_variant_and_still_falls_back_to_the_cpu_one()
    {
        var preference = InferenceSidecarService.VariantPreferenceForDevice("cuda");

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(new[] { "win-x64-cuda", "win-x64" }, preference);
        }
        else
        {
            // -cuda is a Windows-only packaging variant (BuildableRids).
            Assert.Equal(new[] { InferenceSidecarService.PreferredRid() }, preference);
        }
    }

    [Fact]
    public void Cpu_and_unknown_devices_never_prefer_the_cuda_variant()
    {
        foreach (var device in new[] { "cpu", "mps", "", null, "gpu" })
        {
            var preference = InferenceSidecarService.VariantPreferenceForDevice(device);
            Assert.Equal(new[] { InferenceSidecarService.PreferredRid() }, preference);
            Assert.DoesNotContain(preference, rid => rid.EndsWith("-cuda", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Selecting_cuda_in_step_2_is_pushed_to_the_running_server()
    {
        var session = new Session();
        var sidecar = new FakeSidecar();
        var vm = new Step2EngineViewModel(session, sidecar, null, null,
            () => new ComputeAvailability(Cuda: true, Mps: false));

        Assert.True(vm.TrySelectDevice("cuda"));

        Assert.Equal("cuda", session.Engine.Device);
        Assert.Contains("cuda", sidecar.PushedDevices);
    }

    // ── Only devices this machine can run are offered ────────────────────────

    [Fact]
    public void Cuda_is_not_offered_without_an_nvidia_driver()
    {
        var vm = new Step2EngineViewModel(new Session(), new FakeSidecar(), null, null,
            () => new ComputeAvailability(Cuda: false, Mps: false));

        Assert.DoesNotContain(vm.DeviceOptions, o => o.Value == "cuda");
        Assert.DoesNotContain(vm.DeviceOptions, o => o.Value == "mps");
        Assert.Equal(new[] { "CPU" }, vm.DeviceOptions.Select(o => o.Name));
        Assert.True(vm.HasDeviceAvailabilityNote);
        Assert.Contains("no NVIDIA CUDA driver", vm.DeviceAvailabilityNote);
        // Picking it is impossible, not merely discouraged.
        Assert.False(vm.TrySelectDevice("cuda"));
        Assert.Equal("cpu", vm.Device);
    }

    [Fact]
    public void Cuda_is_offered_where_the_driver_exists()
    {
        var vm = new Step2EngineViewModel(new Session(), new FakeSidecar(), null, null,
            () => new ComputeAvailability(Cuda: true, Mps: false));

        Assert.Equal(new[] { "CPU", "CUDA" }, vm.DeviceOptions.Select(o => o.Name));
        Assert.False(vm.HasDeviceAvailabilityNote);
        Assert.True(vm.TrySelectDevice("cuda"));
        Assert.Equal("cuda", vm.Device);
    }

    [Fact]
    public void Mps_is_offered_only_where_the_probe_reports_it()
    {
        var vm = new Step2EngineViewModel(new Session(), new FakeSidecar(), null, null,
            () => new ComputeAvailability(Cuda: false, Mps: true));

        Assert.Equal(new[] { "CPU", "MPS" }, vm.DeviceOptions.Select(o => o.Name));
    }

    [Fact]
    public void A_persisted_cuda_device_falls_back_when_the_machine_cannot_run_it()
    {
        // The setting is in config.json from a machine that had the driver, or
        // from a build that offered CUDA everywhere: the run must not be left on
        // a device that silently serves from the CPU.
        var session = new Session();
        session.Engine.Device = "cuda";

        var vm = new Step2EngineViewModel(session, new FakeSidecar(), null, null,
            () => new ComputeAvailability(Cuda: false, Mps: false));

        Assert.Equal("cpu", vm.Device);
        Assert.Equal("cpu", session.Engine.Device);
        Assert.Contains("CUDA was selected", vm.DeviceAvailabilityNote);
        Assert.Contains("no NVIDIA CUDA driver", vm.DeviceAvailabilityNote);
        Assert.Contains("using the CPU", vm.DeviceAvailabilityNote);
    }

    [Fact]
    public async Task Entering_step_2_re_probes_the_machine()
    {
        // A driver installed while the app was open must become selectable
        // without a restart: the probe runs again on the way into the step.
        var available = new ComputeAvailability(Cuda: false, Mps: false);
        var vm = new Step2EngineViewModel(new Session(), new FakeSidecar(), null, null, () => available);
        Assert.DoesNotContain(vm.DeviceOptions, o => o.Value == "cuda");

        available = new ComputeAvailability(Cuda: true, Mps: false);
        await vm.OnEnteredAsync();

        Assert.Contains(vm.DeviceOptions, o => o.Value == "cuda");
    }

    [Fact]
    public void The_real_probe_answers_and_is_cached_per_process()
    {
        // Not an assertion about this machine's hardware (CUDA is present or not
        // depending on the box); the probe must always answer, and answer once.
        ComputeAvailability.ResetCache();
        var first = ComputeAvailability.Detect();

        Assert.Same(first, ComputeAvailability.Detect());
        Assert.False(first.Cuda && OperatingSystem.IsMacOS());
    }

    [Fact]
    public async Task Entering_step_2_restates_the_device_the_server_must_use()
    {
        var session = new Session();
        session.Engine.Device = "cuda";
        var sidecar = new FakeSidecar();
        var vm = new Step2EngineViewModel(session, sidecar, null, null,
            () => new ComputeAvailability(Cuda: true, Mps: false));
        var pushesBeforeEntering = sidecar.PushedDevices.Count;

        await vm.OnEnteredAsync();

        // Re-stated on entry, not only on a change: a server started before the
        // selection (or reused after another device was set) has to be corrected.
        Assert.True(sidecar.PushedDevices.Count > pushesBeforeEntering);
        Assert.Equal("cuda", sidecar.PushedDevices[^1]);
    }
}

using Avalonia.Headless.XUnit;
using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.ViewModels;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// Replacing the sidecar executable requires the server to be stopped first —
/// on Windows a running process holds the executable open and PyInstaller dies
/// with PermissionError only after minutes of packaging (the reproduced
/// "cannot build from scratch" failure). The direct Build command now shares
/// Update's stop/restart wrapper, and the build service itself sweeps orphans
/// before starting PyInstaller.
/// </summary>
public class SidecarBuildStopRestartTests
{
    private static string MakeTempExe()
    {
        var path = Path.Combine(Path.GetTempPath(), $"synapic-variant-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, new byte[] { 1 });
        return path;
    }

    private static MainWindowViewModel Shell(
        FakeSidecar sidecar, FakeBuildService build, string? cudaExe)
    {
        var session = new Session();
        return new MainWindowViewModel(
            sidecar,
            build,
            session,
            sidecarExecutableLocator: () => cudaExe,
            sidecarVariantLocator: rid => rid.EndsWith("-cuda", StringComparison.Ordinal) ? cudaExe : null,
            download: new FakeDownloadService());
    }

    [AvaloniaFact]
    public async Task Direct_build_stops_a_running_server_and_restarts_it_afterwards()
    {
        var cudaExe = MakeTempExe();
        try
        {
            var sidecar = new FakeSidecar();
            var build = new FakeBuildService();
            var vm = Shell(sidecar, build, cudaExe);
            await vm.DetectServerAsync();
            sidecar.RaiseStatus(SidecarStatus.Ready);   // the server is up, running from the CUDA exe

            // The row that is not built yet - building it while another
            // variant's server runs must still stop the server first.
            var cpu = vm.SidecarVariants.Single(v => !v.IsBuilt);
            await cpu.BuildCommand.ExecuteAsync(null);

            Assert.Equal(1, sidecar.StopCalls);
            Assert.Equal(cpu.Rid, Assert.Single(build.BuiltRids));
            Assert.Equal(1, sidecar.StartCalls);        // put back the way the user left it
        }
        finally
        {
            File.Delete(cudaExe);
        }
    }

    [AvaloniaFact]
    public async Task Build_with_no_running_server_touches_no_server_state()
    {
        var sidecar = new FakeSidecar();
        var build = new FakeBuildService();
        var vm = Shell(sidecar, build, cudaExe: null);
        await vm.DetectServerAsync();
        Assert.Equal(ServerUiState.NotDetected, vm.ServerState);

        var cpu = vm.SidecarVariants.First(v => !v.IsBuilt);
        await cpu.BuildCommand.ExecuteAsync(null);

        Assert.Equal(0, sidecar.StopCalls);
        Assert.Equal(0, sidecar.StartCalls);
        Assert.Equal(cpu.Rid, Assert.Single(build.BuiltRids));
    }

    [Fact]
    public async Task Build_fails_in_seconds_with_a_clear_message_when_the_output_is_locked()
    {
        var root = InferenceSidecarService.FindRepoRoot();
        Assert.NotNull(root);

        // A synthetic RID: the lock can only ever be held by this test process
        // (which the pre-flight never kills), so the test can neither disturb a
        // real sidecar nor depend on one existing. The output name must match
        // what the build service checks - it is only .exe on Windows.
        var exeName = OperatingSystem.IsWindows() ? "synapic-inference.exe" : "synapic-inference";
        var exe = Path.Combine(root!, "artifacts", "win-test", exeName);
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        if (!File.Exists(exe)) File.WriteAllBytes(exe, new byte[] { 1 });

        try
        {
            using var held = File.Open(exe, FileMode.Open, FileAccess.Read, FileShare.None);

            var service = new SidecarBuildService();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.BuildAsync("win-test", _ => { }, new Progress<SidecarBuildProgress>(), CancellationToken.None));

            Assert.Contains("in use", ex.Message);
            Assert.Contains(exeName, ex.Message);
            Assert.False(service.IsBuilding);   // failed before a build ever started
        }
        finally
        {
            File.Delete(exe);
            try { Directory.Delete(Path.GetDirectoryName(exe)!, recursive: false); }
            catch (IOException) { }
        }
    }
}

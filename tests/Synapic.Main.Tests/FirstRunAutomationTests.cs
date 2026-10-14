using System;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using Synapic.Main.Models;
using Synapic.Main.Services;
using Synapic.Main.ViewModels;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// First-run automation: a fresh install on a CUDA-capable machine must come up
/// on the CUDA sidecar and device (code review R20 — the seeding used to be
/// unreachable), a downloaded variant must land in the user's writable sidecar
/// directory so the two variants cannot overwrite each other (code review
/// R11), and — when no sidecar exists at all — detection must fetch the
/// recommended variant and start the server without a button press.
/// </summary>
public class FirstRunAutomationTests
{
    /// <summary>A config file of this test's own, so nothing touches the real one
    /// (same pattern the SettingsViewTests use).</summary>
    private sealed class TempConfig : IDisposable
    {
        public TempConfig()
        {
            Directory = Path.Combine(Path.GetTempPath(), "synapic-firstrun-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Service = new ConfigService(Path.Combine(Directory, "config.json"));
        }

        public string Directory { get; }
        public ConfigService Service { get; }

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Directory, true); }
            catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>Scoped setter for <c>SYNAPIC_SIDECAR_ROOT</c> (the sidecar
    /// directory's relocation seam — only these tests use the variable).</summary>
    private sealed class SidecarRootOverride : IDisposable
    {
        private readonly string? _previous;

        public SidecarRootOverride(string path)
        {
            _previous = Environment.GetEnvironmentVariable(InferenceSidecarService.UserSidecarRootEnvVar);
            Environment.SetEnvironmentVariable(InferenceSidecarService.UserSidecarRootEnvVar, path);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(InferenceSidecarService.UserSidecarRootEnvVar, _previous);
        }
    }

    // ── Device seeding (the R20 root cause) ──────────────────────────────────

    [Fact]
    public void Auto_default_is_the_first_option_and_seeds_no_explicit_device()
    {
        Assert.Equal("", SettingsViewModel.DefaultDeviceOptions[0].Value);
        Assert.Contains("Auto", SettingsViewModel.DefaultDeviceOptions[0].Label);
        Assert.Contains("cpu", SettingsViewModel.DefaultDeviceOptions[1].Value);
    }

    [Fact]
    public void Auto_on_a_cuda_machine_seeds_cuda()
    {
        var session = new Session();
        using var temp = new TempConfig();
        var vm = new SettingsViewModel(session,
            configProvider: () => temp.Service,   // nothing saved → Auto
            availabilityProbe: () => new ComputeAvailability(Cuda: true, Mps: false));

        vm.ApplyDefaultsToNewSession();

        Assert.Equal("cuda", session.Engine.Device);
    }

    [Fact]
    public void Auto_on_a_cpu_machine_seeds_cpu()
    {
        var session = new Session();
        using var temp = new TempConfig();
        var vm = new SettingsViewModel(session,
            configProvider: () => temp.Service,
            availabilityProbe: () => new ComputeAvailability(Cuda: false, Mps: false));

        vm.ApplyDefaultsToNewSession();

        Assert.Equal("cpu", session.Engine.Device);
    }

    [Fact]
    public void An_explicit_cpu_default_is_respected_even_with_cuda_present()
    {
        var session = new Session();
        using var temp = new TempConfig();
        var config = temp.Service.Load();
        config.Ui.DefaultDevice = "cpu";
        temp.Service.Save(config);

        var vm = new SettingsViewModel(session,
            configProvider: () => temp.Service,
            availabilityProbe: () => new ComputeAvailability(Cuda: true, Mps: false));

        vm.ApplyDefaultsToNewSession();

        Assert.Equal("cpu", session.Engine.Device);
    }

    [Fact]
    public void A_saved_default_this_machine_cannot_run_falls_back_to_the_probe()
    {
        var session = new Session();
        using var temp = new TempConfig();
        var config = temp.Service.Load();
        config.Ui.DefaultDevice = "cuda";
        temp.Service.Save(config);

        var vm = new SettingsViewModel(session,
            configProvider: () => temp.Service,
            availabilityProbe: () => new ComputeAvailability(Cuda: false, Mps: false));

        vm.ApplyDefaultsToNewSession();

        Assert.Equal("cpu", session.Engine.Device);
    }

    // ── Per-user sidecar directory (the R11 fix) ─────────────────────────────

    [Fact]
    public void User_sidecar_root_follows_the_override()
    {
        var root = Directory.CreateTempSubdirectory("synapic-sidecar-root-");
        try
        {
            using var _ = new SidecarRootOverride(root.FullName);
            Assert.Equal(root.FullName, InferenceSidecarService.UserSidecarRoot());
        }
        finally
        {
            try { root.Delete(recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void A_downloaded_variant_is_found_in_its_own_per_user_folder()
    {
        // A folder per RID beneath the user's sidecar root: the CPU and CUDA
        // executables share a file name, so sibling folders are what keeps one
        // download from destroying the other (code review R11).
        var root = Directory.CreateTempSubdirectory("synapic-sidecar-install-");
        try
        {
            var rid = "test-rid";   // deliberately not PreferredRid or a -cuda twin
            using var _ = new SidecarRootOverride(root.FullName);
            var dir = Path.Combine(root.FullName, rid);
            System.IO.Directory.CreateDirectory(dir);
            var exe = Path.Combine(dir, InferenceSidecarService.ExeName);
            File.WriteAllText(exe, "stub");

            var found = InferenceSidecarService.FindExecutableForRid(rid);

            Assert.Equal(exe, found);
        }
        finally
        {
            try { root.Delete(recursive: true); } catch { /* best effort */ }
        }
    }

    // ── First-run auto-install + auto-boot ───────────────────────────────────

    private static string ExpectedCudaRid() =>
        OperatingSystem.IsWindows() ? "win-x64-cuda" : InferenceSidecarService.PreferredRid();

    /// <summary>
    /// Per-user sidecar paths relocated into a temp directory for the duration
    /// of one test. With a repo checkout, SidecarInstallPath resolves into the
    /// repo's real artifacts/ folder — a download must never touch that.
    /// </summary>
    private static string RelocateSidecarRoot(out DirectoryInfo root)
    {
        root = Directory.CreateTempSubdirectory("synapic-firstrun-sidecar-");
        return Environment.GetEnvironmentVariable(InferenceSidecarService.UserSidecarRootEnvVar) ?? "";
    }

    /// <summary>Restores the SYNAPIC_SIDECAR_ROOT override and the temp folder.</summary>
    private static void RestoreSidecarRoot(string? previous, DirectoryInfo root)
    {
        Environment.SetEnvironmentVariable(InferenceSidecarService.UserSidecarRootEnvVar, previous);
        try { root.Delete(recursive: true); } catch { /* best effort */ }   // may hold the exe on shutdown races
    }

    [AvaloniaFact]
    public async Task Detection_fetches_the_recommended_variant_and_boots_the_server()
    {
        using var temp = new TempConfig();
        var previous = RelocateSidecarRoot(out var sidecarRoot);
        using var rootOverride = new SidecarRootOverride(sidecarRoot.FullName);
        try
        {
            var downloads = new FakeDownloadService
            {
                OnDownload = (_, dest, _, _) =>
                {
                    var destInfo = new FileInfo(dest);
                    if (destInfo.Directory is { } dir)
                    {
                        System.IO.Directory.CreateDirectory(dir.FullName);
                        File.WriteAllText(dest, "stub");   // simulate the sidecar landing
                    }
                    return Task.CompletedTask;
                },
            };
            var sidecar = new FakeSidecar();

            // A CUDA-capable machine, per-user install paths, no executable
            // anywhere. "On disk" = in the relocated location the download writes.
            string? variantFor(string rid)
            {
                var exe = Path.Combine(sidecarRoot.FullName, rid, InferenceSidecarService.ExeName);
                return File.Exists(exe) ? exe : null;
            }
            var vm = new MainWindowViewModel(sidecar, new Session(),
                () => variantFor(ExpectedCudaRid()),
                null, null,
                rid => variantFor(rid),
                download: downloads,
                configService: temp.Service,
                computeAvailabilityProbe: () => new ComputeAvailability(Cuda: true, Mps: false));

            await vm.DetectServerAsync();

            Assert.Equal(new[] { ExpectedCudaRid() }, downloads.DownloadedRids);
            var downloadedExe = Path.Combine(sidecarRoot.FullName, ExpectedCudaRid(), InferenceSidecarService.ExeName);
            Assert.True(File.Exists(downloadedExe), $"downloaded exe missing: {downloadedExe}");
            Assert.True(vm.IsSidecarReady, "IsSidecarReady");
            Assert.True(vm.IsWorkspaceEnabled);
            // The download landed, so the automation boots the server in the same
            // chain: the user never presses anything.
            Assert.Equal(1, sidecar.StartCalls);
            Assert.Equal(ServerUiState.Running, vm.ServerState);
        }
        finally
        {
            RestoreSidecarRoot(previous, sidecarRoot);
        }
    }

    [AvaloniaFact]
    public async Task Detection_does_nothing_automatically_when_auto_install_is_off()
    {
        using var temp = new TempConfig();
        var config = temp.Service.Load();
        config.Ui.AutoInstallSidecar = false;
        temp.Service.Save(config);

        var downloads = new FakeDownloadService();
        var sidecar = new FakeSidecar();
        var vm = new MainWindowViewModel(sidecar, new Session(),
            () => null, null, null, _ => null,
            download: downloads,
            configService: temp.Service,
            computeAvailabilityProbe: () => new ComputeAvailability(Cuda: true, Mps: false));

        await vm.DetectServerAsync();

        Assert.Empty(downloads.DownloadedRids);
        Assert.Equal(0, sidecar.StartCalls);
        Assert.Equal(ServerUiState.NotDetected, vm.ServerState);
    }

    [AvaloniaFact]
    public async Task A_failed_auto_install_reports_once_and_keeps_the_workspace_unlockable_by_hand()
    {
        using var temp = new TempConfig();
        var previous = RelocateSidecarRoot(out var sidecarRoot);
        using var rootOverride = new SidecarRootOverride(sidecarRoot.FullName);
        try
        {
            var downloads = new FakeDownloadService
            {
                OnDownload = (_, _, _, _) => throw new InvalidOperationException("boom"),
            };
            var sidecar = new FakeSidecar();
            var vm = new MainWindowViewModel(sidecar, new Session(),
                () => null, null, null, _ => null,
                download: downloads,
                configService: temp.Service,
                computeAvailabilityProbe: () => new ComputeAvailability(Cuda: true, Mps: false));

            await vm.DetectServerAsync();
            // One failed attempt must not loop: exactly one download call.
            Assert.Equal(new[] { ExpectedCudaRid() }, downloads.DownloadedRids);
            Assert.Equal(ServerUiState.NotDetected, vm.ServerState);

            // The user remains in control: a manual retry still works.
            await vm.SidecarVariants[0].DownloadCommand.ExecuteAsync(null);
            Assert.Equal(new[] { ExpectedCudaRid(), vm.SidecarVariants[0].Rid }, downloads.DownloadedRids);
        }
        finally
        {
            RestoreSidecarRoot(previous, sidecarRoot);
        }
    }

    [AvaloniaFact]
    public async Task The_recommended_row_is_marked_for_this_machine()
    {
        var vm = new MainWindowViewModel(new FakeSidecar(), new Session(),
            () => null, null, null, _ => null,
            computeAvailabilityProbe: () => new ComputeAvailability(Cuda: true, Mps: false));

        await vm.DetectServerAsync();

        Assert.Equal(ExpectedCudaRid(), vm.RecommendedRid);
        var recommended = vm.SidecarVariants.Single(v => v.IsRecommended);
        Assert.Equal(ExpectedCudaRid(), recommended.Rid);
        Assert.Equal("Recommended for this machine", recommended.RecommendedText);
        Assert.All(vm.SidecarVariants.Where(v => !v.IsRecommended), v => Assert.False(v.IsRecommended));
    }
}

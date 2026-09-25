using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.ViewModels;
using Synapic.Shared.Contracts;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// Startup server-detection behavior: black "not detected" + one-off Build
/// button when the sidecar executable is missing, red "stopped" with Start
/// enabled when present, green "running" with Stop enabled when an instance
/// is already alive — plus the always-visible model download indicator.
/// </summary>
public class ServerDetectionTests
{
    private const string Exe = "synapic-inference.exe";

    [AvaloniaFact]
    public async Task Missing_executable_shows_not_detected_with_build_button()
    {
        var vm = new MainWindowViewModel(new FakeSidecar(), new FakeBuildService(), new Session(), () => null, null, null, _ => null);

        await vm.DetectServerAsync();

        Assert.Equal(ServerUiState.NotDetected, vm.ServerState);
        Assert.Equal(Colors.Black, ((ISolidColorBrush)vm.ServerBrush).Color);
        Assert.Equal("Server not detected", vm.StatusText);
        Assert.True(vm.IsBuildButtonVisible);
        Assert.False(vm.StartServerCommand.CanExecute(null));
        Assert.False(vm.StopServerCommand.CanExecute(null));
        // The sidecar gates the workspace: nothing is usable until one exists.
        Assert.True(vm.IsSidecarRequired);
        Assert.False(vm.IsWorkspaceEnabled);
        Assert.True(vm.IsSidecarPanelVisible);
    }

    [AvaloniaFact]
    public async Task Present_executable_shows_stopped_with_start_enabled()
    {
        var vm = new MainWindowViewModel(new FakeSidecar(), new FakeBuildService(), new Session(), () => Exe, null, null, _ => Exe);

        await vm.DetectServerAsync();

        Assert.Equal(ServerUiState.Stopped, vm.ServerState);
        Assert.Equal(Colors.Red, ((ISolidColorBrush)vm.ServerBrush).Color);
        Assert.Equal("Server stopped", vm.StatusText);
        Assert.False(vm.IsBuildButtonVisible);
        Assert.True(vm.StartServerCommand.CanExecute(null));
        Assert.False(vm.StopServerCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Detection_shows_stopped_and_never_adopts_or_auto_starts()
    {
        // Strict lifecycle: launch = server not running; only Start (button or
        // auto-launch) starts it, and nothing adopts leftover servers.
        var sidecar = new FakeSidecar();
        var vm = new MainWindowViewModel(sidecar, new FakeBuildService(), new Session(), () => Exe, null, null, _ => Exe);

        await vm.DetectServerAsync();

        Assert.Equal(ServerUiState.Stopped, vm.ServerState);
        Assert.Equal(0, sidecar.StartCalls);
        Assert.True(vm.StartServerCommand.CanExecute(null));
        Assert.False(vm.StopServerCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Build_command_builds_then_redetects_and_enables_start()
    {
        var exe = new string?[] { null };
        var sidecar = new FakeSidecar();
        var build = new FakeBuildService
        {
            OnBuild = (_, _, _, _) => { exe[0] = Exe; return Task.CompletedTask; },
        };
        var vm = new MainWindowViewModel(sidecar, build, new Session(), () => exe[0], null, null,
            rid => rid == InferenceSidecarService.PreferredRid() ? exe[0] : null);

        await vm.DetectServerAsync();
        Assert.Equal(ServerUiState.NotDetected, vm.ServerState);
        Assert.False(vm.IsWorkspaceEnabled);

        await vm.BuildServerCommand.ExecuteAsync(null);

        Assert.Equal(1, build.BuildCalls);
        Assert.Equal(ServerUiState.Stopped, vm.ServerState);
        Assert.True(vm.StartServerCommand.CanExecute(null));
        Assert.False(vm.IsBuildButtonVisible);
        Assert.True(vm.IsWorkspaceEnabled);
    }

    [AvaloniaFact]
    public async Task Failed_build_returns_to_not_detected_so_button_reappears()
    {
        var build = new FakeBuildService
        {
            OnBuild = (_, _, _, _) => throw new InvalidOperationException("boom"),
        };
        var vm = new MainWindowViewModel(new FakeSidecar(), build, new Session(), () => null, null, null, _ => null);

        await vm.DetectServerAsync();
        await vm.BuildServerCommand.ExecuteAsync(null);

        Assert.Equal(ServerUiState.NotDetected, vm.ServerState);
        Assert.True(vm.IsBuildButtonVisible);
    }

    [AvaloniaFact]
    public void Sidecar_status_events_drive_indicator_colors()
    {
        var sidecar = new FakeSidecar();
        var vm = new MainWindowViewModel(sidecar, new FakeBuildService(), new Session(), () => Exe);

        sidecar.RaiseStatus(SidecarStatus.Starting);
        Assert.Equal(ServerUiState.Starting, vm.ServerState);
        Assert.Equal(Colors.Orange, ((ISolidColorBrush)vm.ServerBrush).Color);

        sidecar.RaiseStatus(SidecarStatus.Ready);
        Assert.Equal(ServerUiState.Running, vm.ServerState);
        Assert.Equal(Colors.ForestGreen, ((ISolidColorBrush)vm.ServerBrush).Color);
        Assert.Contains("Server running", vm.StatusText);
    }

    // ── Per-platform sidecar setup (the workspace gate) ─────────────────────

    [AvaloniaFact]
    public async Task Variants_list_one_row_per_buildable_rid_for_this_machine()
    {
        var vm = new MainWindowViewModel(new FakeSidecar(), new FakeBuildService(), new Session(),
            () => null, null, null, _ => null);

        await vm.DetectServerAsync();

        Assert.Equal(InferenceSidecarService.BuildableRids(), vm.SidecarVariants.Select(v => v.Rid).ToArray());
        Assert.All(vm.SidecarVariants, v => Assert.False(v.IsBuilt));
        Assert.All(vm.SidecarVariants, v => Assert.True(v.IsBuildButtonVisible));
        Assert.All(vm.SidecarVariants, v => Assert.Equal("Not built", v.StatusText));
    }

    [AvaloniaFact]
    public async Task Cuda_variant_is_offered_on_windows_only()
    {
        var vm = new MainWindowViewModel(new FakeSidecar(), new FakeBuildService(), new Session(),
            () => null, null, null, _ => null);

        await vm.DetectServerAsync();

        var cuda = vm.SidecarVariants
            .FirstOrDefault(v => v.Rid.EndsWith("-cuda", StringComparison.OrdinalIgnoreCase));
        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(cuda);
            Assert.Contains("CUDA", cuda!.DisplayName);
        }
        else
        {
            Assert.Null(cuda);
        }
    }

    [AvaloniaFact]
    public async Task Per_variant_build_calls_the_service_with_that_variant_rid()
    {
        var build = new FakeBuildService();
        var vm = new MainWindowViewModel(new FakeSidecar(), build, new Session(),
            () => null, null, null, _ => null);
        await vm.DetectServerAsync();

        var target = vm.SidecarVariants[^1];
        await target.BuildCommand.ExecuteAsync(null);

        Assert.Equal(new[] { target.Rid }, build.BuiltRids);
    }

    [AvaloniaFact]
    public async Task Failed_variant_build_leaves_the_workspace_disabled()
    {
        var build = new FakeBuildService
        {
            OnBuild = (_, _, _, _) => throw new InvalidOperationException("boom"),
        };
        var vm = new MainWindowViewModel(new FakeSidecar(), build, new Session(),
            () => null, null, null, _ => null);

        await vm.DetectServerAsync();
        await vm.SidecarVariants[0].BuildCommand.ExecuteAsync(null);

        Assert.False(vm.IsWorkspaceEnabled);
        Assert.Equal(ServerUiState.NotDetected, vm.ServerState);
        Assert.True(vm.SidecarVariants[0].IsBuildButtonVisible);
    }

    [AvaloniaFact]
    public async Task Building_a_variant_unlocks_the_workspace()
    {
        var exe = new string?[] { null };
        var build = new FakeBuildService
        {
            OnBuild = (_, _, _, _) => { exe[0] = Exe; return Task.CompletedTask; },
        };
        var vm = new MainWindowViewModel(new FakeSidecar(), build, new Session(), () => exe[0], null, null,
            rid => rid == InferenceSidecarService.PreferredRid() ? exe[0] : null);

        await vm.DetectServerAsync();
        Assert.False(vm.IsWorkspaceEnabled);

        await vm.SidecarVariants[0].BuildCommand.ExecuteAsync(null);

        Assert.True(vm.IsWorkspaceEnabled);
        Assert.True(vm.StartServerCommand.CanExecute(null));
        Assert.Equal(InferenceSidecarService.PreferredRid(), build.BuiltRids.Single());
    }

    [AvaloniaFact]
    public async Task Built_variant_survives_while_the_other_platform_is_still_offered()
    {
        var cpuRid = InferenceSidecarService.PreferredRid();
        var vm = new MainWindowViewModel(new FakeSidecar(), new FakeBuildService(), new Session(),
            () => Exe, null, null, rid => rid == cpuRid ? Exe : null);

        await vm.DetectServerAsync();

        Assert.True(vm.IsSidecarReady);
        Assert.True(vm.IsWorkspaceEnabled);
        Assert.False(vm.IsSidecarRequired);

        var built = vm.SidecarVariants.Single(v => v.Rid == cpuRid);
        Assert.True(built.IsBuilt);
        Assert.False(built.IsBuildButtonVisible);
        Assert.Contains("Built", built.StatusText);

        // Whatever else this machine can build stays available to add later.
        foreach (var missing in vm.SidecarVariants.Where(v => v.Rid != cpuRid))
        {
            Assert.False(missing.IsBuilt);
            Assert.True(missing.IsBuildButtonVisible);
        }
    }

    // ── Updating an already-built variant ───────────────────────────────────

    [AvaloniaFact]
    public async Task Built_variant_offers_update_instead_of_build_or_download()
    {
        var rid = InferenceSidecarService.PreferredRid();
        var vm = new MainWindowViewModel(new FakeSidecar(), new FakeBuildService(), new Session(),
            () => Exe, null, null, r => r == rid ? Exe : null);
        await vm.DetectServerAsync();

        var built = vm.SidecarVariants.Single(v => v.Rid == rid);

        Assert.True(built.IsBuilt);
        Assert.False(built.IsBuildButtonVisible);
        Assert.False(built.IsDownloadButtonVisible);
        Assert.True(built.IsUpdateButtonVisible);
        Assert.True(built.UpdateCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Update_rebuilds_from_source_when_the_toolchain_is_available()
    {
        var rid = InferenceSidecarService.PreferredRid();
        var build = new FakeBuildService();
        var download = new FakeDownloadService();
        var vm = new MainWindowViewModel(new FakeSidecar(), build, new Session(),
            () => Exe, null, null, r => r == rid ? Exe : null, download: download);
        await vm.DetectServerAsync();

        await vm.SidecarVariants.Single(v => v.Rid == rid).UpdateCommand.ExecuteAsync(null);

        Assert.Equal(new[] { rid }, build.BuiltRids);
        Assert.Empty(download.DownloadedRids);
    }

    [AvaloniaFact]
    public async Task Update_falls_back_to_the_release_when_this_machine_cannot_build()
    {
        var rid = InferenceSidecarService.PreferredRid();
        var build = new FakeBuildService { CanBuild = false };
        var download = new FakeDownloadService();
        var vm = new MainWindowViewModel(new FakeSidecar(), build, new Session(),
            () => Exe, null, null, r => r == rid ? Exe : null, download: download);
        await vm.DetectServerAsync();

        var built = vm.SidecarVariants.Single(v => v.Rid == rid);
        await built.UpdateCommand.ExecuteAsync(null);

        Assert.Equal(new[] { rid }, download.DownloadedRids);
        Assert.Empty(build.BuiltRids);
    }

    [AvaloniaFact]
    public async Task Update_stops_a_running_server_and_starts_it_again_on_the_new_build()
    {
        var rid = InferenceSidecarService.PreferredRid();
        var sidecar = new FakeSidecar();
        var build = new FakeBuildService();
        var vm = new MainWindowViewModel(sidecar, build, new Session(),
            () => Exe, null, null, r => r == rid ? Exe : null);
        await vm.DetectServerAsync();
        await vm.StartServerCommand.ExecuteAsync(null);
        Assert.Equal(ServerUiState.Running, vm.ServerState);

        await vm.SidecarVariants.Single(v => v.Rid == rid).UpdateCommand.ExecuteAsync(null);

        // Windows will not let either the build or the file move overwrite a
        // locked executable, and the replacement only counts once it is launched.
        Assert.Equal(1, sidecar.StopCalls);
        Assert.Equal(2, sidecar.StartCalls);
        Assert.Equal(ServerUiState.Running, vm.ServerState);
        Assert.Equal(new[] { rid }, build.BuiltRids);
    }

    [AvaloniaFact]
    public async Task Stale_build_reopens_the_panel_and_flags_the_row()
    {
        var rid = InferenceSidecarService.PreferredRid();
        var dir = Directory.CreateTempSubdirectory("synapic-stale-");
        try
        {
            var exe = Path.Combine(dir.FullName, InferenceSidecarService.ExeName);
            File.WriteAllText(exe, "stub");

            // Every variant exists, so only staleness can hold the panel open.
            var vm = new MainWindowViewModel(new FakeSidecar(), new FakeBuildService(), new Session(),
                () => exe, null, null, _ => exe);

            File.SetLastWriteTimeUtc(exe, DateTime.UtcNow.AddHours(1));
            await vm.DetectServerAsync();
            Assert.False(vm.IsSidecarPanelVisible);
            Assert.All(vm.SidecarVariants, v => Assert.False(v.IsStale));
            // Nothing is stale, but the button is still there: replacing a build is
            // always legitimate, staleness only explains why it is worth doing.
            Assert.All(vm.SidecarVariants, v => Assert.True(v.IsUpdateButtonVisible));

            // Older than the sidecar source: an update exists, and the panel that
            // offers it has to come back into view to say so.
            File.SetLastWriteTimeUtc(exe, DateTime.UtcNow.AddYears(-1));
            await vm.DetectServerAsync();

            Assert.True(vm.IsSidecarPanelVisible);
            var stale = vm.SidecarVariants.Single(v => v.Rid == rid);
            Assert.True(stale.IsStale);
            Assert.Contains("Update available", stale.StaleText);
            Assert.True(stale.IsUpdateButtonVisible);
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch { /* best effort */ }
        }
    }

    [AvaloniaFact]
    public async Task Build_in_progress_disables_the_other_variants_button()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var build = new FakeBuildService
        {
            OnBuild = (_, _, _, _) => gate.Task,
        };
        var vm = new MainWindowViewModel(new FakeSidecar(), build, new Session(),
            () => null, null, null, _ => null);
        await vm.DetectServerAsync();

        if (vm.SidecarVariants.Count < 2) return; // a second variant exists on Windows only

        var first = vm.SidecarVariants[0];
        var second = vm.SidecarVariants[1];

        var run = first.BuildCommand.ExecuteAsync(null);

        // The build body runs synchronously up to its first await, so the
        // one-build-at-a-time side effects are observable immediately.
        Assert.True(first.IsBuilding);
        Assert.False(second.CanBuild);
        Assert.False(second.BuildCommand.CanExecute(null));

        gate.SetResult();
        await run;
    }

    // ── Build progress wiring ───────────────────────────────────────────────

    [AvaloniaFact]
    public async Task Build_progress_streams_into_the_variant_row()
    {
        var build = new FakeBuildService
        {
            OnBuild = (_, _, progress, _) =>
            {
                progress.Report(new SidecarBuildProgress(8, "Downloading CUDA torch wheels (~2.5 GB) - the longest step"));
                progress.Report(new SidecarBuildProgress(66, "Packaging: assembling archive"));
                return Task.CompletedTask;
            },
        };
        var vm = new MainWindowViewModel(new FakeSidecar(), build, new Session(),
            () => null, null, null, _ => null);
        await vm.DetectServerAsync();

        var variant = vm.SidecarVariants[0];
        // Before any stage arrives the bar spins rather than sitting at zero.
        Assert.False(variant.HasProgress);
        Assert.True(variant.IsBuildIndeterminate);

        await variant.BuildCommand.ExecuteAsync(null);

        // Reports are marshalled through Progress<T>, so let the queue drain.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!variant.HasProgress && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(variant.HasProgress);
        Assert.False(variant.IsBuildIndeterminate);
        Assert.Equal(66, variant.BuildPercent);
        Assert.Contains("Packaging", variant.BuildStage);
    }

    [AvaloniaFact]
    public async Task Starting_a_build_clears_a_stale_bar()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var build = new FakeBuildService
        {
            OnBuild = (_, _, _, _) => gate.Task,
        };
        var vm = new MainWindowViewModel(new FakeSidecar(), build, new Session(),
            () => null, null, null, _ => null);
        await vm.DetectServerAsync();

        var variant = vm.SidecarVariants[0];

        // A leftover reading from a previous run must not persist.
        variant.ApplyProgress(new SidecarBuildProgress(80, "Packaging: assembling archive"));
        Assert.True(variant.HasProgress);

        var run = variant.BuildCommand.ExecuteAsync(null);
        Assert.False(variant.HasProgress);
        Assert.True(variant.IsBuildIndeterminate);

        gate.SetResult();
        await run;
    }

    // ── Model download indicator ─────────────────────────────────────────────

    [AvaloniaFact]
    public void Download_progress_shows_bytes_and_percent()
    {
        var vm = new MainWindowViewModel(new FakeSidecar(), new FakeBuildService(), new Session(), () => Exe, null, null, _ => Exe);

        vm.ApplyDownloadStatus(new HealthResponse
        {
            Status = "ready",
            Download = new ModelDownloadProgress
            {
                ModelId = "LiquidAI/LFM2.5-VL-450M",
                Status = "downloading",
                DoneBytes = 471_859_200,  // 450 MB
                TotalBytes = 943_718_400, // 900 MB
            },
        });

        Assert.True(vm.IsDownloadVisible);
        Assert.Equal(50, vm.DownloadPercent);
        Assert.Contains("Downloading model", vm.DownloadText);
        Assert.Contains("450 / 900 MB", vm.DownloadText);
        Assert.Contains("50%", vm.DownloadText);
    }

    [AvaloniaFact]
    public void Download_without_total_shows_bytes_only()
    {
        var vm = new MainWindowViewModel(new FakeSidecar(), new FakeBuildService(), new Session(), () => Exe, null, null, _ => Exe);

        vm.ApplyDownloadStatus(new HealthResponse
        {
            Download = new ModelDownloadProgress { Status = "downloading", DoneBytes = 12_345_678 },
        });

        Assert.True(vm.IsDownloadVisible);
        Assert.Contains("Downloading model: 12 MB", vm.DownloadText);
    }

    [AvaloniaFact]
    public void Download_transitions_log_once_then_panel_hides_when_clear()
    {
        var vm = new MainWindowViewModel(new FakeSidecar(), new FakeBuildService(), new Session(), () => Exe, null, null, _ => Exe);

        var downloading = new HealthResponse
        {
            Download = new ModelDownloadProgress { ModelId = "LiquidAI/LFM2.5-VL-450M", Status = "downloading", DoneBytes = 1, TotalBytes = 2 },
        };
        vm.ApplyDownloadStatus(downloading);
        vm.ApplyDownloadStatus(downloading);
        Assert.Equal(1, vm.LogEntries.Count(l => l.RenderedMessage.Contains("Downloading model LiquidAI/LFM2.5-VL-450M")));

        vm.ApplyDownloadStatus(new HealthResponse
        {
            Download = new ModelDownloadProgress { ModelId = "LiquidAI/LFM2.5-VL-450M", Status = "complete", DoneBytes = 2, TotalBytes = 2 },
        });
        Assert.Equal(100, vm.DownloadPercent);
        Assert.Contains("Model ready", vm.DownloadText);
        Assert.Contains("Model downloaded: LiquidAI/LFM2.5-VL-450M", vm.LogEntries.Single(l => l.RenderedMessage.Contains("Model downloaded")).RenderedMessage);

        vm.ApplyDownloadStatus(new HealthResponse());
        Assert.False(vm.IsDownloadVisible);
    }

    [AvaloniaFact]
    public void Download_failure_is_shown_and_logged()
    {
        var vm = new MainWindowViewModel(new FakeSidecar(), new FakeBuildService(), new Session(), () => Exe, null, null, _ => Exe);

        vm.ApplyDownloadStatus(new HealthResponse
        {
            Download = new ModelDownloadProgress { ModelId = "LiquidAI/LFM2.5-VL-450M", Status = "failed", Error = "offline" },
        });

        Assert.True(vm.IsDownloadVisible);
        Assert.Contains("failed", vm.DownloadText);
        Assert.Contains("Model download failed for LiquidAI/LFM2.5-VL-450M: offline",
            vm.LogEntries.Single(l => l.RenderedMessage.Contains("Model download failed")).RenderedMessage);
    }
}

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
        var vm = new MainWindowViewModel(new FakeSidecar(), new FakeBuildService(), new Session(), () => null);

        await vm.DetectServerAsync();

        Assert.Equal(ServerUiState.NotDetected, vm.ServerState);
        Assert.Equal(Colors.Black, ((ISolidColorBrush)vm.ServerBrush).Color);
        Assert.Equal("Server not detected", vm.StatusText);
        Assert.True(vm.IsBuildButtonVisible);
        Assert.False(vm.StartServerCommand.CanExecute(null));
        Assert.False(vm.StopServerCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Present_executable_shows_stopped_with_start_enabled()
    {
        var vm = new MainWindowViewModel(new FakeSidecar(), new FakeBuildService(), new Session(), () => Exe);

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
        var vm = new MainWindowViewModel(sidecar, new FakeBuildService(), new Session(), () => Exe);

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
            OnBuild = (_, _) => { exe[0] = Exe; return Task.CompletedTask; },
        };
        var vm = new MainWindowViewModel(sidecar, build, new Session(), () => exe[0]);

        await vm.DetectServerAsync();
        Assert.Equal(ServerUiState.NotDetected, vm.ServerState);

        await vm.BuildServerCommand.ExecuteAsync(null);

        Assert.Equal(1, build.BuildCalls);
        Assert.Equal(ServerUiState.Stopped, vm.ServerState);
        Assert.True(vm.StartServerCommand.CanExecute(null));
        Assert.False(vm.IsBuildButtonVisible);
    }

    [AvaloniaFact]
    public async Task Failed_build_returns_to_not_detected_so_button_reappears()
    {
        var build = new FakeBuildService
        {
            OnBuild = (_, _) => throw new InvalidOperationException("boom"),
        };
        var vm = new MainWindowViewModel(new FakeSidecar(), build, new Session(), () => null);

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

    // ── Model download indicator ─────────────────────────────────────────────

    [AvaloniaFact]
    public void Download_progress_shows_bytes_and_percent()
    {
        var vm = new MainWindowViewModel(new FakeSidecar(), new FakeBuildService(), new Session(), () => Exe);

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
        var vm = new MainWindowViewModel(new FakeSidecar(), new FakeBuildService(), new Session(), () => Exe);

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
        var vm = new MainWindowViewModel(new FakeSidecar(), new FakeBuildService(), new Session(), () => Exe);

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
        var vm = new MainWindowViewModel(new FakeSidecar(), new FakeBuildService(), new Session(), () => Exe);

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

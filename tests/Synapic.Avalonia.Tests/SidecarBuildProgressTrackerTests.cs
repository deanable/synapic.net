using Synapic.Avalonia.Services;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// The build progress table: markers in the real pipeline output must map to a
/// sensible stage and a percentage that never moves backwards. A temp path is
/// used for the repo root so no real build artifacts are read.
/// </summary>
public class SidecarBuildProgressTrackerTests
{
    private static SidecarBuildProgressTracker Tracker() =>
        new(Path.Combine(Path.GetTempPath(), $"synapic-progress-{Guid.NewGuid():N}"), "win-x64");

    [Fact]
    public void Starts_idle_at_zero()
    {
        var tracker = Tracker();

        Assert.Equal(0, tracker.Percent);
        Assert.Equal("Starting build pipeline", tracker.Stage);
        Assert.False(tracker.IsPackaging);
    }

    [Fact]
    public void Cuda_and_cpu_installs_are_labelled_with_their_download_size()
    {
        var cuda = Tracker();
        cuda.Observe("Installing torch/torchvision (CUDA 12.6 wheels)");
        Assert.Contains("2.5 GB", cuda.Stage);
        Assert.Equal(8, cuda.Percent);

        var cpu = Tracker();
        cpu.Observe("Installing torch/torchvision (CPU wheels)");
        Assert.Contains("250 MB", cpu.Stage);
        Assert.Equal(8, cpu.Percent);
    }

    [Fact]
    public void Python_fetch_is_recognised_but_barely_moves_the_bar()
    {
        var tracker = Tracker();

        tracker.Observe("[1/3] Fetching standalone Python for win-x64 (one-time, ~30 MB)...");
        Assert.True(tracker.Percent is > 0 and <= 5);

        tracker.Observe("[1/3] Standalone Python already present - skipping fetch.");
        Assert.Equal(4, tracker.Percent);
        Assert.Equal("Python runtime ready", tracker.Stage);
    }

    [Fact]
    public void Progress_never_moves_backwards()
    {
        var tracker = Tracker();
        tracker.Observe("Installing torch/torchvision (CUDA 12.6 wheels)");
        tracker.Observe("Running PyInstaller for win-x64");
        var peak = tracker.Percent;

        // A late or repeated marker must not rewind the bar.
        tracker.Observe("Installing sidecar Python dependencies...");
        tracker.Observe("[1/3] Standalone Python already present - skipping fetch.");

        Assert.Equal(peak, tracker.Percent);
    }

    [Fact]
    public void Pip_percentage_interpolates_inside_the_install_band()
    {
        var tracker = Tracker();
        tracker.Observe("Installing torch/torchvision (CUDA 12.6 wheels)");
        Assert.Equal(8, tracker.Percent);

        // No percentage on this line, so nothing moves yet.
        tracker.Observe("  Downloading torch-2.9.1+cu126-cp311-cp311-win_amd64.whl (2.5 GB)");
        Assert.Equal(8, tracker.Percent);

        tracker.Observe("  50% |##########----------| 1.2 GB/2.5 GB 25.3 MB/s eta 0:01:00");

        Assert.True(tracker.Percent is > 8 and < 52, $"expected mid-band, got {tracker.Percent}");
    }

    [Fact]
    public void Packaging_markers_walk_through_to_ready()
    {
        var tracker = Tracker();

        tracker.Observe("Running PyInstaller for win-x64");
        Assert.True(tracker.IsPackaging);

        tracker.Observe("1234 INFO: Building PKG (CArchive) synapic-inference.pkg completed successfully.");
        var afterArchive = tracker.Percent;

        tracker.Observe("1234 INFO: Building EXE from EXE-00.toc completed successfully.");
        tracker.Observe("1234 INFO: Build complete! The results are available in: artifacts/win-x64");
        Assert.True(tracker.Percent > afterArchive);

        tracker.Observe("Sidecar build complete: artifacts/win-x64/synapic-inference.exe");
        Assert.Equal(100, tracker.Percent);
        Assert.False(tracker.IsPackaging);
        Assert.Equal("Sidecar ready", tracker.Stage);
    }

    [Fact]
    public void Byte_polling_is_ignored_outside_packaging()
    {
        var tracker = Tracker();
        tracker.Observe("Installing torch/torchvision (CPU wheels)");
        var before = tracker.Percent;

        tracker.PollBytes();

        Assert.Equal(before, tracker.Percent);
        Assert.DoesNotContain("written", tracker.Stage);
    }
}

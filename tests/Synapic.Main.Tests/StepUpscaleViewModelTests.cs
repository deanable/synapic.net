using Avalonia.Headless.XUnit;
using Synapic.Main.Models;
using Synapic.Main.ViewModels.Steps;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// The upscale step (port of step_upscale.py): BuildOptions must produce the
/// sidecar contract's exact wire values with the original app's clamps, the
/// settings that only mean something for a specific workflow enable/disable,
/// and Start must run the shared WorkflowRunner kernel over the Step 1 source
/// and report its summary.
/// </summary>
public class StepUpscaleViewModelTests
{
    private static Session LocalSession(string folder)
    {
        var session = new Session();
        session.Datasource.Type = "local";
        session.Datasource.LocalPath = folder;
        return session;
    }

    private static StepUpscaleViewModel VmForFolder(string folder, Synapic.Main.Services.IInferenceSidecar? sidecar = null)
        => new(new Step1DatasourceViewModel(LocalSession(folder)), sidecar);

    // ── BuildOptions: the parameters that differ between workflows ─────────

    [Fact]
    public void BuildOptions_maps_dropdown_indices_to_the_wire_values()
    {
        var vm = VmForFolder(Path.GetTempPath());
        vm.SelectedWorkflow = 1;   // balanced
        vm.SelectedFactor = 1;     // 4x
        vm.SelectedPrecision = 2;  // fp32
        vm.SelectedOutputFormat = 3; // WEBP
        vm.JpegQuality = 88;
        vm.DenoiseStrength = 0.25;
        vm.SharpenAmount = 1.5;
        vm.OverwriteExisting = false;

        var options = vm.BuildOptions();

        Assert.Equal("balanced", options.Workflow);
        Assert.Equal(4, options.Factor);
        Assert.Equal("fp32", options.Precision);
        Assert.Equal("WEBP", options.OutputFormat);
        Assert.Equal(88, options.JpegQuality);
        Assert.Equal(0.25, options.DenoiseStrength);
        Assert.Equal(1.5, options.SharpenAmount);
        Assert.False(options.OverwriteExisting);
    }

    [Fact]
    public void BuildOptions_clamps_every_parameter_to_the_contract_ranges()
    {
        var vm = VmForFolder(Path.GetTempPath());
        vm.SelectedWorkflow = -5;
        vm.SelectedFactor = -1;
        vm.SelectedPrecision = -99;
        vm.SelectedOutputFormat = -3;
        vm.JpegQuality = 10;          // below 70
        vm.DenoiseStrength = -2.5;    // below 0
        vm.SharpenAmount = 9;         // above 2

        var low = vm.BuildOptions();
        Assert.Equal("quality", low.Workflow);
        Assert.Equal(2, low.Factor);
        Assert.Equal("auto", low.Precision);
        Assert.Equal("keep", low.OutputFormat);
        Assert.Equal(70, low.JpegQuality);
        Assert.Equal(0.0, low.DenoiseStrength);
        Assert.Equal(2.0, low.SharpenAmount);      // 9 → clamped down to 2

        vm.SelectedWorkflow = 99;
        vm.SelectedFactor = 99;
        vm.SelectedPrecision = 99;
        vm.SelectedOutputFormat = 99;
        vm.JpegQuality = 500;         // above 100
        vm.DenoiseStrength = 3;       // above 1
        vm.SharpenAmount = -7;        // below 0

        var high = vm.BuildOptions();
        Assert.Equal("fast", high.Workflow);
        Assert.Equal(4, high.Factor);
        Assert.Equal("fp32", high.Precision);
        Assert.Equal("WEBP", high.OutputFormat);
        Assert.Equal(100, high.JpegQuality);
        Assert.Equal(1.0, high.DenoiseStrength);
        Assert.Equal(0.0, high.SharpenAmount);
    }

    // ── Workflow-dependent settings ─────────────────────────────────────────

    [Fact]
    public void Denoise_is_enabled_only_for_balanced_and_precision_only_for_ai_workflows()
    {
        var vm = VmForFolder(Path.GetTempPath());

        vm.SelectedWorkflow = 0;     // quality
        Assert.False(vm.IsDenoiseEnabled);
        Assert.True(vm.IsPrecisionEnabled);

        vm.SelectedWorkflow = 1;     // balanced
        Assert.True(vm.IsDenoiseEnabled);
        Assert.True(vm.IsPrecisionEnabled);

        vm.SelectedWorkflow = 2;     // fast (pure Lanczos)
        Assert.False(vm.IsDenoiseEnabled);
        Assert.False(vm.IsPrecisionEnabled);
    }

    // ── Source (read back from Step 1) ──────────────────────────────────────

    [Fact]
    public void Source_without_a_datasource_step_is_not_ready()
    {
        var vm = new StepUpscaleViewModel();

        Assert.False(vm.SourceReady);
        Assert.Equal("No datasource step available", vm.SourceSummary);
    }

    [Fact]
    public void Local_source_is_ready_only_when_the_folder_exists()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"synapic-gone-{Guid.NewGuid():N}");
        Assert.False(VmForFolder(missing).SourceReady);

        var folder = Directory.CreateTempSubdirectory("synapic-upscale-vm-").FullName;
        try
        {
            var vm = VmForFolder(folder);
            Assert.True(vm.SourceReady);
            Assert.Equal($"Source: local folder {folder}", vm.SourceSummary);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ── Command gating ──────────────────────────────────────────────────────

    [Fact]
    public void Start_requires_both_a_usable_source_and_a_sidecar()
    {
        var folder = Directory.CreateTempSubdirectory("synapic-upscale-vm-").FullName;
        try
        {
            // Ready source, but no sidecar → cannot start.
            var noSidecar = VmForFolder(folder, sidecar: null);
            Assert.False(noSidecar.StartCommand.CanExecute(null));
            Assert.False(noSidecar.StopCommand.CanExecute(null));

            // Sidecar, but no folder → cannot start.
            var noSource = VmForFolder(Path.Combine(Path.GetTempPath(), $"synapic-gone-{Guid.NewGuid():N}"),
                sidecar: new FakeSidecar());
            Assert.False(noSource.StartCommand.CanExecute(null));

            // Both present → start is offered.
            var ready = VmForFolder(folder, sidecar: new FakeSidecar());
            Assert.True(ready.StartCommand.CanExecute(null));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ── The run itself: shared kernel over an (empty) source ────────────────

    [AvaloniaFact]
    public async Task Start_runs_the_shared_kernel_and_reports_the_summary()
    {
        var folder = Directory.CreateTempSubdirectory("synapic-upscale-vm-").FullName;
        try
        {
            var vm = VmForFolder(folder, sidecar: new FakeSidecar { IsRunning = true });
            vm.SelectedWorkflow = 1;    // balanced
            vm.SelectedFactor = 1;      // 4x
            vm.JpegQuality = 88;
            vm.DenoiseStrength = 0.5;
            vm.SharpenAmount = 0.25;

            await vm.StartCommand.ExecuteAsync(null);

            Assert.False(vm.IsRunning);
            Assert.True(vm.IsIdle);
            Assert.Equal("Done. Processed 0, succeeded 0, failed 0", vm.ProgressText);

            // The original app's log lines: run start + the Parameters echo
            // (written on the UI thread, so they are on disk by now).
            Assert.Contains(vm.LogLines, l => l.Contains("Upscale run started.", StringComparison.Ordinal));
            var parameters = vm.LogLines.Single(l => l.Contains("Parameters:", StringComparison.Ordinal));
            Assert.Equal(
                "Parameters: workflow=balanced, factor=4x, precision=auto, max_dimension=2048, " +
                "output=keep, quality=88, denoise=0.50, sharpen=0.25, overwrite=true",
                parameters[(parameters.IndexOf(']') + 2)..]);      // strip "[HH:mm:ss] "
            Assert.Contains(vm.LogLines, l => l.Contains("Done. Processed 0", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

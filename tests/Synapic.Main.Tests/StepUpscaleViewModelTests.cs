using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Synapic.Main.Models;
using Synapic.Main.ViewModels.Steps;
using Synapic.Main.Views.Operation;
using Synapic.Main.Views.Wizard;
using Xunit;

using Synapic.Core.Services;
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

    private static StepUpscaleViewModel VmForFolder(string folder, Synapic.Core.Services.IInferenceSidecar? sidecar = null)
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

    /// <summary>
    /// A local source with nothing picked says so instead of rendering an empty
    /// value that reads like a half-loaded form.
    /// </summary>
    [Fact]
    public void Source_summary_says_a_local_folder_is_not_chosen_yet()
    {
        var vm = VmForFolder("");

        Assert.False(vm.SourceReady);
        Assert.Equal("Source: local folder (not chosen)", vm.SourceSummary);
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
    public void Start_requires_a_usable_source_a_sidecar_and_a_running_server()
    {
        var folder = Directory.CreateTempSubdirectory("synapic-upscale-vm-").FullName;
        try
        {
            // Ready source, but no sidecar → cannot start.
            var noSidecar = VmForFolder(folder, sidecar: null);
            Assert.False(noSidecar.StartCommand.CanExecute(null));
            Assert.Equal("No inference sidecar is available", noSidecar.StartBlockedReason);
            Assert.False(noSidecar.StopCommand.CanExecute(null));

            // Sidecar, but no folder → cannot start, and the hint names both
            // ways a source can exist rather than only the local one.
            var noSource = VmForFolder(Path.Combine(Path.GetTempPath(), $"synapic-gone-{Guid.NewGuid():N}"),
                sidecar: new FakeSidecar { IsRunning = true });
            Assert.False(noSource.StartCommand.CanExecute(null));
            Assert.Equal("Choose an existing folder or connect to Daminion in Settings",
                noSource.StartBlockedReason);

            // Source and executable, but the server is stopped → cannot start.
            var stopped = VmForFolder(folder, sidecar: new FakeSidecar());
            Assert.False(stopped.StartCommand.CanExecute(null));
            Assert.Equal("Start the inference server in Settings", stopped.StartBlockedReason);

            // Both present and the server running → start is offered.
            var runningSidecar = new FakeSidecar { IsRunning = true };
            var ready = VmForFolder(folder, sidecar: runningSidecar);
            Assert.True(ready.StartCommand.CanExecute(null));
            Assert.Null(ready.StartBlockedReason);

            // The gate follows the server's status, not just its first reading.
            runningSidecar.IsRunning = false;
            runningSidecar.RaiseStatus(Synapic.Core.Services.SidecarStatus.Stopped);
            Assert.False(ready.StartCommand.CanExecute(null));
            runningSidecar.IsRunning = true;
            runningSidecar.RaiseStatus(Synapic.Core.Services.SidecarStatus.Ready);
            Assert.True(ready.StartCommand.CanExecute(null));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Start_with_missing_local_source_reports_a_failed_run_in_the_output()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"synapic-upscale-missing-{Guid.NewGuid():N}");
        var vm = VmForFolder(missing, new UpscaleTestSidecar());
        var window = new Window { Content = new RunStateBar { DataContext = vm }, Width = 1000, Height = 700 };
        window.Show();
        try
        {
            await vm.StartCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Failed", vm.ProgressText);
            Assert.Contains(vm.LogLines, line => line.Contains($"Folder not found: {missing}", StringComparison.Ordinal));
            Assert.Contains(vm.LogLines, line => line.Contains("Upscale run failed:", StringComparison.Ordinal));
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == vm.ProgressText && text.IsVisible);
            Assert.False(vm.IsRunning);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Start_with_items_but_no_outputs_shows_actionable_failure_guidance()
    {
        var folder = Directory.CreateTempSubdirectory("synapic-upscale-failed-").FullName;
        try
        {
            var sample = FindSampleImage() ?? throw new InvalidOperationException("TestData/sample.jpg not found");
            File.Copy(sample, Path.Combine(folder, "one.jpg"));
            var sidecar = new UpscaleTestSidecar();
            var vm = VmForFolder(folder, sidecar);
            var window = new Window { Content = new RunStateBar { DataContext = vm }, Width = 1000, Height = 700 };
            window.Show();
            try
            {
                await vm.StartCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();

                const string guidance = "No upscale outputs were created — check the run log for failures, then adjust the source or settings before retrying.";
                Assert.Equal(guidance, vm.ProgressText);
                Assert.Contains(vm.LogLines, line => line.Contains(guidance, StringComparison.Ordinal));
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Text == guidance && text.IsVisible);
                Assert.Equal(1, sidecar.UpscaleCalls);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Stop_during_upscale_cancels_the_run_and_restores_idle_state()
    {
        var folder = Directory.CreateTempSubdirectory("synapic-upscale-cancel-").FullName;
        try
        {
            var sample = FindSampleImage() ?? throw new InvalidOperationException("TestData/sample.jpg not found");
            File.Copy(sample, Path.Combine(folder, "one.jpg"));
            var enteredUpscale = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sidecar = new UpscaleTestSidecar
            {
                CancellableUpscaleResult = async ct =>
                {
                    enteredUpscale.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                    return new Synapic.Shared.Contracts.UpscaleResponse();
                },
            };
            var vm = VmForFolder(folder, sidecar);
            var window = new Window { Content = new RunStateBar { DataContext = vm }, Width = 1000, Height = 700 };
            window.Show();
            try
            {
                var run = vm.StartCommand.ExecuteAsync(null);
                await enteredUpscale.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(vm.IsRunning);
                Assert.True(vm.StopCommand.CanExecute(null));

                vm.StopCommand.Execute(null);
                await run;
                Dispatcher.UIThread.RunJobs();

                Assert.Equal("Stopped", vm.ProgressText);
                Assert.Contains(vm.LogLines, line => line.Contains("Upscale stopped by user.", StringComparison.Ordinal));
                Assert.False(vm.IsRunning);
                Assert.True(vm.IsIdle);
                Assert.False(vm.StopCommand.CanExecute(null));
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Text == "Stopped" && text.IsVisible);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Start_with_mixed_success_and_failure_reports_partial_completion_not_empty_output()
    {
        var folder = Directory.CreateTempSubdirectory("synapic-upscale-partial-").FullName;
        var window = (Window?)null;
        try
        {
            var sample = FindSampleImage() ?? throw new InvalidOperationException("TestData/sample.jpg not found");
            var goodPath = Path.Combine(folder, "good.jpg");
            File.Copy(sample, goodPath);
            File.Copy(sample, Path.Combine(folder, "bad.jpg"));
            var sidecar = new UpscaleTestSidecar
            {
                UpscaleResult = request => request.ImagePath.EndsWith("bad.jpg", StringComparison.Ordinal)
                    ? Task.FromResult(new Synapic.Shared.Contracts.UpscaleResponse())
                    : CompleteFakeUpscale(request),
            };
            var vm = VmForFolder(folder, sidecar);
            vm.SelectedWorkflow = 2;
            window = new Window { Content = new RunStateBar { DataContext = vm }, Width = 1000, Height = 700 };
            window.Show();

            await vm.StartCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Done. Processed 2, succeeded 1, failed 1", vm.ProgressText);
            Assert.Contains(vm.LogLines, line => line.Contains("Batch finished: 1 ok, 1 failed, 2 total", StringComparison.Ordinal));
            Assert.DoesNotContain(vm.LogLines, line => line.Contains("No upscale outputs were created", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(folder, "good_upscaled.jpg")));
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == vm.ProgressText && text.IsVisible);
        }
        finally
        {
            window?.Close();
            Directory.Delete(folder, recursive: true);
        }
    }

    private static Task<Synapic.Shared.Contracts.UpscaleResponse> CompleteFakeUpscale(
        Synapic.Shared.Contracts.UpscaleRequest request)
    {
        var output = Path.Combine(Path.GetDirectoryName(request.ImagePath)!,
            $"{Path.GetFileNameWithoutExtension(request.ImagePath)}_upscaled{Path.GetExtension(request.ImagePath)}");
        File.Copy(request.ImagePath, output, overwrite: true);
        return Task.FromResult(new Synapic.Shared.Contracts.UpscaleResponse
        {
            OutputPath = output,
            OriginalWidth = 32,
            OriginalHeight = 24,
            Width = 64,
            Height = 48,
            Workflow = "fast",
            Factor = 2,
        });
    }

    private static string? FindSampleImage()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Synapic.Main.Tests", "TestData", "sample.jpg");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private sealed class UpscaleTestSidecar : Synapic.Core.Services.IInferenceSidecar
    {
        public int UpscaleCalls { get; private set; }
        public Func<Synapic.Shared.Contracts.UpscaleRequest, Task<Synapic.Shared.Contracts.UpscaleResponse>>? UpscaleResult { get; set; }
        public Func<CancellationToken, Task<Synapic.Shared.Contracts.UpscaleResponse>>? CancellableUpscaleResult { get; set; }
        public List<Synapic.Shared.Contracts.UpscaleRequest> UpscaleRequests { get; } = new();
        public Synapic.Core.Services.SidecarStatus CurrentStatus => Synapic.Core.Services.SidecarStatus.Ready;
        public int SidecarPort => 0;
        public bool IsRunning => true;
        public int ListModelsCalls { get; private set; }
        public int DownloadCalls { get; private set; }
        public Synapic.Shared.Contracts.ModelInfo[] Models { get; set; } = Array.Empty<Synapic.Shared.Contracts.ModelInfo>();
#pragma warning disable CS0067
        public event EventHandler<Synapic.Core.Services.SidecarStatusChangedEventArgs>? StatusChanged;
        public event Action<string>? LogReceived;
#pragma warning restore CS0067
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(TimeSpan? gracefulTimeout = null) => Task.CompletedTask;
        public Task<Synapic.Shared.Contracts.TagResponse> TagAsync(Synapic.Shared.Contracts.TagRequest request, CancellationToken ct = default)
            => Task.FromResult(new Synapic.Shared.Contracts.TagResponse());
        public Task<Synapic.Shared.Contracts.UpscaleResponse> UpscaleAsync(Synapic.Shared.Contracts.UpscaleRequest request, CancellationToken ct = default)
        {
            UpscaleCalls++;
            UpscaleRequests.Add(request);
            if (CancellableUpscaleResult is { } cancellable) return cancellable(ct);
            return UpscaleResult is { } result
                ? result(request)
                : Task.FromResult(new Synapic.Shared.Contracts.UpscaleResponse());
        }
        public Task<Synapic.Shared.Contracts.ModelInfo[]> ListModelsAsync(CancellationToken ct = default)
        {
            ListModelsCalls++;
            return Task.FromResult(Models);
        }
        public Task DownloadModelAsync(string modelId, CancellationToken ct = default)
        {
            DownloadCalls++;
            return Task.CompletedTask;
        }
        public Task<Synapic.Shared.Contracts.HealthResponse> GetHealthAsync(CancellationToken ct = default)
            => Task.FromResult(new Synapic.Shared.Contracts.HealthResponse());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [AvaloniaFact]
    public async Task Model_picker_lists_and_downloads_through_the_sidecar()
    {
        var sidecar = new UpscaleTestSidecar
        {
            Models = [new Synapic.Shared.Contracts.ModelInfo { Id = "org/example-vlm", Task = "image-text-to-text", Downloaded = true }],
        };
        var session = new Session();
        var vm = new Synapic.Main.ViewModels.Steps.Step2EngineViewModel(session, sidecar);
        var view = new Step2Engine { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 700 };
        window.Show();
        try
        {
            await vm.RefreshModelsCommand.ExecuteAsync(null);
            Assert.Equal(1, sidecar.ListModelsCalls);
            var model = Assert.Single(vm.LocalModels);
            Assert.Equal("org/example-vlm", model.Id);
            Assert.Equal("1 local models found", vm.ModelsMessage);

            vm.SelectedModel = model;
            Assert.Equal("org/example-vlm", session.Engine.ModelId);
            vm.ManualModelId = "org/example-vlm";
            await vm.DownloadSelectedCommand.ExecuteAsync(null);
            Assert.Equal(1, sidecar.DownloadCalls);
            // The download command reloads the list after reporting the start,
            // so the completed refresh is the stable final status.
            Assert.Equal("1 local models found", vm.ModelsMessage);
            Assert.Equal(2, sidecar.ListModelsCalls); // download completion refreshes the picker
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Successful_upscale_creates_reported_output_and_preserves_the_source()
    {
        var folder = Directory.CreateTempSubdirectory("synapic-upscale-success-").FullName;
        try
        {
            var sample = FindSampleImage() ?? throw new InvalidOperationException("TestData/sample.jpg not found");
            var source = Path.Combine(folder, "one.jpg");
            var output = Path.Combine(folder, "one_upscaled.jpg");
            File.Copy(sample, source);
            var originalBytes = await File.ReadAllBytesAsync(source);
            var sidecar = new UpscaleTestSidecar
            {
                UpscaleResult = request =>
                {
                    Assert.Equal(source, request.ImagePath);
                    Assert.NotNull(request.Options);
                    Assert.Equal("fast", request.Options!.Workflow);
                    Assert.Equal(2, request.Options.Factor);
                    File.Copy(request.ImagePath, output);
                    return Task.FromResult(new Synapic.Shared.Contracts.UpscaleResponse
                    {
                        OutputPath = output,
                        OriginalWidth = 32,
                        OriginalHeight = 24,
                        Width = 64,
                        Height = 48,
                        Workflow = "fast",
                        Factor = 2,
                    });
                },
            };
            var vm = VmForFolder(folder, sidecar);
            vm.SelectedWorkflow = 2; // fast: no model inference required by this desktop test
            var window = new Window { Content = new RunStateBar { DataContext = vm }, Width = 1000, Height = 700 };
            window.Show();
            try
            {
                await vm.StartCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(1, sidecar.UpscaleCalls);
                Assert.Single(sidecar.UpscaleRequests);
                Assert.True(File.Exists(output));
                Assert.Equal(originalBytes, await File.ReadAllBytesAsync(source));
                Assert.Equal("Done. Processed 1, succeeded 1, failed 0", vm.ProgressText);
                Assert.Contains(vm.LogLines, line => line.Contains("Batch finished: 1 ok, 0 failed, 1 total", StringComparison.Ordinal));
                Assert.DoesNotContain(vm.LogLines, line => line.Contains("No upscale outputs were created", StringComparison.Ordinal));
            }
            finally
            {
                window.Close();
            }
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
            Assert.Equal("No images were found in this source — choose a folder or catalog scope with images, then run upscale again.",
                vm.ProgressText);

            // The original app's log lines: run start + the Parameters echo
            // (written on the UI thread, so they are on disk by now).
            Assert.Contains(vm.LogLines, l => l.Contains("Upscale run started.", StringComparison.Ordinal));
            var parameters = vm.LogLines.Single(l => l.Contains("Parameters:", StringComparison.Ordinal));
            Assert.Equal(
                "Parameters: workflow=balanced, factor=4x, precision=auto, max_dimension=2048, " +
                "output=keep, quality=88, denoise=0.50, sharpen=0.25, overwrite=false",
                parameters[(parameters.IndexOf(']') + 2)..]);      // strip "[HH:mm:ss] " — version
                // control ships on: an un-touched Overwrite box stays off, so the
                // run writes _v2 versions instead of replacing previous outputs.
            Assert.Contains(vm.LogLines, l => l.Contains("No images were found in this source — choose a folder or catalog scope with images, then run upscale again.", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

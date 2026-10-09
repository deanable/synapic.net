using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Main.Services;
using Synapic.Main.Services.Processing;
using Synapic.Main.ViewModels.Operations;
using Synapic.Shared.Contracts;

namespace Synapic.Main.ViewModels.Steps;

/// <summary>
/// Upscale wizard step (port of step_upscale.py) — the Daminion "Feature
/// enhancement" utility. Pick the workflow (quality/balanced/fast), factor,
/// precision and output settings, then run the batch over the Step 1 source
/// (catalog scope or local folder). The run is the shared
/// <see cref="WorkflowRunner"/> kernel; the per-item operation is
/// <see cref="UpscaleItemHandler"/> (checkout → download → upscale → check-in
/// for catalog items) and the algorithm itself lives in the sidecar's
/// POST /upscale — the original app's Swin2SR/Lanczos upscaler. The run surface
/// (progress/ETA/log/stop) is the shared <see cref="RunStateViewModel"/>.
/// </summary>
public partial class StepUpscaleViewModel : RunStateViewModel
{
    private readonly Step1DatasourceViewModel? _step1;
    private readonly IInferenceSidecar? _sidecar;

    /// <param name="step1">Provides the source (connection + scope or folder), like tagging and dedup.</param>
    /// <param name="sidecar">Runs the algorithm via POST /upscale.</param>
    public StepUpscaleViewModel(Step1DatasourceViewModel? step1 = null, IInferenceSidecar? sidecar = null)
    {
        _step1 = step1;
        _sidecar = sidecar;

        // The source can be re-picked (or re-connected) while this step shows:
        // re-evaluate Start and the source line on any Step 1 change.
        if (_step1 is not null)
            _step1.PropertyChanged += (_, _) =>
            {
                StartCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(SourceSummary));
                OnPropertyChanged(nameof(SourceReady));
            };
    }

    // ── Settings (the parameters that differ from the other workflows) ─────

    /// <summary>Workflow labels in dropdown order — index = the wire value.</summary>
    public string[] Workflows { get; } = { "quality", "balanced", "fast" };

    public string[] Factors { get; } = { "2x", "4x" };

    public string[] Precisions { get; } = { "auto", "fp16", "fp32" };

    public string[] OutputFormats { get; } = { "keep", "JPEG", "PNG", "WEBP" };

    private const int BalancedIndex = 1;
    private const int FastIndex = 2;

    [ObservableProperty]
    private int _selectedWorkflow;

    [ObservableProperty]
    private int _selectedFactor;

    [ObservableProperty]
    private int _selectedPrecision;

    [ObservableProperty]
    private int _selectedOutputFormat;

    [ObservableProperty]
    private int _jpegQuality = 95;

    [ObservableProperty]
    private double _denoiseStrength = 1.0;

    [ObservableProperty]
    private double _sharpenAmount;

    [ObservableProperty]
    private bool _overwriteExisting = true;

    /// <summary>Denoise blending only means anything for the balanced workflow.</summary>
    public bool IsDenoiseEnabled => SelectedWorkflow == BalancedIndex;

    /// <summary>Precision is irrelevant for the fast (Lanczos) workflow.</summary>
    public bool IsPrecisionEnabled => SelectedWorkflow != FastIndex;

    partial void OnSelectedWorkflowChanged(int value)
    {
        OnPropertyChanged(nameof(IsDenoiseEnabled));
        OnPropertyChanged(nameof(IsPrecisionEnabled));
        OnPropertyChanged(nameof(SettingsSummary));
    }

    partial void OnSelectedFactorChanged(int value) => OnPropertyChanged(nameof(SettingsSummary));

    partial void OnSelectedPrecisionChanged(int value) => OnPropertyChanged(nameof(SettingsSummary));

    partial void OnSelectedOutputFormatChanged(int value) => OnPropertyChanged(nameof(SettingsSummary));

    partial void OnJpegQualityChanged(int value) => OnPropertyChanged(nameof(SettingsSummary));

    partial void OnDenoiseStrengthChanged(double value) => OnPropertyChanged(nameof(SettingsSummary));

    partial void OnSharpenAmountChanged(double value) => OnPropertyChanged(nameof(SettingsSummary));

    partial void OnOverwriteExistingChanged(bool value) => OnPropertyChanged(nameof(SettingsSummary));

    /// <summary>
    /// Read-back of the parameters for the run page: they live in the Parameters
    /// region now, and a batch started from parameters nobody can see is a batch
    /// nobody can explain afterwards.
    /// </summary>
    public string SettingsSummary
    {
        get
        {
            var workflow = Workflows[Math.Clamp(SelectedWorkflow, 0, Workflows.Length - 1)];
            var factor = Factors[Math.Clamp(SelectedFactor, 0, Factors.Length - 1)];
            var precision = Precisions[Math.Clamp(SelectedPrecision, 0, Precisions.Length - 1)];
            var format = OutputFormats[Math.Clamp(SelectedOutputFormat, 0, OutputFormats.Length - 1)];

            var denoise = IsDenoiseEnabled ? $" · denoise {DenoiseStrength:0.00}" : "";
            var sharpen = SharpenAmount > 0 ? $" · sharpen {SharpenAmount:0.00}" : "";
            var quality = format is "JPEG" or "WEBP" ? $" · quality {JpegQuality:0}" : "";
            var overwrite = OverwriteExisting ? " · overwrite output" : " · keep existing output";

            return $"{workflow} · {factor} · precision {precision} · output {format}"
                + quality + denoise + sharpen + overwrite;
        }
    }

    /// <summary>
    /// The run's parameters as the sidecar contract wants them (port of
    /// step_upscale._build_upscale_options, clamps included). Public for tests.
    /// </summary>
    public UpscaleOptions BuildOptions() => new()
    {
        Workflow = Workflows[Math.Clamp(SelectedWorkflow, 0, Workflows.Length - 1)],
        Factor = (Math.Clamp(SelectedFactor, 0, Factors.Length - 1) + 1) * 2,
        Precision = Precisions[Math.Clamp(SelectedPrecision, 0, Precisions.Length - 1)],
        DenoiseStrength = Math.Clamp(DenoiseStrength, 0.0, 1.0),
        SharpenAmount = Math.Clamp(SharpenAmount, 0.0, 2.0),
        MaxDimension = 2048, // cap input to 2048px before Swin2SR — model works on 64px patches
        OutputFormat = OutputFormats[Math.Clamp(SelectedOutputFormat, 0, OutputFormats.Length - 1)],
        JpegQuality = Math.Clamp(JpegQuality, 70, 100),
        OverwriteExisting = OverwriteExisting,
    };

    // ── Source (read back from Step 1 — same as the tagging route) ──────────

    private DatasourceSelection BuildSelection()
        => _step1?.ToSelectionForProcessing(_step1.ConnectedClient) ?? new DatasourceSelection();

    /// <summary>True when the chosen source can actually be run.</summary>
    public bool SourceReady
    {
        get
        {
            if (_step1 is null) return false;
            var ds = BuildSelection();
            return ds.IsDaminion
                ? _step1.ConnectedClient is not null
                : !string.IsNullOrWhiteSpace(ds.LocalPath) && Directory.Exists(ds.LocalPath);
        }
    }

    /// <summary>Read-only mirror of the source picked in Step 1.</summary>
    public string SourceSummary
    {
        get
        {
            if (_step1 is null) return "No datasource step available";
            var ds = BuildSelection();
            return ds.IsDaminion
                ? $"Source: {_step1.ScopeDescription} — configured in Step 1"
                : $"Source: local folder {(string.IsNullOrWhiteSpace(ds.LocalPath) ? "(not chosen)" : ds.LocalPath)}";
        }
    }

    // ── Run state (shared with every operation) ─────────────────────────────

    protected override void NotifyRunCommandsCanExecuteChanged()
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    protected override bool CanStart() => !IsRunning && _sidecar is not null && SourceReady;

    protected override async Task StartCoreAsync(CancellationToken ct)
    {
        var ds = BuildSelection();
        var options = BuildOptions();
        BeginRun();
        ProgressPercent = 0;
        EtaText = "";
        CurrentFile = "";
        ProgressText = "Starting…";

        // The parameters, echoed like the original app's "Parameters:" line —
        // they are also the batch description in the file log. Dots are
        // locale-independent, exactly like the Python f-strings we port.
        var denoise = FormattableString.Invariant($"{options.DenoiseStrength:0.00}");
        var sharpen = FormattableString.Invariant($"{options.SharpenAmount:0.00}");
        var description =
            $"workflow={options.Workflow}, factor={options.Factor}x, precision={options.Precision}, " +
            $"max_dimension={options.MaxDimension}, output={options.OutputFormat}, quality={options.JpegQuality}, " +
            $"denoise={denoise}, sharpen={sharpen}, " +
            $"overwrite={options.OverwriteExisting.ToString().ToLowerInvariant()}";
        AppendLog("Upscale run started.");
        AppendLog($"Parameters: {description}");
        SynapicLog.Info(nameof(StepUpscaleViewModel),
            $"Upscale starting — source={(ds.IsDaminion ? $"Daminion catalog, {_step1?.ScopeDescription}" : $"local folder '{ds.LocalPath}'")}, {description}");

        if (_sidecar is null)
        {
            AppendLog("No inference sidecar available — run cancelled.");
            EndRun();
            return;
        }
        if (!_sidecar.IsRunning)
            AppendLog("Warning: the inference server is not running — start it from the toolbar (Start Server).");

        var workflow = new WorkflowDefinition(
            Key: "upscale",
            Title: "Upscaling",
            // One inference at a time, exactly like the original app's single
            // worker thread (and one CPU model in RAM).
            MaxDegreeOfParallelism: 1,
            Description: description);

        var progress = new Progress<ProcessProgress>(ApplyProgress);

        var handler = new UpscaleItemHandler(ds.DaminionClient, _sidecar, options);
        try
        {
            var summary = await new WorkflowRunner().RunAsync(
                workflow, ds, handler, progress,
                line => { AppendLog(line); return Task.CompletedTask; },
                pause: null, Cts!.Token);

            var line = $"Done. Processed {summary.Processed}, succeeded {summary.Succeeded}, failed {summary.Failed}";
            ProgressText = line;
            AppendLog(line);
            SynapicLog.Info(nameof(StepUpscaleViewModel),
                $"Upscale run finished — processed={summary.Processed}, succeeded={summary.Succeeded}, failed={summary.Failed}");
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Stopped";
            AppendLog("Upscale stopped by user.");
            SynapicLog.Info(nameof(StepUpscaleViewModel), "Upscale run stopped by user");
        }
        catch (Exception e)
        {
            ProgressText = "Failed";
            AppendLog($"Upscale run failed: {e.Message}");
            SynapicLog.Error(nameof(StepUpscaleViewModel), $"Upscale run failed: {e}");
        }
        finally
        {
            EndRun();
        }
    }

    private bool CanStop() => IsRunning;

    /// <summary>Stop the run (port of step_upscale.stop_upscale): in-flight
    /// requests are cancelled and no further items start.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        CancelRun();
        AppendLog("Stopping…");
    }
}

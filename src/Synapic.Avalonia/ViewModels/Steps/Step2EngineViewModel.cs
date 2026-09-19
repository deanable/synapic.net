using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;
using Synapic.Shared.Contracts;

namespace Synapic.Avalonia.ViewModels.Steps;

/// <summary>
/// Step 2: Engine (port of step2_tagging.py) — local model picker (HF cache
/// via /models/list), device selection, thresholds and probability mode.
/// Inference is exclusively the local LFM-based Python sidecar.
/// </summary>
public partial class Step2EngineViewModel : ViewModelBase
{
    private readonly Session _session;
    private readonly IInferenceSidecar _sidecar;

    [ObservableProperty]
    private string _manualModelId = "LiquidAI/LFM2.5-VL-450M";

    // Combo boxes bind indices (SelectedIndex ↔ string never selects on Avalonia).
    public string[] TaskOptions { get; } = { "Description (image-text-to-text)", "Keywords (image-classification)", "Categories (zero-shot)" };
    public string[] DeviceOptions { get; } = { "CPU", "CUDA", "MPS" };
    public string[] ProbabilityModeOptions { get; } = { "LLM only", "Probability only", "Both" };

    [ObservableProperty]
    private int _taskIndex;

    [ObservableProperty]
    private int _deviceIndex;

    [ObservableProperty]
    private int _probabilityModeIndex;

    public string Task => TaskIndexToString(TaskIndex);
    public string Device => DeviceIndexToString(DeviceIndex);
    public string ProbabilityMode => ProbabilityModeIndexToString(ProbabilityModeIndex);

    public static string TaskIndexToString(int i) => i switch
    {
        1 => "image-classification",
        2 => "zero-shot",
        _ => "image-text-to-text",
    };

    public static int TaskStringToIndex(string s) => s switch
    {
        "image-classification" => 1,
        "zero-shot" => 2,
        _ => 0,
    };

    public static string DeviceIndexToString(int i) => i switch
    {
        1 => "cuda",
        2 => "mps",
        _ => "cpu",
    };

    public static int DeviceStringToIndex(string s) => s?.ToLowerInvariant() switch
    {
        "cuda" => 1,
        "mps" => 2,
        _ => 0,
    };

    public static string ProbabilityModeIndexToString(int i) => i switch
    {
        0 => "llm",
        1 => "probability",
        _ => "both",
    };

    public static int ProbabilityModeStringToIndex(string s) => s switch
    {
        "llm" => 0,
        "probability" => 1,
        _ => 2,
    };

    public Step2EngineViewModel(Session session, IInferenceSidecar sidecar)
    {
        _session = session;
        _sidecar = sidecar;
        var engine = session.Engine;
        ManualModelId = engine.ModelId;
        TaskIndex = TaskStringToIndex(engine.Task);
        DeviceIndex = DeviceStringToIndex(engine.Device);
        ConfidenceThreshold = engine.ConfidenceThreshold;
        ProbabilityModeIndex = ProbabilityModeStringToIndex(engine.ProbabilityMode);
        ProbabilityThreshold = engine.ProbabilityThreshold;
        ProbabilityCandidates = string.Join(", ", engine.ProbabilityCandidates);
        SystemPrompt = engine.SystemPrompt;
        EmbeddingRescueEnabled = engine.EmbeddingRescueEnabled;
    }

    // ── Local engine (the only engine) ──────────────────────────────────────

    public ObservableCollection<ModelInfo> LocalModels { get; } = new();

    [ObservableProperty]
    private ModelInfo? _selectedModel;

    [ObservableProperty]
    private double _confidenceThreshold = 0.3;

    [ObservableProperty]
    private double _probabilityThreshold = 0.5;

    [ObservableProperty]
    private string _probabilityCandidates = "";

    [ObservableProperty]
    private string _systemPrompt = "";

    [ObservableProperty]
    private bool _embeddingRescueEnabled;

    [ObservableProperty]
    private bool _isLoadingModels;

    [ObservableProperty]
    private string? _modelsMessage;

    partial void OnSelectedModelChanged(ModelInfo? value)
    {
        if (value is not null)
        {
            ManualModelId = value.Id;
            if (!string.IsNullOrEmpty(value.Task)) TaskIndex = TaskStringToIndex(value.Task);
        }
        PushToSession();
    }

    partial void OnManualModelIdChanged(string value) => PushToSession();
    partial void OnTaskIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Task));
        PushToSession();
    }
    partial void OnDeviceIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Device));
        PushToSession();
    }
    partial void OnConfidenceThresholdChanged(double value) => PushToSession();
    partial void OnProbabilityModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ProbabilityMode));
        PushToSession();
    }
    partial void OnProbabilityThresholdChanged(double value) => PushToSession();
    partial void OnSystemPromptChanged(string value) => PushToSession();
    partial void OnEmbeddingRescueEnabledChanged(bool value) => PushToSession();

    partial void OnProbabilityCandidatesChanged(string value)
    {
        PushToSession();
    }

    private void PushToSession()
    {
        var engine = _session.Engine;
        engine.ModelId = ManualModelId;
        engine.Task = Task;
        engine.Device = Device;
        engine.ConfidenceThreshold = ConfidenceThreshold;
        engine.ProbabilityMode = ProbabilityMode;
        engine.ProbabilityThreshold = ProbabilityThreshold;
        engine.ProbabilityCandidates = ProbabilityCandidates
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        engine.SystemPrompt = SystemPrompt;
        engine.EmbeddingRescueEnabled = EmbeddingRescueEnabled;
    }

    [RelayCommand]
    private async Task RefreshModelsAsync(CancellationToken ct)
    {
        IsLoadingModels = true;
        ModelsMessage = null;
        try
        {
            var models = await _sidecar.ListModelsAsync(ct);
            LocalModels.Clear();
            foreach (var m in models.OrderByDescending(m => m.Downloaded).ThenBy(m => m.Id))
                LocalModels.Add(m);
            ModelsMessage = $"{LocalModels.Count} local models found";
        }
        catch (Exception e)
        {
            ModelsMessage = $"Could not list models: {e.Message} (is the server running?)";
        }
        finally
        {
            IsLoadingModels = false;
        }
    }

    [RelayCommand]
    private async Task DownloadSelectedAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ManualModelId)) return;
        IsLoadingModels = true;
        ModelsMessage = $"Downloading {ManualModelId}… (progress in the log)";
        try
        {
            await _sidecar.DownloadModelAsync(ManualModelId, ct);
            ModelsMessage = $"Download started for {ManualModelId}";
            await RefreshModelsAsync(ct);
        }
        catch (Exception e)
        {
            ModelsMessage = $"Download failed: {e.Message}";
        }
        finally
        {
            IsLoadingModels = false;
        }
    }

    public void MakeSelectionValid()
    {
        // Called on step transition: ensure the session reflects the UI state.
        PushToSession();
    }

    /// <summary>Populate the model list from the running server (invoked when entering Step 2).</summary>
    public async Task OnEnteredAsync()
    {
        if (LocalModels.Count == 0 && !IsLoadingModels)
            await RefreshModelsAsync(CancellationToken.None);
    }
}

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
    private readonly EngineSettingsStore? _engineStore;

    [ObservableProperty]
    private string _manualModelId = "LiquidAI/LFM2.5-VL-450M";

    // The sidecar is LFM-only. One multimodal call returns category, keywords
    // and description together, so there is no per-field task choice: the old
    // TaskOptions entries (image-classification / zero-shot) were unrelated
    // transformer pipeline tasks that a VLM cannot actually run.
    public string[] DeviceOptions { get; } = { "CPU", "CUDA", "MPS" };
    public string[] ProbabilityModeOptions { get; } = { "LLM only", "Probability only", "Both" };

    /// <summary>
    /// Fixed pipeline task. LFM2.5-VL is image-text-to-text and its prompt
    /// always yields category, keywords and description in one JSON payload.
    /// </summary>
    public const string MultimodalTask = "image-text-to-text";

    [ObservableProperty]
    private int _deviceIndex;

    [ObservableProperty]
    private int _probabilityModeIndex;

    public string Device => DeviceIndexToString(DeviceIndex);
    public string ProbabilityMode => ProbabilityModeIndexToString(ProbabilityModeIndex);

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

    public Step2EngineViewModel(Session session, IInferenceSidecar sidecar, EngineSettingsStore? engineStore = null)
    {
        _session = session;
        _sidecar = sidecar;
        _engineStore = engineStore;
        HydrateFromStore();
        var engine = session.Engine;
        ManualModelId = engine.ModelId;
        // Correct any stale per-field task persisted by an older session.
        engine.Task = MultimodalTask;
        DeviceIndex = DeviceStringToIndex(engine.Device);
        ConfidenceThreshold = engine.ConfidenceThreshold;
        ProbabilityModeIndex = ProbabilityModeStringToIndex(engine.ProbabilityMode);
        ProbabilityThreshold = engine.ProbabilityThreshold;
        ProbabilityCandidates = string.Join(", ", engine.ProbabilityCandidates);
        SystemPrompt = engine.SystemPrompt;
        UserPrompt = engine.UserPrompt;
        EmbeddingRescueEnabled = engine.EmbeddingRescueEnabled;
        TagKeywords = engine.TagKeywords;
        TagCategories = engine.TagCategories;
        TagDescription = engine.TagDescription;
    }

    /// <summary>Pre-fill the Step 2 form from the registry (last run's engine settings).</summary>
    private void HydrateFromStore()
    {
        if (_engineStore is null) return;
        try
        {
            var saved = _engineStore.Load();
            if (saved is null) return;

            // Model + task.
            ManualModelId = saved.ModelId;
            _session.Engine.ModelId = saved.ModelId;
            _session.Engine.Task = MultimodalTask;

            // Device + threshold sliders.
            DeviceIndex = DeviceStringToIndex(saved.Device);
            ConfidenceThreshold = (float)saved.ConfidenceThreshold;
            ProbabilityModeIndex = ProbabilityModeStringToIndex(saved.ProbabilityMode);
            ProbabilityThreshold = (float)saved.ProbabilityThreshold;

            // Probability candidates + system prompt + tag instruction.
            ProbabilityCandidates = string.Join(", ", saved.ProbabilityCandidates);
            SystemPrompt = saved.SystemPrompt;
            UserPrompt = saved.UserPrompt;
            EmbeddingRescueEnabled = saved.EmbeddingRescueEnabled;

            // Tag-field checkboxes.
            TagKeywords = saved.TagKeywords;
            TagCategories = saved.TagCategories;
            TagDescription = saved.TagDescription;

            SynapicLog.Info(nameof(Step2EngineViewModel),
                $"Pre-filled Step 2 engine settings from registry: model={saved.ModelId}, device={saved.Device}");
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(Step2EngineViewModel),
                $"Failed to pre-fill engine settings from registry: {e.Message}");
        }
    }

    // ── Tag field selection (original Step 2 checkboxes) ─────────────────────

    [ObservableProperty]
    private bool _tagKeywords = true;

    [ObservableProperty]
    private bool _tagCategories = true;

    [ObservableProperty]
    private bool _tagDescription = true;

    /// <summary>Drives the "select at least one" hint; at least one must stay checked to proceed.</summary>
    public bool HasNoTagFieldSelected => !(TagKeywords || TagCategories || TagDescription);

    /// <summary>Persist the current Step 2 settings to the registry (called on step exit).</summary>
    public void SaveToStore()
    {
        if (_engineStore is null) return;
        try
        {
            var engine = _session.Engine;
            _engineStore.Save(new EngineSettingsParams(
                engine.ModelId,
                engine.Task,
                engine.Device,
                engine.ConfidenceThreshold,
                engine.ProbabilityMode,
                engine.ProbabilityThreshold,
                engine.ProbabilityCandidates,
                engine.SystemPrompt,
                engine.UserPrompt,
                engine.EmbeddingRescueEnabled,
                engine.TagKeywords,
                engine.TagCategories,
                engine.TagDescription));
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(Step2EngineViewModel),
                $"Failed to persist engine settings: {e.Message}");
        }
    }

    partial void OnTagKeywordsChanged(bool value)
    {
        PushToSession();
        OnPropertyChanged(nameof(HasNoTagFieldSelected));
    }

    partial void OnTagCategoriesChanged(bool value)
    {
        PushToSession();
        OnPropertyChanged(nameof(HasNoTagFieldSelected));
    }

    partial void OnTagDescriptionChanged(bool value)
    {
        PushToSession();
        OnPropertyChanged(nameof(HasNoTagFieldSelected));
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

    /// <summary>
    /// The tag instruction sent as /tag's <c>user_prompt</c>. Blank means "use
    /// the sidecar's built-in instruction", so clearing the box is the way back
    /// to a known-good prompt - the built-in one is what reliably yields JSON.
    /// </summary>
    [ObservableProperty]
    private string _userPrompt = "";

    /// <summary>Cached copy of the sidecar's instruction, fetched on demand.</summary>
    [ObservableProperty]
    private string _builtInUserPrompt = "";

    /// <summary>Feedback for the prompt buttons ("custom instruction in use", errors).</summary>
    [ObservableProperty]
    private string? _promptMessage;

    /// <summary>Drives the "you have replaced the built-in instruction" hint.</summary>
    public bool HasCustomUserPrompt => !string.IsNullOrWhiteSpace(UserPrompt);

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
        }
        PushToSession();
    }

    partial void OnManualModelIdChanged(string value) => PushToSession();
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
    partial void OnUserPromptChanged(string value)
    {
        PushToSession();
        OnPropertyChanged(nameof(HasCustomUserPrompt));
    }


    partial void OnEmbeddingRescueEnabledChanged(bool value) => PushToSession();

    partial void OnProbabilityCandidatesChanged(string value)
    {
        PushToSession();
    }

    private void PushToSession()
    {
        var engine = _session.Engine;
        engine.ModelId = ManualModelId;
        engine.Task = MultimodalTask;
        engine.Device = Device;
        engine.ConfidenceThreshold = ConfidenceThreshold;
        engine.ProbabilityMode = ProbabilityMode;
        engine.ProbabilityThreshold = ProbabilityThreshold;
        engine.ProbabilityCandidates = ProbabilityCandidates
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        engine.SystemPrompt = SystemPrompt;
        engine.UserPrompt = UserPrompt;
        engine.EmbeddingRescueEnabled = EmbeddingRescueEnabled;
        engine.TagKeywords = TagKeywords;
        engine.TagCategories = TagCategories;
        engine.TagDescription = TagDescription;
    }

    /// <summary>
    /// Put the sidecar's built-in instruction into the editable box, so it can be
    /// tweaked from the shipped wording instead of retyped. Fetches it on first
    /// use; the sidecar owns the text, so the host never keeps its own copy.
    /// </summary>
    [RelayCommand]
    private async Task UseBuiltInPromptAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(BuiltInUserPrompt))
        {
            try
            {
                var defaults = await _sidecar.GetPromptDefaultsAsync(ct);
                BuiltInUserPrompt = defaults.DefaultUserPrompt;
            }
            catch (Exception e)
            {
                PromptMessage = $"Could not read the built-in instruction: {e.Message} (is the server running?)";
                return;
            }

            if (string.IsNullOrWhiteSpace(BuiltInUserPrompt))
            {
                // An empty reply means the sidecar did not answer as expected;
                // filling the box with nothing would look like a reset.
                PromptMessage = "The server returned no built-in instruction (is the server running?).";
                return;
            }
        }

        UserPrompt = BuiltInUserPrompt;
        PromptMessage = "Loaded the built-in instruction - edit it as needed.";
    }

    /// <summary>Clear the box, i.e. go back to the sidecar's built-in instruction.</summary>
    [RelayCommand]
    private void ResetUserPrompt()
    {
        UserPrompt = "";
        PromptMessage = "Using the built-in instruction.";
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

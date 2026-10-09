using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Main.Models;
using Synapic.Main.Services;
using Synapic.Main.Services.Processing;
using Synapic.Shared.Contracts;

namespace Synapic.Main.ViewModels.Steps;

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
    private readonly SystemPromptPresetStore? _presetStore;
    private readonly Func<ComputeAvailability> _availability;

    [ObservableProperty]
    private string _manualModelId = "LiquidAI/LFM2.5-VL-450M";

    // The sidecar is LFM-only. One multimodal call returns category, keywords
    // and description together, so there is no per-field task choice: the old
    // TaskOptions entries (image-classification / zero-shot) were unrelated
    // transformer pipeline tasks that a VLM cannot actually run.

    /// <summary>
    /// The Device combo's entries: only the devices this machine can run (see
    /// <see cref="ComputeAvailability"/>). CUDA on a machine with no NVIDIA driver
    /// and MPS away from macOS are not offered at all — a choice that silently
    /// falls back to the CPU is worse than no choice.
    /// </summary>
    public ObservableCollection<DeviceOption> DeviceOptions { get; } = new();

    public string[] ProbabilityModeOptions { get; } = { "LLM only", "Probability only", "Both" };

    /// <summary>
    /// Fixed pipeline task. LFM2.5-VL is image-text-to-text and its prompt
    /// always yields category, keywords and description in one JSON payload.
    /// </summary>
    public const string MultimodalTask = "image-text-to-text";

    /// <summary>
    /// The device the Device combo shows. Hand-written rather than generated:
    /// the setter has to refuse the <c>null</c> a two-way <c>SelectedItem</c>
    /// binding writes back (see <see cref="HandleSelectedDeviceChanged"/>), and
    /// <c>[ObservableProperty]</c>'s generated setter has no hook that can
    /// rewrite the incoming value.
    /// </summary>
    private DeviceOption _selectedDevice = new("CPU", "cpu");

    public DeviceOption SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            // A null from the view means "the combo let go of its selection",
            // not "the user picked nothing": keep what is already selected.
            var next = value ?? _selectedDevice;
            if (EqualityComparer<DeviceOption>.Default.Equals(_selectedDevice, next)) return;

            OnPropertyChanging(new PropertyChangingEventArgs(nameof(SelectedDevice)));
            _selectedDevice = next;
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(SelectedDevice)));
            HandleSelectedDeviceChanged(next);
        }
    }

    [ObservableProperty]
    private int _probabilityModeIndex;

    /// <summary>
    /// Why a device is missing from the list, and what a run will use instead.
    /// Blank when every device the app knows about is offered.
    /// </summary>
    [ObservableProperty]
    private string _deviceAvailabilityNote = "";

    public bool HasDeviceAvailabilityNote => DeviceAvailabilityNote.Length > 0;

    partial void OnDeviceAvailabilityNoteChanged(string value) =>
        OnPropertyChanged(nameof(HasDeviceAvailabilityNote));

    /// <summary>
    /// The device the session, the launcher and the inference server speak.
    /// Never throws: the <see cref="SelectedDevice"/> setter refuses the null a
    /// two-way binding can write back, and the session is where the last pushed
    /// value already lives, so that is the honest fallback rather than an
    /// invented one.
    /// </summary>
    public string Device => SelectedDevice?.Value ?? _session.Engine.Device;

    public string ProbabilityMode => ProbabilityModeIndexToString(ProbabilityModeIndex);

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

    /// <param name="availabilityProbe">
    /// Overridable so tests (and future re-probing) can state what the machine
    /// can run instead of depending on the GPU under the test runner.
    /// </param>
    public Step2EngineViewModel(Session session, IInferenceSidecar sidecar,
        EngineSettingsStore? engineStore = null, SystemPromptPresetStore? presetStore = null,
        Func<ComputeAvailability>? availabilityProbe = null)
    {
        _session = session;
        _sidecar = sidecar;
        _engineStore = engineStore;
        _presetStore = presetStore;
        _availability = availabilityProbe ?? ComputeAvailability.Detect;
        // The list must exist before anything hydrates into it.
        RefreshDeviceOptions();
        HydrateFromStore();
        var engine = session.Engine;
        ManualModelId = engine.ModelId;
        // Correct any stale per-field task persisted by an older session.
        engine.Task = MultimodalTask;
        TrySelectDevice(engine.Device);
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
        LoadSystemPromptPresets();
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
            TrySelectDevice(saved.Device);
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

    /// <summary>
    /// True when a run would drop one of the three fields the model returns.
    /// The checkboxes are the only thing that decides this, and the write step
    /// honours it silently, so it is called out on screen: otherwise a partial
    /// selection is invisible until nothing but keywords turns up in Daminion.
    /// </summary>
    public bool HasPartialTagFieldSelection =>
        !HasNoTagFieldSelected && !(TagKeywords && TagCategories && TagDescription);

    /// <summary>
    /// The fields a run will write, in words ("keywords only"). The multimodal
    /// instruction always asks for all three, so this describes the write step
    /// and not what the model produced.
    /// </summary>
    public string TagFieldSummary =>
        new TagFieldSelection(TagCategories, TagKeywords, TagDescription).Summary;

    /// <summary>On-screen warning for a partial selection.</summary>
    public string TagFieldWarning =>
        $"Writing {TagFieldSummary} — the model still returns all three fields. "
        + "Tick the other boxes to tag those too.";

    /// <summary>Persist the current Step 2 settings to the registry (called on step exit).</summary>
    public void SaveToStore()
    {
        // Leaving Step 2 commits the prompt history too, so a prompt that was
        // typed and used is never only in memory.
        RememberSystemPrompt();
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
        NotifyTagFieldSelectionChanged();
    }

    partial void OnTagCategoriesChanged(bool value)
    {
        PushToSession();
        NotifyTagFieldSelectionChanged();
    }

    partial void OnTagDescriptionChanged(bool value)
    {
        PushToSession();
        NotifyTagFieldSelectionChanged();
    }

    /// <summary>Refresh every hint that describes the current field selection.</summary>
    private void NotifyTagFieldSelectionChanged()
    {
        OnPropertyChanged(nameof(HasNoTagFieldSelected));
        OnPropertyChanged(nameof(HasPartialTagFieldSelection));
        OnPropertyChanged(nameof(TagFieldSummary));
        OnPropertyChanged(nameof(TagFieldWarning));
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
        OnPropertyChanged(nameof(ModelSummary));
    }

    partial void OnManualModelIdChanged(string value)
    {
        PushToSession();
        OnPropertyChanged(nameof(ModelSummary));
    }

    /// <summary>
    /// The model a run would load, in words (the settings summary on step 3
    /// reports it). Reads the picked model first, then a hand-typed id, so it
    /// says the same thing the sidecar will be asked for.
    /// </summary>
    public string ModelSummary
    {
        get
        {
            if (SelectedModel is { } picked) return picked.Id;
            return string.IsNullOrWhiteSpace(ManualModelId)
                ? "Not chosen yet — pick a model on step 1 or here."
                : ManualModelId;
        }
    }

    /// <summary>How a run scores tags, in words (the settings summary reports it).</summary>
    public string ProbabilitySummary => ProbabilityMode switch
    {
        "Probability only" => "Probability only" + ThresholdSuffix(),
        "Both" => "LLM + probability" + ThresholdSuffix(),
        _ => "LLM only (the model's own answer)",
    };

    private string ThresholdSuffix() =>
        $" (candidates at {ProbabilityThreshold:0.00}"
        + (string.IsNullOrWhiteSpace(ProbabilityCandidates) ? ")" : $": {ProbabilityCandidates})");

    /// <summary>Which tag instruction a run sends (the settings summary reports it).</summary>
    public string PromptSummary =>
        HasCustomUserPrompt ? "Custom tag instruction" : "Built-in tag instruction";
    /// <summary>
    /// Everything that follows a device change: the <c>Device</c> property, the
    /// session, and the running server. Never called with a null — the setter
    /// keeps <see cref="SelectedDevice"/> non-null.
    /// </summary>
    private void HandleSelectedDeviceChanged(DeviceOption value)
    {
        OnPropertyChanged(nameof(Device));
        PushToSession();
        _ = PushDeviceToServerAsync();
    }

    /// <summary>
    /// Rebuild the offered devices from the machine's capabilities and keep the
    /// current choice when it is still on the list. Called on entry to the step as
    /// well as at construction, so a driver installed (or a GPU plugged in) while
    /// the app was open is picked up without a restart.
    /// </summary>
    public void RefreshDeviceOptions()
    {
        var availability = _availability();
        var wanted = SelectedDevice.Value;

        DeviceOptions.Clear();
        DeviceOptions.Add(new DeviceOption("CPU", "cpu"));
        if (availability.Cuda) DeviceOptions.Add(new DeviceOption("CUDA", "cuda"));
        if (availability.Mps) DeviceOptions.Add(new DeviceOption("MPS", "mps"));

        var kept = DeviceOptions.FirstOrDefault(o => o.Value == wanted);
        if (kept is null && wanted != "cpu")
        {
            // The device was selectable a moment ago and is not any more (a
            // driver removed, a GPU unplugged, a settings file from a machine
            // that had one) - say what happened to it.
            DeviceAvailabilityNote = DeviceUnavailableNote(wanted);
        }
        else if (!availability.Cuda)
        {
            DeviceAvailabilityNote = "CUDA is not offered here: no NVIDIA CUDA driver was detected.";
        }
        else
        {
            DeviceAvailabilityNote = "";
        }

        SelectedDevice = kept ?? DeviceOptions[0];
    }

    /// <summary>
    /// Select a device by value ("cpu"/"cuda"/"mps"). False when this machine
    /// does not offer it — the caller keeps the fallback instead of a device that
    /// would silently run on the CPU, and the reason is put on screen.
    /// </summary>
    public bool TrySelectDevice(string? value)
    {
        var wanted = (value ?? "").Trim().ToLowerInvariant();
        var match = DeviceOptions.FirstOrDefault(o => o.Value == wanted);
        if (match is null)
        {
            if (wanted is "cuda" or "mps") DeviceAvailabilityNote = DeviceUnavailableNote(wanted);
            return false;
        }
        SelectedDevice = match;
        return true;
    }

    /// <summary>What to say when a device that was asked for cannot be used.</summary>
    private static string DeviceUnavailableNote(string value) => value switch
    {
        "cuda" => "CUDA was selected, but this machine has no NVIDIA CUDA driver \u2014 using the CPU.",
        "mps" => "MPS was selected, but it is not available on this machine \u2014 using the CPU.",
        _ => "",
    };

    /// <summary>
    /// Tell a running server which device this run expects. The device is part
    /// of the server's session configuration and its tag pipeline is built from
    /// it, so a server launched before this selection would otherwise keep
    /// serving on whatever it started with - the reason choosing CUDA could
    /// leave inference on the CPU. Silent when nothing is running (a server that
    /// starts later gets the device from its launch environment) and never
    /// fatal: /health still reports the device the run actually got.
    /// </summary>
    private async Task PushDeviceToServerAsync()
    {
        try
        {
            await _sidecar.SetDeviceAsync(Device);
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(Step2EngineViewModel),
                $"Could not tell the server to use {Device}: {e.Message}");
        }
    }
    partial void OnConfidenceThresholdChanged(double value) => PushToSession();
    partial void OnProbabilityModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ProbabilityMode));
        OnPropertyChanged(nameof(ProbabilitySummary));
        PushToSession();
    }
    partial void OnProbabilityThresholdChanged(double value)
    {
        PushToSession();
        OnPropertyChanged(nameof(ProbabilitySummary));
    }
    partial void OnSystemPromptChanged(string value)
    {
        PushToSession();
        // Keep the preset combobox honest: it highlights the saved prompt that
        // matches the box, or nothing once the text has been edited away from
        // one. Delete acts on the highlight, so it must never point at a preset
        // the box is no longer showing.
        var text = (value ?? "").Trim();
        if (!string.Equals(SelectedSystemPromptPreset, text, StringComparison.Ordinal))
            SelectedSystemPromptPreset =
                SystemPromptPresets.Contains(text, StringComparer.Ordinal) ? text : null;
    }

    partial void OnSelectedSystemPromptPresetChanged(string? value)
    {
        // Picking a preset from the list fills the box. Setting the text here
        // (rather than relying on the control) keeps the combobox selection and
        // the model in step, which is what the Delete key acts on.
        if (value is not null && !string.Equals(SystemPrompt, value, StringComparison.Ordinal))
            SystemPrompt = value;
    }
    partial void OnUserPromptChanged(string value)
    {
        PushToSession();
        OnPropertyChanged(nameof(HasCustomUserPrompt));
        OnPropertyChanged(nameof(PromptSummary));
    }


    partial void OnEmbeddingRescueEnabledChanged(bool value) => PushToSession();

    partial void OnProbabilityCandidatesChanged(string value)
    {
        PushToSession();
        OnPropertyChanged(nameof(ProbabilitySummary));
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

    // ── System-prompt presets (the combobox history) ─────────────────────────

    /// <summary>
    /// Every system prompt the user has committed, most recently used first.
    /// Backed by <see cref="SystemPromptPresetStore"/> (system-prompts.json), so
    /// the wording survives restarts without the user keeping notes.
    /// </summary>
    public ObservableCollection<string> SystemPromptPresets { get; } = new();

    /// <summary>The preset highlighted in the combobox; Delete removes it.</summary>
    [ObservableProperty]
    private string? _selectedSystemPromptPreset;

    /// <summary>Feedback under the combobox (saved, removed, nothing selected).</summary>
    [ObservableProperty]
    private string? _systemPromptMessage;

    /// <summary>Load the saved history into the combobox (startup).</summary>
    private void LoadSystemPromptPresets()
    {
        if (_presetStore is null) return;
        try
        {
            ApplyPresets(_presetStore.Load());
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(Step2EngineViewModel),
                $"Failed to read system-prompt presets: {e.Message}");
        }
    }

    private void ApplyPresets(IReadOnlyList<string> presets)
    {
        SystemPromptPresets.Clear();
        foreach (var preset in presets) SystemPromptPresets.Add(preset);
    }

    /// <summary>
    /// Remember the system prompt currently in the box. Called when the box is
    /// committed (Enter or leaving the field) and when the user leaves Step 2, so
    /// a prompt is always in the history by the time it has been used for a run.
    /// Blank input is not a preset; a prompt that is already saved is only
    /// re-selected, never duplicated.
    /// </summary>
    public void RememberSystemPrompt()
    {
        var value = (SystemPrompt ?? "").Trim();
        if (value.Length == 0) return;

        if (SystemPromptPresets.Contains(value, StringComparer.Ordinal))
        {
            SelectedSystemPromptPreset = value;
            return;
        }

        try
        {
            if (_presetStore is not null)
            {
                ApplyPresets(_presetStore.Add(value));
            }
            else
            {
                // No store (headless tests): keep the list working in memory.
                SystemPromptPresets.Insert(0, value);
            }
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(Step2EngineViewModel),
                $"Failed to save the system-prompt preset: {e.Message}");
            return;
        }

        SelectedSystemPromptPreset = value;
        SystemPromptMessage = "Saved to the system-prompt presets.";
    }

    /// <summary>
    /// Remove a preset from the history (the Delete key in the combobox).
    ///
    /// Removing the prompt that is currently in the box clears the box as well:
    /// leaving it there would only see it re-saved on the next commit, which makes
    /// Delete look broken. Removing something else never touches the box.
    /// </summary>
    [RelayCommand]
    private void DeleteSystemPromptPreset(string? preset)
    {
        var value = (preset ?? SelectedSystemPromptPreset ?? SystemPrompt ?? "").Trim();
        if (value.Length == 0)
        {
            SystemPromptMessage = "Select a saved system prompt first, then press Delete to remove it.";
            return;
        }

        if (!SystemPromptPresets.Contains(value, StringComparer.Ordinal))
        {
            SystemPromptMessage = $"\"{value}\" is not one of the saved system prompts.";
            return;
        }

        try
        {
            if (_presetStore is not null) ApplyPresets(_presetStore.Remove(value));
            else SystemPromptPresets.Remove(value);
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(Step2EngineViewModel),
                $"Failed to remove the system-prompt preset: {e.Message}");
            SystemPromptPresets.Remove(value);
        }

        if (string.Equals((SystemPrompt ?? "").Trim(), value, StringComparison.Ordinal))
            SystemPrompt = "";
        if (string.Equals(SelectedSystemPromptPreset, value, StringComparison.Ordinal))
            SelectedSystemPromptPreset = null;

        SystemPromptMessage = $"Removed the system-prompt preset \"{value}\".";
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
        RememberSystemPrompt();
    }

    /// <summary>Populate the model list from the running server (invoked when entering Step 2).</summary>
    public async Task OnEnteredAsync()
    {
        // The machine may have gained (or lost) a GPU driver since the last
        // look, so the offered devices are re-probed before the run starts.
        RefreshDeviceOptions();
        // A device chosen while another server was running (or before this one
        // existed) is re-stated here, so entering the step always leaves the
        // server and the combo box agreeing.
        await PushDeviceToServerAsync();
        await EnsureModelsLoadedAsync();
    }

    /// <summary>
    /// Load the model list once, if it has not been loaded and nothing else is
    /// loading it. The model picker is not only on step 2 any more — step 1 and
    /// the start screen show it too — so the shell calls this when the server
    /// becomes ready and the list is useful wherever the user happens to be.
    /// </summary>
    public async Task EnsureModelsLoadedAsync()
    {
        if (LocalModels.Count > 0 || IsLoadingModels) return;
        await RefreshModelsAsync(CancellationToken.None);
    }

}

/// <summary>
/// One entry in Step 2's Device combo. <see cref="Value"/> is the string the
/// session, the launcher and the inference server all speak
/// ("cpu"/"cuda"/"mps"); <see cref="ToString"/> is the label the combo shows, so
/// the list needs no item template.
/// </summary>
public sealed record DeviceOption(string Name, string Value)
{
    public override string ToString() => Name;
}

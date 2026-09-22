using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.ViewModels.Steps;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// Registry persistence of Step 2 engine settings (model id, device, thresholds,
/// prompt, tag-field checkboxes). Tests run against an isolated HKCU key path so
/// they never touch the real Software\Synapic\Engine key, and clear up after
/// themselves. Skipped on non-Windows (CI runs the suite on ubuntu-22.04; the
/// store is a no-op there by design).
/// </summary>
public class EngineSettingsStoreTests : IDisposable
{
    private readonly string _keyPath =
        $@"Software\Synapic\Tests_Engine_{Guid.NewGuid():N}";

    private readonly EngineSettingsStore _store;

    public EngineSettingsStoreTests() => _store = new EngineSettingsStore(_keyPath);

    public void Dispose() => _store.Clear();

    [Fact]
    public void Save_then_Load_round_trips_all_engine_fields()
    {
        if (!OperatingSystem.IsWindows()) return;

        _store.Save(new EngineSettingsParams(
            ModelId: "LiquidAI/LFM2.5-VL-450M",
            Task: "image-text-to-text",
            Device: "cuda",
            ConfidenceThreshold: 0.42,
            ProbabilityMode: "probability",
            ProbabilityThreshold: 0.66,
            ProbabilityCandidates: new[] { "cat", "dog", "bird" },
            SystemPrompt: "You are a tagging engine.",
            EmbeddingRescueEnabled: true,
            TagKeywords: true,
            TagCategories: false,
            TagDescription: true));

        var loaded = _store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("LiquidAI/LFM2.5-VL-450M", loaded.ModelId);
        Assert.Equal("image-text-to-text", loaded.Task);
        Assert.Equal("cuda", loaded.Device);
        Assert.Equal(0.42, loaded.ConfidenceThreshold, 6);
        Assert.Equal("probability", loaded.ProbabilityMode);
        Assert.Equal(0.66, loaded.ProbabilityThreshold, 6);
        Assert.Equal(new[] { "cat", "dog", "bird" }, loaded.ProbabilityCandidates);
        Assert.Equal("You are a tagging engine.", loaded.SystemPrompt);
        Assert.True(loaded.EmbeddingRescueEnabled);
        Assert.True(loaded.TagKeywords);
        Assert.False(loaded.TagCategories);
        Assert.True(loaded.TagDescription);
    }

    [Fact]
    public void Load_returns_null_when_nothing_saved()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Null(_store.Load());
    }

    [Fact]
    public void Load_defaults_missing_fields_to_Session_defaults()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Save a bare-bones params (only model id non-empty; everything else is
        // either empty string / zero bool so the loader treats it as "not set").
        _store.Save(new EngineSettingsParams(
            ModelId: "some/model",
            Task: "",
            Device: "",
            ConfidenceThreshold: 0.3,
            ProbabilityMode: "",
            ProbabilityThreshold: 0.5,
            ProbabilityCandidates: Array.Empty<string>(),
            SystemPrompt: "",
            EmbeddingRescueEnabled: false,
            TagKeywords: false,
            TagCategories: false,
            TagDescription: false));

        var loaded = _store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("some/model", loaded.ModelId);
        Assert.Equal("image-text-to-text", loaded.Task);
        Assert.Equal("cpu", loaded.Device);
        Assert.Equal(0.3, loaded.ConfidenceThreshold, 6);
        Assert.Equal("both", loaded.ProbabilityMode);
        Assert.Equal(0.5, loaded.ProbabilityThreshold, 6);
        Assert.False(loaded.EmbeddingRescueEnabled);
        Assert.False(loaded.TagKeywords);
        Assert.False(loaded.TagCategories);
        Assert.False(loaded.TagDescription);
    }

    [Fact]
    public void Step2_engine_fields_hydrate_from_store()
    {
        if (!OperatingSystem.IsWindows()) return;

        _store.Save(new EngineSettingsParams(
            ModelId: "org/model-v2",
            Task: "image-text-to-text",
            Device: "cuda",
            ConfidenceThreshold: 0.5,
            ProbabilityMode: "llm",
            ProbabilityThreshold: 0.7,
            ProbabilityCandidates: new[] { "x" },
            SystemPrompt: "prompt",
            EmbeddingRescueEnabled: false,
            TagKeywords: true,
            TagCategories: true,
            TagDescription: false));

        var vm = new Step2EngineViewModel(new Session(), new FakeSidecar(), _store);

        Assert.Equal("org/model-v2", vm.ManualModelId);
        Assert.Equal(1, vm.DeviceIndex); // cuda
        Assert.Equal(0.5, vm.ConfidenceThreshold);
        Assert.Equal(0, vm.ProbabilityModeIndex); // llm
        Assert.Equal(0.7, vm.ProbabilityThreshold, 6);
        Assert.Equal("x", vm.ProbabilityCandidates);
        Assert.Equal("prompt", vm.SystemPrompt);
        Assert.False(vm.EmbeddingRescueEnabled);
        Assert.True(vm.TagKeywords);
        Assert.True(vm.TagCategories);
        Assert.False(vm.TagDescription);
    }

    [Fact]
    public void Step2_without_store_uses_session_defaults()
    {
        var vm = new Step2EngineViewModel(new Session(), new FakeSidecar());

        Assert.Equal("LiquidAI/LFM2.5-VL-450M", vm.ManualModelId);
        Assert.Equal(0, vm.DeviceIndex); // cpu
        Assert.Equal(0.3, vm.ConfidenceThreshold);
        Assert.Equal(2, vm.ProbabilityModeIndex); // both
        Assert.Equal(0.5, vm.ProbabilityThreshold);
        Assert.Equal("", vm.ProbabilityCandidates);
        Assert.Equal("", vm.SystemPrompt);
        Assert.False(vm.EmbeddingRescueEnabled);
        Assert.True(vm.TagKeywords);
        Assert.True(vm.TagCategories);
        Assert.True(vm.TagDescription);
    }

    [Fact]
    public void Save_to_store_after_session_push_round_trips()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Hydration path: store -> VM -> user nudges a value -> SaveToStore -> reload.
        var vm = new Step2EngineViewModel(new Session(), new FakeSidecar(), _store);

        // The store was empty, so VM is at session defaults. Nudge a few fields
        // and persist.
        vm.ManualModelId = "user/model";
        vm.DeviceIndex = 1; // cuda
        vm.ConfidenceThreshold = 0.25;
        vm.ProbabilityModeIndex = 1; // probability
        vm.ProbabilityThreshold = 0.8;
        vm.ProbabilityCandidates = "a, b , c";
        vm.SystemPrompt = "sys";
        vm.TagKeywords = false;
        vm.TagDescription = false;
        vm.TagCategories = true;
        vm.SaveToStore();

        var loaded = _store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("user/model", loaded.ModelId);
        Assert.Equal("cuda", loaded.Device);
        Assert.Equal(0.25, loaded.ConfidenceThreshold, 6);
        Assert.Equal("probability", loaded.ProbabilityMode);
        Assert.Equal(0.8, loaded.ProbabilityThreshold, 6);
        Assert.Equal(new[] { "a", "b", "c" }, loaded.ProbabilityCandidates);
        Assert.True(loaded.TagCategories);
        Assert.False(loaded.TagKeywords);
        Assert.False(loaded.TagDescription);
    }

    [Fact]
    public void Clear_removes_saved_engine_settings()
    {
        if (!OperatingSystem.IsWindows()) return;

        _store.Save(new EngineSettingsParams(
            ModelId: "m", Task: "image-text-to-text", Device: "cpu",
            ConfidenceThreshold: 0.3, ProbabilityMode: "both",
            ProbabilityThreshold: 0.5, ProbabilityCandidates: Array.Empty<string>(),
            SystemPrompt: "", EmbeddingRescueEnabled: false,
            TagKeywords: true, TagCategories: true, TagDescription: true));
        _store.Clear();

        Assert.Null(_store.Load());
    }
}



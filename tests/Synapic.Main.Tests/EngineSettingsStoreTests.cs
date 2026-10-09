using System.Globalization;
using Synapic.Main.Models;
using Synapic.Main.Services;
using Synapic.Main.ViewModels.Steps;
using Xunit;

namespace Synapic.Main.Tests;

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
            UserPrompt: "Give me JSON with description, category and keywords.",
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
        Assert.Equal("Give me JSON with description, category and keywords.", loaded.UserPrompt);
        Assert.True(loaded.EmbeddingRescueEnabled);
        Assert.True(loaded.TagKeywords);
        Assert.False(loaded.TagCategories);
        Assert.True(loaded.TagDescription);
    }

    /// <summary>
    /// The registry is machine-wide; the process culture is not. A threshold
    /// written while the app ran in one regional format has to come back as the
    /// same number when it runs in another — otherwise the stored text itself is
    /// the bug, since a dot-decimal culture reads the comma of "0,42" as a group
    /// separator, never as the decimal point it was written to mean.
    ///
    /// The fact pins both ends, because the machine it runs on is only one of
    /// them: the stored text is asserted locale-independent, the read is done
    /// under the other culture, and a value already in the registry from a build
    /// that wrote comma decimals still reads as that number.
    /// </summary>
    [Fact]
    public void Saved_thresholds_survive_a_culture_change()
    {
        if (!OperatingSystem.IsWindows()) return;

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");   // comma decimals
            _store.Save(new EngineSettingsParams(
                ModelId: "some/model", Task: "image-text-to-text", Device: "cpu",
                ConfidenceThreshold: 0.42, ProbabilityMode: "both",
                ProbabilityThreshold: 0.66, ProbabilityCandidates: Array.Empty<string>(),
                SystemPrompt: "", UserPrompt: "", EmbeddingRescueEnabled: false,
                TagKeywords: true, TagCategories: true, TagDescription: true));

            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(_keyPath))
            {
                Assert.NotNull(key);
                Assert.Equal("0.42", key!.GetValue("ConfidenceThreshold") as string);
                Assert.Equal("0.66", key.GetValue("ProbabilityThreshold") as string);
            }

            CultureInfo.CurrentCulture = new CultureInfo("en-US");   // dot decimals
            var loaded = _store.Load();
            Assert.NotNull(loaded);
            Assert.Equal(0.42, loaded.ConfidenceThreshold, 6);
            Assert.Equal(0.66, loaded.ProbabilityThreshold, 6);

            // An install that already holds a comma-decimal value (written by a
            // build before this was invariant) keeps meaning that number.
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(_keyPath, writable: true))
            {
                Assert.NotNull(key);
                key!.SetValue("ConfidenceThreshold", "0,42", Microsoft.Win32.RegistryValueKind.String);
                key.SetValue("ProbabilityThreshold", "0,66", Microsoft.Win32.RegistryValueKind.String);
            }

            var legacy = _store.Load();
            Assert.NotNull(legacy);
            Assert.Equal(0.42, legacy.ConfidenceThreshold, 6);
            Assert.Equal(0.66, legacy.ProbabilityThreshold, 6);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
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
            UserPrompt: "",
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
            UserPrompt: "custom tag instruction",
            EmbeddingRescueEnabled: false,
            TagKeywords: true,
            TagCategories: true,
            TagDescription: false));

        // A CUDA-capable machine is pinned: the device combo only offers CUDA
        // where the driver exists, and the test runner is not a GPU box.
        var vm = new Step2EngineViewModel(new Session(), new FakeSidecar(), _store,
            null, () => new ComputeAvailability(Cuda: true, Mps: false));

        Assert.Equal("org/model-v2", vm.ManualModelId);
        Assert.Equal("cuda", vm.Device);
        Assert.Equal(0.5, vm.ConfidenceThreshold);
        Assert.Equal(0, vm.ProbabilityModeIndex); // llm
        Assert.Equal(0.7, vm.ProbabilityThreshold, 6);
        Assert.Equal("x", vm.ProbabilityCandidates);
        Assert.Equal("prompt", vm.SystemPrompt);
        Assert.Equal("custom tag instruction", vm.UserPrompt);
        Assert.False(vm.EmbeddingRescueEnabled);
        Assert.True(vm.TagKeywords);
        Assert.True(vm.TagCategories);
        Assert.False(vm.TagDescription);
    }

    [Fact]
    public void Load_treats_a_missing_user_prompt_as_the_built_in_one()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Anyone upgrading from a build without the editable tag instruction has
        // no UserPrompt value at all: it must load as blank ("use the built-in
        // instruction"), not crash and not invent a prompt.
        _store.Save(new EngineSettingsParams(
            ModelId: "some/model", Task: "image-text-to-text", Device: "cpu",
            ConfidenceThreshold: 0.3, ProbabilityMode: "both",
            ProbabilityThreshold: 0.5, ProbabilityCandidates: Array.Empty<string>(),
            SystemPrompt: "", UserPrompt: "custom", EmbeddingRescueEnabled: false,
            TagKeywords: true, TagCategories: true, TagDescription: true));

        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(_keyPath, writable: true))
        {
            Assert.NotNull(key);
            key!.DeleteValue("UserPrompt");
        }

        var loaded = _store.Load();

        Assert.NotNull(loaded);
        Assert.Equal("", loaded.UserPrompt);
    }

    [Fact]
    public void Step2_without_store_uses_session_defaults()
    {
        var vm = new Step2EngineViewModel(new Session(), new FakeSidecar());

        Assert.Equal("LiquidAI/LFM2.5-VL-450M", vm.ManualModelId);
        Assert.Equal("cpu", vm.Device);
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
        var vm = new Step2EngineViewModel(new Session(), new FakeSidecar(), _store,
            null, () => new ComputeAvailability(Cuda: true, Mps: false));

        // The store was empty, so VM is at session defaults. Nudge a few fields
        // and persist.
        vm.ManualModelId = "user/model";
        Assert.True(vm.TrySelectDevice("cuda"));
        vm.ConfidenceThreshold = 0.25;
        vm.ProbabilityModeIndex = 1; // probability
        vm.ProbabilityThreshold = 0.8;
        vm.ProbabilityCandidates = "a, b , c";
        vm.SystemPrompt = "sys";
        vm.UserPrompt = "instruction";
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
        Assert.Equal("instruction", loaded.UserPrompt);
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
            SystemPrompt: "", UserPrompt: "", EmbeddingRescueEnabled: false,
            TagKeywords: true, TagCategories: true, TagDescription: true));
        _store.Clear();

        Assert.Null(_store.Load());
    }
}



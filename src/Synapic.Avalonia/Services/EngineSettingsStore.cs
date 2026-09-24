using Microsoft.Win32;

namespace Synapic.Avalonia.Services;

/// <summary>Step 2 engine settings (model id, device, thresholds, prompt, tag-field checkboxes).</summary>
/// Persisted to HKCU\Software\Synapic\Engine so the user does not re-enter them on consecutive runs.
/// Non-Windows = no-op (cross-platform unchanged).
public sealed record EngineSettingsParams(
    string ModelId,
    string Task,
    string Device,
    double ConfidenceThreshold,
    string ProbabilityMode,
    double ProbabilityThreshold,
    string[] ProbabilityCandidates,
    string SystemPrompt,
    string UserPrompt,
    bool EmbeddingRescueEnabled,
    bool TagKeywords,
    bool TagCategories,
    bool TagDescription);

/// <summary>
/// Persists Step 2 engine settings to the Windows registry (HKCU\Software\Synapic\Engine).
/// No secrets here, so no DPAPI — plain strings/doubles/ints.
/// On non-Windows the store is a no-op.
/// </summary>
public sealed class EngineSettingsStore
{
    private const string KeyPath = @"Software\Synapic\Engine";
    private const string ModelIdValue = "ModelId";
    private const string TaskValue = "Task";
    private const string DeviceValue = "Device";
    private const string ConfidenceThresholdValue = "ConfidenceThreshold";
    private const string ProbabilityModeValue = "ProbabilityMode";
    private const string ProbabilityThresholdValue = "ProbabilityThreshold";
    private const string ProbabilityCandidatesValue = "ProbabilityCandidates";
    private const string SystemPromptValue = "SystemPrompt";
    private const string UserPromptValue = "UserPrompt";
    private const string EmbeddingRescueEnabledValue = "EmbeddingRescueEnabled";
    private const string TagKeywordsValue = "TagKeywords";
    private const string TagCategoriesValue = "TagCategories";
    private const string TagDescriptionValue = "TagDescription";

    private readonly string? _keyPathOverride;

    public EngineSettingsStore(string? keyPathOverride = null) => _keyPathOverride = keyPathOverride;

    private string EffectiveKeyPath => _keyPathOverride ?? KeyPath;

    // Known Session/Engine defaults (mirror EngineState), used when a saved
    // value is absent so the loaded params match a first-launch default state.
    private const double DefaultConfidenceThreshold = 0.3;
    private const double DefaultProbabilityThreshold = 0.5;
    private const string DefaultTask = "image-text-to-text";
    private const string DefaultDevice = "cpu";
    private const string DefaultProbabilityMode = "both";

    /// <summary>Load saved engine settings; null when none saved (use Session defaults).</summary>
    public EngineSettingsParams? Load()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(EffectiveKeyPath);
            if (key is null) return null;

            var modelId = key.GetValue(ModelIdValue) as string;
            if (string.IsNullOrEmpty(modelId)) return null; // nothing meaningful saved

            // Strings (REG_SZ). Empty saved strings are treated as "not set" so
            // a bare-bones save still hydrates the full form defaults.
            string AsDefault(string? v, string d) => string.IsNullOrEmpty(v) ? d : v;

            var task = AsDefault(key.GetValue(TaskValue) as string, DefaultTask);
            var device = AsDefault(key.GetValue(DeviceValue) as string, DefaultDevice);
            var probabilityMode = AsDefault(key.GetValue(ProbabilityModeValue) as string, DefaultProbabilityMode);
            var systemPrompt = key.GetValue(SystemPromptValue) as string ?? "";
            // Absent for anyone upgrading from a build without the editable tag
            // instruction: blank means "use the sidecar's built-in one".
            var userPrompt = key.GetValue(UserPromptValue) as string ?? "";

            // Doubles: stored as REG_SZ strings (lossless, exact round-trip).
            double ParseDouble(string? v, double fallback) =>
                double.TryParse(v, out var d) ? d : fallback;

            var confidenceThreshold = ParseDouble(
                key.GetValue(ConfidenceThresholdValue) as string, DefaultConfidenceThreshold);
            var probabilityThreshold = ParseDouble(
                key.GetValue(ProbabilityThresholdValue) as string, DefaultProbabilityThreshold);

            var candidates = Array.Empty<string>();
            if (key.GetValue(ProbabilityCandidatesValue) is string pc && !string.IsNullOrEmpty(pc))
                candidates = pc.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // Booleans (REG_DWORD).
            var embeddingRescueEnabled = key.GetValue(EmbeddingRescueEnabledValue) is int er && er != 0;
            var tagKeywords = key.GetValue(TagKeywordsValue) is int tkw && tkw != 0;
            var tagCategories = key.GetValue(TagCategoriesValue) is int tca && tca != 0;
            var tagDescription = key.GetValue(TagDescriptionValue) is int tds && tds != 0;

            return new EngineSettingsParams(
                modelId, task, device, confidenceThreshold, probabilityMode, probabilityThreshold,
                candidates, systemPrompt, userPrompt, embeddingRescueEnabled,
                tagKeywords, tagCategories, tagDescription);
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(EngineSettingsStore),
                $"Failed to read engine settings from registry: {e.Message}");
            return null;
        }
    }

    /// <summary>Save the current engine settings.</summary>
    public void Save(EngineSettingsParams params_)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(EffectiveKeyPath, writable: true);
            key.SetValue(ModelIdValue, params_.ModelId, RegistryValueKind.String);
            key.SetValue(TaskValue, params_.Task, RegistryValueKind.String);
            key.SetValue(DeviceValue, params_.Device, RegistryValueKind.String);
            key.SetValue(ConfidenceThresholdValue, params_.ConfidenceThreshold.ToString(), RegistryValueKind.String);
            key.SetValue(ProbabilityModeValue, params_.ProbabilityMode, RegistryValueKind.String);
            key.SetValue(ProbabilityThresholdValue, params_.ProbabilityThreshold.ToString(), RegistryValueKind.String);
            key.SetValue(ProbabilityCandidatesValue,
                string.Join(",", params_.ProbabilityCandidates), RegistryValueKind.String);
            key.SetValue(SystemPromptValue, params_.SystemPrompt, RegistryValueKind.String);
            key.SetValue(UserPromptValue, params_.UserPrompt, RegistryValueKind.String);
            key.SetValue(EmbeddingRescueEnabledValue, params_.EmbeddingRescueEnabled ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue(TagKeywordsValue, params_.TagKeywords ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue(TagCategoriesValue, params_.TagCategories ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue(TagDescriptionValue, params_.TagDescription ? 1 : 0, RegistryValueKind.DWord);
            SynapicLog.Info(nameof(EngineSettingsStore),
                $"Saved Step 2 engine settings to registry ({EffectiveKeyPath}): {params_.ModelId}, device={params_.Device}");
        }
        catch (Exception e)
        {
            SynapicLog.Error(nameof(EngineSettingsStore),
                $"Failed to save engine settings to registry: {e.Message}");
        }
    }

    /// <summary>Clear saved engine settings.</summary>
    public void Clear()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(EffectiveKeyPath, throwOnMissingSubKey: false);
            SynapicLog.Info(nameof(EngineSettingsStore), $"Cleared registry key {EffectiveKeyPath}");
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(EngineSettingsStore), $"Failed to clear registry key: {e.Message}");
        }
    }
}

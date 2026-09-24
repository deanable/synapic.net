using System.IO;
using System.Text.Json;

namespace Synapic.Avalonia.Services;

/// <summary>The file shape: a JSON object holding the prompt list.</summary>
public sealed class SystemPromptPresetFile
{
    public int Version { get; set; } = 1;

    /// <summary>Most recently used first.</summary>
    public List<string> Prompts { get; set; } = new();
}

/// <summary>
/// The system-prompt presets: every system prompt the user has committed, kept
/// in %APPDATA%/Synapic/system-prompts.json so Step 2's combobox can offer the
/// wording back later.
///
/// This is history, not settings — the *current* system prompt lives in
/// Session/registry (EngineSettingsStore) like every other Step 2 field. Keeping
/// the two apart is what makes the list safe to prune with the Delete key: losing
/// an entry costs a retype, never the run's configuration.
///
/// The file is plain JSON the user can read and edit; the loader tolerates a bare
/// array (a hand-written list) and a missing or malformed file (empty history).
/// </summary>
public sealed class SystemPromptPresetStore
{
    /// <summary>
    /// How many prompts to keep. The oldest fall off the end so the file cannot
    /// grow without bound.
    /// </summary>
    public const int MaxPresets = 25;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;

    public SystemPromptPresetStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(AppConfig.DefaultDirectory, "system-prompts.json");
    }

    public string FilePath => _filePath;

    /// <summary>Saved presets, most recently used first. Never throws.</summary>
    public IReadOnlyList<string> Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return Array.Empty<string>();
            return Normalize(ReadRawPrompts(File.ReadAllText(_filePath)));
        }
        catch (Exception e)
        {
            // A corrupt file must not cost the user their Step 2 form: report it
            // and carry on with an empty history.
            SynapicLog.Warning(nameof(SystemPromptPresetStore),
                $"Failed to load system-prompt presets ({e.Message}); starting from an empty list");
            return Array.Empty<string>();
        }
    }

    /// <summary>Replace the stored list; returns what was actually written.</summary>
    public IReadOnlyList<string> Save(IEnumerable<string>? prompts)
    {
        var normalized = Normalize(prompts);
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var file = new SystemPromptPresetFile { Prompts = normalized.ToList() };
            File.WriteAllText(_filePath, JsonSerializer.Serialize(file, Options));
        }
        catch (Exception e)
        {
            SynapicLog.Error(nameof(SystemPromptPresetStore),
                $"Failed to save system-prompt presets: {e.Message}");
        }

        return normalized;
    }

    /// <summary>
    /// Add a prompt as the most recently used one. Re-using a prompt moves it back
    /// to the front rather than duplicating it; blank input changes nothing.
    /// </summary>
    public IReadOnlyList<string> Add(string? prompt)
    {
        var value = (prompt ?? "").Trim();
        if (value.Length == 0) return Load();

        var prompts = Load().Where(p => !string.Equals(p, value, StringComparison.Ordinal)).ToList();
        prompts.Insert(0, value);
        return Save(prompts);
    }

    /// <summary>Remove a prompt; an entry that is not there is a no-op.</summary>
    public IReadOnlyList<string> Remove(string? prompt)
    {
        var value = (prompt ?? "").Trim();
        var prompts = Load().ToList();
        if (value.Length == 0) return prompts;

        var removed = prompts.RemoveAll(p => string.Equals(p, value, StringComparison.Ordinal));
        return removed == 0 ? prompts : Save(prompts);
    }

    /// <summary>Trim, drop blanks and duplicates (first wins), cap at MaxPresets.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? prompts)
    {
        if (prompts is null) return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var raw in prompts)
        {
            var value = (raw ?? "").Trim();
            if (value.Length == 0 || !seen.Add(value)) continue;
            result.Add(value);
            if (result.Count == MaxPresets) break;
        }

        return result;
    }

    /// <summary>Read either {"prompts": [...]} or a bare [...] array.</summary>
    private static IReadOnlyList<string>? ReadRawPrompts(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
            return Strings(root);

        // The property is matched by name, not by JsonSerializer, so the
        // case-insensitive option does not apply: a hand-edited file may spell
        // it "prompts", "Prompts" or anything in between.
        if (root.ValueKind == JsonValueKind.Object
            && FindPromptsArray(root) is { } prompts)
            return Strings(prompts);

        return null;

        static JsonElement? FindPromptsArray(JsonElement root)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name.Equals("prompts", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Array)
                    return property.Value;
            }

            return null;
        }

        // Materialized inside the document's lifetime: a lazy sequence over a
        // disposed JsonDocument throws on enumeration, which reads as an empty
        // history and would drop every saved prompt on the next start.
        static List<string> Strings(JsonElement array) =>
            array.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? "")
                .ToList();
    }
}

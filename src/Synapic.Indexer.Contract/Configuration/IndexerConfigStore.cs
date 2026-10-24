using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Synapic.Indexer.Contract.Configuration;

/// <summary>One configuration problem, named by the setting's dotted path.</summary>
public sealed record ConfigError(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}

/// <summary>
/// The result of reading both sources. <see cref="Errors"/> is non-empty when a
/// value could not be read or the JSON file is malformed; the config still holds
/// every value that could be read, so the caller can report all problems at once.
/// </summary>
public sealed record IndexerConfigLoadResult(
    IndexerConfig Config,
    IReadOnlyList<string> Sources,
    IReadOnlyList<ConfigError> Errors,
    IReadOnlyList<ConfigError> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Reads the indexer's configuration: registry machine defaults first, then the
/// ProgramData JSON file's members applied on top, value by value (D-11-05).
///
/// Registry values are named after the JSON paths (<c>service.logLevel</c>,
/// <c>schedule.intervalMinutes</c>, …) so an administrator can set one without
/// inventing a second vocabulary. Both paths are constructor-overridable so
/// tests never touch HKLM or the real ProgramData.
/// </summary>
public sealed class IndexerConfigStore
{
    public const string DefaultRegistryKeyPath = @"SOFTWARE\Synapic\Indexer";
    public const string DefaultConfigFileName = "indexer-config.json";

    private readonly string _registryKeyPath;
    private readonly bool _registryIsTestOverride;
    private readonly string _configFilePath;

    public IndexerConfigStore(string? registryKeyPathOverride = null, string? configFilePathOverride = null)
    {
        _registryIsTestOverride = !string.IsNullOrWhiteSpace(registryKeyPathOverride);
        _registryKeyPath = string.IsNullOrWhiteSpace(registryKeyPathOverride)
            ? DefaultRegistryKeyPath
            : registryKeyPathOverride;
        _configFilePath = string.IsNullOrWhiteSpace(configFilePathOverride)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Synapic",
                DefaultConfigFileName)
            : configFilePathOverride;
    }

    public string ConfigFilePath => _configFilePath;

    public IndexerConfigLoadResult Load()
    {
        var config = IndexerConfig.CreateDefault();
        var sources = new List<string>();
        var errors = new List<ConfigError>();
        var warnings = new List<ConfigError>();

        if (TryReadRegistry(out var registryValues, out var registryError))
        {
            if (registryValues.Count > 0)
            {
                Apply(registryValues, "registry", config, errors, warnings);
                sources.Add($"registry:{_registryKeyPath}");
            }
        }
        else if (registryError is not null)
        {
            errors.Add(registryError);
        }

        if (File.Exists(_configFilePath))
        {
            var json = ReadJson(_configFilePath, errors);
            if (json is not null)
            {
                var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                Flatten(json, prefix: "", values, errors);
                Apply(values, "file", config, errors, warnings);
                sources.Add($"file:{_configFilePath}");
            }
        }

        return new IndexerConfigLoadResult(config, sources, errors, warnings);
    }

    /// <summary>
    /// Writes only <c>datasource.daminion.encryptedPassword</c>, preserving every
    /// other member of the file (parsed as a JSON tree, not through the model, so
    /// a field this build does not know about survives — D-11-07).
    /// </summary>
    public void SaveDaminionPassword(string encryptedPassword)
    {
        var root = ReadTree();
        root["version"] ??= 1;
        var datasource = root["datasource"] as JsonObject ?? new JsonObject();
        root["datasource"] = datasource;
        var daminion = datasource["daminion"] as JsonObject ?? new JsonObject();
        datasource["daminion"] = daminion;
        daminion["encryptedPassword"] = encryptedPassword;
        SaveTree(root);
    }

    /// <summary>
    /// The config file as a JSON tree, so a partial update can preserve members
    /// this build does not know about. An absent file is an empty object; a
    /// malformed one throws — callers that must not lose data validate first.
    /// </summary>
    public JsonObject ReadTree()
    {
        if (!File.Exists(_configFilePath)) return new JsonObject();
        var text = File.ReadAllText(_configFilePath);
        return string.IsNullOrWhiteSpace(text)
            ? new JsonObject()
            : JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
              {
                  CommentHandling = JsonCommentHandling.Skip,
                  AllowTrailingCommas = true,
              }) as JsonObject
              ?? throw new InvalidOperationException($"{_configFilePath} is not a JSON object.");
    }

    /// <summary>
    /// Writes the whole tree, indented. The partial-update commands
    /// (<c>--set-daminion-password</c>, <c>--apply-config</c>) go through this so
    /// there is exactly one writer of the file.
    /// </summary>
    public void SaveTree(JsonObject root)
    {
        var directory = Path.GetDirectoryName(_configFilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(_configFilePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private bool TryReadRegistry(out Dictionary<string, object?> values, out ConfigError? error)
    {
        values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        error = null;
        // The registry defaults exist only on Windows; elsewhere the JSON file
        // (or the built-in defaults) is the whole configuration, which is not an
        // error. The guard also satisfies CA1416 on the cross-platform callers.
        if (!OperatingSystem.IsWindows()) return true;
        var hive = _registryIsTestOverride ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = baseKey.OpenSubKey(_registryKeyPath, writable: false);
            if (key is null) return true;

            foreach (var name in key.GetValueNames())
            {
                if (name.Length == 0) continue;
                values[name] = key.GetValue(name);
            }
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            error = new ConfigError(
                $"registry:{_registryKeyPath}",
                $"could not be read ({e.GetType().Name}: {e.Message}); the service account must be able to read the key");
            return false;
        }
    }

    private static JsonNode? ReadJson(string path, List<ConfigError> errors)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException e)
        {
            errors.Add(new ConfigError(path, $"is not valid JSON: {e.Message}"));
            return null;
        }
        catch (IOException e)
        {
            errors.Add(new ConfigError(path, $"could not be read: {e.Message}"));
            return null;
        }
    }

    /// <summary>Turns the JSON tree into the same dotted-path dictionary the registry produces.</summary>
    private static void Flatten(JsonNode? node, string prefix, Dictionary<string, object?> values, List<ConfigError> errors)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, child) in obj)
            {
                var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
                Flatten(child, path, values, errors);
            }
            return;
        }

        if (prefix.Length == 0) return;

        values[prefix] = node switch
        {
            null => null,
            JsonArray array => array.Select(ToPrimitive).ToList(),
            JsonValue value => ToPrimitive(value),
            _ => node.ToJsonString(),
        };

        static object? ToPrimitive(JsonNode? item) => item switch
        {
            null => null,
            JsonValue value when value.TryGetValue<bool>(out var b) => b,
            JsonValue value when value.TryGetValue<long>(out var l) => l,
            JsonValue value when value.TryGetValue<double>(out var d) => d,
            JsonValue value when value.TryGetValue<string>(out var s) => s,
            _ => item.ToJsonString(),
        };
    }

    private static void Apply(
        Dictionary<string, object?> values,
        string source,
        IndexerConfig config,
        List<ConfigError> errors,
        List<ConfigError> warnings)
    {
        foreach (var (path, raw) in values)
        {
            switch (path.ToLowerInvariant())
            {
                case "version":
                    if (TryInt(raw, path, source, errors, out var version)) config.Version = version;
                    break;

                case "service.autostart":
                    if (TryBool(raw, path, source, errors, out var autoStart)) config.Service.AutoStart = autoStart;
                    break;
                case "service.loglevel":
                    if (TryString(raw, path, source, errors, out var logLevel)) config.Service.LogLevel = logLevel;
                    break;
                case "service.maxconcurrentitems":
                    if (TryInt(raw, path, source, errors, out var concurrency)) config.Service.MaxConcurrentItems = concurrency;
                    break;
                case "service.idlesidecartimeoutminutes":
                    if (TryInt(raw, path, source, errors, out var idle)) config.Service.IdleSidecarTimeoutMinutes = idle;
                    break;

                case "schedule.mode":
                    if (TryString(raw, path, source, errors, out var mode)) config.Schedule.Mode = mode;
                    break;
                case "schedule.intervalminutes":
                    if (TryInt(raw, path, source, errors, out var interval)) config.Schedule.IntervalMinutes = interval;
                    break;
                case "schedule.watchpaths":
                    if (TryStringList(raw, path, source, errors, out var paths)) config.Schedule.WatchPaths = paths;
                    break;
                case "schedule.watchrecursive":
                    if (TryBool(raw, path, source, errors, out var recursive)) config.Schedule.WatchRecursive = recursive;
                    break;

                case "engine.device":
                    if (TryString(raw, path, source, errors, out var device)) config.Engine.Device = device;
                    break;
                case "engine.modelid":
                    if (TryString(raw, path, source, errors, out var model)) config.Engine.ModelId = model;
                    break;
                case "engine.confidencethreshold":
                    if (TryDouble(raw, path, source, errors, out var confidence)) config.Engine.ConfidenceThreshold = confidence;
                    break;
                case "engine.probabilitymode":
                    if (TryString(raw, path, source, errors, out var probabilityMode)) config.Engine.ProbabilityMode = probabilityMode;
                    break;
                case "engine.probabilitythreshold":
                    if (TryDouble(raw, path, source, errors, out var probability)) config.Engine.ProbabilityThreshold = probability;
                    break;
                case "engine.userprompt":
                    if (TryNullableString(raw, path, source, errors, out var prompt)) config.Engine.UserPrompt = prompt;
                    break;

                case "datasource.type":
                    if (TryString(raw, path, source, errors, out var type)) config.Datasource.Type = type;
                    break;
                case "datasource.daminion.serverurl":
                    if (TryString(raw, path, source, errors, out var serverUrl)) config.Datasource.Daminion.ServerUrl = serverUrl;
                    break;
                case "datasource.daminion.catalogid":
                    if (TryInt(raw, path, source, errors, out var catalogId)) config.Datasource.Daminion.CatalogId = catalogId;
                    break;
                case "datasource.daminion.username":
                    if (TryString(raw, path, source, errors, out var username)) config.Datasource.Daminion.Username = username;
                    break;
                case "datasource.daminion.encryptedpassword":
                    if (TryNullableString(raw, path, source, errors, out var encrypted))
                        config.Datasource.Daminion.EncryptedPassword = encrypted;
                    break;
                case "datasource.daminion.scope":
                    if (TryString(raw, path, source, errors, out var scope)) config.Datasource.Daminion.Scope = scope;
                    break;
                case "datasource.daminion.untaggedfields":
                    if (TryStringList(raw, path, source, errors, out var fields)) config.Datasource.Daminion.UntaggedFields = fields;
                    break;

                case "tagging.writecategory":
                    if (TryBool(raw, path, source, errors, out var category)) config.Tagging.WriteCategory = category;
                    break;
                case "tagging.writekeywords":
                    if (TryBool(raw, path, source, errors, out var keywords)) config.Tagging.WriteKeywords = keywords;
                    break;
                case "tagging.writedescription":
                    if (TryBool(raw, path, source, errors, out var description)) config.Tagging.WriteDescription = description;
                    break;
                case "tagging.overwriteexistingtags":
                    if (TryBool(raw, path, source, errors, out var overwrite)) config.Tagging.OverwriteExistingTags = overwrite;
                    break;

                default:
                    // A future field must not stop an older build (the password
                    // command preserves such members), but a typo should still be
                    // visible in the log rather than silently ignored.
                    warnings.Add(new ConfigError(
                        $"{source}:{path}",
                        "is not a recognized setting for schema version 1 and was ignored"));
                    break;
            }
        }
    }

    private static bool TryInt(object? raw, string path, string source, List<ConfigError> errors, out int value)
    {
        value = 0;
        switch (raw)
        {
            case int i: value = i; return true;
            case long l when l is >= int.MinValue and <= int.MaxValue: value = (int)l; return true;
            case string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                value = parsed;
                return true;
            default:
                errors.Add(new ConfigError($"{source}:{path}", "must be a whole number"));
                return false;
        }
    }

    private static bool TryDouble(object? raw, string path, string source, List<ConfigError> errors, out double value)
    {
        value = 0;
        switch (raw)
        {
            case double d: value = d; return true;
            case long l: value = l; return true;
            case int i: value = i; return true;
            case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                value = parsed;
                return true;
            default:
                errors.Add(new ConfigError($"{source}:{path}", "must be a number"));
                return false;
        }
    }

    private static bool TryBool(object? raw, string path, string source, List<ConfigError> errors, out bool value)
    {
        value = false;
        switch (raw)
        {
            case bool b: value = b; return true;
            // Registry DWORDs are the natural way to store a switch.
            case int i when i is 0 or 1: value = i == 1; return true;
            case long l when l is 0 or 1: value = l == 1; return true;
            case string s when bool.TryParse(s, out var parsed): value = parsed; return true;
            case string s when s == "0" || s == "1": value = s == "1"; return true;
            default:
                errors.Add(new ConfigError($"{source}:{path}", "must be true or false"));
                return false;
        }
    }

    private static bool TryString(object? raw, string path, string source, List<ConfigError> errors, out string value)
    {
        value = "";
        if (raw is string s)
        {
            value = s;
            return true;
        }
        errors.Add(new ConfigError($"{source}:{path}", "must be a string"));
        return false;
    }

    private static bool TryNullableString(
        object? raw, string path, string source, List<ConfigError> errors, out string? value)
    {
        value = null;
        if (raw is null) return true;
        if (raw is string s)
        {
            value = s;
            return true;
        }
        errors.Add(new ConfigError($"{source}:{path}", "must be a string or null"));
        return false;
    }

    private static bool TryStringList(
        object? raw, string path, string source, List<ConfigError> errors, out List<string> value)
    {
        value = new List<string>();
        switch (raw)
        {
            // REG_MULTI_SZ arrives as string[]; a single REG_SZ uses ';'.
            case string[] items:
                value = items.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).ToList();
                return true;
            case List<object?> list:
                if (list.Any(i => i is not string))
                {
                    errors.Add(new ConfigError($"{source}:{path}", "must be a list of strings"));
                    return false;
                }
                value = list.Select(i => ((string)i!).Trim()).Where(i => i.Length > 0).ToList();
                return true;
            case string joined:
                value = joined
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
                return true;
            default:
                errors.Add(new ConfigError($"{source}:{path}", "must be a list of strings"));
                return false;
        }
    }
}

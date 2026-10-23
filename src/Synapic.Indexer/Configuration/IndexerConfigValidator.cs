using System.Globalization;
using Synapic.Core.Services;

namespace Synapic.Indexer.Configuration;

/// <summary>
/// Checks a loaded <see cref="IndexerConfig"/> against the rules the service
/// cannot guess its way past (D-11-05). Every failure names the setting's dotted
/// path, and startup reports all of them at once: fixing one bad value at a time,
/// one restart per typo, is not a debugging experience.
/// </summary>
public static class IndexerConfigValidator
{
    private static readonly string[] Modes = { "interval", "watch", "intervalandwatch" };
    private static readonly string[] ProbabilityModes = { "both", "threshold" };
    private static readonly string[] DatasourceTypes = { "local", "daminion" };

    public static IReadOnlyList<ConfigError> Validate(IndexerConfig config)
    {
        var errors = new List<ConfigError>();

        if (config.Version != 1)
            errors.Add(new ConfigError("version", $"must be 1 (this build understands schema version 1); was {config.Version}"));

        // Watch paths are a local concern: a Daminion run polls the catalog, so
        // requiring local folders from it would reject a perfectly good config.
        var isLocal = (config.Datasource.Type ?? "").Trim().ToLowerInvariant() == "local";

        ValidateService(config.Service, errors);
        ValidateSchedule(config.Schedule, isLocal, errors);
        ValidateEngine(config.Engine, errors);
        ValidateDatasource(config, errors);
        ValidateTagging(config.Tagging, errors);

        return errors;
    }

    private static void ValidateService(ServiceSettings service, List<ConfigError> errors)
    {
        if (!IsLogLevel(service.LogLevel))
            errors.Add(new ConfigError("service.logLevel",
                $"must be one of Verbose, Debug, Information, Warning, Error, Fatal; was '{service.LogLevel}'"));

        if (service.MaxConcurrentItems is < 1 or > 32)
            errors.Add(new ConfigError("service.maxConcurrentItems",
                $"must be between 1 and 32; was {service.MaxConcurrentItems}"));

        if (service.IdleSidecarTimeoutMinutes is < 0 or > 1440)
            errors.Add(new ConfigError("service.idleSidecarTimeoutMinutes",
                $"must be between 0 and 1440 minutes (0 = never stop the sidecar); was {service.IdleSidecarTimeoutMinutes}"));
    }

    private static void ValidateSchedule(ScheduleSettings schedule, bool isLocal, List<ConfigError> errors)
    {
        var mode = (schedule.Mode ?? "").Trim().ToLowerInvariant();
        if (!Modes.Contains(mode))
        {
            errors.Add(new ConfigError("schedule.mode",
                $"must be Interval, Watch or IntervalAndWatch; was '{schedule.Mode}'"));
            return;
        }

        if (schedule.IntervalMinutes is < 1 or > 1440)
            errors.Add(new ConfigError("schedule.intervalMinutes",
                $"must be between 1 and 1440 minutes; was {schedule.IntervalMinutes}"));

        var needsWatchPaths = isLocal && mode is "watch" or "intervalandwatch";
        if (needsWatchPaths && schedule.WatchPaths.Count == 0)
        {
            errors.Add(new ConfigError("schedule.watchPaths",
                $"mode {schedule.Mode} needs at least one folder to watch"));
        }

        for (var i = 0; i < schedule.WatchPaths.Count; i++)
        {
            var path = schedule.WatchPaths[i];
            var setting = $"schedule.watchPaths[{i}]";
            if (string.IsNullOrWhiteSpace(path))
            {
                errors.Add(new ConfigError(setting, "must not be empty"));
                continue;
            }

            if (!Path.IsPathFullyQualified(path))
            {
                errors.Add(new ConfigError(setting, $"must be an absolute path; was '{path}'"));
                continue;
            }

            // A missing watch root is only fatal when the watcher would need it;
            // an interval-only run tolerates a share that is offline right now.
            if (needsWatchPaths && !Directory.Exists(path))
                errors.Add(new ConfigError(setting, $"does not exist or is not reachable: '{path}'"));
        }
    }

    private static void ValidateEngine(EngineSettings engine, List<ConfigError> errors)
    {
        var device = (engine.Device ?? "").Trim().ToLowerInvariant();
        if (device is not ("cpu" or "cuda" or "mps"))
            errors.Add(new ConfigError("engine.device",
                $"must be cpu, cuda or mps; was '{engine.Device}'"));

        if (string.IsNullOrWhiteSpace(engine.ModelId))
            errors.Add(new ConfigError("engine.modelId", "must name a model (for example LiquidAI/LFM2.5-VL-1.6B)"));

        if (engine.ConfidenceThreshold is < 0 or > 1)
            errors.Add(new ConfigError("engine.confidenceThreshold",
                $"must be between 0 and 1; was {engine.ConfidenceThreshold.ToString(CultureInfo.InvariantCulture)}"));

        if (engine.ProbabilityThreshold is < 0 or > 1)
            errors.Add(new ConfigError("engine.probabilityThreshold",
                $"must be between 0 and 1; was {engine.ProbabilityThreshold.ToString(CultureInfo.InvariantCulture)}"));

        if (!ProbabilityModes.Contains((engine.ProbabilityMode ?? "").Trim().ToLowerInvariant()))
            errors.Add(new ConfigError("engine.probabilityMode",
                $"must be both or threshold; was '{engine.ProbabilityMode}'"));
    }

    private static void ValidateDatasource(IndexerConfig config, List<ConfigError> errors)
    {
        var type = (config.Datasource.Type ?? "").Trim().ToLowerInvariant();
        if (!DatasourceTypes.Contains(type))
        {
            errors.Add(new ConfigError("datasource.type",
                $"must be local or daminion; was '{config.Datasource.Type}'"));
            return;
        }

        if (type == "daminion")
        {
            ValidateDaminion(config.Datasource.Daminion, errors);

            // A catalog cannot be watched; catalog work only appears when the
            // service asks. A watch-only Daminion run would never detect anything.
            if ((config.Schedule.Mode ?? "").Trim().ToLowerInvariant() == "watch")
            {
                errors.Add(new ConfigError("schedule.mode",
                    "must be Interval or IntervalAndWatch for a Daminion datasource: a catalog cannot be watched"));
            }
        }

        // For a local run, the watch-path rules in ValidateSchedule are what
        // guarantee the scan has a scope: watch modes require paths, and an
        // interval run may start with none and have its scope configured later.
    }

    private static void ValidateDaminion(DaminionSettings daminion, List<ConfigError> errors)
    {
        if (string.IsNullOrWhiteSpace(daminion.ServerUrl))
        {
            errors.Add(new ConfigError("datasource.daminion.serverUrl", "is required for a Daminion datasource"));
        }
        else if (!Uri.TryCreate(daminion.ServerUrl, UriKind.Absolute, out var uri)
                 || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add(new ConfigError("datasource.daminion.serverUrl",
                $"must be an absolute http(s) URL; was '{daminion.ServerUrl}'"));
        }

        if (string.IsNullOrWhiteSpace(daminion.Username))
            errors.Add(new ConfigError("datasource.daminion.username", "is required for a Daminion datasource"));

        if (string.IsNullOrWhiteSpace(daminion.EncryptedPassword))
            errors.Add(new ConfigError("datasource.daminion.encryptedPassword",
                "is required; run Synapic.Indexer.exe --set-daminion-password as an administrator"));

        if (daminion.CatalogId < 0)
            errors.Add(new ConfigError("datasource.daminion.catalogId",
                $"must be zero or a catalog id; was {daminion.CatalogId}"));

        if (daminion.UntaggedFields.Count == 0)
            errors.Add(new ConfigError("datasource.daminion.untaggedFields",
                "must list the fields that make a record untagged (for example keywords, categories)"));

        if (daminion.UntaggedFields.Any(string.IsNullOrWhiteSpace))
            errors.Add(new ConfigError("datasource.daminion.untaggedFields", "must not contain empty field names"));

        // This build has no saved-search/collection id fields, so those scopes
        // would silently query the whole catalog.
        var scope = (daminion.Scope ?? "").Trim().ToLowerInvariant();
        if (scope is "saved-search" or "saved_search" or "collection" or "search")
        {
            errors.Add(new ConfigError("datasource.daminion.scope",
                $"must be 'all' or 'catalog' in this build; '{daminion.Scope}' needs an id the config cannot hold yet"));
        }
    }

    private static void ValidateTagging(TaggingSettings tagging, List<ConfigError> errors)
    {
        if (!tagging.WriteCategory && !tagging.WriteKeywords && !tagging.WriteDescription)
            errors.Add(new ConfigError("tagging",
                "at least one of writeCategory, writeKeywords or writeDescription must be true"));
    }

    private static bool IsLogLevel(string level) =>
        level.Trim().ToLowerInvariant() is "verbose" or "debug" or "information" or "warning" or "error" or "fatal";

    /// <summary>
    /// Convenience for the host: normalize the device through the same whitelist
    /// Core launches with, so a stray case ("CUDA") is accepted rather than
    /// rejected by a second, divergent rule.
    /// </summary>
    public static string NormalizeDevice(string? device) => InferenceSidecarService.NormalizeDevice(device);
}

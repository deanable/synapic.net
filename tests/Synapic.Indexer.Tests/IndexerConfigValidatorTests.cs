using Synapic.Indexer.Configuration;
using Xunit;

namespace Synapic.Indexer.Tests;

public class IndexerConfigValidatorTests
{
    private static IndexerConfig LocalInterval()
    {
        var config = IndexerConfig.CreateDefault();
        config.Engine.ModelId = "LiquidAI/LFM2.5-VL-1.6B";
        config.Schedule.Mode = "Interval";
        return config;
    }

    private static IndexerConfig Daminion()
    {
        var config = IndexerConfig.CreateDefault();
        config.Engine.ModelId = "LiquidAI/LFM2.5-VL-1.6B";
        config.Datasource.Type = "daminion";
        config.Datasource.Daminion.ServerUrl = "http://daminion-server:8080";
        config.Datasource.Daminion.CatalogId = 1;
        config.Datasource.Daminion.Username = "indexer-bot";
        config.Datasource.Daminion.EncryptedPassword = "AQAAANCMnd8BFdERjHoB";
        config.Datasource.Daminion.UntaggedFields = new List<string> { "keywords", "categories" };
        return config;
    }

    [Fact]
    public void The_spec_defaults_with_a_model_are_valid_for_a_local_interval_run()
    {
        Assert.Empty(IndexerConfigValidator.Validate(LocalInterval()));
    }

    [Fact]
    public void A_complete_daminion_config_is_valid()
    {
        Assert.Empty(IndexerConfigValidator.Validate(Daminion()));
    }

    public static IEnumerable<object[]> InvalidCases()
    {
        yield return Case("version", c => c.Version = 2);
        yield return Case("service.logLevel", c => c.Service.LogLevel = "Chatty");
        yield return Case("service.maxConcurrentItems", c => c.Service.MaxConcurrentItems = 33);
        yield return Case("service.maxConcurrentItems", c => c.Service.MaxConcurrentItems = 0);
        yield return Case("service.idleSidecarTimeoutMinutes", c => c.Service.IdleSidecarTimeoutMinutes = -1);
        yield return Case("service.idleSidecarTimeoutMinutes", c => c.Service.IdleSidecarTimeoutMinutes = 1441);
        yield return Case("schedule.mode", c => c.Schedule.Mode = "Hourly");
        yield return Case("schedule.intervalMinutes", c => c.Schedule.IntervalMinutes = 0);
        yield return Case("schedule.intervalMinutes", c => c.Schedule.IntervalMinutes = 1441);
        yield return Case("schedule.watchPaths", c => c.Schedule.Mode = "Watch");
        yield return Case("engine.device", c => c.Engine.Device = "gpu");
        yield return Case("engine.modelId", c => c.Engine.ModelId = "  ");
        yield return Case("engine.confidenceThreshold", c => c.Engine.ConfidenceThreshold = 1.5);
        yield return Case("engine.probabilityThreshold", c => c.Engine.ProbabilityThreshold = -0.1);
        yield return Case("engine.probabilityMode", c => c.Engine.ProbabilityMode = "any");
        yield return Case("tagging", c =>
        {
            c.Tagging.WriteCategory = false;
            c.Tagging.WriteKeywords = false;
            c.Tagging.WriteDescription = false;
        });
        yield return Case("datasource.type", c => c.Datasource.Type = "ftp");

        static object[] Case(string path, Action<IndexerConfig> mutate) => new object[] { path, mutate };
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void Each_rule_names_the_offending_setting(string expectedPath, Action<IndexerConfig> mutate)
    {
        var config = LocalInterval();
        mutate(config);

        var errors = IndexerConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Path == expectedPath);
    }

    public static IEnumerable<object[]> WatchPathCases()
    {
        yield return new object[] { "relative\\folder", "schedule.watchPaths[0]", "absolute" };
        yield return new object[]
        {
            Path.Combine(Path.GetTempPath(), "synapic-does-not-exist-" + Guid.NewGuid().ToString("N")),
            "schedule.watchPaths[0]",
            "does not exist",
        };
    }

    [Theory]
    [MemberData(nameof(WatchPathCases))]
    public void Watch_paths_must_be_absolute_and_present(string path, string expectedSetting, string expectedText)
    {
        var config = LocalInterval();
        config.Schedule.Mode = "IntervalAndWatch";
        config.Schedule.WatchPaths = new List<string> { path };

        var errors = IndexerConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Path == expectedSetting && e.Message.Contains(expectedText));
    }

    [Fact]
    public void An_existing_absolute_watch_path_satisfies_watch_mode()
    {
        var config = LocalInterval();
        config.Schedule.Mode = "IntervalAndWatch";
        config.Schedule.WatchPaths = new List<string> { Path.GetTempPath() };

        Assert.Empty(IndexerConfigValidator.Validate(config));
    }

    public static IEnumerable<object[]> DaminionCases()
    {
        yield return DaminionCase("datasource.daminion.serverUrl", c => c.Datasource.Daminion.ServerUrl = "");
        yield return DaminionCase("datasource.daminion.serverUrl", c => c.Datasource.Daminion.ServerUrl = "daminion-server:8080");
        yield return DaminionCase("datasource.daminion.username", c => c.Datasource.Daminion.Username = "");
        yield return DaminionCase("datasource.daminion.encryptedPassword", c => c.Datasource.Daminion.EncryptedPassword = null);
        yield return DaminionCase("datasource.daminion.catalogId", c => c.Datasource.Daminion.CatalogId = -1);
        yield return DaminionCase("datasource.daminion.untaggedFields", c => c.Datasource.Daminion.UntaggedFields.Clear());

        static object[] DaminionCase(string path, Action<IndexerConfig> mutate) => new object[] { path, mutate };
    }

    [Theory]
    [MemberData(nameof(DaminionCases))]
    public void A_daminion_datasource_requires_its_connection_details(string expectedPath, Action<IndexerConfig> mutate)
    {
        var config = Daminion();
        mutate(config);

        var errors = IndexerConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Path == expectedPath);
    }

    [Fact]
    public void Every_problem_is_reported_in_one_pass()
    {
        var config = LocalInterval();
        config.Version = 3;
        config.Service.MaxConcurrentItems = 99;
        config.Engine.Device = "gpu";
        config.Datasource.Type = "local";
        config.Tagging.WriteCategory = false;
        config.Tagging.WriteKeywords = false;
        config.Tagging.WriteDescription = false;

        var errors = IndexerConfigValidator.Validate(config);

        Assert.Equal(4, errors.Count);
        Assert.Contains(errors, e => e.Path == "version");
        Assert.Contains(errors, e => e.Path == "service.maxConcurrentItems");
        Assert.Contains(errors, e => e.Path == "engine.device");
        Assert.Contains(errors, e => e.Path == "tagging");
    }
}

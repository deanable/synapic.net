using Microsoft.Win32;
using Synapic.Indexer.Configuration;
using Xunit;

namespace Synapic.Indexer.Tests;

public sealed class IndexerConfigStoreTests : IDisposable
{
    private readonly string _registryKeyPath;
    private readonly string _dir;
    private readonly string _jsonPath;

    public IndexerConfigStoreTests()
    {
        _registryKeyPath = $@"Software\Synapic\Indexer.Tests\{Guid.NewGuid():N}";
        _dir = Path.Combine(Path.GetTempPath(), "synapic-indexer-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _jsonPath = Path.Combine(_dir, "indexer-config.json");
    }

    public void Dispose()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_registryKeyPath, writable: true);
            key?.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(_registryKeyPath, throwOnMissingSubKey: false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A test-only key that could not be removed is harmless.
        }

        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private IndexerConfigStore Store() => new(_registryKeyPath, _jsonPath);

    private void SetRegistry(string name, object value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_registryKeyPath, writable: true);
        key.SetValue(name, value);
    }

    private void WriteJson(string json)
    {
        File.WriteAllText(_jsonPath, json);
    }

    [Fact]
    public void Neither_source_means_the_spec_defaults()
    {
        var result = Store().Load();

        Assert.Empty(result.Errors);
        Assert.Empty(result.Sources);
        Assert.Equal(1, result.Config.Version);
        Assert.Equal(4, result.Config.Service.MaxConcurrentItems);
        Assert.Equal(30, result.Config.Service.IdleSidecarTimeoutMinutes);
        Assert.Equal(15, result.Config.Schedule.IntervalMinutes);
        Assert.Equal("IntervalAndWatch", result.Config.Schedule.Mode);
        Assert.Equal("cpu", result.Config.Engine.Device);
        Assert.Equal("local", result.Config.Datasource.Type);
        Assert.True(result.Config.Tagging.WriteKeywords);
        Assert.False(result.Config.Tagging.OverwriteExistingTags);
    }

    [Fact]
    public void The_json_file_overrides_the_registry_one_value_at_a_time()
    {
        SetRegistry("service.maxConcurrentItems", 8);
        SetRegistry("service.logLevel", "Warning");
        SetRegistry("schedule.watchPaths", new[] { Path.GetTempPath() });
        WriteJson("""
        {
          "service": { "maxConcurrentItems": 16 },
          "engine": { "modelId": "LiquidAI/LFM2.5-VL-1.6B", "device": "cuda" }
        }
        """);

        var result = Store().Load();

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Sources.Count);
        // Overridden by the file…
        Assert.Equal(16, result.Config.Service.MaxConcurrentItems);
        // …while the registry's other values survive the overlay.
        Assert.Equal("Warning", result.Config.Service.LogLevel);
        Assert.Single(result.Config.Schedule.WatchPaths);
        Assert.Equal(Path.GetTempPath(), result.Config.Schedule.WatchPaths[0]);
        // Values only the file carries.
        Assert.Equal("cuda", result.Config.Engine.Device);
        Assert.Equal("LiquidAI/LFM2.5-VL-1.6B", result.Config.Engine.ModelId);
    }

    [Fact]
    public void A_registry_value_with_the_wrong_type_is_named_and_skipped()
    {
        SetRegistry("schedule.intervalMinutes", "soon");

        var result = Store().Load();

        var error = Assert.Single(result.Errors);
        Assert.Contains("schedule.intervalMinutes", error.Path);
        Assert.Contains("whole number", error.Message);
        // The rest of the config still loaded.
        Assert.Equal(15, result.Config.Schedule.IntervalMinutes);
    }

    [Fact]
    public void A_malformed_file_is_an_error_naming_the_file_not_an_exception()
    {
        WriteJson("{ \"service\": { \"logLevel\": ");

        var result = Store().Load();

        var error = Assert.Single(result.Errors);
        Assert.Equal(_jsonPath, error.Path);
        Assert.Contains("not valid JSON", error.Message);
    }

    [Fact]
    public void An_unknown_setting_is_reported_as_a_warning_without_blocking_startup()
    {
        // A future field must not stop an older build (SaveDaminionPassword
        // preserves members this build does not know), but a typo still has to be
        // visible rather than silently dropped.
        WriteJson("""
        { "service": { "logLevle": "Warning" } }
        """);

        var result = Store().Load();

        Assert.True(result.IsValid);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("service.logLevle", warning.Path);
        Assert.Contains("not a recognized setting", warning.Message);
        Assert.Equal("Information", result.Config.Service.LogLevel);
    }

    [Fact]
    public void A_json_list_of_paths_and_a_registry_multi_string_both_read()
    {
        WriteJson("""
        { "schedule": { "watchPaths": ["C:\\Media\\Incoming", "D:\\Photos"] } }
        """);

        var fromJson = Store().Load();
        Assert.Equal(new[] { @"C:\Media\Incoming", @"D:\Photos" }, fromJson.Config.Schedule.WatchPaths);

        File.Delete(_jsonPath);
        SetRegistry("schedule.watchPaths", new[] { @"E:\One", @"E:\Two" });
        var fromRegistry = Store().Load();
        Assert.Equal(new[] { @"E:\One", @"E:\Two" }, fromRegistry.Config.Schedule.WatchPaths);
    }

    [Fact]
    public void Saving_the_password_preserves_members_this_build_does_not_know()
    {
        WriteJson("""
        {
          "version": 1,
          "service": { "logLevel": "Warning" },
          "datasource": { "type": "daminion", "daminion": { "username": "indexer-bot" } },
          "futureSection": { "kept": true }
        }
        """);

        Store().SaveDaminionPassword("ciphertext-here");

        var text = File.ReadAllText(_jsonPath);
        Assert.Contains("futureSection", text);
        Assert.Contains("indexer-bot", text);
        Assert.Contains("ciphertext-here", text);

        var reloaded = Store().Load();
        Assert.True(reloaded.IsValid);
        Assert.Equal("ciphertext-here", reloaded.Config.Datasource.Daminion.EncryptedPassword);
        Assert.Equal("Warning", reloaded.Config.Service.LogLevel);
    }

    [Fact]
    public void Saving_the_password_creates_the_file_when_absent()
    {
        Store().SaveDaminionPassword("cipher-only");

        Assert.True(File.Exists(_jsonPath));
        Assert.Equal("cipher-only", Store().Load().Config.Datasource.Daminion.EncryptedPassword);
    }

    [Fact]
    public void Dpapi_round_trips_a_password_under_the_machine_scope()
    {
        const string secret = "correct horse battery staple";

        Assert.True(DpapiCredentialProtector.TryProtect(secret, out var cipher, out var protectError));
        Assert.Null(protectError);
        Assert.NotNull(cipher);
        Assert.NotEqual(secret, cipher);
        Assert.DoesNotContain(secret, cipher!);

        Assert.True(DpapiCredentialProtector.TryUnprotect(cipher!, out var back, out var unprotectError));
        Assert.Null(unprotectError);
        Assert.True(back == secret);
    }

    [Fact]
    public void Unprotecting_a_non_ciphertext_fails_with_a_reason_and_no_exception()
    {
        Assert.False(DpapiCredentialProtector.TryUnprotect("not base64!", out var plaintext, out var error));
        Assert.Null(plaintext);
        Assert.NotNull(error);
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Synapic.Indexer.Configuration;
using Synapic.Indexer.Contract.Configuration;
using Synapic.Indexer.Install;
using Xunit;

namespace Synapic.Indexer.Tests;

/// <summary>
/// <c>--apply-config</c>: the Service tab's one elevated write path. What must
/// hold: the patch merges over the file (unknown members survive), the merged
/// result is validated before anything is saved, a refusal leaves the file
/// byte-identical, and a running service is restarted while a stopped one is
/// left alone. No test touches the real SCM or %ProgramData%.
/// </summary>
public sealed class ApplyConfigCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "synapic-apply-config-" + Guid.NewGuid().ToString("N"));

    public ApplyConfigCommandTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string ConfigPath => Path.Combine(_root, "indexer-config.json");
    private string PatchPath => Path.Combine(_root, "patch.json");

    private IndexerConfigStore Store() => new(configFilePathOverride: ConfigPath);

    private void WriteConfig(string json) => File.WriteAllText(ConfigPath, json);
    private void WritePatch(string json) => File.WriteAllText(PatchPath, json);
    private void WritePatch(JsonObject patch) =>
        File.WriteAllText(PatchPath, patch.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    // The validator refuses a config with no model, watch paths that do not
    // exist on disk, and a watch mode with nothing to watch, so the valid
    // fixtures carry what the real ones would.
    private string WatchFolder => Directory.CreateDirectory(Path.Combine(_root, "watch")).FullName;
    private string ValidConfig => $$"""
        { "version": 1, "engine": { "modelId": "LiquidAI/LFM2.5-VL-1.6B" },
          "schedule": { "mode": "Interval", "intervalMinutes": 15 }, "future": { "unknown": true } }
        """;

    private sealed class FakeSc : IScRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();
        public Func<IReadOnlyList<string>, ScResult> Respond { get; set; } = _ => new(0, "", "");

        public Task<ScResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            Calls.Add(arguments.ToArray());
            return Task.FromResult(Respond(arguments));
        }
    }

    private static ScResult Running => new(0, "STATE : 4 RUNNING", "");
    private static ScResult Stopped => new(0, "STATE : 1 STOPPED", "");
    private static ScResult NotInstalled => new(1060, "not installed", "");

    [Fact]
    public void The_parser_takes_the_patch_path_and_keeps_it_out_of_the_modes()
    {
        Assert.True(ServiceCommandLine.TryParse(
            new[] { "--apply-config", @"C:\Temp\patch.json" }, out var invocation, out var error));
        Assert.Null(error);
        Assert.Equal(ServiceMode.ApplyConfig, invocation.Mode);
        Assert.Equal(@"C:\Temp\patch.json", invocation.ConfigPath);

        Assert.False(ServiceCommandLine.TryParse(new[] { "--apply-config" }, out _, out var missing));
        Assert.Contains("needs the path", missing);

        Assert.False(ServiceCommandLine.TryParse(
            new[] { "--apply-config", "a.json", "--status" }, out _, out var two));
        Assert.Contains("Only one mode", two);
    }

    [Fact]
    public async Task A_patch_merges_over_the_file_and_preserves_members_this_build_does_not_know()
    {
        WriteConfig(ValidConfig);
        WritePatch(new JsonObject
        {
            ["schedule"] = new JsonObject
            {
                ["mode"] = "Watch",
                ["watchPaths"] = new JsonArray(WatchFolder),
                ["watchRecursive"] = false,
            },
        });
        var sc = new FakeSc { Respond = _ => Stopped };

        var exit = await ApplyConfigCommand.Run(Store(), new ServiceInstaller(sc, (_, _) => { }), PatchPath, Console.Out, Console.Error);

        Assert.Equal(0, exit);
        var saved = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
        Assert.Equal("Watch", saved["schedule"]!["mode"]!.GetValue<string>());
        Assert.Equal(15, (int)saved["schedule"]!["intervalMinutes"]!);
        Assert.Equal(WatchFolder, saved["schedule"]!["watchPaths"]![0]!.GetValue<string>());
        Assert.False(saved["schedule"]!["watchRecursive"]!.GetValue<bool>());
        Assert.True(saved["future"]!["unknown"]!.GetValue<bool>(), "an unknown member must survive the merge");
    }

    [Fact]
    public async Task An_invalid_merged_result_is_refused_and_the_file_stays_byte_identical()
    {
        WriteConfig("""{ "schedule": { "intervalMinutes": 15 } }""");
        WritePatch("""{ "schedule": { "intervalMinutes": 0 } }""");
        var before = File.ReadAllText(ConfigPath);
        var sc = new FakeSc();

        var error = new StringWriter();
        var exit = await ApplyConfigCommand.Run(Store(), new ServiceInstaller(sc, (_, _) => { }), PatchPath, Console.Out, error);

        Assert.Equal(2, exit);
        Assert.Contains("unchanged", error.ToString());
        Assert.Equal(before, File.ReadAllText(ConfigPath));
        Assert.Empty(sc.Calls);
    }

    [Fact]
    public async Task A_running_service_is_restarted_and_a_stopped_one_is_not()
    {
        WriteConfig(ValidConfig);
        WritePatch(new JsonObject { ["schedule"] = new JsonObject { ["watchRecursive"] = true } });

        var errors1 = new StringWriter();
        var queries = 0;
        var running = new FakeSc
        {
            Respond = a =>
            {
                if (a[0] != "query") return new(0, "", "");
                // First query is the "is it running" check; after sc stop the
                // restart loop polls until the service reports STOPPED.
                queries++;
                return queries == 1 ? Running : Stopped;
            },
        };
        var exitRunning = await ApplyConfigCommand.Run(Store(), new ServiceInstaller(running, (_, _) => { }), PatchPath, Console.Out, errors1);
        Assert.True(exitRunning == 0, errors1.ToString());
        Assert.Equal(new[] { "query", "stop", "query", "start" }, running.Calls.Select(a => a[0]).ToArray());

        var errors2 = new StringWriter();
        var stopped = new FakeSc { Respond = _ => Stopped };
        var exitStopped = await ApplyConfigCommand.Run(Store(), new ServiceInstaller(stopped, (_, _) => { }), PatchPath, Console.Out, errors2);
        Assert.True(exitStopped == 0, errors2.ToString());
        Assert.Equal(new[] { "query" }, stopped.Calls.Select(a => a[0]).ToArray());
    }

    [Fact]
    public async Task A_missing_service_is_reported_not_restarted_and_the_config_still_applies()
    {
        WriteConfig(ValidConfig);
        WritePatch(new JsonObject { ["schedule"] = new JsonObject { ["watchRecursive"] = true } });
        var sc = new FakeSc { Respond = _ => NotInstalled };

        var output = new StringWriter();
        var errors3 = new StringWriter();
        var exit = await ApplyConfigCommand.Run(Store(), new ServiceInstaller(sc, (_, _) => { }), PatchPath, output, errors3);

        Assert.True(exit == 0, errors3.ToString());
        Assert.Contains("was not running", output.ToString());
        Assert.True(File.Exists(ConfigPath), "the config applies even with no service installed yet");
    }

    [Fact]
    public async Task An_unreadable_patch_fails_without_touching_the_config()
    {
        WriteConfig("""{ "version": 1 }""");
        var before = File.ReadAllText(ConfigPath);
        var sc = new FakeSc();

        var exit = await ApplyConfigCommand.Run(
            Store(), new ServiceInstaller(sc, (_, _) => { }),
            Path.Combine(_root, "does-not-exist.json"), Console.Out, Console.Error);

        Assert.Equal(1, exit);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
    }
}

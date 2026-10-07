using System.IO;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.ViewModels;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// Pins ConfigService.Save's document-merge promise (codebase-guide §7):
/// unknown top-level/nested keys survive every save, known sections are
/// replaced, and the write is atomic (temp file + move, no .tmp left behind).
/// </summary>
public class ConfigServicePersistenceTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"synapic_cfg_{Guid.NewGuid():N}.json");

    [Fact]
    public void Save_preserves_unknown_top_level_and_nested_keys()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path,
                """
                {
                  "Version": 2,
                  "FutureTopLevel": {"a": 1},
                  "Engine": {
                    "ModelId": "old/model",
                    "Device": "cuda",
                    "HandEditedField": "keep me"
                  }
                }
                """);

            var service = new ConfigService(path);
            var config = service.Load();
            config.Engine.ModelId = "new/model";
            service.Save(config);

            var raw = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

            Assert.Equal("new/model", raw["Engine"]!.AsObject()["ModelId"]!.ToString());
            Assert.Equal("keep me", raw["Engine"]!.AsObject()["HandEditedField"]!.ToString());
            Assert.NotNull(raw["FutureTopLevel"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_leaves_no_temp_file_and_round_trips_typed_values()
    {
        var path = TempPath();
        try
        {
            var service = new ConfigService(path);
            var config = service.Load();
            config.Processing.MaxItems = 42;
            config.Ui.Theme = "dark";
            service.Save(config);

            Assert.False(File.Exists(path + ".tmp"));
            var reloaded = new ConfigService(path).Load();
            Assert.Equal(42, reloaded.Processing.MaxItems);
            Assert.Equal("dark", reloaded.Ui.Theme);
            Assert.Equal(2, reloaded.Version);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_on_corrupt_file_returns_defaults_instead_of_throwing()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{corrupt json");
            Assert.Equal(2, new ConfigService(path).Load().Version);
        }
        finally
        {
            File.Delete(path);
        }
    }
    /// <summary>
    /// The start screen's source survives a restart: the folder and the source
    /// type are persisted into config.json and come back with the session, which
    /// is what lets the launch sequence gate the workflows and count the folder
    /// before the user touches anything.
    /// </summary>
    [AvaloniaFact]
    public void Persisted_source_comes_back_for_the_next_launch()
    {
        var path = TempPath();
        try
        {
            var folder = Path.GetTempPath();
            var session = new Session();
            session.Datasource.Type = "local";
            session.Datasource.LocalPath = folder;
            session.Datasource.LocalRecursive = true;
            var vm = new MainWindowViewModel(
                new FakeSidecar(), new FakeBuildService(), session,
                () => "x.exe", null, null, _ => null);

            vm.PersistConfig(new ConfigService(path));

            var next = new Session();
            next.Datasource.ApplyStoredSource(new ConfigService(path).Load().Datasource);

            Assert.Equal("local", next.Datasource.Type);
            Assert.Equal(folder, next.Datasource.LocalPath);
            Assert.True(next.Datasource.LocalRecursive);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Regression: PersistConfig used to resolve ConfigService from DI where it
    /// was never registered, silently writing nothing. With the service passed
    /// in, the wizard's merged snapshot must really land on disk.
    /// </summary>
    [AvaloniaFact]
    public void PersistConfig_writes_the_merged_snapshot_to_disk()
    {
        var path = TempPath();
        try
        {
            var session = new Session();
            session.Engine.ModelId = "test/model";
            var vm = new MainWindowViewModel(
                new FakeSidecar(), new FakeBuildService(), session,
                () => "x.exe", null, null, _ => null);

            vm.PersistConfig(new ConfigService(path));

            var raw = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(path))!.AsObject();
            Assert.Equal("test/model", raw["Engine"]!.AsObject()["ModelId"]!.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }
}

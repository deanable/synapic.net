using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// Regression pin for the config-persistence wiring: MainWindowViewModel
/// resolves ConfigService from the static DI container, and a version that
/// registered only the loaded AppConfig made PersistConfig a silent no-op
/// (config.json was never written). These are the two strings that must not
/// drift apart.
/// </summary>
public class AppConfigDiRegistrationTests
{
    [Fact]
    public void App_registers_the_config_service_it_persist_resolves()
    {
        var appSource = System.IO.Path.Combine(RepoRoot(), "src", "Synapic.Main", "App.axaml.cs");
        var appText = System.IO.File.ReadAllText(appSource);

        Assert.Contains("services.AddSingleton(configService);", appText);
        Assert.Contains("services.AddSingleton(config);", appText);

        var vmSource = System.IO.File.ReadAllText(System.IO.Path.Combine(
            RepoRoot(), "src", "Synapic.Main", "ViewModels", "MainWindowViewModel.cs"));
        Assert.Contains("GetService(typeof(Synapic.Main.Services.ConfigService))", vmSource);
    }

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
            if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Synapic.Net.sln")))
                return dir.FullName;
        throw new System.IO.FileNotFoundException("Repo root containing Synapic.Net.sln not found");
    }
}

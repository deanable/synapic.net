using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Synapic.Avalonia;

[assembly: AvaloniaTestApplication(typeof(Synapic.Avalonia.Tests.TestAppBuilder))]

namespace Synapic.Avalonia.Tests;

/// <summary>
/// Headless Avalonia bootstrap for XUnit: spins up the real <see cref="App"/>
/// so compiled-XAML population runs exactly as it does at desktop startup.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

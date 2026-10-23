using Microsoft.Extensions.DependencyInjection;
using Synapic.Core.Services;
using Synapic.Main.Models;

namespace Synapic.Main.Services;

/// <summary>
/// The desktop's registrations for the engine services that now live in
/// <c>Synapic.Core</c>. One method so the app and the device-selection test
/// cannot wire the sidecar host differently; <see cref="Session"/> must be
/// registered before this runs.
/// </summary>
public static class EngineServiceRegistration
{
    public static IServiceCollection AddSynapicEngine(this IServiceCollection services)
    {
        services.AddSingleton<ISidecarDeviceProvider>(sp =>
            new SessionSidecarDeviceProvider(sp.GetRequiredService<Session>()));
        services.AddSingleton(sp => new SidecarLaunchOptions
        {
            DeviceProvider = sp.GetRequiredService<ISidecarDeviceProvider>(),
        });
        services.AddSingleton<IInferenceSidecar>(sp =>
            new InferenceSidecarService(options: sp.GetRequiredService<SidecarLaunchOptions>()));
        // The metadata writer is stateless and thread-safe; one instance for both
        // the tagging route and anything a future headless host adds here.
        services.AddSingleton<IMetadataWriter, MetadataWriterService>();
        return services;
    }
}

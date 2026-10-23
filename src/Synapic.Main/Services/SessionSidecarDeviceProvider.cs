using Synapic.Core.Services;
using Synapic.Main.Models;

namespace Synapic.Main.Services;

/// <summary>
/// Desktop implementation of the Core device seam: the sidecar launches for
/// the device chosen in Step 2 and persisted in the session.
/// </summary>
public sealed class SessionSidecarDeviceProvider : ISidecarDeviceProvider
{
    private readonly Session _session;

    public SessionSidecarDeviceProvider(Session session) => _session = session;

    public string? DeviceSelection => _session.Engine.Device;
}

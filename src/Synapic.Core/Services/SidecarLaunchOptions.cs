namespace Synapic.Core.Services;

/// <summary>
/// Supplies the compute device the sidecar host should launch for. The desktop
/// implements this over its session (Step 2's device choice); a service host
/// supplies the configured device. Keeping it an interface is what lets
/// <see cref="InferenceSidecarService"/> live outside the UI assembly.
/// </summary>
public interface ISidecarDeviceProvider
{
    /// <summary>Requested device: "cpu", "cuda" or "mps". Anything else normalizes to CPU.</summary>
    string? DeviceSelection { get; }
}

/// <summary>A fixed device provider, for hosts and tests with no mutable session state.</summary>
public sealed class StaticSidecarDeviceProvider : ISidecarDeviceProvider
{
    public StaticSidecarDeviceProvider(string? device = null) => DeviceSelection = device;

    public string? DeviceSelection { get; }
}

/// <summary>
/// Host-supplied sidecar launch inputs. The defaults preserve the desktop's
/// existing behavior exactly: packaged executable first, then the per-user
/// sidecar root, then a dev checkout's artifacts directory, and CPU when no
/// device is supplied.
/// </summary>
public sealed record SidecarLaunchOptions
{
    public ISidecarDeviceProvider? DeviceProvider { get; init; }

    /// <summary>
    /// Explicit executable path (a service's bundled sidecar). Used only when the
    /// file exists; otherwise discovery falls back to the standard locations.
    /// </summary>
    public string? ExecutablePathOverride { get; init; }

    /// <summary>
    /// Whether a sidecar older than the sidecar source may launch. True keeps the
    /// desktop's dev-checkout guard, where a stale binary silently reproduces bugs
    /// that are already fixed. A packaged host ships the executable it was built
    /// with and has no source tree or rebuild action to offer, so it turns this
    /// off explicitly rather than being unable to start.
    /// </summary>
    public bool EnforceSourceStaleness { get; init; } = true;
}

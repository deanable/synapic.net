using Synapic.Core.Services;

namespace Synapic.Indexer.Hosting;

/// <summary>
/// Where the service looks for <c>synapic-inference.exe</c> (D-11-13): beside
/// the service executable first (the installer ships both together), then the
/// desktop's per-user sidecar root and a dev checkout's artifacts. There is no
/// configuration key for this path — a service must not be pointable at an
/// arbitrary binary by a JSON file.
/// </summary>
public sealed record SidecarExecutable(string? Path, IReadOnlyList<string> Searched)
{
    public static SidecarExecutable Locate()
    {
        var beside = System.IO.Path.Combine(AppContext.BaseDirectory, InferenceSidecarService.ExeName);
        var userRoot = InferenceSidecarService.UserSidecarRoot();
        var searched = new List<string>
        {
            beside,
            System.IO.Path.Combine(userRoot, InferenceSidecarService.PreferredRid(), InferenceSidecarService.ExeName),
            userRoot,
        };

        if (File.Exists(beside)) return new SidecarExecutable(beside, searched);

        var discovered = InferenceSidecarService.FindExecutable();
        if (discovered is not null && !searched.Contains(discovered, StringComparer.OrdinalIgnoreCase))
            searched.Add(discovered);

        return new SidecarExecutable(discovered, searched);
    }

    /// <summary>The Fatal message for a missing sidecar: every place that was tried, in order.</summary>
    public static string MissingMessage(IReadOnlyList<string> searched) =>
        "synapic-inference.exe was not found. Searched: " + string.Join("; ", searched) +
        ". Reinstall Synapic, or place the sidecar beside Synapic.Indexer.exe.";
}

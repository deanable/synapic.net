using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Synapic.Main.Services;

/// <summary>How the startup runtime check ended.</summary>
public enum RuntimeCheckOutcome
{
    /// <summary>Not Windows: the platform's package manager owns the dependency.</summary>
    NotApplicable,

    /// <summary>The required runtime (or newer) was already installed.</summary>
    RuntimePresent,

    /// <summary>The runtime was missing and the silent install put a usable one in place.</summary>
    Installed,

    /// <summary>The runtime is missing and no installer could be fetched (offline, proxy, or no win-x64 asset).</summary>
    DownloadFailed,

    /// <summary>The runtime is missing and running the installer did not produce a usable runtime.</summary>
    InstallFailed,
}

/// <summary>What <see cref="DotNetRuntimeCheckService.EnsureRuntimeAsync"/> found and did.</summary>
/// <param name="Outcome">The result of the check.</param>
/// <param name="InstalledVersion">
/// The desktop runtime version that was seen, if any - it can be older than the
/// minimum, which is exactly why <see cref="NeedsManualInstall"/> is the property
/// to test.
/// </param>
/// <param name="DownloadUrl">The official download page for the required .NET major version.</param>
/// <param name="InstallerUrl">The exact installer that was downloaded and run, when there was one.</param>
public sealed record RuntimeCheckResult(
    RuntimeCheckOutcome Outcome,
    Version? InstalledVersion,
    string DownloadUrl,
    string? InstallerUrl)
{
    /// <summary>
    /// True when the runtime is still unusable, so the user has to install it
    /// themselves from <see cref="DownloadUrl"/>. This is the case the app has to
    /// carry the link for: an installer that refuses to run never gets to explain
    /// itself, and a silent install that fails has no UI at all.
    /// </summary>
    public bool NeedsManualInstall => Outcome
        is RuntimeCheckOutcome.DownloadFailed or RuntimeCheckOutcome.InstallFailed;
}

/// <summary>
/// Startup guard (Windows only): verifies the .NET 10 Desktop Runtime is
/// present and, when it is missing, downloads the official installer from the
/// dotnet release metadata and runs it silently (/install /quiet /norestart)
/// before the app continues. Never blocks or crashes startup - on any failure
/// the app keeps running, the reason is recorded in the log, and the official
/// download link is offered through <see cref="RuntimeUnavailable"/> so the user
/// is never left with an app that will not start and nowhere to go.
/// </summary>
public sealed class DotNetRuntimeCheckService
{
    private const string MetadataUrl =
        "https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json";

    /// <summary>Oldest desktop runtime that satisfies the app (any 10.x ≥ this passes).</summary>
    public static Version MinimumVersion { get; } = new(10, 0, 0);

    /// <summary>The framework whose presence this check is really about.</summary>
    internal const string FrameworkName = "Microsoft.WindowsDesktop.App";

    /// <summary>
    /// The page a user should be sent to when the runtime cannot be installed
    /// for them. Derived from <see cref="MinimumVersion"/>, so a target-framework
    /// bump moves the link with the requirement, and it is the same page the
    /// MSI's refusal message names.
    /// </summary>
    public static string DownloadPageUrl { get; } =
        $"https://dotnet.microsoft.com/download/dotnet/{MinimumVersion.Major}.{MinimumVersion.Minor}";

    private readonly HttpClient _http;
    private readonly Func<Version?> _installedVersion;
    private readonly bool _isWindows;

    /// <param name="httpClient">Used to fetch the release metadata and the installer; injectable for tests.</param>
    /// <param name="installedVersionProbe">
    /// How the installed runtime is detected; defaults to this machine. Injectable
    /// so the missing-runtime path can be exercised anywhere, without having to
    /// uninstall a framework to test the code that offers the link.
    /// </param>
    /// <param name="isWindows">Platform override for tests; defaults to the real platform.</param>
    public DotNetRuntimeCheckService(
        HttpClient? httpClient = null,
        Func<Version?>? installedVersionProbe = null,
        bool? isWindows = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _installedVersion = installedVersionProbe ?? GetInstalledDesktopRuntimeVersion;
        _isWindows = isWindows ?? OperatingSystem.IsWindows();
    }

    /// <summary>
    /// Raised when the runtime is missing and the silent install did not fix it,
    /// carrying the link the user needs (see
    /// <see cref="RuntimeCheckResult.DownloadUrl"/>). Raised on whichever thread
    /// the check is running on - the UI subscription hops to the UI thread - and
    /// fired once per failed path; a service with no subscriber still logs the link.
    /// </summary>
    public event Action<RuntimeCheckResult>? RuntimeUnavailable;

    /// <summary>
    /// Runs the check-and-silent-install flow. Safe to fire-and-forget: it
    /// returns the outcome instead of throwing, and offers the download link
    /// when the runtime is still missing at the end.
    /// </summary>
    public async Task<RuntimeCheckResult> EnsureRuntimeAsync(Action<string> log, CancellationToken ct = default)
    {
        if (!_isWindows)
        {
            log("Non-Windows platform — runtime check skipped (package manager handles dependencies)");
            return new RuntimeCheckResult(RuntimeCheckOutcome.NotApplicable, null, DownloadPageUrl, null);
        }

        var installed = _installedVersion();
        if (Satisfies(installed))
        {
            log($".NET Desktop Runtime {installed} detected — no install needed");
            return new RuntimeCheckResult(RuntimeCheckOutcome.RuntimePresent, installed, DownloadPageUrl, null);
        }

        // A runtime that is present but too old is as unusable as a missing one,
        // and it is the case where being told where to go matters most.
        log(installed is null
            ? $".NET Desktop Runtime {MinimumVersion} not found — starting silent install"
            : $".NET Desktop Runtime {installed} is older than {MinimumVersion} — starting silent install");

        var installer = await DownloadInstallerAsync(log, ct).ConfigureAwait(false);
        if (installer is null)
        {
            log("No installer could be located — continuing without installing");
            return OfferManualInstall(RuntimeCheckOutcome.DownloadFailed, installed, log);
        }

        var ok = await RunSilentInstallAsync(installer.Path, log, ct).ConfigureAwait(false);
        if (!ok)
        {
            log("Silent install did not succeed (missing elevation?) — continuing anyway");
            return OfferManualInstall(RuntimeCheckOutcome.InstallFailed, installed, log, installer.Url);
        }

        installed = _installedVersion();
        if (Satisfies(installed))
        {
            log($".NET Desktop Runtime {installed} installed successfully");
            return new RuntimeCheckResult(RuntimeCheckOutcome.Installed, installed, DownloadPageUrl, installer.Url);
        }

        log("Installer finished but the runtime is still not registered — continuing anyway");
        return OfferManualInstall(RuntimeCheckOutcome.InstallFailed, installed, log, installer.Url);
    }

    /// <summary>
    /// The runtime is unusable and automatic repair did not work, so hand over
    /// the link: logs it (the in-app log panel is always there, even if no dialog
    /// can be shown) and raises <see cref="RuntimeUnavailable"/> for the UI.
    /// </summary>
    private RuntimeCheckResult OfferManualInstall(
        RuntimeCheckOutcome outcome,
        Version? installed,
        Action<string> log,
        string? installerUrl = null)
    {
        log($"Install the .NET {MinimumVersion.Major} Desktop Runtime from {DownloadPageUrl} and restart Synapic");
        var result = new RuntimeCheckResult(outcome, installed, DownloadPageUrl, installerUrl);
        RuntimeUnavailable?.Invoke(result);
        return result;
    }

    /// <summary>
    /// Highest installed Microsoft.WindowsDesktop.App version, read from the
    /// runtime's own layout on disk with `dotnet --list-runtimes` as the
    /// fallback (for a portable or DOTNET_ROOT install the folder scan misses).
    ///
    /// Deliberately not from the registry. The documented key,
    /// <c>HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost</c>, holds
    /// the *host* version rather than the framework's, and the two drift: a
    /// machine with the 10.0.12 desktop runtime and no 10 host reports 9.0.20
    /// there. Accepting that as proof of the framework is how a machine with no
    /// desktop runtime was told "no install needed" and never saw the link.
    /// </summary>
    public Version? GetInstalledDesktopRuntimeVersion()
    {
        var fromDisk = ReadDiskRuntimeVersion();
        var fromCli = ReadCliRuntimeVersion();

        // Highest known, even when below the minimum, so the caller can log what
        // it saw; null means "no desktop runtime at all".
        if (fromDisk is null) return fromCli;
        if (fromCli is null) return fromDisk;
        return fromDisk > fromCli ? fromDisk : fromCli;
    }

    /// <summary>True when the version satisfies the app's minimum.</summary>
    public static bool Satisfies(Version? version) => version is not null && version >= MinimumVersion;

    private static Version? ReadDiskRuntimeVersion()
    {
        // The real platform, not the injected one: a test that pretends to be
        // Windows must not go looking for Program Files here.
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var roots = new List<string>();
            var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrWhiteSpace(dotnetRoot)) roots.Add(dotnetRoot);
            roots.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));

            return HighestInstalledVersion(
                roots.Select(root => Path.Combine(root, "shared", FrameworkName)));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Highest version among the given framework directories, each of which
    /// holds one subdirectory per installed version - the layout
    /// <c>dotnet</c> itself uses. Split out from the lookup above so the
    /// comparison can be tested against real folders the test creates, rather
    /// than whatever the test machine happens to have installed.
    /// </summary>
    internal static Version? HighestInstalledVersion(IEnumerable<string> frameworkDirectories)
    {
        Version? best = null;
        foreach (var directory in frameworkDirectories)
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var versionDirectory in Directory.EnumerateDirectories(directory))
            {
                if (Version.TryParse(Path.GetFileName(versionDirectory), out var version)
                    && (best is null || version > best))
                {
                    best = version;
                }
            }
        }
        return best;
    }

    private static Version? ReadCliRuntimeVersion()
    {
        try
        {
            var psi = new ProcessStartInfo("dotnet", "--list-runtimes")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);

            Version? best = null;
            foreach (var line in output.Split('\n'))
            {
                // Format: "Microsoft.WindowsDesktop.App 10.0.12 [C:\...]"
                var parts = line.Trim().Split(' ', 3);
                if (parts.Length >= 2 && parts[0].StartsWith("Microsoft.WindowsDesktop.App")
                    && Version.TryParse(parts[1], out var v)
                    && (best is null || v > best))
                {
                    best = v;
                }
            }
            return best;
        }
        catch
        {
            return null; // dotnet may not exist at all — that is the case we fix
        }
    }

    /// <summary>An installer that was fetched to disk, and where it came from.</summary>
    private sealed record DownloadedInstaller(string Path, string Url, string? Version);

    /// <summary>
    /// Resolve the latest .NET 10 Desktop Runtime win-x64 installer URL from
    /// the official release metadata and download it to %TEMP%.
    /// </summary>
    private async Task<DownloadedInstaller?> DownloadInstallerAsync(Action<string> log, CancellationToken ct)
    {
        try
        {
            log("Fetching .NET 10 release metadata…");
            using var stream = await _http.GetStreamAsync(MetadataUrl, ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            string? url = null;
            string? version = null;
            foreach (var release in doc.RootElement.GetProperty("releases").EnumerateArray())
            {
                if (!release.TryGetProperty("windowsdesktop", out var desktop)) continue;
                version = desktop.GetProperty("version").GetString();
                foreach (var file in desktop.GetProperty("files").EnumerateArray())
                {
                    var rid = file.GetProperty("rid").GetString();
                    var name = file.GetProperty("name").GetString();
                    if (rid == "win-x64" && name is not null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        url = file.GetProperty("url").GetString();
                        break;
                    }
                }
                if (url is not null) break;
            }

            if (url is null)
            {
                log("Release metadata contained no win-x64 desktop runtime installer");
                return null;
            }

            log($"Downloading .NET Desktop Runtime {version} ({url})…");
            var target = Path.Combine(Path.GetTempPath(), $"synapic-{Path.GetFileName(url)}");
            await using (var remote = await _http.GetStreamAsync(url, ct).ConfigureAwait(false))
            await using (var local = File.Create(target))
            {
                await remote.CopyToAsync(local, ct).ConfigureAwait(false);
            }
            log($"Installer saved: {target}");
            return new DownloadedInstaller(target, url, version);
        }
        catch (Exception e)
        {
            log($"Could not download the runtime installer: {e.Message}");
            return null;
        }
    }

    private static async Task<bool> RunSilentInstallAsync(string installer, Action<string> log, CancellationToken ct)
    {
        try
        {
            // Standard .NET installer silent switches; 0 = success, 3010 = success, reboot required.
            var psi = new ProcessStartInfo(installer, "/install /quiet /norestart")
            {
                UseShellExecute = true, // elevation via UAC if needed
                CreateNoWindow = true,
            };
            log("Running silent install (this can take a few minutes)…");
            using var process = Process.Start(psi);
            if (process is null) return false;
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var code = process.ExitCode;
            var ok = code is 0 or 3010;
            log(ok
                ? $"Installer exited with {code} ({(code == 3010 ? "success, reboot recommended" : "success")})"
                : $"Installer exited with {code}");
            return ok;
        }
        catch (Exception e)
        {
            log($"Silent install failed: {e.Message}");
            return false;
        }
    }
}

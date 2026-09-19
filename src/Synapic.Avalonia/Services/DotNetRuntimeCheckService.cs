using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Synapic.Avalonia.Services;

/// <summary>
/// Startup guard (Windows only): verifies the .NET 10 Desktop Runtime is
/// present and, when it is missing, downloads the official installer from the
/// dotnet release metadata and runs it silently (/install /quiet /norestart)
/// before the app continues. Never blocks or crashes startup — on any failure
/// the app keeps running and the reason is recorded in the log.
/// </summary>
public sealed class DotNetRuntimeCheckService
{
    private const string MetadataUrl =
        "https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json";

    /// <summary>Oldest desktop runtime that satisfies the app (any 10.x ≥ this passes).</summary>
    public static Version MinimumVersion { get; } = new(10, 0, 0);

    private readonly HttpClient _http;

    public DotNetRuntimeCheckService(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    /// <summary>Runs the check-and-silent-install flow. Safe to fire-and-forget.</summary>
    public async Task EnsureRuntimeAsync(Action<string> log, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            log("Non-Windows platform — runtime check skipped (package manager handles dependencies)");
            return;
        }

        var installed = GetInstalledDesktopRuntimeVersion();
        if (installed is not null)
        {
            log($".NET Desktop Runtime {installed} detected — no install needed");
            return;
        }

        log($".NET Desktop Runtime {MinimumVersion} not found — starting silent install");
        var installer = await DownloadInstallerAsync(log, ct).ConfigureAwait(false);
        if (installer is null)
        {
            log("No installer could be located — continuing without installing");
            return;
        }

        var ok = await RunSilentInstallAsync(installer, log, ct).ConfigureAwait(false);
        if (!ok)
        {
            log("Silent install did not succeed (missing elevation?) — continuing anyway");
            return;
        }

        installed = GetInstalledDesktopRuntimeVersion();
        log(installed is not null
            ? $".NET Desktop Runtime {installed} installed successfully"
            : "Installer finished but the runtime is still not registered — continuing anyway");
    }

    /// <summary>
    /// Installed Microsoft.WindowsDesktop.App version per the documented
    /// registry location, with a `dotnet --list-runtimes` fallback for when
    /// the registry key is absent (portable/zip installs).
    /// </summary>
    public Version? GetInstalledDesktopRuntimeVersion()
    {
        var fromRegistry = ReadRegistryRuntimeVersion();
        if (fromRegistry is not null && Satisfies(fromRegistry))
            return fromRegistry;

        var fromCli = ReadCliRuntimeVersion();
        if (fromCli is not null && Satisfies(fromCli))
            return fromCli;

        // Return the highest known version even when below the minimum so the
        // caller can log what it saw; null means "no desktop runtime at all".
        return fromRegistry ?? fromCli;
    }

    /// <summary>True when the version satisfies the app's minimum.</summary>
    public static bool Satisfies(Version? version) => version is not null && version >= MinimumVersion;

    private static Version? ReadRegistryRuntimeVersion()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            // Official detection point (docs: "How to check that .NET is already installed"):
            // HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\<arch>\sharedhost → Version (REG_SZ)
            using var baseKey = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                Environment.Is64BitProcess ? RegistryView.Registry64 : RegistryView.Default);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost");
            var value = key?.GetValue("Version") as string;
            return Version.TryParse(value, out var v) ? v : null;
        }
        catch
        {
            return null;
        }
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

    /// <summary>
    /// Resolve the latest .NET 10 Desktop Runtime win-x64 installer URL from
    /// the official release metadata and download it to %TEMP%.
    /// </summary>
    private async Task<string?> DownloadInstallerAsync(Action<string> log, CancellationToken ct)
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
            return target;
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

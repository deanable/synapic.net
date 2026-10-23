using System.Diagnostics;
using Synapic.Indexer.Hosting;

namespace Synapic.Indexer.Install;

/// <summary>
/// Creates <c>%ProgramData%\Synapic</c> with the ACLs the service account needs
/// (spec §5.1, §7.2). The grants use well-known SIDs, not localized account
/// names: "NT AUTHORITY\SYSTEM" is spelled differently on a German machine, but
/// S-1-5-18 is not.
///
/// The rule is deliberately narrow — SYSTEM and LocalService may modify, nobody
/// else is granted anything — because this tree holds the DPAPI-protected
/// Daminion password and the service log.
/// </summary>
public static class ProgramDataSetup
{
    public const string SystemSid = "S-1-5-18";
    public const string LocalServiceSid = "S-1-5-19";

    /// <param name="runIcacls">
    /// Invoked once per grant with the full argument list; tests substitute a
    /// recorder so they can assert the SIDs without touching real ACLs.
    /// </param>
    public static void Ensure(
        IndexerPaths paths,
        Action<IReadOnlyList<string>>? runIcacls = null,
        Action<string>? log = null)
    {
        paths.EnsureCreated();
        log?.Invoke($"Ensured {paths.Root}, {paths.LogsDirectory} and {paths.StateDirectory} exist.");

        var run = runIcacls ?? RunIcacls;
        foreach (var sid in new[] { SystemSid, LocalServiceSid })
        {
            var arguments = BuildGrantArguments(paths.Root, sid);
            run(arguments);
            log?.Invoke($"Granted modify on {paths.Root} to *{sid} (inherited by children).");
        }
    }

    /// <summary>
    /// <c>icacls &lt;root&gt; /grant *&lt;sid&gt;:(OI)(CI)M /T /C</c> — object and
    /// container inherit, modify, applied to the existing tree, continuing past
    /// individual file errors.
    /// </summary>
    public static IReadOnlyList<string> BuildGrantArguments(string root, string sid) => new[]
    {
        root,
        "/grant",
        $"*{sid}:(OI)(CI)M",
        "/T",
        "/C",
    };

    private static void RunIcacls(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "icacls.exe"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        if (process is null) return;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"icacls failed with exit code {process.ExitCode}: {output.Trim()}");
        }
    }
}

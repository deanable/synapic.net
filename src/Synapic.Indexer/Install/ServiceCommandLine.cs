namespace Synapic.Indexer.Install;

/// <summary>What the process was asked to do. No arguments means the SCM started it.</summary>
public enum ServiceMode
{
    Service,
    Install,
    Uninstall,
    Console,
    SetDaminionPassword,
    Status,
    ApplyConfig,
}

public sealed record ServiceInvocation(ServiceMode Mode, bool FailedOnly = false, string? ConfigPath = null);

/// <summary>
/// The control surface's contract (D-11-10, HOST-07). A mistyped flag must fail
/// loudly: silently falling through to "run as a service" is how a typo becomes a
/// service that does nothing and a support call.
/// </summary>
public static class ServiceCommandLine
{
    public const string ServiceName = "SynapicIndexer";
    public const string DisplayName = "Synapic Background Image Indexer";

    /// <summary>
    /// The account the service runs under (spec §7.1): LocalService, the least
    /// privileged built-in service account. sc.exe defaults to LocalSystem when
    /// no obj= is given, which would let the indexer write user folders with
    /// SYSTEM rights; the grant is explicit so the installed account matches the
    /// documented one.
    /// </summary>
    public const string ServiceAccount = @"NT AUTHORITY\LocalService";
    public const int UsageExitCode = 64;

    public const string Usage = """
        Synapic Indexer - the unattended image tagging service.

        Usage:
          Synapic.Indexer.exe                     Start as a Windows service (used by the SCM)
          Synapic.Indexer.exe --console           Run in the foreground until Ctrl+C
          Synapic.Indexer.exe --install           Register the service, its recovery actions and directories
          Synapic.Indexer.exe --uninstall         Stop and remove the service (keeps logs and data)
          Synapic.Indexer.exe --set-daminion-password   Store the Daminion password with DPAPI
          Synapic.Indexer.exe --apply-config <file>     Merge a partial JSON config over the current one,
                                                  validate it, save, and restart the service if it runs
          Synapic.Indexer.exe --status             Show queue counts and recent failures (read-only)
          Synapic.Indexer.exe --status --failed    List up to 50 failed and retrying items with their errors
          Synapic.Indexer.exe --help              Show this text

        --install, --uninstall and --apply-config need an elevated prompt.
        """;

    public static bool TryParse(string[] args, out ServiceInvocation invocation, out string? error)
    {
        invocation = new ServiceInvocation(ServiceMode.Service);
        error = null;

        var modes = new List<ServiceMode>();
        var failedOnly = false;
        string? configPath = null;
        for (var i = 0; i < args.Length; i++)
        {
            var normalized = args[i].Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "" or "--service":
                    continue;
                case "--install":
                    modes.Add(ServiceMode.Install);
                    break;
                case "--uninstall":
                    modes.Add(ServiceMode.Uninstall);
                    break;
                case "--console":
                    modes.Add(ServiceMode.Console);
                    break;
                case "--set-daminion-password":
                    modes.Add(ServiceMode.SetDaminionPassword);
                    break;
                case "--status":
                    modes.Add(ServiceMode.Status);
                    break;
                case "--failed":
                    failedOnly = true;
                    break;
                case "--apply-config":
                {
                    // The patch is a file path, not an inline value: the GUI writes
                    // it to %TEMP% so no quoting rule and no secret ever has to
                    // survive a command line.
                    if (i + 1 >= args.Length)
                    {
                        error = "--apply-config needs the path of the partial JSON file.";
                        return false;
                    }
                    configPath = args[++i];
                    modes.Add(ServiceMode.ApplyConfig);
                    break;
                }
                case "--help" or "-h" or "/?":
                    error = null;
                    invocation = new ServiceInvocation(ServiceMode.Service);
                    return false;
                default:
                    error = $"Unknown argument '{args[i]}'.";
                    return false;
            }
        }

        if (modes.Count > 1)
        {
            error = $"Only one mode can be given; got {string.Join(", ", modes.Select(Flag))}.";
            return false;
        }

        // --failed is a view of --status, never a mode of its own: accepting it
        // alone would start nothing and print nothing.
        if (failedOnly && !modes.Contains(ServiceMode.Status))
        {
            error = "--failed can only be used with --status.";
            return false;
        }

        invocation = new ServiceInvocation(
            modes.Count == 0 ? ServiceMode.Service : modes[0],
            failedOnly && modes.Contains(ServiceMode.Status),
            configPath);
        return true;
    }

    public static string Flag(ServiceMode mode) => mode switch
    {
        ServiceMode.Install => "--install",
        ServiceMode.Uninstall => "--uninstall",
        ServiceMode.Console => "--console",
        ServiceMode.SetDaminionPassword => "--set-daminion-password",
        ServiceMode.Status => "--status",
        ServiceMode.ApplyConfig => "--apply-config",
        _ => "(service)",
    };

    /// <summary>
    /// `sc.exe create SynapicIndexer binPath= "&lt;exe&gt;" start= auto obj= "NT AUTHORITY\LocalService" DisplayName= "…"`.
    /// The tokens are separate because <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>
    /// quotes a path with spaces correctly; a joined string is where quoting bugs live.
    /// A built-in service account needs no password token.
    /// </summary>
    public static IReadOnlyList<string> BuildCreateArguments(string executablePath) => new[]
    {
        "create", ServiceName,
        "binPath=", executablePath,
        "start=", "auto",
        "obj=", ServiceAccount,
        "DisplayName=", DisplayName,
    };

    /// <summary>Restart three times, one minute apart, with a daily failure-count reset (spec §7.1).</summary>
    public static IReadOnlyList<string> BuildFailureArguments() => new[]
    {
        "failure", ServiceName,
        "reset=", "86400",
        "actions=", "restart/60000/restart/60000/restart/60000",
    };

    public static IReadOnlyList<string> BuildQueryArguments() => new[] { "query", ServiceName };
    public static IReadOnlyList<string> BuildStartArguments() => new[] { "start", ServiceName };
    public static IReadOnlyList<string> BuildStopArguments() => new[] { "stop", ServiceName };
    public static IReadOnlyList<string> BuildDeleteArguments() => new[] { "delete", ServiceName };
}

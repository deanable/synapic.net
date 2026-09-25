using System.Diagnostics;

namespace Synapic.Avalonia.Services;

/// <summary>
/// The help topics the app can open, named here and nowhere else. Keeping the
/// file names in one place means a renamed topic is a compile error instead of
/// a blank page, and HelpServiceTests checks every name below against the help
/// sources: it has to exist in <c>docs/help</c> *and* be listed in
/// <c>Synapic.hhp</c> [FILES], which is what decides whether it is inside the
/// compiled <c>.chm</c> at all.
/// </summary>
public static class HelpTopics
{
    public const string Home = "index.html";
    public const string FirstRunSidecar = "first-run-sidecar.html";
    public const string Troubleshooting = "troubleshooting.html";

    /// <summary>
    /// The topic for a <c>WizardViewModel</c> step index: 0 = Step 1, 1 =
    /// Step 2, 2 = Step 3, 3 = Step 4, 4 = Deduplication.
    /// </summary>
    public static string ForStepIndex(int stepIndex) => stepIndex switch
    {
        0 => "step1-datasource.html",
        1 => "step2-engine.html",
        2 => "step3-process.html",
        3 => "step4-results.html",
        4 => "dedup.html",
        _ => Home,
    };
}

/// <summary>One way of opening a help topic: what to run, and with what.</summary>
/// <param name="FileName">
/// An executable (<c>hh.exe</c>) or, for the HTML fallback, the topic file
/// itself - opened through the shell, so the default browser handles it.
/// </param>
/// <param name="Arguments">Command-line arguments; empty for the HTML fallback.</param>
public sealed record HelpTarget(string FileName, string Arguments)
{
    public string Description => Arguments.Length == 0 ? FileName : $"{FileName} {Arguments}";

    public ProcessStartInfo ToStartInfo() => new(FileName)
    {
        Arguments = Arguments,
        UseShellExecute = true,
        CreateNoWindow = true,
    };
}

/// <summary>Opens the user help for a topic.</summary>
public interface IHelpService
{
    /// <summary>True when this machine actually has help to open.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Opens <paramref name="topic"/> (null = the home page). Returns false
    /// when there is no help payload or nothing could be started; the reason is
    /// logged, so it shows up in the in-app log view.
    /// </summary>
    bool Open(string? topic = null);
}

/// <summary>
/// The help system (docs/help): the same topics the Windows <c>.chm</c> is
/// compiled from, shipped as HTML on every platform so macOS and Linux get help
/// too, and so a Windows machine whose <c>hh.exe</c> is missing still opens
/// something.
///
/// Lookup order:
/// <list type="number">
///   <item>Windows, when <c>Synapic.chm</c> sits next to the app: the compiled
///   help, opened at the requested topic through <c>hh.exe</c> and the
///   <c>ms-its:</c> moniker.</item>
///   <item>The <c>help/</c> folder next to the app, in the default browser.</item>
///   <item>A source checkout's <c>docs/help</c>, so a dev build works before
///   anything has been published or compiled.</item>
/// </list>
///
/// The list is ordered, not exclusive: when the first entry fails to start, the
/// next one is tried, which is what makes the HTML copy a real fallback.
/// </summary>
public sealed class HelpService : IHelpService
{
    public const string ChmName = "Synapic.chm";
    public const string TopicsFolderName = "help";

    private readonly string _appDirectory;
    private readonly string? _repoRoot;
    private readonly Action<ProcessStartInfo> _launch;
    private readonly bool _isWindows;

    /// <param name="appDirectory">Where the app and its bundled payload live; defaults to the app base directory.</param>
    /// <param name="repoRoot">A source checkout to fall back to; defaults to <c>InferenceSidecarService.FindRepoRoot()</c>.</param>
    /// <param name="launch">How a target is started; injectable so tests can watch (or refuse) the launch.</param>
    /// <param name="isWindows">Platform override for tests; defaults to the real platform.</param>
    public HelpService(
        string? appDirectory = null,
        string? repoRoot = null,
        Action<ProcessStartInfo>? launch = null,
        bool? isWindows = null)
    {
        _appDirectory = appDirectory ?? AppContext.BaseDirectory;
        _repoRoot = repoRoot ?? InferenceSidecarService.FindRepoRoot();
        _launch = launch ?? DefaultLaunch;
        _isWindows = isWindows ?? OperatingSystem.IsWindows();
    }

    /// <summary>Where topics are looked for, shipped copy first.</summary>
    public IEnumerable<string> TopicDirectories
    {
        get
        {
            yield return Path.Combine(_appDirectory, TopicsFolderName);
            if (!string.IsNullOrEmpty(_repoRoot))
                yield return Path.Combine(_repoRoot, "docs", TopicsFolderName);
        }
    }

    /// <summary>The bundled compiled help, when this build carries one.</summary>
    public string? CompiledChm
    {
        get
        {
            var path = Path.Combine(_appDirectory, ChmName);
            return File.Exists(path) ? path : null;
        }
    }

    public bool IsAvailable => ResolveTargets(null).Count > 0;

    /// <summary>
    /// Every way of opening <paramref name="topic"/> that this machine can
    /// actually offer, best first. Empty means there is no help payload at all.
    /// </summary>
    public IReadOnlyList<HelpTarget> ResolveTargets(string? topic)
    {
        var page = NormalizeTopic(topic);
        var targets = new List<HelpTarget>();

        // Windows: the compiled help is the real thing - Contents, Index and
        // full-text search - and ms-its: opens it straight at one topic.
        if (_isWindows && CompiledChm is { } chm)
            targets.Add(new HelpTarget("hh.exe", $"\"ms-its:{chm}::/{page}\""));

        // Everywhere: the same un-compiled topics, in the default browser.
        foreach (var directory in TopicDirectories)
        {
            var file = Path.Combine(directory, page);
            if (File.Exists(file))
                targets.Add(new HelpTarget(file, ""));
        }

        return targets;
    }

    public bool Open(string? topic = null)
    {
        var targets = ResolveTargets(topic);
        if (targets.Count == 0)
        {
            SynapicLog.Warning(nameof(HelpService),
                $"Help is not available: no {ChmName} and no {TopicsFolderName}/ beside {_appDirectory}, " +
                $"and no docs/{TopicsFolderName} under {_repoRoot ?? "<repo root not found>"}. " +
                "See docs/help/README.md.");
            return false;
        }

        foreach (var target in targets)
        {
            try
            {
                _launch(target.ToStartInfo());
                SynapicLog.Info(nameof(HelpService), $"Help: opened {target.Description}");
                return true;
            }
            catch (Exception ex)
            {
                // A missing hh.exe or an unreadable target must not end the
                // attempt: the HTML copy of the same topic is next in line.
                SynapicLog.Warning(nameof(HelpService),
                    $"Help: could not start {target.FileName}: {ex.Message}");
            }
        }

        return false;
    }

    /// <summary>
    /// A topic name the help folder can hold: a plain .html file name. Anything
    /// else - null, a path, a value with a separator - is the home page, so a
    /// bad name can never reach outside the help folder.
    /// </summary>
    internal static string NormalizeTopic(string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return HelpTopics.Home;

        var name = topic.Trim();
        if (name.IndexOfAny(['/', '\\']) >= 0) return HelpTopics.Home;
        if (!name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) return HelpTopics.Home;
        return name;
    }

    private static void DefaultLaunch(ProcessStartInfo startInfo)
    {
        // ShellExecute rather than a bare process: hh.exe is resolved like the
        // shell would, and an .html path goes to the default browser on
        // Windows, macOS and Linux alike.
        using var process = Process.Start(startInfo);
    }
}

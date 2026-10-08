using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Synapic.Main.Services;

/// <summary>
/// The help topics the app can open, named here and nowhere else. Keeping the
/// file names in one place means a renamed topic is a compile error instead of
/// a blank page, and HelpServiceTests checks every name below against the help
/// sources: it has to exist in <c>docs/help</c> *and* be listed in
/// <c>Synapic.hhp</c> [FILES], which is what decides whether it is inside the
/// compiled <c>.chm</c> at all. The views annotate controls with topics as
/// well (<c>HelpScope.Topic</c> in the .axaml files), and HelpScopeTests
/// checks those names - anchors included - the same way.
/// </summary>
public static class HelpTopics
{
    public const string Home = "index.html";
    public const string FirstRunSidecar = "first-run-sidecar.html";
    public const string Troubleshooting = "troubleshooting.html";

    /// <summary>The guided setup walkthrough: from install to a finished first batch.</summary>
    public const string SetupGuide = "setup-guide.html";

    /// <summary>Every setting: what it does, its default, and where it lives.</summary>
    public const string SettingsReference = "settings-reference.html";

    /// <summary>Tips and useful info for getting the most out of Synapic.</summary>
    public const string Tips = "tips.html";

    /// <summary>The Feature enhancement utility (the upscaling operation).</summary>
    public const string Upscale = "upscale.html";

    /// <summary>
    /// The topic for a <c>WizardViewModel</c> step index: 0 = Step 1, 1 =
    /// Step 2, 2 = Step 3, 3 = Step 4, 4 = Deduplication, 5 = Upscaling.
    /// </summary>
    public static string ForStepIndex(int stepIndex) => stepIndex switch
    {
        0 => "step1-datasource.html",
        1 => "step2-engine.html",
        2 => "step3-process.html",
        3 => "step4-results.html",
        4 => "dedup.html",
        5 => Upscale,
        _ => Home,
    };
}

/// <summary>
/// The compiled help as it is carried inside the application: the bytes of the
/// <c>.chm</c>, the file name it was compiled under, and the SHA-256 that
/// <c>docs/help/build-chm.ps1</c> recorded alongside it. The hash travels in
/// the same assembly as the bytes, which makes this a corruption check rather
/// than a security guarantee - its job is to catch a damaged install, a
/// truncated download or an edited file, not a determined attacker.
/// </summary>
public sealed record EmbeddedHelp(byte[] Chm, string Sha256, string FileName);

/// <summary>One way of opening a help topic: what to run, and with what.</summary>
/// <param name="FileName">
/// An executable (<c>hh.exe</c>, for the compiled help) or - on the platforms
/// with no <c>.chm</c> viewer - the topic file itself, opened through the
/// shell so the default browser handles it.
/// </param>
/// <param name="Arguments">Command-line arguments; empty for the HTML topics.</param>
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
/// The help system (docs/help). On Windows the help is one compiled
/// <c>.chm</c> embedded in this assembly, opened through <c>hh.exe</c> and the
/// <c>ms-its:</c> moniker - which is what gives it the Contents, Index and
/// full-text search panes, and what makes context sensitivity a topic name
/// rather than a search. Embedding it is deliberate: thirty loose HTML files in
/// an install directory is a payload a user, an updater or a failing disk can
/// damage one file at a time, whereas the assembly either loads or it does not.
/// The bytes are checked against the SHA-256 recorded when they were compiled,
/// so a damaged install is reported rather than opened.
///
/// Lookup order:
/// <list type="number">
///   <item>Windows, with a compiled help inside the assembly: that help, and
///   nothing else.</item>
///   <item>Everywhere else - and on a Windows build that carries no compiled
///   help, which is a checkout that has not run <c>build-chm.ps1</c> - the
///   <c>help/</c> topics in the default browser, then a source checkout's
///   <c>docs/help</c>.</item>
/// </list>
///
/// The two never mix on Windows. A compiled help that fails its hash, or that
/// will not start, is logged and left alone rather than quietly replaced by
/// loose files sitting next to the binary: help the user asked for, out of a
/// file that could not be trusted, is worse than an honest failure.
/// </summary>
public sealed class HelpService : IHelpService
{
    public const string ChmName = "Synapic.chm";
    public const string TopicsFolderName = "help";

    /// <summary>The manifest build-chm.ps1 writes next to the compiled help.</summary>
    internal const string ManifestResourceName = "Synapic.Help.help-payload.json";

    private readonly string _appDirectory;
    private readonly string? _repoRoot;
    private readonly Action<ProcessStartInfo> _launch;
    private readonly bool _isWindows;
    private readonly Func<EmbeddedHelp?> _embeddedHelp;
    private readonly string? _cacheDirectory;

    /// <param name="appDirectory">Where the app and its bundled payload live; defaults to the app base directory.</param>
    /// <param name="repoRoot">A source checkout to fall back to; defaults to <c>InferenceSidecarService.FindRepoRoot()</c>.</param>
    /// <param name="launch">How a target is started; injectable so tests can watch (or refuse) the launch.</param>
    /// <param name="isWindows">Platform override for tests; defaults to the real platform.</param>
    /// <param name="embeddedHelp">The compiled help inside the assembly; injectable so tests can supply or withhold one.</param>
    /// <param name="cacheDirectory">Where the verified help is unpacked for the viewer; defaults to the local app data folder.</param>
    public HelpService(
        string? appDirectory = null,
        string? repoRoot = null,
        Action<ProcessStartInfo>? launch = null,
        bool? isWindows = null,
        Func<EmbeddedHelp?>? embeddedHelp = null,
        string? cacheDirectory = null)
    {
        _appDirectory = appDirectory ?? AppContext.BaseDirectory;
        _repoRoot = repoRoot ?? InferenceSidecarService.FindRepoRoot();
        _launch = launch ?? DefaultLaunch;
        _isWindows = isWindows ?? OperatingSystem.IsWindows();
        _embeddedHelp = embeddedHelp ?? (() => ReadEmbeddedHelp(typeof(HelpService).Assembly));
        _cacheDirectory = cacheDirectory;
    }

    /// <summary>Where the HTML topics are looked for, shipped copy first.</summary>
    public IEnumerable<string> TopicDirectories
    {
        get
        {
            yield return Path.Combine(_appDirectory, TopicsFolderName);
            if (!string.IsNullOrEmpty(_repoRoot))
                yield return Path.Combine(_repoRoot, "docs", TopicsFolderName);
        }
    }

    public bool IsAvailable
    {
        get
        {
            // A compiled help whose hash does not match is still the payload the
            // user asked for, so availability is about what is there - not about
            // whether it opens. Open() is where damage gets reported.
            if (_isWindows && _embeddedHelp() is not null) return true;
            return ResolveTargets(null).Count > 0;
        }
    }

    /// <summary>
    /// Every way of opening <paramref name="topic"/> that this machine can
    /// actually offer, best first. Empty means there is no help payload at all.
    /// </summary>
    public IReadOnlyList<HelpTarget> ResolveTargets(string? topic)
    {
        var page = NormalizeTopic(topic);
        var hash = page.IndexOf('#');
        var file = hash >= 0 ? page[..hash] : page;
        var fragment = hash >= 0 ? page[(hash + 1)..] : string.Empty;

        if (_isWindows && _embeddedHelp() is { } embedded)
        {
            // ms-its: opens the compiled help straight at one topic, at the
            // requested #anchor when the topic carries one. It is the only
            // target there is: the HTML topics are not shipped on Windows, and
            // a help file that fails its hash gets reported, not substituted.
            var chm = Unpack(embedded);
            return chm is null
                ? []
                : [new HelpTarget("hh.exe", $"\"ms-its:{chm}::/{page}\"")];
        }

        // No compiled help: the same topics, in the default browser. A fragment
        // cannot ride along in a path - the shell would treat it as part of the
        // file name - so an anchored topic opens as a file: URL.
        var targets = new List<HelpTarget>();
        foreach (var directory in TopicDirectories)
        {
            var path = Path.Combine(directory, file);
            if (!File.Exists(path)) continue;

            targets.Add(new HelpTarget(
                fragment.Length == 0 ? path : new Uri(path).AbsoluteUri + "#" + fragment,
                ""));
        }

        return targets;
    }

    public bool Open(string? topic = null)
    {
        var targets = ResolveTargets(topic);
        if (targets.Count == 0)
        {
            SynapicLog.Warning(nameof(HelpService),
                $"Help is not available: no compiled {ChmName} inside the app (this build carries no compiled " +
                $"help - see docs/help/build-chm.ps1), no {TopicsFolderName}/ beside {_appDirectory}, and no " +
                $"docs/{TopicsFolderName} under {_repoRoot ?? "<repo root not found>"}. See docs/help/README.md.");
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
                // A missing hh.exe or an unreadable target must not throw at the
                // user. There is no compiled help to fall back to on Windows, so
                // this ends in a logged refusal rather than a silent substitute.
                SynapicLog.Warning(nameof(HelpService),
                    $"Help: could not start {target.FileName}: {ex.Message}");
            }
        }

        return false;
    }

    /// <summary>
    /// A topic name the help can hold: a plain .html file name, with an
    /// optional #anchor into that page. Anything else - null, a path, a value
    /// with a separator, a malformed fragment - is the home page, so a bad name
    /// can never reach outside the help.
    /// </summary>
    internal static string NormalizeTopic(string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return HelpTopics.Home;

        var name = topic.Trim();
        var hash = name.IndexOf('#');
        var file = hash >= 0 ? name[..hash] : name;
        var fragment = hash >= 0 ? name[(hash + 1)..] : string.Empty;

        if (file.IndexOfAny(['/', '\\']) >= 0) return HelpTopics.Home;
        if (hash >= 0 && (fragment.Length == 0 || fragment.IndexOfAny(['/', '\\', '#']) >= 0))
            return HelpTopics.Home;
        if (!file.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) return HelpTopics.Home;

        return hash >= 0 ? $"{file}#{fragment}" : file;
    }

    /// <summary>
    /// The compiled help carried by <paramref name="assembly"/>, or null when
    /// this build has none - which is every checkout that has not run
    /// <c>docs/help/build-chm.ps1</c>. A payload with no manifest beside it is
    /// treated as no payload at all: bytes that cannot be checked against the
    /// hash they were compiled with are not help this service will open.
    /// </summary>
    internal static EmbeddedHelp? ReadEmbeddedHelp(Assembly assembly) => FromManifest(
        ReadResource(assembly, ChmResourceName(ChmName)),
        ReadResource(assembly, ManifestResourceName));

    /// <summary>
    /// The compiled help described by <paramref name="manifest"/>, or null when
    /// either half is missing or unusable. Split out from the resource read so
    /// a test can hand it the same two byte arrays a damaged install would.
    /// </summary>
    internal static EmbeddedHelp? FromManifest(byte[]? chm, byte[]? manifest)
    {
        if (chm is null || manifest is null) return null;

        string? sha256 = null;
        string? fileName = null;
        try
        {
            // A BOM is skipped here because Windows PowerShell's Set-Content
            // -Encoding UTF8 writes one, and JsonDocument does not.
            var json = manifest.Length >= 3 && manifest[0] == 0xEF && manifest[1] == 0xBB && manifest[2] == 0xBF
                ? manifest[3..]
                : manifest;

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("sha256", out var hash)) sha256 = hash.GetString();
            if (document.RootElement.TryGetProperty("chm", out var name)) fileName = name.GetString();
        }
        catch (JsonException ex)
        {
            SynapicLog.Warning(nameof(HelpService),
                $"Help: the embedded help manifest is not readable JSON ({ex.Message}); treating the help as absent.");
            return null;
        }

        if (!IsSha256(sha256))
        {
            SynapicLog.Warning(nameof(HelpService),
                "Help: the embedded help manifest carries no usable SHA-256; treating the help as absent rather " +
                "than opening a file that cannot be checked.");
            return null;
        }

        // The name only labels the unpacked file and the log lines, but it comes
        // off disk, so take the file name from it and nothing else.
        var leaf = Path.GetFileName(fileName ?? string.Empty);
        return new EmbeddedHelp(chm, sha256!.ToLowerInvariant(),
            leaf.Length > 0 && leaf.EndsWith(".chm", StringComparison.OrdinalIgnoreCase) ? leaf : ChmName);
    }

    /// <summary>
    /// Verifies the embedded help against the hash it was compiled with and
    /// unpacks it somewhere the viewer can read it, returning that path - or
    /// null when the bytes are damaged or cannot be written out.
    /// </summary>
    private string? Unpack(EmbeddedHelp help)
    {
        var actual = Sha256(help.Chm);
        if (!string.Equals(actual, help.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            // Refusing is the whole point of the hash. Falling through to the
            // loose topics would hide a damaged install behind help that looks
            // fine and is not the help this build was compiled with.
            SynapicLog.Error(nameof(HelpService),
                $"Help: the compiled {help.FileName} inside this app does not match the SHA-256 it was built " +
                $"with ({actual}, expected {help.Sha256}). The install is damaged, so the help is not opened. " +
                "Reinstall Synapic to restore it.");
            return null;
        }

        var directory = _cacheDirectory ?? DefaultCacheDirectory();
        if (directory is null)
        {
            SynapicLog.Error(nameof(HelpService),
                "Help: there is no local application data folder to unpack the compiled help into.");
            return null;
        }

        var path = Path.Combine(directory, CacheFileName(help));
        try
        {
            Directory.CreateDirectory(directory);

            // The unpacked copy is what hh.exe actually reads, and it sits in a
            // folder the user can write to - so it is the one that has to be
            // checked every time, not only the bytes inside the assembly.
            if (File.Exists(path) && FileSha256(path).Equals(help.Sha256, StringComparison.OrdinalIgnoreCase))
                return path;

            // Write beside it and move into place, so an interrupted run leaves
            // a complete file rather than a half-written one the next F1 trusts.
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, help.Chm);
            File.Move(temporary, path, overwrite: true);
            SynapicLog.Info(nameof(HelpService), $"Help: unpacked the compiled {help.FileName} to {path}.");
            return path;
        }
        catch (Exception ex)
        {
            SynapicLog.Error(nameof(HelpService),
                $"Help: could not unpack the compiled {help.FileName} into {directory}.", ex);
            return null;
        }
    }

    /// <summary>The resource name the csproj embeds the compiled help under.</summary>
    internal static string ChmResourceName(string chmFileName) => $"Synapic.Help.{chmFileName}";

    /// <summary>
    /// Keyed by the hash, so a new build of the app unpacks beside an older
    /// copy instead of overwriting a file another instance may have open.
    /// </summary>
    private static string CacheFileName(EmbeddedHelp help)
    {
        var stem = new string(Path.GetFileNameWithoutExtension(help.FileName)
            .TakeWhile(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '.')
            .ToArray());
        if (stem.Length == 0) stem = "help";

        var key = help.Sha256.Length >= 12 ? help.Sha256[..12] : help.Sha256;
        return $"{stem}-{key}.chm";
    }

    private static string? DefaultCacheDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(local) ? null : Path.Combine(local, "Synapic", TopicsFolderName);
    }

    private static byte[]? ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null) return null;

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string FileSha256(string path) => Sha256(File.ReadAllBytes(path));

    private static void DefaultLaunch(ProcessStartInfo startInfo)
    {
        // ShellExecute rather than a bare process: hh.exe is resolved like the
        // shell would, and an .html path goes to the default browser on
        // Windows, macOS and Linux alike.
        using var process = Process.Start(startInfo);
    }
}

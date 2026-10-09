using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Synapic.Main.Models;
using Synapic.Main.Services;
using Synapic.Main.ViewModels;
using Avalonia.VisualTree;
using Synapic.Main.Views;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// The help system wiring (docs/help): where the app gets its help from, how a
/// topic is opened, and which topic the Help button and F1 land on for what the
/// user is looking at.
///
/// The Windows payload is the compiled .chm - embedded in the assembly and
/// checked against a SHA-256 recorded when it was compiled - and every other
/// platform gets the same topics as HTML. These tests pin both halves of that
/// split, and pin the part that is tempting to get wrong: because the HTML
/// topics are right there and they would open, Windows must never quietly fall
/// back to them.
/// </summary>
public class HelpServiceTests : IDisposable
{
    private const string Exe = "synapic-inference.exe";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "synapic-help-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ── Windows: the compiled help inside the assembly ────────────────────

    [Fact]
    public void Windows_opens_the_compiled_help_it_carries_inside_the_assembly()
    {
        var app = NewDirectory("app");
        // Right beside the binary, and ignored: if this test could pass by
        // opening that, it would not be testing the compiled help at all.
        WriteTopic(Path.Combine(app, HelpService.TopicsFolderName), "step2-engine.html");

        var launched = new List<ProcessStartInfo>();
        var embedded = CompiledHelp();
        var help = NewService(launched.Add, isWindows: true, embedded: embedded);

        Assert.True(help.IsAvailable);
        Assert.True(help.Open("step2-engine.html"));

        var start = Assert.Single(launched);
        Assert.Equal("hh.exe", start.FileName);
        Assert.True(start.UseShellExecute);

        var (chmPath, topic) = MsIts(start.Arguments);
        Assert.Equal("step2-engine.html", topic);
        Assert.StartsWith(NewDirectory("cache"), chmPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(embedded.Chm, File.ReadAllBytes(chmPath));
    }

    [Fact]
    public void The_unpacked_copy_is_reused_when_it_is_intact()
    {
        var embedded = CompiledHelp();
        var cache = NewDirectory("cache");
        var launched = new List<ProcessStartInfo>();

        var first = NewService(launched.Add, true, embedded, cache: cache);
        first.Open();
        var (chmPath, _) = MsIts(Assert.Single(launched).Arguments);
        var stamp = File.GetLastWriteTimeUtc(chmPath);

        // hh.exe holds the file open, so re-unpacking it under a viewer that is
        // still reading it would fail; an intact copy has to be left alone.
        var second = NewService(launched.Add, true, embedded, cache: cache);
        second.Open();

        Assert.Equal(chmPath, MsIts(launched[1].Arguments).ChmPath);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(chmPath));
    }

    [Fact]
    public void An_unpacked_copy_someone_edited_is_replaced_not_trusted()
    {
        var embedded = CompiledHelp();
        var cache = NewDirectory("cache");
        var launched = new List<ProcessStartInfo>();

        var first = NewService(launched.Add, true, embedded, cache: cache);
        first.Open();
        var (chmPath, _) = MsIts(Assert.Single(launched).Arguments);

        // This is the file the viewer actually reads, and it sits somewhere the
        // user can write to, so it is checked every time rather than trusted
        // because it was written by a previous run of this same code.
        File.WriteAllBytes(chmPath, Encoding.UTF8.GetBytes("edited"));

        var second = NewService(launched.Add, true, embedded, cache: cache);
        Assert.True(second.Open());

        Assert.Equal(embedded.Chm, File.ReadAllBytes(chmPath));
    }

    [Fact]
    public void Help_whose_bytes_do_not_match_their_hash_is_not_opened()
    {
        var app = NewDirectory("app");
        WriteTopic(Path.Combine(app, HelpService.TopicsFolderName), HelpTopics.Home);

        var launched = new List<ProcessStartInfo>();
        var damaged = CompiledHelp() with { Sha256 = new string('0', 64) };
        var help = NewService(launched.Add, isWindows: true, embedded: damaged);

        // Still "available": the payload is there, and it is Open that reports
        // that what is there cannot be trusted.
        Assert.True(help.IsAvailable);
        Assert.False(help.Open());

        Assert.Empty(launched);
        Assert.Empty(Directory.GetFiles(NewDirectory("cache")));
    }

    [Fact]
    public void Damaged_help_never_falls_back_to_the_html_topics_beside_the_app()
    {
        var app = NewDirectory("app");
        WriteTopic(Path.Combine(app, HelpService.TopicsFolderName), HelpTopics.Home);

        var launched = new List<ProcessStartInfo>();
        var help = NewService(launched.Add, isWindows: true,
            embedded: CompiledHelp() with { Sha256 = new string('0', 64) });

        Assert.False(help.Open(HelpTopics.Home));
        Assert.Empty(launched);

        // The working file is right there and would open. That is the point:
        // silently serving some other help would hide a damaged install behind
        // something that looks fine.
    }

    [Fact]
    public void A_compiled_help_that_will_not_start_does_not_fall_back_either()
    {
        var app = NewDirectory("app");
        WriteTopic(Path.Combine(app, HelpService.TopicsFolderName), HelpTopics.Home);

        var attempted = new List<ProcessStartInfo>();
        var help = NewService(psi =>
        {
            attempted.Add(psi);
            throw new InvalidOperationException("hh.exe not found");
        }, isWindows: true, embedded: CompiledHelp());

        Assert.False(help.Open());
        Assert.Equal("hh.exe", Assert.Single(attempted).FileName);
    }

    [Fact]
    public void A_build_that_compiled_the_help_really_carries_and_opens_it()
    {
        // The whole chain at once, against this build's own assembly: the
        // csproj's EmbeddedResource rules, the resource names, the manifest,
        // the hash check and the unpack. Nothing else here fakes the payload,
        // so this is what would catch a rename of either resource, a
        // LogicalName that no longer matches, or a .chm that got compiled and
        // then embedded stale.
        //
        // A checkout that has not run build-chm.ps1 carries no help, so there
        // is nothing to check and this returns. CI builds the help for the
        // publish job, not the test job, so it usually returns there too.
        var carried = HelpService.ReadEmbeddedHelp(typeof(HelpService).Assembly);
        if (carried is null) return;

        Assert.True(carried.Chm.Length > 4 * 1024,
            $"the embedded help is {carried.Chm.Length} bytes, which is too small to be help - a failed compile?");
        Assert.Equal((byte)'I', carried.Chm[0]);
        Assert.Equal((byte)'T', carried.Chm[1]);
        Assert.Equal((byte)'S', carried.Chm[2]);
        Assert.Equal((byte)'F', carried.Chm[3]); // the CHM container's own signature

        var repoRoot = InferenceSidecarService.FindRepoRoot();
        if (repoRoot is not null)
        {
            var compiled = File.ReadAllBytes(
                Path.Combine(repoRoot, "docs", HelpService.TopicsFolderName, HelpService.ChmName));
            Assert.Equal(compiled, carried.Chm);
        }

        // No injected payload this time: the service reads the real one out of
        // this assembly, so the hash check runs on bytes nobody chose here.
        var cache = NewDirectory("cache");
        var launched = new List<ProcessStartInfo>();
        var help = new HelpService(NewDirectory("app"), "", launched.Add, isWindows: true,
            cacheDirectory: cache);

        Assert.True(help.Open("settings-reference.html#auto-paginate"));

        var (chmPath, topic) = MsIts(Assert.Single(launched).Arguments);
        Assert.Equal("settings-reference.html#auto-paginate", topic);
        Assert.StartsWith(cache, chmPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(carried.Chm, File.ReadAllBytes(chmPath));
    }

    // ── The manifest: what makes the embedded help checkable ─────────────

    [Fact]
    public void The_manifest_pairs_the_bytes_with_the_hash_they_were_compiled_with()
    {
        var bytes = Encoding.UTF8.GetBytes("compiled help");
        var manifest = Manifest(Sha256Of(bytes), HelpService.ChmName);

        var help = HelpService.FromManifest(bytes, manifest);

        Assert.NotNull(help);
        Assert.Equal(bytes, help!.Chm);
        Assert.Equal(Sha256Of(bytes), help.Sha256);
        Assert.Equal(HelpService.ChmName, help.FileName);
    }

    [Fact]
    public void A_manifest_written_by_windows_powershell_is_still_readable()
    {
        // Set-Content -Encoding UTF8 writes a BOM and JsonDocument does not
        // skip one, so that is a shape the manifest can genuinely arrive in.
        var bytes = Encoding.UTF8.GetBytes("compiled help");
        var manifest = Encoding.UTF8.GetPreamble()
            .Concat(Manifest(Sha256Of(bytes), HelpService.ChmName)).ToArray();

        Assert.NotNull(HelpService.FromManifest(bytes, manifest));
    }

    [Fact]
    public void Help_without_a_manifest_is_no_help_at_all()
    {
        // Bytes that cannot be checked against the hash they were compiled with
        // are not help this service will open, so the pair is all or nothing -
        // and a checkout that has not run build-chm.ps1 has neither half.
        var bytes = Encoding.UTF8.GetBytes("compiled help");

        Assert.Null(HelpService.FromManifest(null, Manifest(Sha256Of(bytes), HelpService.ChmName)));
        Assert.Null(HelpService.FromManifest(bytes, null));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"sha256\":\"\"}")]
    [InlineData("{\"sha256\":\"deadbeef\"}")]
    [InlineData("{\"sha256\":\"" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeZ\"}")]
    public void An_unusable_manifest_is_treated_as_no_help(string manifest)
        => Assert.Null(HelpService.FromManifest(
            Encoding.UTF8.GetBytes("compiled help"), Encoding.UTF8.GetBytes(manifest)));

    [Fact]
    public void A_manifest_cannot_point_the_unpacked_file_outside_the_cache()
    {
        // The name only labels the unpacked file and the log lines, but it is
        // read off disk, so nothing in it is taken as a path: whatever it says,
        // the file is composed inside the cache directory and nowhere else.
        var bytes = Encoding.UTF8.GetBytes("compiled help");
        var embedded = HelpService.FromManifest(
            bytes, Manifest(Sha256Of(bytes), "../../System32/drivers/etc/hosts.chm"))!;

        var cache = NewDirectory("cache");
        var launched = new List<ProcessStartInfo>();
        var help = NewService(launched.Add, isWindows: true, embedded: embedded, cache: cache);

        Assert.True(help.Open());

        var (chmPath, _) = MsIts(Assert.Single(launched).Arguments);
        Assert.StartsWith(cache, chmPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(bytes, File.ReadAllBytes(chmPath));
    }

    // ── Everywhere else: the same topics, as HTML ────────────────────────

    [Fact]
    public void Other_platforms_never_open_the_compiled_help()
    {
        // macOS and Linux have no .chm viewer. The bytes ride along in the same
        // assembly as on Windows, and are still not opened.
        var app = NewDirectory("app");
        var topics = Path.Combine(app, HelpService.TopicsFolderName);
        WriteTopic(topics, "step1-datasource.html");

        var launched = new List<ProcessStartInfo>();
        var help = NewService(launched.Add, isWindows: false, embedded: CompiledHelp());

        Assert.True(help.Open("step1-datasource.html"));
        Assert.Equal(Path.Combine(topics, "step1-datasource.html"), Assert.Single(launched).FileName);
    }

    [Fact]
    public void A_windows_build_with_no_compiled_help_opens_the_html_topics()
    {
        // A checkout that has not run build-chm.ps1: no bytes, no manifest, so
        // the topics beside the app - or in the checkout - are all there is.
        var app = NewDirectory("app");
        var topics = Path.Combine(app, HelpService.TopicsFolderName);
        WriteTopic(topics, HelpTopics.Home);

        var launched = new List<ProcessStartInfo>();
        var help = NewService(launched.Add, isWindows: true, embedded: null);

        Assert.True(help.Open());
        Assert.Equal(Path.Combine(topics, HelpTopics.Home), Assert.Single(launched).FileName);
    }

    [Fact]
    public void A_source_checkout_is_the_last_resort()
    {
        var app = NewDirectory("app");
        var repo = NewDirectory("repo");
        var topics = Path.Combine(repo, "docs", HelpService.TopicsFolderName);
        WriteTopic(topics, HelpTopics.Home);
        WriteTopic(topics, HelpTopics.Troubleshooting);

        var launched = new List<ProcessStartInfo>();
        var help = NewService(launched.Add, repo: repo, embedded: null);

        Assert.True(help.IsAvailable);
        Assert.True(help.Open(HelpTopics.Troubleshooting));
        Assert.Equal(Path.Combine(topics, HelpTopics.Troubleshooting), Assert.Single(launched).FileName);
    }

    [Fact]
    public void The_bundled_copy_wins_over_the_source_checkout()
    {
        var app = NewDirectory("app");
        var repo = NewDirectory("repo");
        WriteTopic(Path.Combine(app, HelpService.TopicsFolderName), HelpTopics.Home);
        WriteTopic(Path.Combine(repo, "docs", HelpService.TopicsFolderName), HelpTopics.Home);

        var launched = new List<ProcessStartInfo>();
        var help = NewService(launched.Add, repo: repo);

        help.Open();

        Assert.Equal(Path.Combine(app, HelpService.TopicsFolderName, HelpTopics.Home),
            Assert.Single(launched).FileName);
    }

    [Fact]
    public void No_payload_anywhere_is_reported_rather_than_thrown()
    {
        var launched = new List<ProcessStartInfo>();
        var help = NewService(launched.Add, isWindows: true, embedded: null);

        Assert.False(help.IsAvailable);
        Assert.False(help.Open(HelpTopics.Home));
        Assert.Empty(launched);
    }

    [Fact]
    public void An_anchored_topic_carries_the_anchor_into_the_compiled_help()
    {
        var launched = new List<ProcessStartInfo>();
        var help = NewService(launched.Add, isWindows: true, embedded: CompiledHelp());

        Assert.True(help.Open("settings-reference.html#model-id"));

        // This is what makes the help context aware: F1 on a setting lands on
        // that setting's row inside the compiled file, not on the topic.
        Assert.Equal("settings-reference.html#model-id", MsIts(Assert.Single(launched).Arguments).Topic);
    }

    [Fact]
    public void An_anchored_topic_opens_the_html_copy_as_a_url_with_the_fragment()
    {
        // A fragment cannot be part of a path the shell opens as one file name,
        // so the HTML side switches to a file: URL - and the file part of that
        // URL must still be the real topic on disk.
        var app = NewDirectory("app");
        WriteTopic(Path.Combine(app, HelpService.TopicsFolderName), "settings-reference.html");

        var launched = new List<ProcessStartInfo>();
        var help = NewService(launched.Add, isWindows: false);

        Assert.True(help.Open("settings-reference.html#model-id"));

        var start = Assert.Single(launched);
        Assert.StartsWith("file:///", start.FileName);
        Assert.EndsWith("#model-id", start.FileName);
        Assert.True(File.Exists(new Uri(start.FileName).LocalPath));
    }

    // ── Topic names ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, HelpTopics.Home)]
    [InlineData("", HelpTopics.Home)]
    [InlineData("   ", HelpTopics.Home)]
    [InlineData("../secrets.html", HelpTopics.Home)]
    [InlineData("sub\\page.html", HelpTopics.Home)]
    [InlineData("notes.txt", HelpTopics.Home)]
    [InlineData("  step2-engine.html  ", "step2-engine.html")]
    [InlineData("settings-reference.html#model-id", "settings-reference.html#model-id")]
    [InlineData("  tips.html#speed  ", "tips.html#speed")]
    [InlineData("tips.html#", HelpTopics.Home)]
    [InlineData("tips.html#a#b", HelpTopics.Home)]
    [InlineData("tips.html#sec/tion", HelpTopics.Home)]
    [InlineData("tips.html#sub\\page", HelpTopics.Home)]
    public void Only_plain_topic_names_survive(string? topic, string expected)
        => Assert.Equal(expected, HelpService.NormalizeTopic(topic));

    [Fact]
    public void Every_topic_the_app_names_is_in_the_help_sources_and_in_the_chm()
    {
        var repoRoot = InferenceSidecarService.FindRepoRoot();
        if (repoRoot is null) return; // no source checkout (an installed app): nothing to check

        var helpDir = Path.Combine(repoRoot, "docs", HelpService.TopicsFolderName);
        var compiled = CompiledFiles(File.ReadAllText(Path.Combine(helpDir, "Synapic.hhp")));

        foreach (var topic in TopicsTheAppCanName())
        {
            Assert.True(File.Exists(Path.Combine(helpDir, topic)),
                $"{topic} is named by the app but is not in docs/help - a renamed topic needs HelpTopics updated.");
            Assert.Contains(topic, compiled);
        }
    }

    [Fact]
    public void The_project_embeds_the_compiled_help_under_the_names_the_service_reads()
    {
        // HelpService reads these two resources by name, so a rename here
        // produces a build that carries no help at all and reports no error.
        var repoRoot = InferenceSidecarService.FindRepoRoot();
        if (repoRoot is null) return;

        var project = File.ReadAllText(Path.Combine(
            repoRoot, "src", "Synapic.Main", "Synapic.Main.csproj"));

        foreach (var file in new[] { HelpService.ChmName, "help-payload.json" })
        {
            var logicalName = HelpService.ChmResourceName(file);
            Assert.True(project.Contains($"LogicalName=\"{logicalName}\"", StringComparison.Ordinal),
                $"The app project does not embed {file} as {logicalName}, so the app carries no verifiable help.");
        }
    }

    // ── What the window opens ───────────────────────────────────────────

    [AvaloniaFact]
    public void The_help_button_opens_the_home_page()
    {
        var help = new FakeHelpService();
        var vm = NewViewModel(help, Exe);

        vm.OpenHelpCommand.Execute(null);

        Assert.Equal(new[] { HelpTopics.Home }, help.OpenedTopics);
    }

    [AvaloniaFact]
    public async Task F1_key_dispatch_opens_the_context_topic_on_dashboard_and_every_route()
    {
        var help = new FakeHelpService();
        var vm = NewViewModel(help, Exe);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        await vm.DetectServerAsync(); // finds the executable, so the sidecar panel is not gating

        var visits = new (Action Enter, string Topic)[]
        {
            (() => { }, HelpTopics.Home),
            (() => vm.StartTaggingRouteCommand.Execute(null), "step3-process.html"),
            (() => vm.StartDedupRouteCommand.Execute(null), "dedup.html"),
            (() => vm.StartUpscaleRouteCommand.Execute(null), HelpTopics.Upscale),
        };

        try
        {
            foreach (var (enter, topic) in visits)
            {
                enter();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.Equal(topic, vm.ContextHelpTopic);
                var openedBefore = help.OpenedTopics.Count;

                window.KeyPressQwerty(PhysicalKey.F1, RawInputModifiers.None);

                Assert.Equal(openedBefore + 1, help.OpenedTopics.Count);
                Assert.Equal(topic, help.OpenedTopics[^1]);
            }

            Assert.Equal(visits.Select(v => v.Topic), help.OpenedTopics);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task F1_opens_the_sidecar_topic_while_that_panel_is_gating()
    {
        var help = new FakeHelpService();
        var vm = NewViewModel(help, exe: null);
        await vm.DetectServerAsync();

        Assert.True(vm.IsSidecarRequired);
        Assert.Equal(HelpTopics.FirstRunSidecar, vm.ContextHelpTopic);

        vm.OpenContextHelpCommand.Execute(null);

        Assert.Equal(HelpTopics.FirstRunSidecar, Assert.Single(help.OpenedTopics));
    }

    [AvaloniaFact]
    public void F1_on_the_real_window_reaches_the_command()
    {
        var help = new FakeHelpService();
        var window = new MainWindow { DataContext = NewViewModel(help, exe: null) };
        window.Show();

        window.KeyPressQwerty(PhysicalKey.F1, RawInputModifiers.None);

        Assert.Equal(HelpTopics.FirstRunSidecar, Assert.Single(help.OpenedTopics));
    }

    [AvaloniaFact]
    public void The_toolbar_help_button_is_bound()
    {
        var window = new MainWindow { DataContext = NewViewModel(new FakeHelpService(), exe: null) };
        window.Show();

        var button = window.GetVisualDescendants().OfType<Button>()
            .SingleOrDefault(b => (b.Content as string) == "Help");

        // A failed {Binding} in XAML leaves Command null and looks fine until
        // someone clicks the button.
        Assert.NotNull(button);
        Assert.NotNull(button!.Command);
        Assert.True(button.Command!.CanExecute(null));
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static MainWindowViewModel NewViewModel(IHelpService help, string? exe) =>
        new(new FakeSidecar(), new FakeBuildService(), new Session(), () => exe, null, null, _ => exe,
            null, null, help);

    /// <summary>Every topic name the app can pass to IHelpService.Open.</summary>
    private static IEnumerable<string> TopicsTheAppCanName()
    {
        yield return HelpTopics.Home;
        yield return HelpTopics.FirstRunSidecar;
        yield return HelpTopics.Troubleshooting;
        yield return HelpTopics.SetupGuide;
        yield return HelpTopics.SettingsReference;
        yield return HelpTopics.Tips;
        for (var step = 0; step <= 5; step++) yield return HelpTopics.ForStepIndex(step);
    }

    /// <summary>The [FILES] entries of the .hhp - the list that decides what is compiled into the .chm.</summary>
    private static List<string> CompiledFiles(string hhp)
    {
        var files = new List<string>();
        var inFiles = false;

        foreach (var raw in hhp.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inFiles = line.Equals("[FILES]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (inFiles && line.Length > 0 && !line.StartsWith(';'))
                files.Add(line);
        }

        return files;
    }

    /// <summary>
    /// A HelpService wired to throwaway folders. <paramref name="embedded"/> is
    /// the compiled help a release build carries; null is a checkout that has
    /// not compiled one.
    /// </summary>
    private HelpService NewService(
        Action<ProcessStartInfo>? launch = null,
        bool isWindows = false,
        EmbeddedHelp? embedded = null,
        string? app = null,
        string? repo = "",
        string? cache = null)
        => new(app ?? NewDirectory("app"), repo, launch, isWindows, () => embedded,
            cache ?? NewDirectory("cache"));

    /// <summary>A compiled help that matches its own hash, as build-chm.ps1 writes it.</summary>
    private static EmbeddedHelp CompiledHelp(string body = "compiled help")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return new EmbeddedHelp(bytes, Sha256Of(bytes), HelpService.ChmName);
    }

    private static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] Manifest(string sha256, string chm) =>
        Encoding.UTF8.GetBytes($"{{\"chm\":\"{chm}\",\"sha256\":\"{sha256}\"}}");

    /// <summary>Splits the ms-its: moniker HelpService builds into its two halves.</summary>
    private static (string ChmPath, string Topic) MsIts(string arguments)
    {
        var inner = arguments.Trim('"');
        const string Prefix = "ms-its:";
        Assert.StartsWith(Prefix, inner);

        var rest = inner[Prefix.Length..];
        var split = rest.IndexOf("::/", StringComparison.Ordinal);
        Assert.True(split > 0, $"expected an ms-its: topic inside {arguments}");

        return (rest[..split], rest[(split + 3)..]);
    }

    private string NewDirectory(string name) =>
        Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private void WriteTopic(string directory, string fileName, string body = "<html></html>")
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), body);
    }
}

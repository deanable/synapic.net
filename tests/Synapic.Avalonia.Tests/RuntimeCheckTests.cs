using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.Views;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// Startup runtime-check logic: the app targets the .NET 10 Desktop Runtime,
/// so detection must find it (the runtime folder on disk, or the dotnet CLI)
/// and the minimum-version gate must accept 10.x and reject older/absent
/// runtimes.
///
/// The paths that matter when it is *not* installed are exercised against
/// injected probes and a dead HttpClient: a machine that cannot install the
/// runtime must still come away with the official download link, and no test
/// may depend on having - or not having - a framework on the test machine.
/// </summary>
public class RuntimeCheckTests
{
    /// <summary>Fails every request, standing in for a machine with no usable network.</summary>
    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }

    private static DotNetRuntimeCheckService Service(Version? installed, bool isWindows = true) =>
        new(new HttpClient(new OfflineHandler()), () => installed, isWindows);

    [Fact]
    public void Installed_desktop_runtime_is_detected_and_satisfies_minimum()
    {
        var service = new DotNetRuntimeCheckService();
        var version = service.GetInstalledDesktopRuntimeVersion();

        if (!OperatingSystem.IsWindows())
        {
            // Linux/macOS cannot host the Windows Desktop Runtime: no runtime
            // folder and no Microsoft.WindowsDesktop.App in `dotnet
            // --list-runtimes` — detection must come up empty.
            Assert.Null(version);
            return;
        }

        Assert.NotNull(version);
        Assert.True(
            DotNetRuntimeCheckService.Satisfies(version),
            $"expected an installed .NET 10+ Desktop Runtime, found {version}");
    }

    /// <summary>
    /// The disk probe decides whether the dialog appears at all, and it has to
    /// survive a runtime that was removed version by version (an empty framework
    /// folder is "not installed") while ignoring anything that is not a version.
    /// </summary>
    [Fact]
    public void Highest_installed_version_reads_the_newest_version_folder()
    {
        var root = Directory.CreateTempSubdirectory("synapic-runtime-");
        try
        {
            var framework = Path.Combine(root.FullName, "Microsoft.WindowsDesktop.App");
            Directory.CreateDirectory(Path.Combine(framework, "10.0.5"));
            Directory.CreateDirectory(Path.Combine(framework, "9.0.20"));
            Directory.CreateDirectory(Path.Combine(framework, "10.0.12"));
            Directory.CreateDirectory(Path.Combine(framework, "not-a-version"));

            Assert.Equal(
                Version.Parse("10.0.12"),
                DotNetRuntimeCheckService.HighestInstalledVersion(new[] { framework }));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Highest_installed_version_is_null_when_versions_are_gone_or_the_folder_is_missing()
    {
        var root = Directory.CreateTempSubdirectory("synapic-runtime-");
        try
        {
            // What dotnet-core-uninstall leaves behind, and what never existed.
            var emptied = Path.Combine(root.FullName, "Microsoft.WindowsDesktop.App");
            Directory.CreateDirectory(emptied);
            var absent = Path.Combine(root.FullName, "never-installed");

            Assert.Null(DotNetRuntimeCheckService.HighestInstalledVersion(new[] { emptied, absent }));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("10.0.0", true)]
    [InlineData("10.0.12", true)]
    [InlineData("11.0.5", true)]
    [InlineData("9.0.9", false)]
    [InlineData("8.0.21", false)]
    public void Satisfies_applies_minimum_version_gate(string version, bool expected)
    {
        Assert.Equal(expected, DotNetRuntimeCheckService.Satisfies(Version.Parse(version)));
    }

    [Fact]
    public void Satisfies_rejects_missing_runtime()
    {
        Assert.False(DotNetRuntimeCheckService.Satisfies(null));
    }

    /// <summary>
    /// The link the app offers has to be the official page for the version the
    /// app actually targets: the installers say nothing when they refuse to
    /// run, so this URL is the whole recovery path.
    /// </summary>
    [Fact]
    public void Download_page_url_is_the_official_page_for_the_required_version()
    {
        var url = DotNetRuntimeCheckService.DownloadPageUrl;

        Assert.StartsWith("https://dotnet.microsoft.com/download/dotnet/", url);
        Assert.Equal(
            $"{DotNetRuntimeCheckService.MinimumVersion.Major}.{DotNetRuntimeCheckService.MinimumVersion.Minor}",
            url.Split('/')[^1]);
        // Concrete on purpose: a target-framework bump must be a visible test failure.
        Assert.Equal("https://dotnet.microsoft.com/download/dotnet/10.0", url);
    }

    /// <summary>
    /// No-op path of the startup flow: on a machine with the runtime (or a
    /// non-Windows platform, where the check skips) the flow must log and
    /// return without attempting any install.
    /// </summary>
    [Fact]
    public async Task EnsureRuntime_noops_when_runtime_present_or_platform_skips()
    {
        var logs = new List<string>();
        await new DotNetRuntimeCheckService().EnsureRuntimeAsync(logs.Add);

        Assert.Contains(logs, l =>
            l.Contains("detected", StringComparison.OrdinalIgnoreCase) ||   // Windows with runtime
            l.Contains("skipped", StringComparison.OrdinalIgnoreCase));     // non-Windows
        Assert.DoesNotContain(logs, l => l.Contains("Downloading"));
        Assert.DoesNotContain(logs, l => l.Contains("silent install", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_satisfied_runtime_offers_no_download_link()
    {
        var logs = new List<string>();
        var offered = false;
        var service = Service(Version.Parse("10.0.12"));
        service.RuntimeUnavailable += _ => offered = true;

        var result = await service.EnsureRuntimeAsync(logs.Add);

        Assert.Equal(RuntimeCheckOutcome.RuntimePresent, result.Outcome);
        Assert.False(result.NeedsManualInstall);
        Assert.False(offered, "nothing to offer while the runtime is there");
        Assert.DoesNotContain(logs, l => l.Contains("Downloading"));
    }

    [Fact]
    public async Task Missing_runtime_with_no_installer_offers_the_download_link()
    {
        var logs = new List<string>();
        RuntimeCheckResult? offered = null;
        var service = Service(installed: null);
        service.RuntimeUnavailable += r => offered = r;

        var result = await service.EnsureRuntimeAsync(logs.Add);

        Assert.Equal(RuntimeCheckOutcome.DownloadFailed, result.Outcome);
        Assert.True(result.NeedsManualInstall);
        Assert.Null(result.InstallerUrl);
        Assert.NotNull(offered);
        Assert.Equal(DotNetRuntimeCheckService.DownloadPageUrl, offered!.DownloadUrl);
        // The log is the fallback carrier when no dialog can be shown.
        Assert.Contains(logs, l => l.Contains(DotNetRuntimeCheckService.DownloadPageUrl));
    }

    /// <summary>
    /// An installed 9.x is not "the runtime is present": it satisfies nothing the
    /// app asks for, and it is exactly the case a user needs the link for.
    /// </summary>
    [Fact]
    public async Task Runtime_older_than_the_minimum_is_treated_as_missing()
    {
        var logs = new List<string>();
        var offered = false;
        var service = Service(Version.Parse("9.0.9"));
        service.RuntimeUnavailable += _ => offered = true;

        var result = await service.EnsureRuntimeAsync(logs.Add);

        Assert.True(result.NeedsManualInstall);
        Assert.Equal(Version.Parse("9.0.9"), result.InstalledVersion);
        Assert.True(offered);
        Assert.Contains(logs, l => l.Contains("older than", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Non_windows_platform_needs_no_download()
    {
        var logs = new List<string>();
        var offered = false;
        var service = Service(installed: null, isWindows: false);
        service.RuntimeUnavailable += _ => offered = true;

        var result = await service.EnsureRuntimeAsync(logs.Add);

        Assert.Equal(RuntimeCheckOutcome.NotApplicable, result.Outcome);
        Assert.False(offered);
        Assert.DoesNotContain(logs, l => l.Contains("silent install", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// The dialog is the visible half of the recovery path, so the button has to
/// open exactly the link the check handed over.
/// </summary>
public class RuntimeDialogTests
{
    [Fact]
    public void Dialog_opens_the_link_it_was_given()
    {
        var opened = new List<string>();
        var vm = new RuntimeDialogViewModel(
            owner: null,
            openUrl: opened.Add)
        {
            DownloadUrl = "https://example.test/custom",
        };

        vm.OpenDownloadCommand.Execute(null);

        Assert.Equal(new[] { "https://example.test/custom" }, opened);
    }

    /// <summary>
    /// The dialog's compiled XAML has to populate - the same guard
    /// <c>MainWindowPopulationTests</c> applies to the shell - with the link and
    /// the explanation the check produced.
    /// </summary>
    [AvaloniaFact]
    public void Dialog_populates_with_the_link_and_the_reason()
    {
        var result = new RuntimeCheckResult(
            RuntimeCheckOutcome.DownloadFailed, null, DotNetRuntimeCheckService.DownloadPageUrl, null);

        var window = new RuntimeDialogWindow();
        window.DataContext = RuntimeDialogWindow.ViewModelFor(result, window);
        window.Show();

        Assert.NotNull(window.Content);
        var vm = Assert.IsType<RuntimeDialogViewModel>(window.DataContext);
        Assert.Contains("does not report", vm.Headline);
        Assert.Contains("could not fetch", vm.Detail);

        // The bindings have to reach the controls: a misspelled name fails
        // silently at runtime, and this dialog is the only place the link is
        // visible when the log is not being watched.
        Assert.Equal(
            DotNetRuntimeCheckService.DownloadPageUrl,
            window.GetVisualDescendants().OfType<TextBox>().Single().Text);
        Assert.Equal(
            $"Download .NET {DotNetRuntimeCheckService.MinimumVersion.Major}",
            window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("accent")).Content);
    }

    [Fact]
    public void Dialog_defaults_to_the_official_page()
    {
        Assert.Equal(DotNetRuntimeCheckService.DownloadPageUrl, new RuntimeDialogViewModel().DownloadUrl);
    }

    [Fact]
    public void Dialog_says_what_was_found_and_why_the_install_failed()
    {
        var offline = new RuntimeCheckResult(RuntimeCheckOutcome.DownloadFailed, null, "https://x.test", null);
        var declined = new RuntimeCheckResult(RuntimeCheckOutcome.InstallFailed, null, "https://x.test", null);
        var tooOld = new RuntimeCheckResult(RuntimeCheckOutcome.DownloadFailed, Version.Parse("9.0.9"), "https://x.test", null);

        Assert.Contains("does not report", RuntimeDialogWindow.Describe(offline));
        Assert.Contains("9.0.9", RuntimeDialogWindow.Describe(tooOld));
        Assert.Contains("could not fetch", RuntimeDialogWindow.Explain(offline));
        Assert.Contains("elevation", RuntimeDialogWindow.Explain(declined));
    }
}

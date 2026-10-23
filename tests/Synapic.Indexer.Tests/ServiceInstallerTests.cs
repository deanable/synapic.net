using Synapic.Indexer.Hosting;
using Synapic.Indexer.Install;
using Xunit;

namespace Synapic.Indexer.Tests;

/// <summary>
/// The install sequence the installer calls (D-11-30): create → recovery →
/// start, idempotent on a second run, with every argument list coming from
/// <see cref="ServiceCommandLine"/>. No test touches the real SCM or
/// <c>%ProgramData%</c>.
/// </summary>
public sealed class ServiceInstallerTests : IDisposable
{
    private const string Exe = @"C:\Program Files\Synapic\Synapic.Indexer.exe";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "synapic-service-installer-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeSc : IScRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();
        public Func<IReadOnlyList<string>, ScResult> Respond { get; set; } = _ => new(0, "", "");

        public Task<ScResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            Calls.Add(arguments.ToArray());
            return Task.FromResult(Respond(arguments));
        }
    }

    private ServiceInstaller Installer(FakeSc sc) =>
        new(sc, ensureProgramData: (_, _) => { });

    private static ScResult NotInstalled => new(1060, "The specified service does not exist as an installed service.", "");

    private static ScResult Ok(string stdout = "") => new(0, stdout, "");

    [Fact]
    public async Task A_fresh_install_creates_sets_recovery_and_starts_in_that_order()
    {
        var sc = new FakeSc
        {
            Respond = args => args[0] == "query" ? NotInstalled : Ok(),
        };

        var exit = await Installer(sc).InstallAsync(Exe, new IndexerPaths(_root));

        Assert.Equal(0, exit);
        Assert.Equal(4, sc.Calls.Count);
        Assert.Equal(ServiceCommandLine.BuildQueryArguments(), sc.Calls[0]);
        Assert.Equal(ServiceCommandLine.BuildCreateArguments(Exe), sc.Calls[1]);
        Assert.Equal(ServiceCommandLine.BuildFailureArguments(), sc.Calls[2]);
        Assert.Equal(ServiceCommandLine.BuildStartArguments(), sc.Calls[3]);
    }

    [Fact]
    public async Task An_existing_service_skips_create_but_still_refreshes_recovery_and_starts()
    {
        var sc = new FakeSc
        {
            Respond = args => args[0] == "query" ? Ok("SERVICE_NAME: SynapicIndexer") : Ok(),
        };

        var exit = await Installer(sc).InstallAsync(Exe, new IndexerPaths(_root));

        Assert.Equal(0, exit);
        Assert.Equal(3, sc.Calls.Count);
        Assert.Equal(ServiceCommandLine.BuildQueryArguments(), sc.Calls[0]);
        Assert.Equal(ServiceCommandLine.BuildFailureArguments(), sc.Calls[1]);
        Assert.Equal(ServiceCommandLine.BuildStartArguments(), sc.Calls[2]);
    }

    [Theory]
    [InlineData(1056)]
    [InlineData(1062)]
    public async Task Start_tolerates_an_already_running_or_not_started_answer(int code)
    {
        var sc = new FakeSc
        {
            Respond = args => args[0] switch
            {
                "query" => Ok(),
                "start" => new ScResult(code, "", ""),
                _ => Ok(),
            },
        };

        var exit = await Installer(sc).InstallAsync(Exe, new IndexerPaths(_root));

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task A_failed_start_is_reported_but_the_service_stays_installed()
    {
        var sc = new FakeSc
        {
            Respond = args => args[0] switch
            {
                "query" => Ok(),
                "start" => new ScResult(5, "", "Access is denied."),
                _ => Ok(),
            },
        };

        var exit = await Installer(sc).InstallAsync(Exe, new IndexerPaths(_root));

        Assert.Equal(5, exit);
        Assert.Contains(sc.Calls, a => a[0] == "failure");
    }

    [Fact]
    public async Task A_failed_create_aborts_before_recovery_or_start()
    {
        var sc = new FakeSc
        {
            Respond = args => args[0] switch
            {
                "query" => NotInstalled,
                "create" => new ScResult(5, "", "Access is denied."),
                _ => Ok(),
            },
        };

        var exit = await Installer(sc).InstallAsync(Exe, new IndexerPaths(_root));

        Assert.Equal(5, exit);
        Assert.Equal(2, sc.Calls.Count);
        Assert.Equal("query", sc.Calls[0][0]);
        Assert.Equal("create", sc.Calls[1][0]);
    }

    [Fact]
    public async Task Uninstall_stops_then_deletes_and_ignores_an_already_stopped_service()
    {
        var sc = new FakeSc
        {
            Respond = args => args[0] switch
            {
                "stop" => new ScResult(1062, "", "The service has not been started."),
                "query" => Ok("STATE : 1  STOPPED"),
                _ => Ok(),
            },
        };

        var exit = await Installer(sc).UninstallAsync(new IndexerPaths(_root));

        Assert.Equal(0, exit);
        Assert.Equal(new[] { "query", "stop", "query", "delete" }, sc.Calls.Select(a => a[0]));
        Assert.Equal(ServiceCommandLine.BuildDeleteArguments(), sc.Calls[^1]);
    }

    [Fact]
    public async Task Uninstalling_a_missing_service_is_a_successful_no_op()
    {
        var sc = new FakeSc { Respond = _ => NotInstalled };

        var exit = await Installer(sc).UninstallAsync(new IndexerPaths(_root));

        Assert.Equal(0, exit);
        Assert.Single(sc.Calls);
        Assert.Equal("query", sc.Calls[0][0]);
    }
}

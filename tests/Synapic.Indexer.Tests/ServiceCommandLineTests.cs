using Synapic.Indexer.Install;
using Xunit;

namespace Synapic.Indexer.Tests;

public class ServiceCommandLineTests
{
    [Theory]
    [InlineData(new string[0], ServiceMode.Service)]
    [InlineData(new[] { "--service" }, ServiceMode.Service)]
    [InlineData(new[] { "--install" }, ServiceMode.Install)]
    [InlineData(new[] { "--uninstall" }, ServiceMode.Uninstall)]
    [InlineData(new[] { "--console" }, ServiceMode.Console)]
    [InlineData(new[] { "--set-daminion-password" }, ServiceMode.SetDaminionPassword)]
    [InlineData(new[] { "--CONSOLE" }, ServiceMode.Console)]
    public void Each_mode_parses(string[] args, ServiceMode expected)
    {
        Assert.True(ServiceCommandLine.TryParse(args, out var invocation, out var error));
        Assert.Null(error);
        Assert.Equal(expected, invocation.Mode);
    }

    [Fact]
    public void No_arguments_is_the_service_start_the_scm_performs()
    {
        Assert.True(ServiceCommandLine.TryParse(Array.Empty<string>(), out var invocation, out _));
        Assert.Equal(ServiceMode.Service, invocation.Mode);
    }

    [Fact]
    public void An_unknown_argument_cannot_fall_through_to_service_mode()
    {
        Assert.False(ServiceCommandLine.TryParse(new[] { "--instal" }, out _, out var error));

        Assert.NotNull(error);
        Assert.Contains("--instal", error);
        Assert.Contains("Unknown argument", error);
    }

    [Fact]
    public void Two_modes_are_rejected()
    {
        Assert.False(ServiceCommandLine.TryParse(new[] { "--install", "--console" }, out _, out var error));

        Assert.NotNull(error);
        Assert.Contains("--install", error);
        Assert.Contains("--console", error);
    }

    [Fact]
    public void Status_accepts_the_failed_view_in_either_order()
    {
        Assert.True(ServiceCommandLine.TryParse(new[] { "--status", "--failed" }, out var invocation, out var error));
        Assert.Null(error);
        Assert.Equal(ServiceMode.Status, invocation.Mode);
        Assert.True(invocation.FailedOnly);

        Assert.True(ServiceCommandLine.TryParse(new[] { "--failed", "--status" }, out invocation, out error));
        Assert.Null(error);
        Assert.Equal(ServiceMode.Status, invocation.Mode);
        Assert.True(invocation.FailedOnly);
    }

    [Fact]
    public void Plain_status_is_not_the_failed_view()
    {
        Assert.True(ServiceCommandLine.TryParse(new[] { "--status" }, out var invocation, out _));
        Assert.False(invocation.FailedOnly);
    }

    [Fact]
    public void Failed_without_status_is_rejected_instead_of_running_nothing()
    {
        Assert.False(ServiceCommandLine.TryParse(new[] { "--failed" }, out _, out var error));
        Assert.NotNull(error);
        Assert.Contains("--status", error);

        Assert.False(ServiceCommandLine.TryParse(new[] { "--console", "--failed" }, out _, out error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Help_is_not_an_error()
    {
        Assert.False(ServiceCommandLine.TryParse(new[] { "--help" }, out _, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Create_arguments_are_separate_tokens_with_the_service_identity()
    {
        const string exe = @"C:\Program Files\Synapic\Synapic.Indexer.exe";

        Assert.Equal(
            new[]
            {
                "create", "SynapicIndexer", "binPath=", exe, "start=", "auto",
                "obj=", @"NT AUTHORITY\LocalService",
                "DisplayName=", "Synapic Background Image Indexer",
            },
            ServiceCommandLine.BuildCreateArguments(exe));
    }

    [Fact]
    public void A_path_with_spaces_stays_one_token_so_quoting_is_the_runtime_s_job()
    {
        const string exe = @"C:\Program Files\Synapic\Synapic.Indexer.exe";

        var arguments = ServiceCommandLine.BuildCreateArguments(exe);

        Assert.Single(arguments, token => token == exe);
        Assert.Equal(10, arguments.Count);
    }

    [Fact]
    public void Failure_arguments_restart_three_times_and_reset_daily()
    {
        Assert.Equal(
            new[] { "failure", "SynapicIndexer", "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000" },
            ServiceCommandLine.BuildFailureArguments());
    }

    [Fact]
    public void Query_start_stop_and_delete_name_the_service()
    {
        Assert.Equal(new[] { "query", "SynapicIndexer" }, ServiceCommandLine.BuildQueryArguments());
        Assert.Equal(new[] { "start", "SynapicIndexer" }, ServiceCommandLine.BuildStartArguments());
        Assert.Equal(new[] { "stop", "SynapicIndexer" }, ServiceCommandLine.BuildStopArguments());
        Assert.Equal(new[] { "delete", "SynapicIndexer" }, ServiceCommandLine.BuildDeleteArguments());
    }
}

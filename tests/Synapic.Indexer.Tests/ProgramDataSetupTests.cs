using Synapic.Indexer.Configuration;
using Synapic.Indexer.Hosting;
using Synapic.Indexer.Install;
using Xunit;

namespace Synapic.Indexer.Tests;

public sealed class ProgramDataSetupTests : IDisposable
{
    private readonly string _root;
    private readonly IndexerPaths _paths;

    public ProgramDataSetupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "synapic-indexer-acl-" + Guid.NewGuid().ToString("N"));
        _paths = new IndexerPaths(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Ensure_creates_the_tree_and_grants_modify_by_sid_not_by_account_name()
    {
        var calls = new List<IReadOnlyList<string>>();

        ProgramDataSetup.Ensure(_paths, calls.Add);

        Assert.True(Directory.Exists(_paths.Root));
        Assert.True(Directory.Exists(_paths.LogsDirectory));
        Assert.True(Directory.Exists(_paths.StateDirectory));

        Assert.Equal(2, calls.Count);
        Assert.Contains(calls, c => c.Contains($"*{ProgramDataSetup.SystemSid}:(OI)(CI)M"));
        Assert.Contains(calls, c => c.Contains($"*{ProgramDataSetup.LocalServiceSid}:(OI)(CI)M"));
        Assert.All(calls, c => Assert.Equal(_paths.Root, c[0]));
        Assert.All(calls, c => Assert.Contains("/T", c));
    }

    [Fact]
    public void The_grant_inherits_to_children_and_is_modify_only()
    {
        var arguments = ProgramDataSetup.BuildGrantArguments(@"C:\ProgramData\Synapic", ProgramDataSetup.SystemSid);

        Assert.Equal(
            new[] { @"C:\ProgramData\Synapic", "/grant", "*S-1-5-18:(OI)(CI)M", "/T", "/C" },
            arguments);
        Assert.DoesNotContain(arguments, a => a.Contains("Everyone", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(arguments, a => a.Contains(":F", StringComparison.Ordinal));
    }

    [Fact]
    public void The_password_command_stores_ciphertext_and_never_prints_the_password()
    {
        Directory.CreateDirectory(_paths.Root);
        var store = new IndexerConfigStore(
            $@"Software\Synapic\Indexer.Tests\{Guid.NewGuid():N}", _paths.ConfigFilePath);
        const string secret = "hunter2-very-secret";
        var output = new StringWriter();

        var exitCode = PasswordCommand.Run(store, () => secret, output);

        Assert.Equal(0, exitCode);
        var saved = store.Load().Config.Datasource.Daminion.EncryptedPassword;
        Assert.NotNull(saved);
        Assert.NotEqual(secret, saved);
        Assert.DoesNotContain(secret, output.ToString());
        Assert.DoesNotContain(secret, File.ReadAllText(_paths.ConfigFilePath));
    }

    [Fact]
    public void An_empty_password_saves_nothing()
    {
        Directory.CreateDirectory(_paths.Root);
        var store = new IndexerConfigStore(null, _paths.ConfigFilePath);
        var output = new StringWriter();

        var exitCode = PasswordCommand.Run(store, () => "", output);

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(_paths.ConfigFilePath));
    }
}

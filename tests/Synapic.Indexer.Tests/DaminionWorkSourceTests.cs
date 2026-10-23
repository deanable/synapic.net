using Synapic.Core.Services.Daminion;
using Synapic.Core.Services.Processing;
using Synapic.Indexer.Configuration;
using Synapic.Indexer.Detection;
using Synapic.Indexer.State;
using Xunit;

namespace Synapic.Indexer.Tests;

public sealed class DaminionWorkSourceTests : IDisposable
{
    private readonly string _directory;
    private readonly IndexingQueueRepository _repository;
    private readonly WorkIntake _intake;

    public DaminionWorkSourceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "synapic-indexer-daminion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _repository = new IndexingQueueRepository(Path.Combine(_directory, "indexer.db"));
        _repository.InitializeAsync().GetAwaiter().GetResult();
        _intake = new WorkIntake(_repository);
    }

    public void Dispose()
    {
        _repository.ClearPool();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private static DatasourceSelection Selection() => new()
    {
        IsDaminion = true,
        Scope = "all",
        UntaggedFields = new[] { "keywords", "categories" },
        StatusFilter = "all",
        MaxItems = 0,
    };

    private static ProcessWorkItem Item(int? id, string name, long hash = 0, long size = 0) => new()
    {
        DaminionId = id,
        FileName = name,
        ServerHashCode = hash,
        SizeBytes = size,
    };

    [Fact]
    public async Task The_selection_reaches_the_shared_fetch_with_the_configured_scope()
    {
        DatasourceSelection? seen = null;
        var source = new DaminionWorkSource(Selection(), _intake, (selection, _) =>
        {
            seen = selection;
            return Task.FromResult<IReadOnlyList<ProcessWorkItem>>(Array.Empty<ProcessWorkItem>());
        });

        await source.ScanAsync(CancellationToken.None);

        Assert.NotNull(seen);
        Assert.True(seen!.IsDaminion);
        Assert.Equal("all", seen.Scope);
        Assert.Equal("all", seen.StatusFilter);
        Assert.Equal(0, seen.MaxItems);
        Assert.Equal(new[] { "keywords", "categories" }, seen.UntaggedFields);
        Assert.Null(seen.DaminionClient);
    }

    [Fact]
    public async Task Items_map_to_daminion_keys_with_server_hash_fingerprints()
    {
        var source = new DaminionWorkSource(Selection(), _intake, (_, _) =>
            Task.FromResult<IReadOnlyList<ProcessWorkItem>>(new[]
            {
                Item(7, "a.jpg", hash: 12345, size: 99),
                Item(8, "b.jpg"),
            }));

        var result = await source.ScanAsync(CancellationToken.None);

        Assert.Equal(2, result.Found);
        Assert.Equal(2, result.Enqueued);
        var first = (await _repository.GetByKeyAsync(PathKey.ForDaminion(7)))!;
        Assert.Equal(7, first.DaminionId);
        Assert.Equal("a.jpg", first.FileName);
        Assert.Equal(99, first.Size);
        Assert.Equal("srv:12345", first.ContentHash);
        Assert.Null(first.Path);
        Assert.Null(first.MtimeUtc);
        Assert.Equal(JobState.Pending, first.State);

        var second = (await _repository.GetByKeyAsync(PathKey.ForDaminion(8)))!;
        Assert.Null(second.ContentHash);
        Assert.Null(second.Size);
    }

    [Fact]
    public async Task A_second_scan_of_the_same_catalog_is_a_no_op()
    {
        var source = new DaminionWorkSource(Selection(), _intake, (_, _) =>
            Task.FromResult<IReadOnlyList<ProcessWorkItem>>(new[] { Item(7, "a.jpg", hash: 5, size: 10) }));

        await source.ScanAsync(CancellationToken.None);
        var again = await source.ScanAsync(CancellationToken.None);

        Assert.Equal(0, again.Enqueued);
        Assert.Equal(1, again.Skipped);
        Assert.Equal(1, (await _repository.GetSummaryAsync()).Total);
    }

    [Fact]
    public async Task An_unreachable_server_leaves_the_queue_untouched()
    {
        var source = new DaminionWorkSource(Selection(), _intake, (_, _) =>
            throw new DaminionNetworkException("connection refused"));

        var result = await source.ScanAsync(CancellationToken.None);

        Assert.True(result.Unavailable);
        Assert.Equal(0, result.Enqueued);
        Assert.Equal(0, (await _repository.GetSummaryAsync()).Total);
    }

    [Fact]
    public async Task An_item_without_an_id_is_skipped_not_queued_under_an_empty_key()
    {
        var source = new DaminionWorkSource(Selection(), _intake, (_, _) =>
            Task.FromResult<IReadOnlyList<ProcessWorkItem>>(new[]
            {
                Item(null, "nameless.jpg"),
                Item(11, "good.jpg"),
            }));

        var result = await source.ScanAsync(CancellationToken.None);

        Assert.Equal(2, result.Found);
        Assert.Equal(1, result.Enqueued);
        Assert.Equal(1, result.Skipped);
        Assert.Null(await _repository.GetByKeyAsync("daminion|"));
    }

    [Fact]
    public void A_missing_password_is_a_fatal_configuration_error_naming_the_key()
    {
        var config = ConfigWith(password: null);
        var error = Assert.Throws<IndexerConfigurationException>(
            () => new DaminionWorkSource(config, _intake));
        Assert.Equal("datasource.daminion.encryptedPassword", error.Setting);
        Assert.Contains("--set-daminion-password", error.Message);
    }

    [Fact]
    public void An_undecryptable_password_is_reported_without_leaking_it()
    {
        var config = ConfigWith(password: "this-is-not-dpapi-ciphertext");
        var error = Assert.Throws<IndexerConfigurationException>(
            () => new DaminionWorkSource(config, _intake));
        Assert.Equal("datasource.daminion.encryptedPassword", error.Setting);
        Assert.DoesNotContain("this-is-not-dpapi-ciphertext", error.Message);
    }

    [Fact]
    public void An_unsupported_scope_is_refused_rather_than_querying_everything()
    {
        var config = IndexerConfig.CreateDefault();
        config.Datasource.Type = "daminion";
        config.Datasource.Daminion.ServerUrl = "http://daminion.example";
        config.Datasource.Daminion.Username = "indexer";
        config.Datasource.Daminion.Scope = "collection";
        config.Datasource.Daminion.UntaggedFields = new List<string> { "keywords" };
        DpapiCredentialProtector.TryProtect("secret", out var ciphertext, out _);
        config.Datasource.Daminion.EncryptedPassword = ciphertext;

        var error = Assert.Throws<IndexerConfigurationException>(
            () => new DaminionWorkSource(config, _intake));
        Assert.Equal("datasource.daminion.scope", error.Setting);
    }

    [Theory]
    [InlineData("all", "all")]
    [InlineData("catalog", "all")]
    [InlineData("ALL", "all")]
    [InlineData("", "all")]
    [InlineData("saved-search", "saved_search")]
    [InlineData("saved_search", "saved_search")]
    [InlineData("collection", "collection")]
    public void Scope_words_map_to_the_client_values(string configured, string expected) =>
        Assert.Equal(expected, DaminionWorkSource.NormalizeScope(configured));

    private static IndexerConfig ConfigWith(string? password)
    {
        var config = IndexerConfig.CreateDefault();
        config.Datasource.Type = "daminion";
        config.Datasource.Daminion.ServerUrl = "http://daminion.example";
        config.Datasource.Daminion.Username = "indexer";
        config.Datasource.Daminion.UntaggedFields = new List<string> { "keywords", "categories" };
        config.Datasource.Daminion.EncryptedPassword = password;
        return config;
    }
}

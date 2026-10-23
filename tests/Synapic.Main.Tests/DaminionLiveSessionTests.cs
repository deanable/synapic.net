using System.Numerics;
using Synapic.Main.Services;
using Synapic.Core.Services.Daminion;
using Synapic.Core.Services.Processing;
using Xunit;
using Xunit.Abstractions;

namespace Synapic.Main.Tests;

/// <summary>
/// Replicates the original Python client's login process against a live server
/// (requests.Session semantics): POST /api/UserManager/Login with credentials
/// in the query string → server issues HttpOnly session cookies → the same
/// HTTP handler replays them on every subsequent call. The C# port must do the
/// same via Refit's underlying handler, or every authenticated call 401s
/// ("Authorization has been denied for this request.").
///
/// Opt-in like Daminion_round_trip_against_live_server: set
/// SYNAPIC_TEST_DAMINION_URL / _USERNAME / _PASSWORD, or use the saved Windows
/// Daminion connection in HKCU. CI without either skips.
/// Read-only: login, authenticated reads, logout — no metadata writes.
/// </summary>
public class DaminionLiveSessionTests
{
    private readonly ITestOutputHelper _output;

    [Trait("Category", "Live")]
    [SkippableFact]
    public async Task Similar_item_ids_hashCode_is_compared_with_original_pixel_phash()
    {
        // This deliberately named test is run explicitly, never as part of the
        // normal suite. Credentials stay in process memory and neither output nor
        // assertions include the saved URL, username or password.
        var url = GetLiveSetting("SYNAPIC_TEST_DAMINION_URL", saved => saved.ServerUrl);
        var username = GetLiveSetting("SYNAPIC_TEST_DAMINION_USERNAME", saved => saved.Username);
        var password = GetLiveSetting("SYNAPIC_TEST_DAMINION_PASSWORD", saved => saved.Password);

        // Opt-in by definition: report a skip, not a pass or a failure, when the
        // probe has nothing configured or nowhere to connect.
        Skip.If(string.IsNullOrWhiteSpace(url), "No saved Daminion server URL is available.");
        Skip.If(string.IsNullOrWhiteSpace(username), "No saved Daminion username is available.");
        Skip.If(string.IsNullOrEmpty(password), "No saved Daminion password is available.");

        var client = new DaminionApiClient(url!, username!, password!);
        try
        {
            await client.AuthenticateAsync();
        }
        catch (DaminionNetworkException e)
        {
            Skip.If(true, $"The saved Daminion server is unreachable: {e.Message}");
            return;
        }
        try
        {
            // The user identified these two different-aspect-ratio images as
            // similar. Fetch only their catalog records and originals; no scan,
            // writes, or broader catalog sampling occurs.
            const int firstId = 86068;
            const int secondId = 86233;
            var items = await client.GetItemsByIdsAsync(new[] { firstId, secondId });
            // The probe compares two specific images the user identified. A
            // catalog without them (another database, a cleaned-up catalog, a
            // different account) cannot answer the question — skip rather than
            // fail a run over an environment that was never configured for it.
            Skip.If(items.Length != 2,
                $"The live catalog does not contain the fixture items {firstId} and {secondId} (returned {items.Length}).");
            var byId = items.ToDictionary(item => item.Id);
            Assert.True(byId.TryGetValue(firstId, out var first), $"Daminion did not return item {firstId}.");
            Assert.True(byId.TryGetValue(secondId, out var second), $"Daminion did not return item {secondId}.");
            Assert.NotNull(first!.HashCode);
            Assert.NotNull(second!.HashCode);

            var originalPaths = new List<string>();
            try
            {
                var firstPath = await client.DownloadOriginalAsync(firstId);
                Assert.False(string.IsNullOrWhiteSpace(firstPath), $"Could not download original for item {firstId}.");
                originalPaths.Add(firstPath!);
                var secondPath = await client.DownloadOriginalAsync(secondId);
                Assert.False(string.IsNullOrWhiteSpace(secondPath), $"Could not download original for item {secondId}.");
                originalPaths.Add(secondPath!);

                var options = new DedupOptions(Algorithm: HashAlgorithm.PHash, MaxDimension: 512);
                var firstPHash = DedupService.ComputeHash(firstPath!, options);
                var secondPHash = DedupService.ComputeHash(secondPath!, options);
                Assert.True(firstPHash.HasValue, $"Could not compute pHash from item {firstId} original.");
                Assert.True(secondPHash.HasValue, $"Could not compute pHash from item {secondId} original.");

                var firstServerHash = unchecked((uint)first.HashCode!.Value);
                var secondServerHash = unchecked((uint)second.HashCode!.Value);
                var serverDistance = BitOperations.PopCount(firstServerHash ^ secondServerHash);
                var pixelDistance = BitOperations.PopCount(firstPHash!.Value ^ secondPHash!.Value);
                _output.WriteLine($"Read-only comparison for similar items {firstId}/{secondId}: " +
                                  $"server hash distance={serverDistance} bits (32-bit); " +
                                  $"original-image pHash distance={pixelDistance} bits (64-bit); " +
                                  $"dimensions={first.Dimensions?.W}x{first.Dimensions?.H} / {second.Dimensions?.W}x{second.Dimensions?.H}.");
                _output.WriteLine(serverDistance == 0
                    ? "Result: server hash matches exactly for this pair; because the images are visually similar but differently sized, this supports a resize-tolerant hash only if the originals are not byte-identical."
                    : serverDistance <= 6
                        ? "Result: server hashes are close under a 32-bit Hamming metric and pixel pHashes are compared separately; this single pair is suggestive but cannot identify the server algorithm."
                        : "Result: server hashes are not close under a 32-bit Hamming metric for this pair; this does not support treating hashCode as a pHash-like fingerprint from this observation alone.");
            }
            finally
            {
                foreach (var path in originalPaths)
                    try { File.Delete(path); } catch (IOException) { }
            }
        }
        finally
        {
            await client.LogoutAsync();
        }
    }

    private static string? GetLiveSetting(string environmentName, Func<DaminionConnectionParams, string> select)
    {
        var environmentValue = Environment.GetEnvironmentVariable(environmentName);
        if (!string.IsNullOrWhiteSpace(environmentValue)) return environmentValue;
        return new DaminionConnectionStore().Load() is { } saved ? select(saved) : null;
    }

    public DaminionLiveSessionTests(ITestOutputHelper output) => _output = output;

    [Trait("Category", "Live")]
    [SkippableFact]
    public async Task Login_establishes_replayed_cookie_session()
    {
        var baseUrl = Environment.GetEnvironmentVariable("SYNAPIC_TEST_DAMINION_URL");
        var username = Environment.GetEnvironmentVariable("SYNAPIC_TEST_DAMINION_USERNAME");
        var password = Environment.GetEnvironmentVariable("SYNAPIC_TEST_DAMINION_PASSWORD");
        // Not configured — the live session flow is opt-in only, and a skip says
        // so honestly (the previous early return was reported as a pass).
        Skip.If(string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password),
            "Live Daminion credentials are not configured (SYNAPIC_TEST_DAMINION_URL/_USERNAME/_PASSWORD).");

        var client = new DaminionApiClient(baseUrl!, username!, password!);

        // 1. Login: query-string credentials; server responds 200 + Set-Cookie.
        try
        {
            await client.AuthenticateAsync();
        }
        catch (DaminionNetworkException e)
        {
            Skip.If(true, $"The configured Daminion server is unreachable: {e.Message}");
            return;
        }
        Assert.True(client.IsAuthenticated);

        // 2. Session replay: an authenticated read must succeed on the SAME
        //    Refit client instance the login used (this is where a missing
        //    cookie jar would surface as 401/403).
        var items = await client.GetItemsFilteredAsync(scope: "all", maxItems: 3);
        Assert.NotEmpty(items);

        // 3. Tag schema: GetDefaultLayout must populate the GUID map on server
        //    11.x (nested propertyName/propertyGuid layout) — an empty map
        //    silently degrades every metadata write to "No metadata ops".
        Assert.True(client.MappedTagGuidCount > 0,
            $"tag GUID map should be populated after login, got {client.MappedTagGuidCount}");
        _output.WriteLine($"Tag GUIDs mapped from GetDefaultLayout: {client.MappedTagGuidCount}");

        // 4. Saved searches walk the tag-schema path (GetDefaultLayout +
        //    GetTags run inside AuthenticateAsync; this exercises cached maps).
        //    Optional: some server builds (11.0.0.3906) break the
        //    IndexedTagValues route — the client must degrade to empty.
        var searches = await client.GetSavedSearchesAsync();
        Assert.NotNull(searches);

        // 5. Scope counting mirrors daminion_client.get_filtered_item_count.
        //    Read-only: all / search / status-filtered / failure contract.
        var allCount = await client.GetFilteredItemCountAsync(scope: "all");
        Assert.True(allCount > 0, $"all-scope count should be > 0, got {allCount}");

        var searchCount = await client.GetFilteredItemCountAsync(scope: "search", searchTerm: "a");
        Assert.True(searchCount >= 0, $"search count must not be the -1 failure sentinel, got {searchCount}");

        var approvedCount = await client.GetFilteredItemCountAsync(scope: "all", statusFilter: "approved");
        Assert.True(approvedCount >= 0, $"flag-filtered count must not be the -1 failure sentinel, got {approvedCount}");
        Assert.True(approvedCount <= allCount, "filtered count cannot exceed the unfiltered count");

        // A bogus saved-search id must never throw. On this server build the
        // structured filter is silently ignored (capped page total), matching
        // the original client's behavior; the -1 sentinel is reserved for
        // transport/handler failures.
        var badCount = await client.GetFilteredItemCountAsync(scope: "saved_search", savedSearchId: 999999999);
        Assert.True(badCount >= 0, $"invalid saved-search id must not throw or return the -1 sentinel, got {badCount}");

        // 6. Logout ends the session server-side.
        await client.LogoutAsync();
        Assert.False(client.IsAuthenticated);
    }
}

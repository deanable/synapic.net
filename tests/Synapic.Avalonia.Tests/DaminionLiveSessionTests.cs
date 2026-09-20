using Synapic.Avalonia.Services.Daminion;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// Replicates the original Python client's login process against a live server
/// (requests.Session semantics): POST /api/UserManager/Login with credentials
/// in the query string → server issues HttpOnly session cookies → the same
/// HTTP handler replays them on every subsequent call. The C# port must do the
/// same via Refit's underlying handler, or every authenticated call 401s
/// ("Authorization has been denied for this request.").
///
/// Opt-in like Daminion_round_trip_against_live_server: set
/// SYNAPIC_TEST_DAMINION_URL / _USERNAME / _PASSWORD. CI (no env vars) skips.
/// Read-only: login, authenticated reads, logout — no metadata writes.
/// </summary>
public class DaminionLiveSessionTests
{
    [Fact]
    public async Task Login_establishes_replayed_cookie_session()
    {
        var baseUrl = Environment.GetEnvironmentVariable("SYNAPIC_TEST_DAMINION_URL");
        var username = Environment.GetEnvironmentVariable("SYNAPIC_TEST_DAMINION_USERNAME");
        var password = Environment.GetEnvironmentVariable("SYNAPIC_TEST_DAMINION_PASSWORD");
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            // Not configured — the live session flow is opt-in only.
            return;
        }

        var client = new DaminionApiClient(baseUrl, username, password);

        // 1. Login: query-string credentials; server responds 200 + Set-Cookie.
        await client.AuthenticateAsync();
        Assert.True(client.IsAuthenticated);

        // 2. Session replay: an authenticated read must succeed on the SAME
        //    Refit client instance the login used (this is where a missing
        //    cookie jar would surface as 401/403).
        var items = await client.GetItemsFilteredAsync(scope: "all", maxItems: 3);
        Assert.NotEmpty(items);

        // 3. Saved searches walk the tag-schema path (GetDefaultLayout +
        //    GetTags run inside AuthenticateAsync; this exercises cached maps).
        //    Optional: some server builds (11.0.0.3906) break the
        //    IndexedTagValues route — the client must degrade to empty.
        var searches = await client.GetSavedSearchesAsync();
        Assert.NotNull(searches);

        // 4. Scope counting mirrors daminion_client.get_filtered_item_count.
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

        // 5. Logout ends the session server-side.
        await client.LogoutAsync();
        Assert.False(client.IsAuthenticated);
    }
}

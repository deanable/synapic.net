using System.Net;
using System.Text;
using System.Text.Json;
using Refit;
using Synapic.Avalonia.Services.Daminion;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// Daminion routes /api/IndexedTagValues on its FULL parameter set: omitting
/// any of indexedTagId / parentValueId / filter / pageIndex / pageSize makes
/// the server answer 404 (verified against a live 11.x server — the same URL
/// returns 401, i.e. a match, as soon as all five are present).
///
/// Refit drops null query parameters, so the earlier nullable
/// ``filter = null`` default meant saved-search enumeration requested a URL
/// the server does not route: a 404 that silently degraded the Step 1 picker
/// to synthesized "Saved Search #id" entries, even though the Python client
/// (which always sends ``filter=``) listed the real names. These tests pin the
/// wire format so that regression cannot come back.
/// </summary>
public class DaminionIndexedTagValuesRequestTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>Base URL intentionally carries a path — Daminion is commonly hosted under one (…/daminion).</summary>
    private static IDaminionApi NewApi(CapturingHandler handler) =>
        RestService.For<IDaminionApi>(
            new HttpClient(handler) { BaseAddress = new Uri("http://damserver.local/daminion") },
            new RefitSettings
            {
                ContentSerializer = new SystemTextJsonContentSerializer(
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            });

    [Fact]
    public async Task Enumeration_request_carries_every_route_required_parameter()
    {
        var handler = new CapturingHandler();

        await NewApi(handler).GetIndexedTagValues(39);

        var uri = handler.LastRequestUri!.ToString();
        Assert.StartsWith("http://damserver.local/daminion/api/IndexedTagValues/GetIndexedTagValues?", uri);
        Assert.Contains("indexedTagId=39", uri);
        Assert.Contains("parentValueId=-2", uri);
        Assert.Contains("filter=", uri);
        Assert.Contains("pageIndex=0", uri);
        Assert.Contains("pageSize=500", uri);
    }

    [Fact]
    public async Task Fallback_route_request_carries_every_route_required_parameter()
    {
        var handler = new CapturingHandler();

        await NewApi(handler).GetIndexedTagValuesFallback(39);

        var uri = handler.LastRequestUri!.ToString();
        Assert.StartsWith("http://damserver.local/daminion/api/IndexedTagValues?", uri);
        Assert.Contains("indexedTagId=39", uri);
        Assert.Contains("parentValueId=-2", uri);
        Assert.Contains("filter=", uri);
        Assert.Contains("pageIndex=0", uri);
        Assert.Contains("pageSize=500", uri);
    }

    [Fact]
    public async Task Enumeration_request_passes_a_supplied_filter_through()
    {
        var handler = new CapturingHandler();

        await NewApi(handler).GetIndexedTagValues(13, filter: "city");

        Assert.Contains("filter=city", handler.LastRequestUri!.ToString());
    }
}

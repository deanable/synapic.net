using System.Net.Sockets;
using System.Text;
using Synapic.Core.Services.Daminion;
using Xunit;
using Xunit.Abstractions;

namespace Synapic.Core.Tests;

/// <summary>
/// "Reject in catalog" (the dedup/similar apply action) writes the Flag field
/// through POST /api/ItemData/BatchChange with the Flag tag's guid and its
/// documented text value â€” and, like the catalog delete, only reports success
/// when a GetByIds read-back confirms the write on every id. The BatchChange
/// route answers success:true even for writes it quietly ignores, so the tests
/// cover both the request shape and the verification read.
/// </summary>
public class DaminionFlagRequestTests
{
    private const string LayoutWithFlagJson =
        """{"properties":[{"propertyName":"Flag","propertyGuid":"flag-guid-1"}]}""";

    private readonly ITestOutputHelper _output;

    public DaminionFlagRequestTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Reject_posts_BatchChange_with_the_flag_tag_and_text_value()
    {
        using var server = new StubServer(
            layoutBody: LayoutWithFlagJson,
            batchChangeResponse: """{"success":true,"errorCode":0}""",
            getByIdsBody: """{"mediaItems":[{"id":7,"flag":2},{"id":8,"flag":2}],"totalCount":2}""");
        var client = new DaminionApiClient($"http://127.0.0.1:{server.Port}", "user", "pass");
        await client.AuthenticateAsync(CancellationToken.None);

        var ok = await client.SetItemsFlagAsync(new[] { 7, 8 }, DaminionFlagValue.Rejected);

        Assert.True(ok);
        Assert.NotNull(server.BatchChangePath);
        Assert.Contains("/api/ItemData/BatchChange", server.BatchChangePath, StringComparison.Ordinal);
        _output.WriteLine($"POST {server.BatchChangePath} body={server.BatchChangeBody}");

        Assert.Contains("\"ids\":[7,8]", server.BatchChangeBody, StringComparison.Ordinal);
        Assert.Contains("\"guid\":\"flag-guid-1\"", server.BatchChangeBody, StringComparison.Ordinal);
        // Text assign, per the route's own contract (id 0 = resolve the text).
        Assert.Contains("\"id\":0", server.BatchChangeBody, StringComparison.Ordinal);
        Assert.Contains("\"value\":\"Rejected\"", server.BatchChangeBody, StringComparison.Ordinal);
        Assert.Contains("\"remove\":false", server.BatchChangeBody, StringComparison.Ordinal);

        // The verification read follows the write.
        Assert.NotNull(server.GetByIdsPath);
        Assert.Contains("/api/MediaItems/GetByIds", server.GetByIdsPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reject_verifies_the_write_back_and_reports_failure_when_it_never_lands()
    {
        // The server accepts the call but the read-back never shows flag 2 â€”
        // the same structure the earlier BatchChange-as-delete no-op had.
        using var server = new StubServer(
            layoutBody: LayoutWithFlagJson,
            batchChangeResponse: """{"success":true,"errorCode":0}""",
            getByIdsBody: """{"mediaItems":[{"id":7,"flag":0}],"totalCount":1}""");
        var client = new DaminionApiClient($"http://127.0.0.1:{server.Port}", "user", "pass");
        await client.AuthenticateAsync(CancellationToken.None);

        Assert.False(await client.SetItemsFlagAsync(new[] { 7 }, DaminionFlagValue.Rejected));
    }

    [Fact]
    public async Task Reject_accepts_the_text_representation_in_the_read_back()
    {
        using var server = new StubServer(
            layoutBody: LayoutWithFlagJson,
            batchChangeResponse: """{"success":true,"errorCode":0}""",
            getByIdsBody: """{"mediaItems":[{"id":7,"flag":"Rejected"}],"totalCount":1}""");
        var client = new DaminionApiClient($"http://127.0.0.1:{server.Port}", "user", "pass");
        await client.AuthenticateAsync(CancellationToken.None);

        Assert.True(await client.SetItemsFlagAsync(new[] { 7 }, DaminionFlagValue.Rejected));
    }

    [Fact]
    public async Task Reject_fails_closed_when_the_layout_has_no_flag_tag()
    {
        using var server = new StubServer(
            layoutBody: """{"properties":[{"propertyName":"Keywords","propertyGuid":"kw-guid"}]}""",
            batchChangeResponse: """{"success":true,"errorCode":0}""",
            getByIdsBody: """{"mediaItems":[],"totalCount":0}""");
        var client = new DaminionApiClient($"http://127.0.0.1:{server.Port}", "user", "pass");
        await client.AuthenticateAsync(CancellationToken.None);

        // No guid, no request: a call the client cannot name is not made.
        Assert.False(await client.SetItemsFlagAsync(new[] { 7 }, DaminionFlagValue.Rejected));
        Assert.Null(server.BatchChangePath);
    }

    [Fact]
    public async Task Reject_of_nothing_is_a_no_op_without_a_request()
    {
        using var server = new StubServer(
            layoutBody: LayoutWithFlagJson,
            batchChangeResponse: "{}", getByIdsBody: "{}");
        var client = new DaminionApiClient($"http://127.0.0.1:{server.Port}", "user", "pass");
        await client.AuthenticateAsync(CancellationToken.None);

        Assert.True(await client.SetItemsFlagAsync(Array.Empty<int>(), DaminionFlagValue.Rejected));
        Assert.Null(server.BatchChangePath);
    }

    /// <summary>Tiny HTTP/1.1 responder over TcpListener (no http.sys, no URL ACL)
    /// that records the BatchChange request and answers the calls the client makes.</summary>
    private sealed class StubServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly string _layoutBody;
        private readonly string _batchChangeResponse;
        private readonly string _getByIdsBody;

        public int Port { get; }

        public string? BatchChangePath { get; private set; }
        public string? BatchChangeBody { get; private set; } = "";
        public string? GetByIdsPath { get; private set; }

        private readonly object _lock = new();

        public StubServer(string layoutBody, string batchChangeResponse, string getByIdsBody)
        {
            _layoutBody = layoutBody;
            _batchChangeResponse = batchChangeResponse;
            _getByIdsBody = getByIdsBody;
            _listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(ct); }
                catch { return; }
                _ = Task.Run(() => { ServeAsync(client, ct).Wait(ct); }, ct);
            }
        }

        private async Task ServeAsync(TcpClient client, CancellationToken ct)
        {
            try
            {
                using var _ = client;
                var stream = client.GetStream();
                var (head, body) = await ReadRequestAsync(stream, ct);
                var requestLine = head.Split('\r', '\n').First();
                var path = requestLine.Split(' ')[1];

                int status; string responseBody;
                if (path.StartsWith("/api/UserManager/Login", StringComparison.Ordinal))
                {
                    status = 200;
                    responseBody = "{}";
                }
                else if (path.StartsWith("/api/ItemData/GetDefaultLayout", StringComparison.Ordinal))
                {
                    status = 200;
                    responseBody = _layoutBody;
                }
                else if (path.StartsWith("/api/ItemData/BatchChange", StringComparison.Ordinal))
                {
                    lock (_lock)
                    {
                        BatchChangePath = path;
                        BatchChangeBody = body;
                    }
                    status = 200;
                    responseBody = _batchChangeResponse;
                }
                else if (path.StartsWith("/api/MediaItems/GetByIds", StringComparison.Ordinal))
                {
                    lock (_lock) GetByIdsPath = path;
                    status = 200;
                    responseBody = _getByIdsBody;
                }
                else
                {
                    // GetTags and anything else during AuthenticateAsync.
                    status = 200;
                    responseBody = "{}";
                }

                var payload = Encoding.UTF8.GetBytes(responseBody);
                var response = Encoding.UTF8.GetBytes(
                    $"HTTP/1.1 {status} OK\r\n" +
                    "Content-Type: application/json\r\n" +
                    $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response.Concat(payload).ToArray(), ct);
                await stream.FlushAsync(ct);
            }
            catch
            {
                // Client went away mid-request â€” irrelevant to the assertions.
            }
        }

        private static async Task<(string Head, string Body)> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[8192];
            var sb = new StringBuilder();
            while (!sb.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0) break;
                sb.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            var head = sb.ToString();
            var separator = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var body = separator < 0 ? "" : head[(separator + 4)..];

            var contentLength = 0;
            foreach (var line in head.Split('\r', '\n'))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line["Content-Length:".Length..].Trim(), out var length))
                {
                    contentLength = length;
                    break;
                }
            }

            while (Encoding.UTF8.GetByteCount(body) < contentLength)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0) break;
                body += Encoding.UTF8.GetString(buffer, 0, read);
            }

            return (head, body);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}

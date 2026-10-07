using System.Net;
using System.Net.Sockets;
using System.Text;
using Synapic.Main.Services.Daminion;
using Xunit;
using Xunit.Abstractions;

namespace Synapic.Main.Tests;

/// <summary>
/// The dedup step's "Delete from catalog" must go to POST /api/MediaItems/Remove
/// (daminion_api.py delete_items) with {ids, delete:false}. It used to post to
/// /api/ItemData/BatchChange — a tag-write route that answers success:true while
/// removing nothing, so the app reported "Deleted N item(s)" and the next scan
/// found every duplicate again. The response envelope alone still proves little,
/// so a follow-up GetByIds decides whether the call reports success or failure.
/// </summary>
public class DaminionDeleteRequestTests
{
    private readonly ITestOutputHelper _output;

    public DaminionDeleteRequestTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Delete_posts_to_MediaItems_Remove_with_catalog_only_flag()
    {
        using var server = new StubServer(removeBody: """{"data":{},"error":null,"success":true,"errorCode":0}""",
            getByIdsBody: """{"mediaItems":[],"totalCount":0,"error":null,"success":true,"errorCode":0}""");
        var client = new DaminionApiClient($"http://127.0.0.1:{server.Port}", "user", "pass");

        var ok = await client.DeleteItemsAsync(new[] { 7, 8 });

        Assert.True(ok);
        Assert.NotNull(server.RemovePath);
        Assert.Contains("/api/MediaItems/Remove", server.RemovePath, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/ItemData/BatchChange", server.RemovePath, StringComparison.Ordinal);
        _output.WriteLine($"POST {server.RemovePath} body={server.RemoveBody}");

        // Catalog entry only: delete:false never touches the files on the server.
        Assert.Contains("\"ids\":[7,8]", server.RemoveBody, StringComparison.Ordinal);
        Assert.Contains("\"delete\":false", server.RemoveBody, StringComparison.Ordinal);
        Assert.Contains("application/json", server.RemoveContentType, StringComparison.OrdinalIgnoreCase);

        // The verification read: the ids are asked for after the write.
        Assert.NotNull(server.GetByIdsPath);
        Assert.Contains("/api/MediaItems/GetByIds", server.GetByIdsPath, StringComparison.Ordinal);
        Assert.Contains("ids=7%2C8", server.GetByIdsPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_reports_failure_when_the_server_keeps_the_items()
    {
        using var server = new StubServer(removeBody: """{"data":{},"error":null,"success":true,"errorCode":0}""",
            getByIdsBody: """{"mediaItems":[{"id":7,"fileName":"a.jpg"}],"totalCount":1,"error":null,"success":true,"errorCode":0}""");
        var client = new DaminionApiClient($"http://127.0.0.1:{server.Port}", "user", "pass");

        // 200 + success:true, but the item is still resolvable → not a delete.
        Assert.False(await client.DeleteItemsAsync(new[] { 7 }));
    }

    [Fact]
    public async Task Delete_reports_failure_when_the_server_rejects_the_call()
    {
        using var server = new StubServer(removeBody: """{"data":null,"error":"permission denied","success":false,"errorCode":404}""",
            getByIdsBody: """{"mediaItems":[{"id":7,"fileName":"a.jpg"}],"totalCount":1,"error":null,"success":true,"errorCode":0}""");
        var client = new DaminionApiClient($"http://127.0.0.1:{server.Port}", "user", "pass");

        Assert.False(await client.DeleteItemsAsync(new[] { 7 }));
    }

    [Fact]
    public async Task Delete_of_nothing_is_a_no_op_without_a_request()
    {
        using var server = new StubServer(removeBody: "{}", getByIdsBody: "{}");
        var client = new DaminionApiClient($"http://127.0.0.1:{server.Port}", "user", "pass");

        Assert.True(await client.DeleteItemsAsync(Array.Empty<int>()));
        Assert.Null(server.RemovePath);
    }

    /// <summary>Tiny HTTP/1.1 responder over TcpListener (no http.sys, no URL ACL)
    /// that records the delete request and answers the two calls the client makes.</summary>
    private sealed class StubServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly string _removeBody;
        private readonly string _getByIdsBody;

        public int Port { get; }

        public string? RemovePath { get; private set; }
        public string? RemoveBody { get; private set; } = "";
        public string? RemoveContentType { get; private set; }
        public string? GetByIdsPath { get; private set; }

        private readonly object _lock = new();

        public StubServer(string removeBody, string getByIdsBody)
        {
            _removeBody = removeBody;
            _getByIdsBody = getByIdsBody;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(ct); }
                catch { return; }
                _ = Task.Run(() => ServeAsync(client, ct), ct);
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
                else if (path.StartsWith("/api/MediaItems/Remove", StringComparison.Ordinal))
                {
                    lock (_lock)
                    {
                        RemovePath = path;
                        RemoveBody = body;
                        RemoveContentType = HeaderValue(head, "Content-Type");
                    }
                    status = 200;
                    responseBody = _removeBody;
                }
                else if (path.StartsWith("/api/MediaItems/GetByIds", StringComparison.Ordinal))
                {
                    lock (_lock) GetByIdsPath = path;
                    status = 200;
                    responseBody = _getByIdsBody;
                }
                else
                {
                    // GetDefaultLayout / GetTags during AuthenticateAsync.
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
                // Client went away mid-request — irrelevant to the assertions.
            }
        }

        private static string? HeaderValue(string head, string name)
        {
            foreach (var line in head.Split('\r', '\n'))
            {
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                if (string.Equals(line[..idx].Trim(), name, StringComparison.OrdinalIgnoreCase))
                    return line[(idx + 1)..].Trim();
            }
            return null;
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

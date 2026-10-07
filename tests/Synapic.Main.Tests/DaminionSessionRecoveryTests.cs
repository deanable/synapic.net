using System.Net;
using System.Net.Sockets;
using System.Text;
using Synapic.Main.Services.Daminion;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// Offline regression for session-expiry recovery: the stub Daminion answers
/// /Login with a session cookie and 401s MediaItems/Get once the session is
/// invalidated. GetItemsFilteredAsync must re-authenticate and replay once,
/// surfacing the recovered items instead of a batch of failures.
/// </summary>
public class DaminionSessionRecoveryTests
{
    [Fact]
    public async Task Expired_session_reauthenticates_and_replays_once()
    {
        using var server = new StubDaminionServer();
        var client = new DaminionApiClient($"http://127.0.0.1:{server.Port}", "user", "pass");

        await client.AuthenticateAsync();
        Assert.True(client.IsAuthenticated);

        var first = await client.GetItemsFilteredAsync(scope: "all", maxItems: 10);
        Assert.Single(first);
        Assert.Equal(1, server.LoginCount);

        server.SessionValid = false; // the long batch outlives the server session
        var recovered = await client.GetItemsFilteredAsync(scope: "all", maxItems: 10);

        Assert.Single(recovered);
        Assert.Equal(2, server.LoginCount); // exactly one re-auth, not a retry storm
        Assert.True(client.IsAuthenticated);
    }

    /// <summary>Minimal HTTP/1.1 responder over TcpListener (no http.sys, no URL ACL).</summary>
    private sealed class StubDaminionServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public int Port { get; }
        public int LoginCount => Interlocked.CompareExchange(ref _loginCount, 0, 0);
        public volatile bool SessionValid = true;
        private int _loginCount;

        public StubDaminionServer()
        {
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
                var head = await ReadHeadAsync(stream, ct);
                var firstLine = head.Split('\r', '\n').First();
                var path = firstLine.Split(' ')[1];
                
                int status; string body; var extra = "";
                if (path.StartsWith("/api/UserManager/Login", StringComparison.Ordinal))
                {
                                        Interlocked.Increment(ref _loginCount);
                    SessionValid = true; // a successful login starts a fresh valid session
                    status = 200;
                    body = "{}";
                    extra = "Set-Cookie: SynapicDam=ok; Path=/\r\n";
                }
                else if (path.StartsWith("/api/MediaItems/Get?", StringComparison.Ordinal)
                         || path == "/api/MediaItems/Get")
                {
                    var hasCookie = head.Contains("Cookie:", StringComparison.OrdinalIgnoreCase)
                                    && head.Contains("SynapicDam=ok", StringComparison.OrdinalIgnoreCase)
                                    && SessionValid;
                                        (status, body) = hasCookie
                        ? (200, """{"items":[{"id":1,"fileName":"a.jpg"}],"totalCount":1}""")
                        : (401, """{"error":"Authorization has been denied for this request."}""");
                }
                else
                {
                    status = 200;
                    body = "{}";
                }

                var payload = Encoding.UTF8.GetBytes(body);
                var response = Encoding.UTF8.GetBytes(
                    $"HTTP/1.1 {status} {status switch { 200 => "OK", 401 => "Unauthorized", _ => "Error" }}\r\n" +
                    $"Content-Type: application/json\r\n{extra}" +
                    $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                response = response.Concat(payload).ToArray();
                await stream.WriteAsync(response, ct);
                await stream.FlushAsync(ct);
            }
            catch
            {
                // Client went away mid-request — irrelevant to the assertions.
            }
        }

        private static async Task<string> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[8192];
            var sb = new StringBuilder();
            while (!sb.ToString().Contains("\r\n\r\n"))
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0) break;
                sb.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}

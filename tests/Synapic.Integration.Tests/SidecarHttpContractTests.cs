using System.Net;
using System.Net.Sockets;
using System.Text;
using Synapic.Avalonia.Services;
using Synapic.Shared.Contracts;
using Xunit;

namespace Synapic.Integration.Tests;

/// <summary>
/// Contract tests for the real InferenceApiClient against a live (stub) HTTP
/// server, so the behaviours [sidecar-protocol.md] pins — 503-then-200 tag
/// retry, health/status payloads, shutdown — hold at the socket level, not
/// just in DTO shape.
/// </summary>
public class SidecarHttpContractTests
{
    [Fact]
    public async Task Health_round_trips_the_ready_payload()
    {
        using var server = new StubSidecarServer((_, path) => path == "/health"
            ? (200, """{"status":"ready","model":"m","device":"cpu","vram_used_mb":42}""")
            : (404, "{}"));

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };
        var api = new InferenceApiClient(http);

        var health = await api.GetHealthAsync();

        Assert.Equal("ready", health.Status);
        Assert.Equal("m", health.Model);
        Assert.Equal(42, health.VramUsedMb);
    }

    [Fact]
    public async Task Tag_retries_once_on_503_and_succeeds()
    {
        var attempts = 0;
        using var server = new StubSidecarServer((_, path) =>
        {
            if (path != "/tag") return (404, "{}");
            return Interlocked.Increment(ref attempts) == 1
                ? (503, """{"detail":"Model loading — retry shortly"}""")
                : (200, """{"keywords":["a"],"inference_ms":5,"model_used":"m"}""");
        });

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };
        var api = new InferenceApiClient(http);

        var tag = await api.TagAsync(new TagRequest { ImagePath = "x.jpg" });

        Assert.Equal(2, attempts);
        Assert.Equal(new[] { "a" }, tag.Keywords);
        Assert.Equal("m", tag.ModelUsed);
    }

    [Fact]
    public async Task Tag_error_detail_surfaces_in_the_exception()
    {
        using var server = new StubSidecarServer((_, path) => path == "/tag"
            ? (404, """{"detail":"Image not found: x.jpg"}""")
            : (404, "{}"));

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };
        var api = new InferenceApiClient(http);

        var error = await Assert.ThrowsAsync<InferenceApiException>(
            () => api.TagAsync(new TagRequest { ImagePath = "x.jpg" }));
        Assert.Equal(404, error.StatusCode);
        Assert.Contains("Image not found", error.Message);
    }

    [Fact]
    public async Task Models_list_and_config_round_trip()
    {
        using var server = new StubSidecarServer((method, path) =>
        {
            if (path == "/models/list")
                return (200, """[{"id":"LiquidAI/LFM2.5-VL-450M","downloaded":true}]""");
            if (path == "/config" && method == "GET")
                return (200, """{"model_id":"LiquidAI/LFM2.5-VL-450M","device":"cpu"}""");
            return (404, "{}");
        });

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };
        var api = new InferenceApiClient(http);

        var models = await api.ListModelsAsync();
        var config = await api.GetConfigAsync();

        Assert.Single(models);
        Assert.Equal("LiquidAI/LFM2.5-VL-450M", models[0].Id);
        Assert.True(models[0].Downloaded);
        Assert.Equal("LiquidAI/LFM2.5-VL-450M", config.ModelId);
        Assert.Equal("cpu", config.Device);
    }

    [Fact]
    public async Task Shutdown_answers_and_the_client_ignores_transport_after()
    {
        using var server = new StubSidecarServer((_, path) => path == "/shutdown"
            ? (200, """{"status":"shutting_down"}""")
            : (404, "{}"));

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };
        var api = new InferenceApiClient(http);

        await api.ShutdownAsync(); // must not throw
    }

    /// <summary>Minimal HTTP/1.1 responder over TcpListener (no http.sys, no URL ACL).</summary>
    private sealed class StubSidecarServer : IDisposable
    {
        private readonly Func<string, string, (int Status, string Body)> _respond;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public int Port { get; }

        public StubSidecarServer(Func<string, string, (int, string)> respond)
        {
            _respond = respond;
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
                var segments = firstLine.Split(' ');
                var method = segments[0];
                var path = segments[1];

                var (status, body) = _respond(method, path);
                var payload = Encoding.UTF8.GetBytes(body);
                var response = Encoding.UTF8.GetBytes(
                    $"HTTP/1.1 {status} {status switch { 200 => "OK", 404 => "Not Found", 503 => "Service Unavailable", _ => "Error" }}\r\n" +
                    "Content-Type: application/json\r\n" +
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

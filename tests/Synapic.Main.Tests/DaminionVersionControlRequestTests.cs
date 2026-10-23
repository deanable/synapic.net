using System.Net;
using System.Net.Sockets;
using System.Text;
using Synapic.Core.Services;
using Synapic.Core.Services.Daminion;
using Synapic.Core.Services.Processing;
using Synapic.Shared.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace Synapic.Main.Tests;

/// <summary>
/// The upscale flow's catalog dance (daminion_api.py VersionControlAPI port):
/// CheckOut/UndoCheckOut must post {"Ids":[â€¦]} to /api/VersionControl/â€¦, and
/// CheckIn must be multipart (id + comment + file), retrying without the
/// comment when the server rejects it. A wrong field name here makes Daminion
/// silently ignore the call, so the request shape is what this asserts â€”
/// not just the response envelope.
/// </summary>
public class DaminionVersionControlRequestTests
{
    private readonly ITestOutputHelper _output;

    public DaminionVersionControlRequestTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task CheckOut_posts_the_ids_body_to_VersionControl_CheckOut()
    {
        using var server = new VersionStub();
        var client = new DaminionApiClient($"http://127.0.0.1:{server.Port}", "user", "pass");

        Assert.True(await client.CheckOutItemsAsync(new[] { 7, 8 }));

        var requests = server.Snapshot();
        var checkOut = Assert.Single(requests, r => r.Path == "/api/VersionControl/CheckOut");
        _output.WriteLine($"POST {checkOut.Path} body={checkOut.Body}");
        Assert.Contains("\"Ids\":[7,8]", checkOut.Body, StringComparison.Ordinal);
        Assert.Contains("application/json", checkOut.ContentType, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(requests, r => r.Path == "/api/VersionControl/UndoCheckOut");
    }

    [Fact]
    public async Task CheckOut_reports_failure_when_the_server_refuses()
    {
        using var server = new VersionStub { CheckOutStatus = 500 };
        var client = new DaminionApiClient($"http://127.0.0.1:{server.Port}", "user", "pass");

        Assert.False(await client.CheckOutItemsAsync(new[] { 3 }));
    }

    [Fact]
    public async Task CheckOut_of_nothing_makes_no_request()
    {
        using var server = new VersionStub();
        var client = new DaminionApiClient($"http://127.0.0.1:{server.Port}", "user", "pass");

        Assert.True(await client.CheckOutItemsAsync(Array.Empty<int>()));
        Assert.Empty(server.Snapshot());
    }

    [Fact]
    public async Task UndoCheckOut_posts_the_ids_body_to_the_undo_route()
    {
        using var server = new VersionStub();
        var client = new DaminionApiClient($"http://127.0.0.1:{server.Port}", "user", "pass");

        Assert.True(await client.UndoCheckOutItemsAsync(new[] { 9 }));

        var undo = Assert.Single(server.Snapshot(), r => r.Path == "/api/VersionControl/UndoCheckOut");
        _output.WriteLine($"POST {undo.Path} body={undo.Body}");
        Assert.Contains("\"Ids\":[9]", undo.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckIn_uploads_id_comment_and_file_as_multipart()
    {
        using var server = new VersionStub();
        var client = new DaminionApiClient($"http://127.0.0.1:{server.Port}", "user", "pass");
        var file = WriteTempFile("ENHANCED-PIXELS");
        try
        {
            const string comment = "Upscaled using quality 2x (precision=auto, output=JPEG, quality=95, denoise=1.00, sharpen=0.00)";
            Assert.True(await client.CheckInItemAsync(42, file, comment));

            var requests = server.Snapshot();
            var checkIn = Assert.Single(requests, r => r.Path == "/api/VersionControl/CheckIn");
            _output.WriteLine($"POST {checkIn.Path} type={checkIn.ContentType}");
            Assert.Contains("multipart/form-data", checkIn.ContentType, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("name=id", checkIn.Body, StringComparison.Ordinal);
            Assert.Contains("name=comment", checkIn.Body, StringComparison.Ordinal);
            Assert.Contains(comment, checkIn.Body, StringComparison.Ordinal);
            Assert.Contains("name=file", checkIn.Body, StringComparison.Ordinal);
            Assert.Contains("ENHANCED-PIXELS", checkIn.Body, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task CheckIn_retries_without_the_comment_when_the_server_rejects_it()
    {
        using var server = new VersionStub { CheckInStatus = attempt => attempt == 1 ? 400 : 200 };
        var client = new DaminionApiClient($"http://127.0.0.1:{server.Port}", "user", "pass");
        var file = WriteTempFile("ENHANCED-PIXELS");
        try
        {
            Assert.True(await client.CheckInItemAsync(5, file, "message"));

            var checkIns = server.Snapshot().Where(r => r.Path == "/api/VersionControl/CheckIn").ToList();
            Assert.Equal(2, checkIns.Count);
            // First attempt carries the comment; the fallback drops it but still
            // uploads the file â€” exactly what daminion_api.py does.
            Assert.Contains("name=comment", checkIns[0].Body, StringComparison.Ordinal);
            Assert.DoesNotContain("name=comment", checkIns[1].Body, StringComparison.Ordinal);
            Assert.Contains("name=id", checkIns[1].Body, StringComparison.Ordinal);
            Assert.Contains("name=file", checkIns[1].Body, StringComparison.Ordinal);
            Assert.Contains("ENHANCED-PIXELS", checkIns[1].Body, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task CheckIn_reports_failure_when_both_attempts_are_refused()
    {
        using var server = new VersionStub { CheckInStatus = _ => 500 };
        var client = new DaminionApiClient($"http://127.0.0.1:{server.Port}", "user", "pass");
        var file = WriteTempFile("ENHANCED-PIXELS");
        try
        {
            Assert.False(await client.CheckInItemAsync(5, file, "message"));
            Assert.Equal(2, server.Snapshot().Count(r => r.Path == "/api/VersionControl/CheckIn"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task CheckIn_of_a_missing_file_makes_no_request()
    {
        using var server = new VersionStub();
        var client = new DaminionApiClient($"http://127.0.0.1:{server.Port}", "user", "pass");

        Assert.False(await client.CheckInItemAsync(1, "does-not-exist.jpg", "message"));
        Assert.DoesNotContain(server.Snapshot(), r => r.Path == "/api/VersionControl/CheckIn");
    }

    [Fact]
    public async Task CheckOut_and_CheckIn_reject_http_200_application_error_envelopes()
    {
        using var server = new VersionStub
        {
            CheckOutBody = """{"data":null,"error":"Item is locked by another user","success":false,"errorCode":403}""",
            CheckInBody = _ => """{"data":{"5":-1},"error":null,"success":true,"errorCode":0}""",
        };
        var client = new DaminionApiClient($"http://127.0.0.1:{server.Port}", "user", "pass");
        var file = WriteTempFile("ENHANCED-PIXELS");
        try
        {
            Assert.False(await client.CheckOutItemsAsync(new[] { 5 }));
            Assert.False(await client.CheckInItemAsync(5, file, "comment"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task VersionControl_recovers_expired_session_on_401_and_replays_once()
    {
        var checkOutAttempt = 0;
        using var server = new VersionStub
        {
            CheckOutStatusSelector = () => ++checkOutAttempt == 1 ? 401 : 200,
        };
        var client = new DaminionApiClient($"http://127.0.0.1:{server.Port}", "user", "pass");

        Assert.True(await client.CheckOutItemsAsync(new[] { 12 }));
        Assert.Equal(2, server.Snapshot().Count(r => r.Path == "/api/VersionControl/CheckOut"));
        Assert.Equal(1, server.LoginCount);
    }

    [Fact]
    public async Task Handler_completes_checkout_download_upscale_checkin_and_preserves_original_filename()
    {
        using var server = new VersionStub { OriginalBytes = Encoding.UTF8.GetBytes("ORIGINAL-IMAGE") };
        var tempDir = Directory.CreateTempSubdirectory("synapic-vc-ok-").FullName;
        var client = new DaminionApiClient($"http://127.0.0.1:{server.Port}", "user", "pass", tempDirectory: tempDir);
        var sidecar = new StubUpscaleSidecar(req =>
        {
            var outPath = Path.Combine(Path.GetDirectoryName(req.ImagePath)!, "42_original_upscaled.png");
            File.WriteAllText(outPath, "UPSCALED-IMAGE");
            return Task.FromResult(new UpscaleResponse
            {
                OutputPath = outPath,
                OriginalWidth = 100,
                OriginalHeight = 80,
                Width = 200,
                Height = 160,
                Workflow = "quality",
                Factor = 2,
                ModelUsed = "caidas/swin2SR-classical-sr-x2-64",
                InferenceMs = 45,
            });
        });

        try
        {
            var logs = new List<string>();
            var handler = new UpscaleItemHandler(client, sidecar, new UpscaleOptions
            {
                Workflow = "quality",
                Factor = 2,
                OutputFormat = "PNG",
            }, line => { logs.Add(line); return Task.CompletedTask; });

            var outcome = await handler.ProcessAsync(new ProcessWorkItem
            {
                DaminionId = 42,
                FileName = " alpine_lake.jpg ",
            }, CancellationToken.None);

            Assert.True(outcome.Success);
            Assert.Equal("Upscaled to alpine_lake.png", outcome.Status);

            var requests = server.Snapshot();
            Assert.Single(requests, r => r.Path == "/api/VersionControl/CheckOut");
            var checkIn = Assert.Single(requests, r => r.Path == "/api/VersionControl/CheckIn");
            Assert.Contains("filename=alpine_lake.png", checkIn.Body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("UPSCALED-IMAGE", checkIn.Body, StringComparison.Ordinal);
            Assert.DoesNotContain(requests, r => r.Path == "/api/VersionControl/UndoCheckOut");
            Assert.Empty(Directory.EnumerateFiles(tempDir));
            Assert.Contains(logs, l => l.Contains("Checked in alpine_lake.jpg (#42) as alpine_lake.png", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Handler_undoes_checkout_when_download_fails()
    {
        using var server = new VersionStub { DownloadStatus = 404 };
        var tempDir = Directory.CreateTempSubdirectory("synapic-vc-dlfail-").FullName;
        var client = new DaminionApiClient($"http://127.0.0.1:{server.Port}", "user", "pass", tempDirectory: tempDir);
        var sidecar = new StubUpscaleSidecar(_ => Task.FromResult(new UpscaleResponse()));

        try
        {
            var handler = new UpscaleItemHandler(client, sidecar, new UpscaleOptions());
            var outcome = await handler.ProcessAsync(new ProcessWorkItem
            {
                DaminionId = 17,
                FileName = "missing.jpg",
            }, CancellationToken.None);

            Assert.False(outcome.Success);
            Assert.Equal("download failed", outcome.Status);

            var requests = server.Snapshot();
            Assert.Single(requests, r => r.Path == "/api/VersionControl/CheckOut");
            var undo = Assert.Single(requests, r => r.Path == "/api/VersionControl/UndoCheckOut");
            Assert.Contains("\"Ids\":[17]", undo.Body, StringComparison.Ordinal);
            Assert.DoesNotContain(requests, r => r.Path == "/api/VersionControl/CheckIn");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Handler_undoes_checkout_when_upscale_fails_or_throws()
    {
        using var server = new VersionStub { OriginalBytes = Encoding.UTF8.GetBytes("ORIGINAL-IMAGE") };
        var tempDir = Directory.CreateTempSubdirectory("synapic-vc-upfail-").FullName;
        var client = new DaminionApiClient($"http://127.0.0.1:{server.Port}", "user", "pass", tempDirectory: tempDir);
        var sidecar = new StubUpscaleSidecar(_ => throw new InvalidOperationException("GPU out of memory"));

        try
        {
            var handler = new UpscaleItemHandler(client, sidecar, new UpscaleOptions());
            var outcome = await handler.ProcessAsync(new ProcessWorkItem
            {
                DaminionId = 23,
                FileName = "broken.jpg",
            }, CancellationToken.None);

            Assert.False(outcome.Success);
            Assert.Equal("upscale failed", outcome.Status);

            var requests = server.Snapshot();
            Assert.Single(requests, r => r.Path == "/api/VersionControl/CheckOut");
            var undo = Assert.Single(requests, r => r.Path == "/api/VersionControl/UndoCheckOut");
            Assert.Contains("\"Ids\":[23]", undo.Body, StringComparison.Ordinal);
            Assert.DoesNotContain(requests, r => r.Path == "/api/VersionControl/CheckIn");
            Assert.Empty(Directory.EnumerateFiles(tempDir));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Handler_undoes_checkout_when_cancelled_mid_item()
    {
        using var server = new VersionStub { OriginalBytes = Encoding.UTF8.GetBytes("ORIGINAL-IMAGE") };
        var tempDir = Directory.CreateTempSubdirectory("synapic-vc-cancel-").FullName;
        var client = new DaminionApiClient($"http://127.0.0.1:{server.Port}", "user", "pass", tempDirectory: tempDir);
        using var cts = new CancellationTokenSource();
        var sidecar = new StubUpscaleSidecar(_ =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return Task.FromResult(new UpscaleResponse());
        });

        try
        {
            var handler = new UpscaleItemHandler(client, sidecar, new UpscaleOptions());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                handler.ProcessAsync(new ProcessWorkItem
                {
                    DaminionId = 31,
                    FileName = "cancelled.jpg",
                }, cts.Token));

            var requests = server.Snapshot();
            Assert.Single(requests, r => r.Path == "/api/VersionControl/CheckOut");
            var undo = Assert.Single(requests, r => r.Path == "/api/VersionControl/UndoCheckOut");
            Assert.Contains("\"Ids\":[31]", undo.Body, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(tempDir));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private sealed class StubUpscaleSidecar : IInferenceSidecar
    {
        private readonly Func<UpscaleRequest, Task<UpscaleResponse>> _upscale;

        public StubUpscaleSidecar(Func<UpscaleRequest, Task<UpscaleResponse>> upscale) => _upscale = upscale;

        public SidecarStatus CurrentStatus => SidecarStatus.Ready;
        public int SidecarPort => 0;
        public bool IsRunning => true;
#pragma warning disable CS0067
        public event EventHandler<SidecarStatusChangedEventArgs>? StatusChanged;
        public event Action<string>? LogReceived;
#pragma warning restore CS0067
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(TimeSpan? gracefulTimeout = null) => Task.CompletedTask;
        public Task<TagResponse> TagAsync(TagRequest request, CancellationToken ct = default)
            => Task.FromResult(new TagResponse());
        public Task<UpscaleResponse> UpscaleAsync(UpscaleRequest request, CancellationToken ct = default)
            => _upscale(request);
        public Task<ModelInfo[]> ListModelsAsync(CancellationToken ct = default)
            => Task.FromResult(Array.Empty<ModelInfo>());
        public Task DownloadModelAsync(string modelId, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<HealthResponse> GetHealthAsync(CancellationToken ct = default)
            => Task.FromResult(new HealthResponse());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string WriteTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"synapic-checkin-{Guid.NewGuid():N}.jpg");
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Tiny HTTP/1.1 responder over TcpListener (no http.sys, no URL ACL)
    /// that records every version-control request and answers with configurable
    /// status codes â€” the comment-fallback path needs both.</summary>
    private sealed class VersionStub : IDisposable
    {
        public sealed record Record(string Path, string Body, string? ContentType);

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _lock = new();
        private readonly List<Record> _requests = new();
        private int _checkInAttempts;
        private int _loginCount;

        public int Port { get; }
        public int LoginCount => Volatile.Read(ref _loginCount);
        public int CheckOutStatus { get; set; } = 200;
        public Func<int>? CheckOutStatusSelector { get; set; }
        public string CheckOutBody { get; set; } = "{}";
        public int UndoCheckOutStatus { get; set; } = 200;
        public string UndoCheckOutBody { get; set; } = "{}";
        public int DownloadStatus { get; set; } = 200;
        public byte[] OriginalBytes { get; set; } = Encoding.UTF8.GetBytes("STUB-ORIGINAL");

        /// <summary>attempt (1-based) â†’ HTTP status for CheckIn.</summary>
        public Func<int, int> CheckInStatus { get; set; } = _ => 200;
        public Func<int, string> CheckInBody { get; set; } = _ => "{}";

        public VersionStub()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public List<Record> Snapshot()
        {
            lock (_lock) return _requests.ToList();
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
                var contentType = HeaderValue(head, "Content-Type");

                if (path.StartsWith("/api/Download/Get/", StringComparison.Ordinal))
                {
                    AddRequest(path, "", contentType);
                    var dlBytes = DownloadStatus == 200 ? OriginalBytes : Array.Empty<byte>();
                    var dlHeaders = Encoding.UTF8.GetBytes(
                        $"HTTP/1.1 {DownloadStatus} Stub\r\n" +
                        "Content-Type: application/octet-stream\r\n" +
                        $"Content-Length: {dlBytes.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(dlHeaders.Concat(dlBytes).ToArray(), ct);
                    await stream.FlushAsync(ct);
                    return;
                }

                int status;
                string responseBody = "{}";
                if (path == "/api/VersionControl/CheckOut")
                {
                    AddRequest(path, body, contentType);
                    status = CheckOutStatusSelector?.Invoke() ?? CheckOutStatus;
                    responseBody = CheckOutBody;
                }
                else if (path == "/api/VersionControl/UndoCheckOut")
                {
                    AddRequest(path, body, contentType);
                    status = UndoCheckOutStatus;
                    responseBody = UndoCheckOutBody;
                }
                else if (path == "/api/VersionControl/CheckIn")
                {
                    int attempt;
                    lock (_lock) attempt = ++_checkInAttempts;
                    AddRequest(path, body, contentType);
                    status = CheckInStatus(attempt);
                    responseBody = CheckInBody(attempt);
                }
                else
                {
                    if (path.StartsWith("/api/UserManager/Login", StringComparison.Ordinal))
                        Interlocked.Increment(ref _loginCount);
                    // Login / GetDefaultLayout / GetTags during AuthenticateAsync.
                    status = 200;
                }

                var payload = Encoding.UTF8.GetBytes(responseBody);
                var response = Encoding.UTF8.GetBytes(
                    $"HTTP/1.1 {status} Stub\r\n" +
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

        private void AddRequest(string path, string body, string? contentType)
        {
            lock (_lock) _requests.Add(new Record(path, body, contentType));
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

            if (HeaderValue(head, "Transfer-Encoding")?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true)
            {
                // Refit streams multipart bodies without a Content-Length:
                // keep reading until the terminal chunk, then de-chunk so the
                // assertions see the plain form-data text.
                while (!body.StartsWith("0\r\n\r\n", StringComparison.Ordinal) &&
                       !body.Contains("\r\n0\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer, ct);
                    if (read == 0) break;
                    body += Encoding.UTF8.GetString(buffer, 0, read);
                }
                body = Dechunk(body);
            }
            else
            {
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
            }

            return (head, body);
        }

        /// <summary>Decode an HTTP/1.1 chunked payload into its plain body.</summary>
        private static string Dechunk(string raw)
        {
            var result = new StringBuilder();
            var pos = 0;
            while (pos < raw.Length)
            {
                var lineEnd = raw.IndexOf("\r\n", pos, StringComparison.Ordinal);
                if (lineEnd < 0) break;
                var sizeLine = raw[pos..lineEnd];
                var extension = sizeLine.IndexOf(';');
                if (extension >= 0) sizeLine = sizeLine[..extension];
                if (!int.TryParse(sizeLine, System.Globalization.NumberStyles.HexNumber, null, out var size))
                    break;
                pos = lineEnd + 2;
                if (size == 0) break;                       // terminal chunk
                if (pos + size > raw.Length) break;         // truncated â€” keep what we have
                result.Append(raw, pos, size);
                pos += size + 2;                            // skip the chunk's trailing CRLF
            }
            return result.ToString();
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}

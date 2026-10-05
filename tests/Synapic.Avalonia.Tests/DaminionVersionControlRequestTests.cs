using System.Net;
using System.Net.Sockets;
using System.Text;
using Synapic.Avalonia.Services.Daminion;
using Xunit;
using Xunit.Abstractions;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// The upscale flow's catalog dance (daminion_api.py VersionControlAPI port):
/// CheckOut/UndoCheckOut must post {"Ids":[…]} to /api/VersionControl/…, and
/// CheckIn must be multipart (id + comment + file), retrying without the
/// comment when the server rejects it. A wrong field name here makes Daminion
/// silently ignore the call, so the request shape is what this asserts —
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
            // uploads the file — exactly what daminion_api.py does.
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

    private static string WriteTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"synapic-checkin-{Guid.NewGuid():N}.jpg");
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Tiny HTTP/1.1 responder over TcpListener (no http.sys, no URL ACL)
    /// that records every version-control request and answers with configurable
    /// status codes — the comment-fallback path needs both.</summary>
    private sealed class VersionStub : IDisposable
    {
        public sealed record Record(string Path, string Body, string? ContentType);

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _lock = new();
        private readonly List<Record> _requests = new();
        private int _checkInAttempts;

        public int Port { get; }
        public int CheckOutStatus { get; set; } = 200;
        public int UndoCheckOutStatus { get; set; } = 200;

        /// <summary>attempt (1-based) → HTTP status for CheckIn.</summary>
        public Func<int, int> CheckInStatus { get; set; } = _ => 200;

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

                int status;
                string responseBody = "{}";
                if (path == "/api/VersionControl/CheckOut")
                {
                    AddRequest(path, body, contentType);
                    status = CheckOutStatus;
                }
                else if (path == "/api/VersionControl/UndoCheckOut")
                {
                    AddRequest(path, body, contentType);
                    status = UndoCheckOutStatus;
                }
                else if (path == "/api/VersionControl/CheckIn")
                {
                    int attempt;
                    lock (_lock) attempt = ++_checkInAttempts;
                    AddRequest(path, body, contentType);
                    status = CheckInStatus(attempt);
                }
                else
                {
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
                // Client went away mid-request — irrelevant to the assertions.
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
                if (pos + size > raw.Length) break;         // truncated — keep what we have
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

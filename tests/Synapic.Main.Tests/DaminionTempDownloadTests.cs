using System.Net;
using System.Net.Sockets;
using System.Text;
using Synapic.Main.Services.Daminion;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// Downloads are streamed into a temp folder and deleted again — Synapic keeps
/// no image cache, so the folder must never grow. These tests pin the three
/// ways that could still happen: a download that dies mid-body leaving a
/// partial file, a crash leaving whole originals behind for ever, and the
/// sweep being so eager it deletes a download that is still in flight.
/// </summary>
public class DaminionTempDownloadTests
{
    private static string NewTempDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"synapic-test-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public async Task Truncated_download_returns_null_and_leaves_no_partial_file()
    {
        var dir = NewTempDir();
        try
        {
            using var server = new RawBodyServer(contentLength: 100000, body: new byte[64]);
            var client = new DaminionApiClient($"http://127.0.0.1:{server.Port}", "user", "pass",
                tempDirectory: dir);

            var path = await client.DownloadOriginalAsync(42);

            Assert.True(string.IsNullOrEmpty(path));           // caller sees a plain failure…
            Assert.Empty(Directory.EnumerateFiles(dir));        // …and no half-written file is kept
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Download_lands_in_the_configured_directory_and_is_the_callers_to_delete()
    {
        var dir = NewTempDir();
        try
        {
            var body = new byte[] { 1, 2, 3, 4 };
            using var server = new RawBodyServer(contentLength: body.Length, body: body);
            var client = new DaminionApiClient($"http://127.0.0.1:{server.Port}", "user", "pass",
                tempDirectory: dir);
            Assert.Equal(dir, client.TempDirectory);

            var path = await client.DownloadOriginalAsync(42);

            Assert.Equal(Path.Combine(dir, "42_original"), path);
            Assert.Equal(body, await File.ReadAllBytesAsync(path!));

            // What the dedup scan does with it: read, then delete — the folder
            // is empty again the moment the item has been hashed.
            File.Delete(path!);
            Assert.Equal((0, 0), DaminionApiClient.TempDirectoryUsage(dir));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Sweep_removes_only_the_leftovers_and_reports_what_it_freed()
    {
        var dir = NewTempDir();
        try
        {
            var stale = Path.Combine(dir, "7_original");
            File.WriteAllBytes(stale, new byte[2048]);
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));

            var fresh = Path.Combine(dir, "8_original");
            File.WriteAllBytes(fresh, new byte[512]);          // written moments ago: in use

            var (files, bytes) = DaminionApiClient.CleanupStaleDownloads(directory: dir);

            Assert.Equal(1, files);
            Assert.Equal(2048, bytes);
            Assert.False(File.Exists(stale));                  // the crash leftover is gone
            Assert.True(File.Exists(fresh));                   // the fresh one is untouched
            Assert.Equal((1, 512), DaminionApiClient.TempDirectoryUsage(dir));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Sweep_on_a_missing_directory_is_a_no_op()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"synapic-test-{Guid.NewGuid():N}");

        Assert.Equal((0, 0), DaminionApiClient.CleanupStaleDownloads(directory: missing));
        Assert.Equal((0, 0), DaminionApiClient.TempDirectoryUsage(missing));
    }

    [Fact]
    public void Stale_cutoff_is_longer_than_the_http_timeout_so_a_live_download_survives()
    {
        // The sweep only removes downloads old enough that nothing can still be
        // writing them; the client's 15-minute request timeout is the ceiling.
        Assert.True(DaminionApiClient.StaleDownloadAge > TimeSpan.FromMinutes(15),
            $"cutoff {DaminionApiClient.StaleDownloadAge} must exceed the 15 min HttpClient timeout");
        Assert.True(DaminionApiClient.StaleDownloadAge >= TimeSpan.FromHours(1));
    }

    /// <summary>Answers 200 with a fixed Content-Length and body. When the
    /// declared length exceeds what is sent, the body read dies mid-transfer —
    /// exactly what a dropped LAN link produces.</summary>
    private sealed class RawBodyServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly int _contentLength;
        private readonly byte[] _body;

        public int Port { get; }

        public RawBodyServer(int contentLength, byte[] body)
        {
            _contentLength = contentLength;
            _body = body;
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
                var buffer = new byte[8192];
                var read = await stream.ReadAsync(buffer, ct);   // request head; we never parse it
                if (read == 0) return;

                var head = Encoding.UTF8.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: application/octet-stream\r\n" +
                    $"Content-Length: {_contentLength}\r\n" +
                    "Connection: close\r\n\r\n");
                await stream.WriteAsync(head, ct);
                await stream.WriteAsync(_body, ct);
                await stream.FlushAsync(ct);
            }
            catch
            {
                // The client aborts the read the moment the body comes up short.
            }

            // Truncated mode: the stream is left open for the client to notice
            // the shortfall, then closed by disposing the TcpClient above.
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}

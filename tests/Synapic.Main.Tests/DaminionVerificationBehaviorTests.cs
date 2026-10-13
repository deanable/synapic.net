using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Synapic.Main.Models;
using Synapic.Main.Services;
using Synapic.Main.Services.Daminion;
using Synapic.Main.Services.Processing;
using Synapic.Main.ViewModels.Steps;
using Synapic.Main.Views.Wizard;
using Xunit;

namespace Synapic.Main.Tests;

public sealed class DaminionVerificationBehaviorTests
{
    [AvaloniaFact]
    public async Task Verification_updates_verified_rows_and_preserves_mismatches_in_report()
    {
        using var server = new CatalogServer(path => path switch
        {
            "/api/ItemData/GetAll/41" => "{\"Category\":\"Nature\",\"Keyword\":\"tree\",\"Description\":\"A forest at dawn\"}",
            "/api/ItemData/GetAll/42" => "{\"Category\":\"\",\"Keyword\":\"\",\"Description\":\"\"}",
            _ => null,
        });
        var session = new Session();
        session.Datasource.Type = "daminion";
        session.Results.Add(Result("verified.jpg", ProcessStatus.SuccessRepaired, 41));
        session.Results.Add(Result("mismatch.jpg", ProcessStatus.Success, 42));
        var source = await ConnectAsync(server, session);
        var vm = ViewModel(session, source);
        vm.Refresh();
        var report = new Step4Results { DataContext = vm };
        var window = new Window { Content = report, Width = 1100, Height = 700 };
        window.Show();
        try
        {
            await vm.VerifyDaminionCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Verified 1/2 Daminion writes — 1 mismatched", vm.Summary);
            Assert.Equal(ProcessStatus.VerifiedRepaired, session.Results.Single(r => r.DaminionId == 41).Status);
            Assert.Equal(ProcessStatus.Success, session.Results.Single(r => r.DaminionId == 42).Status);
            Assert.Contains(SynapicLog.UiSink.Snapshot(), entry =>
                entry.RenderedMessage.Contains("Item 42: not found — category 'Nature'; keywords 'tree'; description", StringComparison.Ordinal));
            Assert.Contains("/api/ItemData/GetAll/41", server.Routes);
            Assert.Contains("/api/ItemData/GetAll/42", server.Routes);
            Assert.False(vm.IsBusy);
            Assert.Contains(report.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == vm.Summary && text.IsVisible);
        }
        finally
        {
            window.Close();
            source.DisconnectCommand.Execute(null);
        }
    }

    [Fact]
    public async Task Invalid_verification_payload_is_reported_without_marking_row_verified()
    {
        using var server = new CatalogServer(path => path == "/api/ItemData/GetAll/51" ? "not valid json" : null);
        var session = new Session();
        session.Datasource.Type = "daminion";
        session.Results.Add(Result("failed-verify.jpg", ProcessStatus.Success, 51));
        var source = await ConnectAsync(server, session);
        var vm = ViewModel(session, source);
        vm.Refresh();

        try
        {
            await vm.VerifyDaminionCommand.ExecuteAsync(null);

            Assert.Equal("Verified 0/1 Daminion writes — 1 mismatched", vm.Summary);
            Assert.Equal(ProcessStatus.Success, Assert.Single(session.Results).Status);
            Assert.Contains(SynapicLog.UiSink.Snapshot(), entry =>
                entry.RenderedMessage.Contains("Item 51: verify failed —", StringComparison.Ordinal));
            Assert.False(vm.IsBusy);
        }
        finally
        {
            source.DisconnectCommand.Execute(null);
        }
    }

    [AvaloniaFact]
    public async Task Verification_cancellation_reports_partial_progress_and_preserves_unverified_rows()
    {
        var verifyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new CatalogServer(_ => null, async (path, ct) =>
        {
            if (path == "/api/ItemData/GetAll/61")
            {
                verifyStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return null;
        });
        var session = new Session();
        session.Datasource.Type = "daminion";
        session.Results.Add(Result("pending-verify.jpg", ProcessStatus.Success, 61));
        var source = await ConnectAsync(server, session);
        var vm = ViewModel(session, source);
        vm.Refresh();
        var report = new Step4Results { DataContext = vm };
        var window = new Window { Content = report, Width = 1100, Height = 700 };
        window.Show();
        try
        {
            var verification = vm.VerifyDaminionCommand.ExecuteAsync(null);
            await verifyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(vm.IsBusy);
            Assert.True(vm.IsVerifying);
            Assert.False(vm.RetryFailedCommand.CanExecute(null));
            Assert.False(report.GetVisualDescendants().OfType<Button>()
                .Single(b => (string?)b.Content == "Retry Failed").IsEnabled);
            var cancelButton = report.FindControl<Button>("CancelVerificationButton")!;
            Assert.True(cancelButton.IsVisible);
            Assert.True(cancelButton.IsEnabled);

            cancelButton.Command!.Execute(null);
            await verification;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Verification cancelled (0/1 verified)", vm.Summary);
            Assert.Equal(ProcessStatus.Success, Assert.Single(session.Results).Status);
            Assert.False(vm.IsVerifying);
            Assert.False(vm.IsBusy);
            Assert.False(cancelButton.IsVisible);
            Assert.Contains(report.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == vm.Summary && text.IsVisible);
        }
        finally
        {
            window.Close();
            source.DisconnectCommand.Execute(null);
        }
    }

    [AvaloniaFact]
    public async Task Connected_catalog_with_no_Daminion_targets_reports_empty_verification_state()
    {
        using var server = new CatalogServer(_ => null);
        var session = new Session();
        session.Datasource.Type = "daminion";
        session.Results.Add(Result("local-only.jpg", ProcessStatus.Success, null));
        var source = await ConnectAsync(server, session);
        var vm = ViewModel(session, source);
        vm.Refresh();
        var report = new Step4Results { DataContext = vm };
        var window = new Window { Content = report, Width = 1100, Height = 700 };
        window.Show();
        try
        {
            await vm.VerifyDaminionCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("No Daminion-backed results to verify", vm.Summary);
            Assert.Single(vm.Results);
            Assert.DoesNotContain(server.Routes, route => route.StartsWith("/api/ItemData/GetAll/", StringComparison.Ordinal));
            Assert.Contains(report.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == vm.Summary && text.IsVisible);
            Assert.False(vm.IsBusy);
        }
        finally
        {
            window.Close();
            source.DisconnectCommand.Execute(null);
        }
    }

    private static ProcessItemResult Result(string name, string status, int? id) =>
        new(name, status, "Cat: Nature", "Nature", ["tree"], "A forest at dawn", null, null, id);

    private static Step4ResultsViewModel ViewModel(Session session, Step1DatasourceViewModel source) =>
        new(session, source, new Step3ProcessViewModel(session, new FakeSidecar(), source));

    private static async Task<Step1DatasourceViewModel> ConnectAsync(CatalogServer server, Session session)
    {
        var source = new Step1DatasourceViewModel(session)
        {
            DaminionUrl = server.BaseUrl,
            DaminionUser = "test",
            DaminionPass = "test",
        };
        await source.ConnectCommand.ExecuteAsync(null);
        Assert.True(source.IsDaminionConnected, source.ConnectionMessage);
        return source;
    }

    private sealed class CatalogServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<string, string?> _response;
        private readonly Func<string, CancellationToken, Task<string?>>? _asyncResponse;
        private readonly ConcurrentQueue<string> _routes = new();
        public string BaseUrl { get; }
        public string[] Routes => _routes.ToArray();

        public CatalogServer(
            Func<string, string?> response,
            Func<string, CancellationToken, Task<string?>>? asyncResponse = null)
        {
            _response = response;
            _asyncResponse = asyncResponse;
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var request = new StringBuilder();
                var buffer = new byte[2048];
                while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var count = await stream.ReadAsync(buffer, _stop.Token);
                    if (count == 0) return;
                    request.Append(Encoding.ASCII.GetString(buffer, 0, count));
                }
                var line = request.ToString().Split('\r', '\n')[0];
                var path = line.Split(' ')[1].Split('?')[0];
                _routes.Enqueue(path);
                var body = _asyncResponse is { } asyncResponse
                    ? await asyncResponse(path, _stop.Token)
                    : _response(path);
                body ??= path switch
                {
                    "/api/MediaItems/Get" => "{\"items\":[],\"totalCount\":0}",
                    "/api/MediaItems/GetCount" => "{\"count\":0}",
                    "/api/Settings/GetCatalogGuid" => "{\"guid\":\"test-catalog\"}",
                    "/api/IndexedTagValues/GetIndexedTagValues" or "/api/IndexedTagValues" => "[]",
                    "/api/Collections/Get" => "[]",
                    _ => "{}",
                };
                var payload = Encoding.UTF8.GetBytes(body);
                var header = Encoding.UTF8.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header.Concat(payload).ToArray(), _stop.Token);
                await stream.FlushAsync(_stop.Token);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}

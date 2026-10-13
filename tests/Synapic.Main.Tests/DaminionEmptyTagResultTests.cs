using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Synapic.Main.Models;
using Synapic.Main.ViewModels.Steps;
using Synapic.Main.Views.Operation;
using Synapic.Main.Views.Wizard;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// The Daminion catalog can be connected and filtered while containing no
/// eligible images. Keep that result distinct from a failed connection and
/// expose it through tagging's Output run surface without a live catalog.
/// </summary>
public sealed class DaminionEmptyTagResultTests
{
    [AvaloniaFact]
    public async Task Empty_Daminion_scope_reports_guidance_in_tag_output()
    {
        using var server = new EmptyCatalogServer();
        var session = new Session();
        session.Datasource.Type = "daminion";
        var source = new Step1DatasourceViewModel(session)
        {
            DaminionUrl = $"http://127.0.0.1:{server.Port}",
            DaminionUser = "test",
            DaminionPass = "test",
        };
        await source.ConnectCommand.ExecuteAsync(null);
        Assert.True(source.IsDaminionConnected, source.ConnectionMessage);

        var run = new Step3ProcessViewModel(session, new FakeSidecar(), source);
        var bar = new RunStateBar { DataContext = run };
        var window = new Window { Content = bar, Width = 900, Height = 600 };
        window.Show();
        try
        {
            await run.StartCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, session.TotalItems);
            Assert.Empty(session.Results);
            Assert.Contains(run.LogLines, line => line.Contains(
                "No images matched this source and its filters — choose a folder or scope with images, then run tagging again.",
                StringComparison.Ordinal));
            Assert.Equal("No items matched the current filters", run.ProgressText);
            Assert.Contains(bar.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "No items matched the current filters" && text.IsVisible);
        }
        finally
        {
            window.Close();
            source.DisconnectCommand.Execute(null);
        }
    }

    [AvaloniaFact]
    public async Task Empty_Daminion_scope_reports_guidance_in_upscale_run()
    {
        using var server = new EmptyCatalogServer();
        var session = new Session();
        session.Datasource.Type = "daminion";
        var source = new Step1DatasourceViewModel(session)
        {
            DaminionUrl = $"http://127.0.0.1:{server.Port}",
            DaminionUser = "test",
            DaminionPass = "test",
        };
        await source.ConnectCommand.ExecuteAsync(null);
        Assert.True(source.IsDaminionConnected, source.ConnectionMessage);

        var vm = new StepUpscaleViewModel(source, new FakeSidecar { IsRunning = true });
        var bar = new RunStateBar { DataContext = vm };
        var window = new Window { Content = bar, Width = 900, Height = 600 };
        window.Show();
        try
        {
            await vm.StartCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            const string guidance = "No images were found in this source — choose a folder or catalog scope with images, then run upscale again.";
            Assert.Equal(guidance, vm.ProgressText);
            Assert.Contains(vm.LogLines, line => line.Contains(guidance, StringComparison.Ordinal));
            Assert.Contains(bar.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == guidance && text.IsVisible);
        }
        finally
        {
            window.Close();
            source.DisconnectCommand.Execute(null);
        }
    }

    [AvaloniaFact]
    public async Task Empty_Daminion_scope_reports_guidance_in_dedup_scan()
    {
        using var server = new EmptyCatalogServer();
        var session = new Session();
        session.Datasource.Type = "daminion";
        var source = new Step1DatasourceViewModel(session)
        {
            DaminionUrl = $"http://127.0.0.1:{server.Port}",
            DaminionUser = "test",
            DaminionPass = "test",
        };
        await source.ConnectCommand.ExecuteAsync(null);
        Assert.True(source.IsDaminionConnected, source.ConnectionMessage);

        var vm = new StepDedupViewModel(step1: source);
        var view = new StepDedup { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 700 };
        window.Show();
        try
        {
            await vm.ScanCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            const string guidance = "No images found in this source — choose a folder or scope with images, then scan again.";
            Assert.Equal(guidance, vm.ScanSummary);
            Assert.Empty(vm.Groups);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == guidance && text.IsVisible);
        }
        finally
        {
            window.Close();
            source.DisconnectCommand.Execute(null);
        }
    }

    /// <summary>Minimal loopback Daminion API that authenticates but returns no catalog items.</summary>
    internal sealed class EmptyCatalogServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        public int Port { get; }

        public EmptyCatalogServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
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
                var head = new StringBuilder();
                var buffer = new byte[1024];
                while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer, _stop.Token);
                    if (read == 0) return;
                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                var request = head.ToString().Split('\r', '\n')[0];
                var path = request.Split(' ')[1].Split('?')[0];
                var body = path switch
                {
                    "/api/MediaItems/Get" => "{\"items\":[],\"totalCount\":0}",
                    "/api/MediaItems/GetCount" => "{\"count\":0}",
                    "/api/Settings/GetCatalogGuid" => "{\"guid\":\"test-catalog\"}",
                    "/api/IndexedTagValues/GetIndexedTagValues" or "/api/IndexedTagValues" => "[]",
                    "/api/Collections/Get" => "[]",
                    _ => "{}",
                };
                var payload = Encoding.UTF8.GetBytes(body);
                var response = Encoding.UTF8.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n")
                    .Concat(payload).ToArray();
                await stream.WriteAsync(response, _stop.Token);
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

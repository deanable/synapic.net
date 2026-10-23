using Avalonia.Headless.XUnit;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Synapic.Main.Models;
using Synapic.Core.Services.Daminion;
using Synapic.Main.ViewModels.Operations;
using Synapic.Main.ViewModels.Steps;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// The "Reject in catalog" action on the dedup and similar apply cards: it
/// writes the Flag("Rejected") field instead of the destructive catalog
/// delete, so it must never prompt (reversible, unlike delete), must dispatch
/// BatchChange + verification, and must trim the acted-on rows out of the
/// review list without discarding the kept results.
/// </summary>
public class StepDedupRejectActionTests
{
    private static DedupItemViewModel CatalogItem(int id, string name, bool keep) =>
        new($"daminion:{id}", name) { DaminionId = id, IsChecked = keep };

    private static DuplicateGroupViewModel Group(params DedupItemViewModel[] items) =>
        new(items, "phash", 0.95);

    [Fact]
    public void Daminion_actions_list_offers_delete_and_reject()
    {
        var session = new Session();
        session.Datasource.Type = "daminion";
        var vm = new StepDedupViewModel(step1: new Step1DatasourceViewModel(session));

        Assert.Equal(new[] { "Delete from catalog", "Reject in catalog" }, vm.Actions);
    }

    [Fact]
    public async Task Reject_marks_the_items_without_prompting_and_trims_the_groups()
    {
        using var server = new FlagsServer();
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
        vm.Groups.Add(Group(
            CatalogItem(1, "keep-a.jpg", keep: true),
            CatalogItem(10, "keep-a2.jpg", keep: true),
            CatalogItem(7, "dup-a.jpg", keep: false),
            CatalogItem(8, "dup-b.jpg", keep: false))); // two acted → stays with 2 kept rows
        vm.Groups.Add(Group(
            CatalogItem(2, "keep-b.jpg", keep: true),
            CatalogItem(9, "dup-c.jpg", keep: false))); // one acted → reduced to one, leaves

        // Rejection is reversible, so it must go through even with the default
        // null (fail-closed-for-delete) confirm hook, and a declined-prompt
        // hook would prove it never asked.
        var prompts = 0;
        vm.ConfirmAction = _ => { prompts++; return Task.FromResult(false); };
        vm.SelectedAction = 1; // "Reject in catalog"

        Assert.True(vm.ApplyCommand.CanExecute(null));
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(0, prompts); // a reject never asked
        Assert.Contains("\"ids\":[7,8,9]", server.BatchChangeBody, StringComparison.Ordinal);
        Assert.Contains("\"value\":\"Rejected\"", server.BatchChangeBody, StringComparison.Ordinal);
        Assert.NotNull(server.GetByIdsPath); // the write was verified, not assumed

        // dup-a (id 7) is gone but the group survives with its kept rows;
        // the second group was reduced to its keeper and left entirely.
        Assert.Single(vm.Groups);
        var survivors = vm.Groups[0].Items.Select(i => i.DaminionId).ToList();
        Assert.Equal(new int?[] { 1, 10 }, survivors);
        Assert.Contains("Rejected 3 item(s) in the Daminion catalog", vm.ScanSummary, StringComparison.Ordinal);
    }

    /// <summary>Loopback Daminion API: authenticates, offers a Flag tag in the
    /// layout, answers BatchChange and a GetByIds read-back that reports every
    /// id as already rejected.</summary>
    private sealed class FlagsServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        public int Port { get; }

        public string? BatchChangeBody { get; private set; }
        public string? GetByIdsPath { get; private set; }

        public FlagsServer()
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
                var buffer = new byte[4096];
                while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer, _stop.Token);
                    if (read == 0) return;
                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                var full = head.ToString();
                var request = full.Split('\r', '\n')[0];
                var rawPath = request.Split(' ')[1];
                var path = rawPath.Split('?')[0];

                string body;
                if (path.StartsWith("/api/ItemData/BatchChange", StringComparison.Ordinal))
                {
                    // Refit's request is < 4 KB, so the single read above already
                    // delivered it behind the header block.
                    var separator = full.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    BatchChangeBody = separator < 0 ? "" : full[(separator + 4)..];
                    body = """{"success":true,"errorCode":0}""";
                }
                else if (path.StartsWith("/api/MediaItems/GetByIds", StringComparison.Ordinal))
                {
                    GetByIdsPath = rawPath;
                    body = """{"mediaItems":[{"id":7,"flag":2},{"id":8,"flag":2},{"id":9,"flag":2}],"totalCount":3}""";
                }
                else if (path.StartsWith("/api/ItemData/GetDefaultLayout", StringComparison.Ordinal))
                {
                    body = """{"properties":[{"propertyName":"Flag","propertyGuid":"flag-guid"}]}""";
                }
                else if (path.StartsWith("/api/MediaItems/GetCount", StringComparison.Ordinal))
                {
                    body = """{"count":0}""";
                }
                else
                {
                    body = "{}";
                }

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

/// <summary>
/// The shared run bar collapses its log box while nothing has been logged —
/// the grey strip that used to sit under the Scan/Start button on every
/// operation page. <see cref="RunStateViewModel.HasLogLines"/> drives that.
/// </summary>
public class RunLogVisibilityTests
{
    private sealed class TestRunState : RunStateViewModel
    {
        public void Log(string line) => AppendLog(line);
        protected override void NotifyRunCommandsCanExecuteChanged() { }
        protected override bool CanStart() => true;
        protected override Task StartCoreAsync(CancellationToken ct) => System.Threading.Tasks.Task.CompletedTask;
    }

    // Avalonia and on the UI thread: RunLog marshals every append through
    // Dispatcher.UIThread, so a shell-initialized worker thread would miss the
    // line entirely (isolation passes, the full suite fails on the post timing).
    [AvaloniaFact]
    public void HasLogLines_Follows_The_First_And_Later_Lines()
    {
        var vm = new TestRunState();
        Assert.False(vm.HasLogLines);

        vm.Log("scanning page 0…");
        Assert.True(vm.HasLogLines);
    }
}

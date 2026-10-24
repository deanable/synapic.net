using System.Text.Json.Nodes;
using Synapic.Indexer.Contract;
using Synapic.Indexer.Contract.Configuration;
using Synapic.ServiceConsole.Services;
using Synapic.ServiceConsole.ViewModels;
using Xunit;

namespace Synapic.ServiceConsole.Tests;

/// <summary>
/// The console's view model against fakes: the status-to-button mapping, the
/// watch-folder round trip through the one elevated save, the queue view, and
/// the branches where something is missing (no service, no executable, no
/// queue, not Windows) - each of those says so instead of showing nothing. The
/// save facts pin the verified round trip (v1.4): the note reports what the
/// elevated command actually did, never a fire-and-forget "requested".
/// </summary>
public sealed class IndexerServiceViewModelTests
{
    private sealed class FakeMonitor : IIndexerServiceMonitor
    {
        public IndexerServiceStatus? Status { get; set; }
        public IndexerQueueSnapshot Queue { get; set; } =
            new(null, Array.Empty<QueueEvent>(), "no queue yet");
        public ScheduleSettings? Schedule { get; set; }

        public IndexerServiceStatus? QueryStatus() => Status;
        public IndexerQueueSnapshot ReadQueue() => Queue;
        public ScheduleSettings? ReadSchedule() => Schedule;
    }

    private sealed class FakeLauncher : IIndexerProcessLauncher
    {
        public bool Present { get; set; } = true;
        public ApplyConfigOutcome? Outcome { get; set; }
        public TaskCompletionSource<ApplyConfigOutcome?>? InFlight { get; set; }
        public List<string[]> Launches { get; } = new();
        public JsonObject? LastPatch { get; private set; }

        public bool IndexerExecutablePresent => Present;
        public string? WriteConfigPatch(JsonObject patch)
        {
            LastPatch = patch;
            return @"C:\Users\u\AppData\Local\Temp\synapic-apply-config.json";
        }

        public async Task<ApplyConfigOutcome?> ApplyConfigAsync(string patchPath, CancellationToken ct = default)
        {
            Launches.Add(new[] { "--apply-config", patchPath });
            if (InFlight is not null)
                return await InFlight.Task.WaitAsync(ct);
            return Outcome;
        }

        public bool LaunchElevated(params string[] arguments)
        {
            Launches.Add(arguments);
            return true;
        }

        public bool LaunchServiceControl(string scVerb)
        {
            Launches.Add(new[] { "sc.exe", scVerb, "SynapicIndexer" });
            return true;
        }
    }

    private static IndexerServiceViewModel VM(
        FakeMonitor monitor, FakeLauncher launcher) => new(monitor, launcher);

    private static IndexerServiceStatus Running(bool running = true) =>
        new(true, running ? "RUNNING" : "STOPPED", "AUTO_START", running);

    // ------------------------------------------------------------ lifecycle

    [Fact]
    public void A_running_service_enables_stop_and_start_is_refused()
    {
        var vm = VM(new FakeMonitor { Status = Running() }, new FakeLauncher());

        Assert.True(vm.IsInstalled);
        Assert.True(vm.IsRunning);
        Assert.Equal("RUNNING", vm.ServiceStatusText);
        Assert.Equal("AUTO_START", vm.StartTypeText);
        Assert.True(vm.StopCommand.CanExecute(null));
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.False(vm.InstallCommand.CanExecute(null));
        // Uninstall is offered while running: the verb stops the service first.
        Assert.True(vm.UninstallCommand.CanExecute(null));
    }

    [Fact]
    public void An_installed_but_stopped_service_enables_start()
    {
        var vm = VM(new FakeMonitor { Status = Running(running: false) }, new FakeLauncher());

        Assert.False(vm.IsRunning);
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.False(vm.StopCommand.CanExecute(null));
        Assert.True(vm.UninstallCommand.CanExecute(null));
    }

    [Fact]
    public void A_machine_without_the_service_shows_not_installed_and_offers_install()
    {
        var vm = VM(new FakeMonitor { Status = new IndexerServiceStatus(false, "", "", false) },
            new FakeLauncher());

        Assert.False(vm.IsInstalled);
        Assert.Equal("not installed", vm.ServiceStatusText);
        Assert.True(vm.InstallCommand.CanExecute(null));
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.False(vm.StopCommand.CanExecute(null));
    }

    [Fact]
    public void Without_the_indexer_executable_nothing_can_be_launched()
    {
        var vm = VM(new FakeMonitor { Status = new IndexerServiceStatus(false, "", "", false) },
            new FakeLauncher { Present = false });

        Assert.False(vm.InstallCommand.CanExecute(null),
            "install must not be offered when the executable is missing");
        Assert.False(vm.SaveCommand.CanExecute(null),
            "save must not be offered when the executable is missing");
    }

    [Fact]
    public void Not_a_windows_machine_says_so_in_the_status_and_the_queue()
    {
        var vm = VM(new FakeMonitor { Status = null }, new FakeLauncher());

        Assert.False(vm.IsSupported);
        Assert.Equal("Windows only", vm.ServiceStatusText);
        Assert.Equal("The background service runs on Windows.", vm.QueueNote);
        Assert.False(vm.InstallCommand.CanExecute(null));
    }

    [Fact]
    public void The_lifecycle_buttons_launch_the_indexers_own_verbs()
    {
        var launcher = new FakeLauncher();
        VM(new FakeMonitor { Status = new IndexerServiceStatus(false, "", "", false) }, launcher)
            .InstallCommand.Execute(null);
        VM(new FakeMonitor { Status = Running() }, launcher).StopCommand.Execute(null);

        Assert.Contains(launcher.Launches, a => a.SequenceEqual(new[] { "--install" }));
        Assert.Contains(launcher.Launches, a => a.SequenceEqual(new[] { "sc.exe", "stop", "SynapicIndexer" }));
    }

    // ---------------------------------------------------------------- queue

    [Fact]
    public void A_missing_queue_is_said_not_hidden()
    {
        var vm = VM(new FakeMonitor { Status = Running() }, new FakeLauncher());

        Assert.Equal("no queue yet", vm.QueueNote);
        Assert.Equal(0, vm.PendingCount);
        Assert.Empty(vm.RecentEvents);
    }

    [Fact]
    public void A_queue_that_cannot_be_read_names_the_problem()
    {
        var vm = VM(new FakeMonitor
        {
            Status = Running(),
            Queue = new IndexerQueueSnapshot(null, Array.Empty<QueueEvent>(),
                "the queue at C:\\ProgramData\\Synapic\\state\\indexer.db could not be read (IOException)"),
        }, new FakeLauncher());

        Assert.Contains("could not be read", vm.QueueNote);
    }

    [Fact]
    public void Queue_counts_failures_and_activity_land_in_the_view_model()
    {
        var summary = new QueueSummary
        {
            Pending = 3, InFlight = 1, Completed = 41, TransientFailure = 2, Failed = 1,
            RecentFailures = new[] { new QueueFailure(9, "local|C:\\P\\x.jpg", "hash failed", 2) },
        };
        var events = new[]
        {
            new QueueEvent(9, "x.jpg", "2026-10-24T06:03:02.1000000Z", null, "Pending", "detected"),
            new QueueEvent(9, "x.jpg", "2026-10-24T06:03:04.1000000Z", "Pending", "InFlight", "claimed"),
        };
        var vm = VM(new FakeMonitor
        {
            Status = Running(),
            Queue = new IndexerQueueSnapshot(summary, events, null),
        }, new FakeLauncher());

        Assert.Equal(3, vm.PendingCount);
        Assert.Equal(1, vm.InFlightCount);
        Assert.Equal(41, vm.CompletedCount);
        Assert.Equal(2, vm.TransientFailureCount);
        Assert.Equal(1, vm.FailedCount);
        Assert.Single(vm.RecentFailures);
        Assert.Contains("local|C:\\P\\x.jpg", vm.RecentFailures[0]);
        Assert.Contains("hash failed", vm.RecentFailures[0]);
        Assert.Equal(2, vm.RecentEvents.Count);
        Assert.Contains("Pending to InFlight", vm.RecentEvents[1]);
        Assert.Contains("claimed", vm.RecentEvents[1]);
    }

    // -------------------------------------------------------- watch folders

    [Fact]
    public async Task A_successful_save_reports_applied_and_restarted()
    {
        var monitor = new FakeMonitor
        {
            Status = Running(),
            Schedule = new ScheduleSettings
            {
                Mode = "Watch", IntervalMinutes = 15, WatchRecursive = true,
                WatchPaths = new List<string> { @"C:\Photos" },
            },
        };
        var launcher = new FakeLauncher
        {
            Outcome = new ApplyConfigOutcome(0, true,
                new[] { "Applied; the service was restarted to pick it up." }),
        };
        var vm = VM(monitor, launcher);

        vm.AddWatchFolderCommand.Execute(@"C:\Camera");
        Assert.True(vm.SaveCommand.CanExecute(null));
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(launcher.LastPatch);
        var saved = launcher.LastPatch!["schedule"]!.AsObject();
        Assert.Equal(2, saved["watchPaths"]!.AsArray().Count);
        Assert.Equal(@"C:\Camera", saved["watchPaths"]![1]!.GetValue<string>());
        Assert.Equal("Watch", saved["mode"]!.GetValue<string>());
        Assert.Equal(15, (int)saved["intervalMinutes"]!);

        Assert.Single(launcher.Launches);
        Assert.Equal(new[] { "--apply-config", @"C:\Users\u\AppData\Local\Temp\synapic-apply-config.json" },
            launcher.Launches[0]);
        Assert.False(vm.IsDirty);
        Assert.Contains("Applied", vm.WatchNote);
        Assert.Contains("restarted", vm.WatchNote);
    }

    [Fact]
    public async Task A_save_that_applied_without_a_running_service_says_so()
    {
        var launcher = new FakeLauncher
        {
            Outcome = new ApplyConfigOutcome(0, false, Array.Empty<string>()),
        };
        var vm = VM(new FakeMonitor { Status = Running() }, launcher);
        vm.AddWatchFolderCommand.Execute(@"C:\Camera");

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Contains("was not running", vm.WatchNote);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task A_refused_save_surfaces_the_validation_messages_and_stays_dirty()
    {
        var launcher = new FakeLauncher
        {
            Outcome = new ApplyConfigOutcome(2, false, new[]
            {
                "schedule.watchPaths: does not exist or is not reachable: 'C:\\Nope'",
                "Nothing was saved; C:\\ProgramData\\Synapic\\indexer-config.json is unchanged.",
            }),
        };
        var vm = VM(new FakeMonitor { Status = Running() }, launcher);
        vm.AddWatchFolderCommand.Execute(@"C:\Nope");

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.StartsWith("Not applied:", vm.WatchNote);
        Assert.Contains("does not exist or is not reachable", vm.WatchNote);
        Assert.Contains("Nothing was saved", vm.WatchNote);
        Assert.True(vm.IsDirty, "a refused save keeps the edit so the user can fix it");
    }

    [Fact]
    public async Task A_save_that_never_started_says_nothing_was_changed()
    {
        var launcher = new FakeLauncher { Outcome = null };
        var vm = VM(new FakeMonitor { Status = Running() }, launcher);
        vm.AddWatchFolderCommand.Execute(@"C:\Camera");

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.StartsWith("Not applied:", vm.WatchNote);
        Assert.Contains("could not be started", vm.WatchNote);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public async Task While_the_save_is_in_flight_the_button_is_off_and_the_note_says_saving()
    {
        var launcher = new FakeLauncher
        {
            InFlight = new TaskCompletionSource<ApplyConfigOutcome?>(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var vm = VM(new FakeMonitor { Status = Running() }, launcher);
        vm.AddWatchFolderCommand.Execute(@"C:\Camera");

        var saving = vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.IsSaving);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.Contains("Saving", vm.WatchNote);

        launcher.InFlight.SetResult(new ApplyConfigOutcome(0, true, Array.Empty<string>()));
        await saving;

        Assert.False(vm.IsSaving);
        Assert.Contains("Applied", vm.WatchNote);
    }

    [Fact]
    public void Adding_a_folder_twice_is_refused_with_a_note()
    {
        var monitor = new FakeMonitor
        {
            Status = Running(),
            Schedule = new ScheduleSettings { WatchPaths = new List<string> { @"C:\Photos" } },
        };
        var vm = VM(monitor, new FakeLauncher());

        vm.AddWatchFolderCommand.Execute(@"C:\Photos");

        Assert.Single(vm.WatchPaths);
        Assert.Equal(@"'C:\Photos' is already watched.", vm.WatchNote);
    }

    [Fact]
    public void Removing_the_selected_folder_marks_the_schedule_dirty()
    {
        var monitor = new FakeMonitor
        {
            Status = Running(),
            Schedule = new ScheduleSettings { WatchPaths = new List<string> { @"C:\Photos" } },
        };
        var vm = VM(monitor, new FakeLauncher());

        vm.SelectedWatchPath = @"C:\Photos";
        vm.RemoveWatchFolderCommand.Execute(null);

        Assert.Empty(vm.WatchPaths);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void A_dirty_schedule_is_not_overwritten_by_a_refresh()
    {
        var monitor = new FakeMonitor
        {
            Status = Running(),
            Schedule = new ScheduleSettings { WatchPaths = new List<string> { @"C:\Photos" } },
        };
        var vm = VM(monitor, new FakeLauncher());
        vm.AddWatchFolderCommand.Execute(@"C:\Camera");

        monitor.Schedule = new ScheduleSettings { WatchPaths = new List<string> { @"C:\Other" } };
        vm.RefreshCommand.Execute(null);

        Assert.True(vm.WatchPaths.Count == 2,
            "an edit in progress must not be clobbered by the poll");
    }
}

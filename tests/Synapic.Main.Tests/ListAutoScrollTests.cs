using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Serilog.Events;
using Serilog.Parsing;
using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.ViewModels;
using Synapic.Avalonia.Views;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// Regression tests for the crash on app close:
/// "InvalidOperationException: Invalid Arrange rectangle", thrown from
/// VirtualizingStackPanel.ArrangeOverride when a log line was appended and the
/// list was asked to reveal it after the window was gone.
///
/// The UI log sink is process-wide and keeps firing while the app shuts down, so
/// a UI-thread update queued just before teardown can still land after it. Both
/// halves of the fix are pinned here: the view model stops accepting UI log lines
/// once the shell detaches it, and a list that is not attached refuses to scroll.
/// Events go straight into the sink because the process-global logger is reset by
/// other tests and would otherwise swallow them.
/// </summary>
public class ListAutoScrollTests
{
    private static MainWindowViewModel NewViewModel() =>
        new(new FakeSidecar(), new FakeBuildService(), new Session(), () => null, null, null, _ => null);

    /// <summary>A log line exactly as <see cref="UiLogSink"/> would deliver one.</summary>
    private static void EmitLogLine() =>
        SynapicLog.UiSink.Emit(new LogEvent(
            DateTimeOffset.Now,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplate(new[] { new TextToken("a line the sidecar printed") }),
            properties: Array.Empty<LogEventProperty>()));

    [AvaloniaFact]
    public void Detaching_the_ui_log_stops_it_feeding_the_log_collection()
    {
        var vm = NewViewModel();

        // Attached: a sink event reaches the collection behind the log list.
        EmitLogLine();
        Dispatcher.UIThread.RunJobs();
        Assert.NotEmpty(vm.LogEntries);

        // Detached (the shell calls this when its window closes): the collection
        // must stop growing, or the next append asks a control that no longer
        // exists to lay itself out.
        vm.DetachUiLog();
        var afterDetach = vm.LogEntries.Count;

        EmitLogLine();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(afterDetach, vm.LogEntries.Count);
    }

    [AvaloniaFact]
    public void A_list_with_no_rows_is_never_scrolled()
    {
        Assert.False(ListAutoScroll.CanReveal(new ListBox(), itemCount: 0));
    }

    [AvaloniaFact]
    public void A_list_that_is_not_in_a_visual_tree_is_never_scrolled()
    {
        var list = new ListBox();

        // Rows exist but the control is detached: exactly the state the log list
        // is in once the window has closed.
        Assert.False(ListAutoScroll.CanReveal(list, itemCount: 5));

        ListAutoScroll.ToEnd(list); // a no-op, not an exception
        Dispatcher.UIThread.RunJobs();
    }
}

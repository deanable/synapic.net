using System.Collections.Specialized;
using Avalonia.Controls;
using Synapic.Main.ViewModels.Operations;

namespace Synapic.Main.Views.Operation;

/// <summary>
/// The shared run bar: one view for the run state every operation inherits from
/// <c>RunStateViewModel</c>. It carries no mode knowledge of its own — the mode's
/// words and its Stop/Pause/Resume arrive through that base's overridable
/// members — and it owns the run log's auto-scroll, since it owns the one log.
/// </summary>
public partial class RunStateBar : UserControl
{
    private RunStateViewModel? _vm;

    public RunStateBar()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.LogLines.CollectionChanged -= OnLogLinesChanged;
            _vm = DataContext as RunStateViewModel;
            if (_vm is not null) _vm.LogLines.CollectionChanged += OnLogLinesChanged;
        };
    }

    /// <summary>
    /// Keep the newest log line visible (port of the Python console's
    /// <c>self.console.see("end")</c>): a run emits progress lines continuously, so
    /// without this the viewer stays pinned to the top. Revealing is guarded so a
    /// line arriving while the window is closing cannot arrange a detached list.
    /// </summary>
    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ListAutoScroll.ToEnd(RunLogList);
}

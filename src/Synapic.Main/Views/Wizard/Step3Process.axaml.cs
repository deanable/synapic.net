using System.Collections.Specialized;
using Avalonia.Controls;
using Synapic.Main.ViewModels.Steps;

namespace Synapic.Main.Views.Wizard;

public partial class Step3Process : UserControl
{
    private Step3ProcessViewModel? _vm;

    public Step3Process()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.LogLines.CollectionChanged -= OnLogLinesChanged;
            _vm = DataContext as Step3ProcessViewModel;
            if (_vm is not null) _vm.LogLines.CollectionChanged += OnLogLinesChanged;
        };
    }

    /// <summary>
    /// Keep the newest log line visible (port of the Python console's
    /// ``self.console.see("end")``): the batch emits progress lines continuously,
    /// so without this the viewer stays pinned to the top of the run. Revealing
    /// is guarded so a line arriving while the window is closing cannot arrange
    /// a detached list.
    /// </summary>
    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ListAutoScroll.ToEnd(LogList);
}

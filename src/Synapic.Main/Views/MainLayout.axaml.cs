using System.Collections.Specialized;
using Avalonia.Controls;
using Synapic.Main.ViewModels;

namespace Synapic.Main.Views;

/// <summary>
/// The application shell (header / sidebar / content / footer) extracted from
/// the window so the layout owns its own wiring — chiefly the log auto-scroll,
/// which follows the log collection rather than the window itself.
/// </summary>
public partial class MainLayout : UserControl
{
    private MainWindowViewModel? _vm;

    public MainLayout()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                _vm = vm;
                vm.LogEntries.CollectionChanged -= OnLogEntriesChanged;
                vm.LogEntries.CollectionChanged += OnLogEntriesChanged;
            }
        };
    }

    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ListAutoScroll.ToEnd(LogList);

    /// <summary>Stop following the log; the window calls this when it closes so
    /// no subscription outlives the control.</summary>
    public void DetachLog()
    {
        if (_vm is not null)
            _vm.LogEntries.CollectionChanged -= OnLogEntriesChanged;
        _vm = null;
    }
}

using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.ViewModels;

namespace Synapic.Avalonia.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();

        // The UI log sink is process-wide and Serilog keeps writing while the app
        // shuts down: once this window is gone, stop feeding the view model, or
        // log lines keep appending to a collection whose control no longer exists.
        Closed += (_, _) => _vm?.DetachUiLog();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                _vm = vm;
                vm.AttachSidecarLog();
                vm.LogEntries.CollectionChanged -= OnLogEntriesChanged;
                vm.LogEntries.CollectionChanged += OnLogEntriesChanged;
            }
        };
    }

    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ListAutoScroll.ToEnd(LogList);

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        SynapicLog.Info(nameof(MainWindow), "Synapic started");
    }
}

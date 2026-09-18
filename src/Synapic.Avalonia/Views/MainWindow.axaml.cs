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
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                _vm = vm;
                vm.AttachSidecarLog();
                vm.LogEntries.CollectionChanged += (_, _) =>
                {
                    if (this.FindControl<ListBox>("LogList") is { } list)
                        list.ScrollIntoView(list.ItemCount - 1);
                };
            }
        };
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        SynapicLog.Info(nameof(MainWindow), "Synapic started");
    }
}

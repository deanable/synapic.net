using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Synapic.Main.ViewModels;

namespace Synapic.Main.Views.Settings;

/// <summary>
/// Code-behind for the Service tab: the folder picker (the same OS dialog the
/// source form uses) and the poll timer that keeps the service state and queue
/// counts current while the tab is visible. The timer lives here, not in the
/// view model, so the view model stays constructible headless in tests.
/// </summary>
public partial class ServiceSection : UserControl
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(5) };

    public ServiceSection()
    {
        InitializeComponent();
        _poll.Tick += (_, _) => (DataContext as MainWindowViewModel)?.IndexerService.Refresh();
        AttachedToVisualTree += (_, _) => _poll.Start();
        DetachedFromVisualTree += (_, _) => _poll.Stop();
    }

    /// <summary>Open the OS folder picker and hand the choice to the view model.</summary>
    private async void OnAddWatchFolder(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is null ||
            DataContext is not MainWindowViewModel { IndexerService: { } vm }) return;
        var storage = TopLevel.GetTopLevel(this)!.StorageProvider;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select a folder for the service to watch",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            vm.AddWatchFolderCommand.Execute(path);
    }
}

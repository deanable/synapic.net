using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Synapic.ServiceConsole.ViewModels;

namespace Synapic.ServiceConsole.Views;

/// <summary>
/// Code-behind for the console's one view: the folder picker (the same OS
/// dialog the main app's source form uses).
/// </summary>
public partial class ServiceSection : UserControl
{
    public ServiceSection()
    {
        InitializeComponent();
    }

    /// <summary>Open the OS folder picker and hand the choice to the view model.</summary>
    private async void OnAddWatchFolder(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is null ||
            DataContext is not IndexerServiceViewModel vm) return;
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

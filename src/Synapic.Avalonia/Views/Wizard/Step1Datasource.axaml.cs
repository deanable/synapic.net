using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Synapic.Avalonia.ViewModels.Steps;

namespace Synapic.Avalonia.Views.Wizard;

public partial class Step1Datasource : UserControl
{
    public Step1Datasource()
    {
        InitializeComponent();
    }

    /// <summary>Open the OS folder picker and store the choice on the view model.</summary>
    private async void OnBrowseFolder(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is null || DataContext is not Step1DatasourceViewModel vm) return;
        var storage = TopLevel.GetTopLevel(this)!.StorageProvider;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the image folder",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            vm.LocalPath = path;
    }
}

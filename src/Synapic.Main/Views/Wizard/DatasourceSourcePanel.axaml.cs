using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Synapic.Main.ViewModels.Steps;

namespace Synapic.Main.Views.Wizard;

/// <summary>
/// The source panel of the start screen: folder picker, Daminion connect, scope,
/// filters and the record count. Bound to the wizard's Step 1 view model, so the
/// source chosen here is the source every route runs on.
/// </summary>
public partial class DatasourceSourcePanel : UserControl
{
    public DatasourceSourcePanel()
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

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Synapic.Avalonia.ViewModels.Steps;

namespace Synapic.Avalonia.Views.Wizard;

public partial class StepDedup : UserControl
{
    public StepDedup()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireConfirm();
    }

    /// <summary>Route the dedup step's destructive-action confirmation through
    /// the modal dialog (Daminion "delete from catalog"). Re-run whenever the
    /// data context is (re)attached — e.g. headless test hosts set it later.</summary>
    private void WireConfirm()
    {
        if (DataContext is not StepDedupViewModel vm) return;
        vm.ConfirmAction = message =>
            ConfirmDialogWindow.ShowAsync(TopLevel.GetTopLevel(this) as Window, message);
    }

    /// <summary>Open the OS folder picker and store the choice on the view model.</summary>
    private async void OnBrowseFolder(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not StepDedupViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the folder to scan for duplicates",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            vm.FolderPath = path;
    }
}

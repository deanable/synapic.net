using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Synapic.Main.ViewModels.Steps;

namespace Synapic.Main.Views.Wizard;

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

    /// <summary>Pick the destination folder for the "Move to folder…" action
    /// and record the selection on the view model (which persists it).</summary>
    private async void OnBrowseMoveFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage ||
            DataContext is not StepDedupViewModel vm) return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the destination folder for moved duplicates",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            vm.MoveDestinationFolder = path;
    }
}

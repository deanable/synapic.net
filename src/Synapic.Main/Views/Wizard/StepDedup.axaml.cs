using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
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
    private async void OnBrowseMoveFolder(object? sender, RoutedEventArgs e)
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

    /// <summary>The row's own data context: the one review image under the pointer.</summary>
    private static DedupItemViewModel? ItemOf(object? sender)
        => sender is Control c ? c.DataContext as DedupItemViewModel : null;

    /// <summary>Double-click on a review row toggles its keep state — the quick
    /// "exclude this one from the action" gesture, no hunting for the checkbox.</summary>
    private void OnGroupItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        ItemOf(sender)?.ToggleKeep();
        e.Handled = true;
    }

    /// <summary>Right-click on a review row: keep (exclude from the action),
    /// select (include it again) and copy the file name. Built here rather than
    /// bound in the template, so the menu's commands always resolve against the
    /// row that was actually clicked. Assigned to the row's ContextFlyout and
    /// left unhandled, so the platform opens it at the pointer.</summary>
    private void OnGroupItemContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (ItemOf(sender) is not { } item || sender is not Control control) return;

        var copyItem = new MenuItem { Header = "Copy file name" };
        copyItem.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(item.DisplayName);
        };

        control.ContextFlyout = new MenuFlyout
        {
            Items =
            {
                new MenuItem
                {
                    Header = "Keep this — exclude from the action",
                    Command = item.KeepItemCommand,
                },
                new MenuItem
                {
                    Header = "Select this — run the action on it",
                    Command = item.SelectForActionCommand,
                },
                new Separator(),
                copyItem,
            },
        };
    }
}

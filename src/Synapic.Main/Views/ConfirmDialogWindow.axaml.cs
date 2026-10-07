using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Synapic.Avalonia.Views;

/// <summary>Message + Yes/No pair rendered by <see cref="ConfirmDialogWindow"/>.</summary>
public sealed record ConfirmDialogState(string Message, string ConfirmLabel);

/// <summary>
/// Modal confirmation prompt used before destructive actions (the dedup step's
/// Daminion "delete from catalog"). Returns the user's answer via
/// <see cref="ShowAsync"/>; the window itself carries no other state.
/// </summary>
public partial class ConfirmDialogWindow : Window
{
    private bool _answer;

    public ConfirmDialogWindow()
    {
        InitializeComponent();
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        _answer = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _answer = false;
        Close();
    }

    /// <summary>Show the prompt modally over <paramref name="owner"/> and return
    /// the user's answer (false when the dialog is dismissed without choosing).
    /// Must run on the UI thread.</summary>
    public static async Task<bool> ShowAsync(Window? owner, string message, string confirmLabel = "Delete")
    {
        var window = new ConfirmDialogWindow
        {
            DataContext = new ConfirmDialogState(message, confirmLabel),
        };
        if (owner is not null)
            await window.ShowDialog(owner);
        else
            window.Show();
        return window._answer;
    }
}

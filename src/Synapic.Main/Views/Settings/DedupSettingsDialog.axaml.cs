using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Synapic.Main.Services;
using Synapic.Main.ViewModels.Steps;

namespace Synapic.Main.Views.Settings;

/// <summary>
/// 3 · Settings for the deduplication operation. It shows a second view of the
/// wizard's own <see cref="StepDedupViewModel"/>, so the dialog is an extra
/// entry point and not a second copy of the rules. Esc closes; F1 opens the
/// topic for whatever has focus, exactly like the main window does.
/// </summary>
public partial class DedupSettingsDialog : Window
{
    public DedupSettingsDialog()
    {
        InitializeComponent();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F1 && DataContext is StepDedupViewModel)
        {
            var topic = HelpScope.Resolve(e.Source) ?? HelpTopics.ForStepIndex(4);
            if (Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow?.DataContext is ViewModels.MainWindowViewModel shell)
            {
                shell.OpenTopicCommand.Execute(topic);
                e.Handled = true;
                return;
            }
        }

        base.OnKeyDown(e);
    }
}

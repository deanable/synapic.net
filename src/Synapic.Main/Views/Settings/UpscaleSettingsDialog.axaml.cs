using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Synapic.Main.Services;
using Synapic.Main.ViewModels.Steps;

namespace Synapic.Main.Views.Settings;

/// <summary>
/// 3 · Settings for the upscaling operation. It shows a second view of the
/// wizard's own <see cref="StepUpscaleViewModel"/>, so the dialog is an extra
/// entry point and not a second copy of the parameters. Esc closes; F1 opens the
/// topic for whatever has focus, exactly like the main window does.
/// </summary>
public partial class UpscaleSettingsDialog : Window
{
    public UpscaleSettingsDialog()
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

        if (e.Key == Key.F1 && DataContext is StepUpscaleViewModel)
        {
            var topic = HelpScope.Resolve(e.Source) ?? HelpTopics.Upscale;
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

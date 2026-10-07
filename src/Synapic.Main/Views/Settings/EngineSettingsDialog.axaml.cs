using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Synapic.Main.Services;
using Synapic.Main.ViewModels.Steps;

namespace Synapic.Main.Views.Settings;

/// <summary>
/// The engine settings modal (proposal §2.2.3). It shows a second view of the
/// wizard's own <see cref="Step2EngineViewModel"/>, so there is exactly one
/// settings view model and one EngineSettingsStore contract behind it — the
/// dialog is an extra entry point, not a second copy of the settings.
/// Esc closes; F1 opens the topic for whatever has focus, exactly like the
/// main window does.
/// </summary>
public partial class EngineSettingsDialog : Window
{
    public EngineSettingsDialog()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as Step2EngineViewModel)?.SaveToStore();
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        (DataContext as Step2EngineViewModel)?.SaveToStore();
        Close();
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

        if (e.Key == Key.F1 && DataContext is Step2EngineViewModel)
        {
            var topic = HelpScope.Resolve(e.Source) ?? HelpTopics.ForStepIndex(1);
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

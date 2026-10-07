using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.ViewModels;

namespace Synapic.Avalonia.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();

        // The UI log sink is process-wide and Serilog keeps writing while the app
        // shuts down: once this window is gone, stop feeding the view model, or
        // log lines keep appending to a collection whose control no longer exists.
        Closed += (_, _) => _vm?.DetachUiLog();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                _vm = vm;
                vm.AttachSidecarLog();
                vm.LogEntries.CollectionChanged -= OnLogEntriesChanged;
                vm.LogEntries.CollectionChanged += OnLogEntriesChanged;
            }
        };
    }

    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ListAutoScroll.ToEnd(LogList);

    /// <summary>
    /// F1 follows the focus: the nearest <see cref="HelpScope"/> around whatever
    /// has focus wins - a section, or one setting's anchored topic - and the
    /// unscoped fallback (sidecar panel, or the step on screen) applies when
    /// focus is inside nothing annotated. The shortcut used to be a
    /// Window.KeyBinding; a key binding cannot see which control has focus, so
    /// it cannot be context aware below the step.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F1 && _vm is not null)
        {
            var topic = HelpScope.Resolve(e.Source) ?? _vm.ContextHelpTopic;
            _vm.OpenTopicCommand.Execute(topic);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        SynapicLog.Info(nameof(MainWindow), "Synapic started");
    }
}

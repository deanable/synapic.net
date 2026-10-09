using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Synapic.Main.ViewModels;
using Synapic.Main.ViewModels.Operations;

namespace Synapic.Main.Views;

/// <summary>
/// The application shell (header / content / footer / diagnostics) extracted from
/// the window so the layout owns its own wiring: the four content slots the
/// operation template renders, and the log auto-scroll, which follows the log
/// collection rather than the window itself.
///
/// The slots are pushed rather than bound because a binding written on
/// <c>OperationLayout</c> resolves against that element's own DataContext, which
/// is not the shell — and because the mode's content depends on
/// <see cref="ShellViewModel.Current"/>, which changes on every mode switch.
/// </summary>
public partial class MainLayout : UserControl
{
    private MainWindowViewModel? _vm;

    public MainLayout()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel vm)
            {
                Detach();
                return;
            }

            Detach();
            _vm = vm;

            // The one shared data source (docs/ui-design.md D4) is the same
            // instance every mode binds, so Region A reads identically everywhere.
            OperationHost.SharedSource = vm.Operations.Source;
            ApplyModeContent();

            vm.Shell.PropertyChanged += OnShellPropertyChanged;
            vm.LogEntries.CollectionChanged += OnLogEntriesChanged;
        };
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.Current))
            ApplyModeContent();
    }

    /// <summary>
    /// Hand the open mode's content to the template's regions (Region B's
    /// parameters, Region C's run and report). On the dashboard there is no mode,
    /// so the slots are cleared and the template renders nothing but its titles.
    /// </summary>
    private void ApplyModeContent()
    {
        var key = _vm?.Shell.Current?.Key;
        OperationHost.Parameters = key is null ? null : _vm!.Operations.ParametersFor(key);
        OperationHost.Run = key is null ? null : _vm!.Operations.RunFor(key);
        OperationHost.Report = key is null ? null : _vm!.Operations.ReportFor(key);
    }

    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ListAutoScroll.ToEnd(LogList);

    /// <summary>Stop following the log; the window calls this when it closes so
    /// no subscription outlives the control. The shell subscription stays: the
    /// layout is still the element the template lives in until it is torn down.</summary>
    public void DetachLog()
    {
        if (_vm is not null)
            _vm.LogEntries.CollectionChanged -= OnLogEntriesChanged;
    }

    private void Detach()
    {
        if (_vm is null) return;
        _vm.Shell.PropertyChanged -= OnShellPropertyChanged;
        _vm.LogEntries.CollectionChanged -= OnLogEntriesChanged;
        _vm = null;
    }
}

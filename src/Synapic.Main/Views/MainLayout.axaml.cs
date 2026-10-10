using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Synapic.Main.ViewModels;
using Synapic.Main.ViewModels.Operations;

namespace Synapic.Main.Views;

/// <summary>
/// The application shell (sidebar / content / diagnostics) extracted from the
/// window so the layout owns its own wiring: the read-only source summary and the
/// two processing slots the operation view renders, and the log auto-scroll,
/// which follows the log collection rather than the window itself.
///
/// The slots are pushed rather than bound because a binding written on
/// <c>OperationLayout</c> resolves against that element's own DataContext, which
/// is not the shell — and because the open view's content depends on
/// <see cref="ShellViewModel.Current"/>, which changes on every view switch.
/// </summary>
public partial class MainLayout : UserControl
{
    /// <summary>
    /// Below this width the sidebar collapses to an icon-only rail, so a narrow
    /// window keeps its content width instead of losing 210 px of it to the
    /// navigation. Chosen so the audited 1024-wide window keeps the full sidebar
    /// and the 900- and 640-wide ones get the rail; Avalonia has no XAML
    /// breakpoint, so <see cref="ApplyCompact"/> sets the class from the layout.
    /// </summary>
    public const double CompactWidth = 1000;

    private MainWindowViewModel? _vm;

    /// <summary>
    /// The form the XAML declares, and the form until a layout pass says
    /// otherwise: the full sidebar. Starting here rather than at "compact" keeps
    /// the labelled rows the default for a control that has not been laid out.
    /// </summary>
    private bool _compact;

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
            // instance every view binds, so the processing view's read-only
            // summary reads identically everywhere and never edits it — the
            // source form itself lives on the Settings view now.
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // The shell's own width, not the window's: the sidebar and the content
        // share it, and it is the shell that has to give the content enough room.
        if (change.Property == BoundsProperty)
            ApplyCompact(change.GetNewValue<Rect>().Width);
    }

    /// <summary>
    /// Collapse the sidebar to an icon-only rail when the shell is narrower than
    /// <see cref="CompactWidth"/>: the navigation rows drop their labels and the
    /// wide status lines go with them, so the content column keeps the width. The
    /// class drives the styles in App.axaml; the Help entry shortens here because
    /// its label is its content. Idempotent, so a layout pass that does not cross
    /// the threshold does nothing (measuring cannot loop).
    /// </summary>
    public void ApplyCompact(double width)
    {
        // A shell that has not been laid out yet reports 0; keep the declared form.
        if (width <= 0) return;

        var compact = width < CompactWidth;
        if (compact == _compact) return;
        _compact = compact;

        Sidebar.Classes.Set("compact", compact);
        HelpButton.Content = compact ? "?" : "Help";
    }

    /// <summary>
    /// Hand the open operation's processing content to the template (its run
    /// surface and its report). On the Settings view there is no operation, so
    /// the slots are cleared and the template renders nothing.
    /// </summary>
    private void ApplyModeContent()
    {
        var key = _vm?.Shell.Current?.Key;
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

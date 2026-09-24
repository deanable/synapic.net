using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Synapic.Avalonia.ViewModels.Steps;

namespace Synapic.Avalonia.Views.Wizard;

public partial class Step4Results : UserControl
{
    private Step4ResultsViewModel? _vm;

    public Step4Results()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.Results.CollectionChanged -= OnResultsChanged;
            _vm = DataContext as Step4ResultsViewModel;
            if (_vm is not null) _vm.Results.CollectionChanged += OnResultsChanged;
        };
    }

    private void OnRefresh(object? sender, RoutedEventArgs e)
    {
        if (DataContext is Step4ResultsViewModel vm) vm.Refresh();
    }

    /// <summary>
    /// Keep the newest result visible as the grid fills: Step 4 is reviewed
    /// front-to-back, so the most recently written row should stay on screen
    /// just like the live log does.
    /// </summary>
    private void OnResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_vm is null || _vm.Results.Count == 0) return;
        var last = _vm.Results[^1];
        // Post so the row container exists before we ask the grid to reveal it,
        // and re-check on the way in: the grid is gone once the window closes.
        Dispatcher.UIThread.Post(() =>
        {
            if (!ListAutoScroll.CanReveal(ResultsGrid, _vm.Results.Count)) return;
            if (ResultsGrid.Columns.Count > 0)
                ResultsGrid.ScrollIntoView(last, ResultsGrid.Columns[0]);
        });
    }
}

using System.Collections.Specialized;
using Avalonia.Controls;
using Synapic.Avalonia.ViewModels.Steps;

namespace Synapic.Avalonia.Views.Wizard;

public partial class StepUpscale : UserControl
{
    private StepUpscaleViewModel? _vm;

    public StepUpscale()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.LogLines.CollectionChanged -= OnLogLinesChanged;
            _vm = DataContext as StepUpscaleViewModel;
            if (_vm is not null) _vm.LogLines.CollectionChanged += OnLogLinesChanged;
        };
    }

    /// <summary>Keep the newest log line visible (same behavior as Step 3's log).</summary>
    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ListAutoScroll.ToEnd(LogList);
}

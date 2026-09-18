using Avalonia.Controls;
using Avalonia.Interactivity;
using Synapic.Avalonia.ViewModels.Steps;

namespace Synapic.Avalonia.Views.Wizard;

public partial class Step4Results : UserControl
{
    public Step4Results()
    {
        InitializeComponent();
    }

    private void OnRefresh(object? sender, RoutedEventArgs e)
    {
        if (DataContext is Step4ResultsViewModel vm) vm.Refresh();
    }
}

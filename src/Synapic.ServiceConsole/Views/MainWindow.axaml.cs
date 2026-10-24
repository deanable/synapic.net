using Avalonia.Controls;
using Avalonia.Threading;
using Synapic.ServiceConsole.ViewModels;

namespace Synapic.ServiceConsole.Views;

/// <summary>
/// The console's window: hosts the one view and owns the poll timer, so the
/// view model stays constructible headless in tests. Five seconds matches the
/// service's own polling habits - often enough to feel live, cheap enough to
/// leave running while the window is open.
/// </summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(5) };

    public MainWindow()
    {
        InitializeComponent();
        _poll.Tick += (_, _) => (DataContext as IndexerServiceViewModel)?.Refresh();
        Opened += (_, _) => _poll.Start();
        Closed += (_, _) => _poll.Stop();
    }
}

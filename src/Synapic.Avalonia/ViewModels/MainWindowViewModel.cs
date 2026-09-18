using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;

namespace Synapic.Avalonia.ViewModels;

/// <summary>
/// Main window shell: hosts the wizard steps, the server status indicator,
/// Start/Stop Server controls, and the live log view (spec §2 Server Status).
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IInferenceSidecar _sidecar;
    private readonly Session _session;

    public MainWindowViewModel(IInferenceSidecar sidecar, Session session)
    {
        _sidecar = sidecar;
        _session = session;

        _sidecar.StatusChanged += OnSidecarStatusChanged;

        Wizard = new WizardViewModel(_session, _sidecar);
        StatusText = "Server stopped";
    }

    public WizardViewModel Wizard { get; }

    public ObservableCollection<UiLogEvent> LogEntries { get; } = new();

    [ObservableProperty]
    private string _statusText = "Server stopped";

    [ObservableProperty]
    private SidecarStatus _serverStatus = SidecarStatus.Stopped;

    [ObservableProperty]
    private bool _isBusy;

    public bool IsServerRunning => ServerStatus is SidecarStatus.Starting or SidecarStatus.Ready;

    /// <summary>Status dot color for the server indicator (bound in MainWindow).</summary>
    public IBrush ServerBrush => ServerStatus switch
    {
        SidecarStatus.Ready => Brushes.ForestGreen,
        SidecarStatus.Starting => Brushes.Orange,
        SidecarStatus.Error => Brushes.OrangeRed,
        _ => Brushes.Gray,
    };

    partial void OnServerStatusChanged(SidecarStatus value)
    {
        OnPropertyChanged(nameof(IsServerRunning));
        OnPropertyChanged(nameof(ServerBrush));
        StatusText = value switch
        {
            SidecarStatus.Stopped => "Server stopped",
            SidecarStatus.Starting => "Server starting… (loading model may take up to 2 minutes)",
            SidecarStatus.Ready => $"Server ready (port {_sidecar.SidecarPort})",
            SidecarStatus.Error => "Server error — see log",
            _ => value.ToString(),
        };
        StartServerCommand.NotifyCanExecuteChanged();
        StopServerCommand.NotifyCanExecuteChanged();
    }

    private void OnSidecarStatusChanged(object? sender, SidecarStatusChangedEventArgs e)
    {
        ServerStatus = e.Status;
        if (e.Status == SidecarStatus.Error && e.Message is not null)
            AppendLog($"[server] {e.Message}");
    }

    [RelayCommand(CanExecute = nameof(CanStartServer))]
    private async Task StartServerAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            await _sidecar.StartAsync(ct);
        }
        catch (Exception e)
        {
            AppendLog($"[server] Failed to start: {e.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanStartServer() => ServerStatus is SidecarStatus.Stopped or SidecarStatus.Error && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStopServer))]
    private async Task StopServerAsync()
    {
        IsBusy = true;
        try
        {
            await _sidecar.StopAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanStopServer() => IsServerRunning && !IsBusy;

    public void AppendLog(string line)
    {
        LogEntries.Add(new UiLogEvent(line, Serilog.Events.LogEventLevel.Information));
        while (LogEntries.Count > 2000) LogEntries.RemoveAt(0);
    }

    /// <summary>Attach the sidecar stdout/stderr stream to the UI log.</summary>
    public void AttachSidecarLog()
    {
        _sidecar.LogReceived -= OnSidecarLog;
        _sidecar.LogReceived += OnSidecarLog;
    }

    private void OnSidecarLog(string line)
    {
        AppendLog(line);
    }
}

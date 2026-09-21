using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Avalonia.Services;

namespace Synapic.Avalonia.Views;

/// <summary>
/// View model for the crash dialog: holds the formatted diagnostics text and
/// implements copy-to-clipboard and open-report-folder actions.
/// </summary>
public partial class CrashDialogViewModel : ObservableObject
{
    private readonly Window? _owner;

    [ObservableProperty]
    private string _headline = "An unexpected error occurred.";

    [ObservableProperty]
    private string _diagnostics = "";

    public CrashDialogViewModel(Window? owner) => _owner = owner;

    [RelayCommand]
    private async Task CopyAsync()
    {
        try
        {
            var clipboard = _owner is null ? null : TopLevel.GetTopLevel(_owner)?.Clipboard;
            if (clipboard is not null)
                await clipboard.SetTextAsync(Diagnostics);
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(CrashDialogViewModel), $"Copy to clipboard failed: {e.Message}");
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            var dir = CrashReporterService.LastReportPath is { } path
                ? Path.GetDirectoryName(path)
                : null;
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                ProcessExtensions.OpenDirectory(dir);
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(CrashDialogViewModel), $"Opening crash folder failed: {e.Message}");
        }
    }

    [RelayCommand]
    private void Continue() => _owner?.Close();
}

/// <summary>
/// "Something went wrong" dialog (P6.1, user decision): shows the captured
/// diagnostics with a copy button so users can report manually. Purely
/// informational — the app continues or exits according to the crash.
/// </summary>
public partial class CrashDialogWindow : Window
{
    public CrashDialogWindow()
    {
        InitializeComponent();
        DataContext = new CrashDialogViewModel(this);
    }

    /// <summary>Show a captured crash. Safe to call from any thread; never throws.</summary>
    public static void Show(CrashReport report)
    {
        try
        {
            var window = new CrashDialogWindow();
            window.DataContext = new CrashDialogViewModel(window)
            {
                Headline = $"{report.ExceptionType}: {report.Message}",
                Diagnostics = CrashReporterUi.FormatDiagnostics(report),
            };
            window.Show();
        }
        catch (Exception e)
        {
            // A dialog must never be the thing that finally kills the process.
            SynapicLog.Error(nameof(CrashDialogWindow), $"Crash dialog failed to show: {e}");
        }
    }
}

/// <summary>Shared formatting helpers for the crash UI.</summary>
public static class CrashReporterUi
{
    public static string FormatDiagnostics(CrashReport report) =>
        $"Time (UTC):  {report.TimestampUtc:O}{Environment.NewLine}" +
        $"Source:      {report.Source}{Environment.NewLine}" +
        $"Terminal:    {report.IsTerminal}{Environment.NewLine}" +
        $"App version: {report.AppVersion}{Environment.NewLine}" +
        $"OS:          {report.OsVersion}{Environment.NewLine}" +
        $"Exception:   {report.ExceptionType}{Environment.NewLine}" +
        $"Message:     {report.Message}{Environment.NewLine}{Environment.NewLine}" +
        $"Stack trace:{Environment.NewLine}{report.StackTrace}";
}

/// <summary>Tiny shell helper (kept separate for testability).</summary>
public static class ProcessExtensions
{
    public static void OpenDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        }
        else if (OperatingSystem.IsLinux())
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", path) { UseShellExecute = true });
        }
        else if (OperatingSystem.IsMacOS())
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("open", path) { UseShellExecute = true });
        }
    }
}

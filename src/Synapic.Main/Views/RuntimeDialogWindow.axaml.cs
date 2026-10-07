using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Main.Services;

namespace Synapic.Main.Views;

/// <summary>
/// View model for the "you need the .NET runtime" dialog: holds the link the
/// runtime check handed over and implements open/copy/continue. The link is the
/// point of the dialog - a silent install that failed has no UI of its own, so
/// this is where the user finds out where to go.
/// </summary>
public partial class RuntimeDialogViewModel : ObservableObject
{
    private readonly Window? _owner;
    private readonly Action<string> _openUrl;

    [ObservableProperty]
    private string _heading = $"Synapic needs the .NET {DotNetRuntimeCheckService.MinimumVersion.Major} Desktop Runtime";

    [ObservableProperty]
    private string _headline = "The automatic install did not complete.";

    [ObservableProperty]
    private string _detail = "Install it from the official page below, then restart Synapic.";

    [ObservableProperty]
    private string _downloadLabel = $"Download .NET {DotNetRuntimeCheckService.MinimumVersion.Major}";

    [ObservableProperty]
    private string _downloadUrl = DotNetRuntimeCheckService.DownloadPageUrl;

    /// <param name="owner">Closed by <see cref="ContinueCommand"/>; null in tests.</param>
    /// <param name="openUrl">How the link is opened; injectable so tests can watch (or refuse) it.</param>
    public RuntimeDialogViewModel(Window? owner = null, Action<string>? openUrl = null)
    {
        _owner = owner;
        _openUrl = openUrl ?? ProcessExtensions.OpenUrl;
    }

    [RelayCommand]
    private void OpenDownload()
    {
        try
        {
            _openUrl(DownloadUrl);
        }
        catch (Exception e)
        {
            // A refused launch must not take the dialog (or the app) down; the
            // URL is on screen and copyable either way.
            SynapicLog.Warning(nameof(RuntimeDialogViewModel), $"Opening {DownloadUrl} failed: {e.Message}");
        }
    }

    [RelayCommand]
    private async Task CopyLinkAsync()
    {
        try
        {
            var clipboard = _owner is null ? null : TopLevel.GetTopLevel(_owner)?.Clipboard;
            if (clipboard is not null)
                await clipboard.SetTextAsync(DownloadUrl);
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(RuntimeDialogViewModel), $"Copy to clipboard failed: {e.Message}");
        }
    }

    [RelayCommand]
    private void Continue() => _owner?.Close();
}

/// <summary>
/// "The .NET runtime is missing" dialog: shown when the startup check could not
/// install the runtime itself, so the user gets the official download link
/// instead of a broken app and no explanation. Purely informational - the app
/// keeps running.
/// </summary>
public partial class RuntimeDialogWindow : Window
{
    public RuntimeDialogWindow()
    {
        InitializeComponent();
        DataContext = new RuntimeDialogViewModel(this);
    }

    /// <summary>Offer the download link for a failed runtime check. Never throws.</summary>
    public static void Show(RuntimeCheckResult result)
    {
        try
        {
            var window = new RuntimeDialogWindow();
            window.DataContext = ViewModelFor(result, window);
            window.Show();
        }
        catch (Exception e)
        {
            // The dialog is a courtesy; the link is in the log regardless.
            SynapicLog.Error(nameof(RuntimeDialogWindow), $"Runtime dialog failed to show: {e}");
        }
    }

    /// <summary>
    /// The view model the dialog renders a result with; split out of
    /// <see cref="Show(RuntimeCheckResult)"/> so the populated window can be
    /// built headlessly in tests.
    /// </summary>
    internal static RuntimeDialogViewModel ViewModelFor(RuntimeCheckResult result, Window? owner = null) =>
        new(owner)
        {
            Headline = Describe(result),
            Detail = $"{Explain(result)} Install it from the official page below, then restart Synapic.",
            DownloadUrl = result.DownloadUrl,
        };

    /// <summary>What the check found on this machine.</summary>
    internal static string Describe(RuntimeCheckResult result)
    {
        var minimum = DotNetRuntimeCheckService.MinimumVersion;
        return result.InstalledVersion is { } installed
            ? $"This machine reports .NET {installed}, older than the {minimum} Synapic targets."
            : $"This machine does not report a .NET {minimum.Major} Desktop Runtime at all.";
    }

    /// <summary>Why the automatic install could not fix it.</summary>
    internal static string Explain(RuntimeCheckResult result) => result.Outcome switch
    {
        RuntimeCheckOutcome.DownloadFailed =>
            "Synapic could not fetch the official installer; the machine is probably offline, or a proxy blocked the download.",
        RuntimeCheckOutcome.InstallFailed =>
            "The official installer ran but the runtime is still not registered; the Windows elevation prompt was probably declined.",
        _ => "The runtime could not be installed automatically.",
    };
}

using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Main.Services;

namespace Synapic.Main.ViewModels;

/// <summary>
/// One sidecar variant (CPU or CUDA) as shown in the startup setup panel:
/// whether it exists on disk, where it lives, and how to get it - fetch the
/// executable the GitHub release already published (the only source; the
/// build-from-source option was retired for release). A row that exists and is
/// current offers no action at all, only a disabled "Up to date" marker.
/// The sidecar is the pivotal part of the app - nothing can be tagged without
/// it - so the panel is the only interactive surface until one variant exists.
/// </summary>
public partial class SidecarVariantViewModel : ViewModelBase
{
    private readonly Func<SidecarVariantViewModel, CancellationToken, Task> _downloadAsync;
    private bool _canDownload = true;

    public SidecarVariantViewModel(
        string rid,
        string detail,
        Func<SidecarVariantViewModel, CancellationToken, Task> downloadAsync)
    {
        Rid = rid;
        DisplayName = InferenceSidecarService.VariantDisplayName(rid);
        Detail = detail;
        _downloadAsync = downloadAsync;
        DownloadCommand = new AsyncRelayCommand(ct => _downloadAsync(this, ct), () => CanDownloadNow);
    }

    public string Rid { get; }

    public string DisplayName { get; }

    /// <summary>One-line guidance on when to choose this variant.</summary>
    public string Detail { get; }

    /// <summary>Fetches the prebuilt executable from the GitHub release.</summary>
    public AsyncRelayCommand DownloadCommand { get; }

    [ObservableProperty]
    private bool _isBuilt;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private string _exePath = string.Empty;

    [ObservableProperty]
    private string _sizeText = string.Empty;

    /// <summary>
    /// Why the executable trails the GitHub release (dev checkouts only), so the
    /// row can say an update is available instead of leaving the reason in the log.
    /// </summary>
    [ObservableProperty]
    private string? _staleNotice;

    /// <summary>True when a newer sidecar build exists than the one on disk.</summary>
    public bool IsStale => StaleNotice is not null;

    public string StaleText => StaleNotice is null ? string.Empty : $"Update available \u2014 {StaleNotice}";

    /// <summary>
    /// Set by the startup GitHub check: a newer prebuilt exists on the release
    /// page for this RID. The row then points at the existing Download/Update
    /// button.
    /// </summary>
    [ObservableProperty]
    private string? _gitHubUpdateText;

    public bool IsGitHubUpdate => GitHubUpdateText is not null;

    /// <summary>Live download completion, 0-100.</summary>
    [ObservableProperty]
    private double _downloadPercent;

    /// <summary>What the download is doing right now, e.g. "Downloading part 2 of 3".</summary>
    [ObservableProperty]
    private string _downloadStage = string.Empty;

    /// <summary>False until the download reports its first stage, so the bar can spin first.</summary>
    [ObservableProperty]
    private bool _hasProgress;

    public bool IsDownloadIndeterminate => !HasProgress;

    /// <summary>False while another row is busy - only one operation runs at a time.</summary>
    public bool CanDownload
    {
        get => _canDownload;
        set
        {
            if (SetProperty(ref _canDownload, value)) NotifyCommands();
        }
    }

    /// <summary>
    /// True while the variant has no executable on disk, or the one it has
    /// trails the GitHub release - exactly the states in which the panel offers
    /// the way forward (Download) and withholds Update.
    /// </summary>
    public bool IsMissingOrOutdated => !IsBuilt || IsStale || IsGitHubUpdate;

    /// <summary>Offered whenever the variant still needs getting: a missing
    /// variant is fetched, an outdated one is replaced.</summary>
    public bool IsDownloadButtonVisible => CanDownload && IsMissingOrOutdated && !IsBusy;

    /// <summary>
    /// The no-action state: the executable exists and the GitHub release has
    /// nothing newer for it. Rendered as a disabled "Up to date" button, so the
    /// row still has the shape of the ones that do offer actions but cannot be
    /// pressed.
    /// </summary>
    public bool IsUpToDateVisible => IsBuilt && !IsMissingOrOutdated && !IsBusy;

    /// <summary>True while this row is fetching an executable.</summary>
    public bool IsBusy => IsDownloading;

    public string StatusText => IsDownloading ? "Downloading\u2026"
        : IsBuilt ? (SizeText.Length > 0 ? $"Built ({SizeText})" : "Built")
        : "Not built";

    /// <summary>Status dot: green once this variant exists on disk.</summary>
    public IBrush StatusBrush => IsBuilt ? Brushes.ForestGreen : Brushes.Gray;

    /// <summary>
    /// Applies the on-disk state detected for this variant, including whether
    /// the executable it found now trails the GitHub release.
    /// </summary>
    public void ApplyBuildState(string? exePath, string? staleNotice = null)
    {
        ExePath = exePath ?? string.Empty;
        // Set the size before IsBuilt so StatusText sees it when it re-reads.
        SizeText = exePath is null ? string.Empty : TryFormatSize(exePath);
        IsBuilt = exePath is not null;
        StaleNotice = IsBuilt ? staleNotice : null;
        NotifyCommands();
    }

    public void NotifyCommands()
    {
        DownloadCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsMissingOrOutdated));
        OnPropertyChanged(nameof(IsDownloadButtonVisible));
        OnPropertyChanged(nameof(IsUpToDateVisible));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
    }

    /// <summary>Clears the bar so a fresh download starts from zero.</summary>
    public void ResetDownloadProgress()
    {
        DownloadPercent = 0;
        DownloadStage = string.Empty;
        HasProgress = false;
    }

    /// <summary>Applies a stage/percentage update streamed from the download.</summary>
    public void ApplyProgress(SidecarDownloadProgress progress)
    {
        void Apply()
        {
            DownloadPercent = progress.Percent;
            DownloadStage = progress.Stage;
            HasProgress = true;
        }

        // Reports arrive on pipeline threads; the bar is bound on the UI thread.
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    // Can-execute mirrors the button visibility exactly: a command may never
    // run in a state its button is hidden in (and vice versa).
    private bool CanDownloadNow => CanDownload && IsMissingOrOutdated && !IsBusy;

    partial void OnIsBuiltChanged(bool value) => NotifyCommands();

    partial void OnIsDownloadingChanged(bool value) => NotifyCommands();

    partial void OnStaleNoticeChanged(string? value)
    {
        OnPropertyChanged(nameof(IsStale));
        OnPropertyChanged(nameof(StaleText));
        // Staleness flips which button the row shows.
        NotifyCommands();
    }

    partial void OnGitHubUpdateTextChanged(string? value)
    {
        OnPropertyChanged(nameof(IsGitHubUpdate));
        // The startup GitHub check flips the row between Update and Download.
        NotifyCommands();
    }

    partial void OnHasProgressChanged(bool value) => OnPropertyChanged(nameof(IsDownloadIndeterminate));

    private static string TryFormatSize(string path)
    {
        try
        {
            var len = new FileInfo(path).Length;
            return len >= 1L << 30
                ? $"{len / (double)(1L << 30):0.0} GB"
                : $"{len / (double)(1L << 20):0} MB";
        }
        catch
        {
            return string.Empty;
        }
    }
}

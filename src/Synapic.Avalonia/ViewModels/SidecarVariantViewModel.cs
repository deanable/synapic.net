using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Avalonia.Services;

namespace Synapic.Avalonia.ViewModels;

/// <summary>
/// One sidecar variant (CPU or CUDA) as shown in the startup setup panel:
/// whether it exists on disk, where it lives, and how to get it - build it from
/// source or fetch the executable the GitHub release already published. A row
/// that exists and is current offers no action at all, only a disabled
/// "Up to date" marker.
/// The sidecar is the pivotal part of the app - nothing can be tagged without
/// it - so the panel is the only interactive surface until one variant exists.
/// </summary>
public partial class SidecarVariantViewModel : ViewModelBase
{
    private readonly Func<SidecarVariantViewModel, CancellationToken, Task> _buildAsync;
    private readonly Func<SidecarVariantViewModel, CancellationToken, Task> _downloadAsync;
    private bool _canBuild;
    private bool _canDownload = true;

    public SidecarVariantViewModel(
        string rid,
        string detail,
        bool canBuild,
        Func<SidecarVariantViewModel, CancellationToken, Task> buildAsync,
        Func<SidecarVariantViewModel, CancellationToken, Task> downloadAsync)
    {
        Rid = rid;
        DisplayName = InferenceSidecarService.VariantDisplayName(rid);
        Detail = detail;
        _canBuild = canBuild;
        _buildAsync = buildAsync;
        _downloadAsync = downloadAsync;
        BuildCommand = new AsyncRelayCommand(ct => _buildAsync(this, ct), () => CanBuildNow);
        DownloadCommand = new AsyncRelayCommand(ct => _downloadAsync(this, ct), () => CanDownloadNow);
    }

    public string Rid { get; }

    public string DisplayName { get; }

    /// <summary>One-line guidance on when to choose this variant.</summary>
    public string Detail { get; }

    public AsyncRelayCommand BuildCommand { get; }

    /// <summary>Fetches the prebuilt executable from the GitHub release instead
    /// of compiling it - also how an outdated build is replaced by the release.</summary>
    public AsyncRelayCommand DownloadCommand { get; }

    [ObservableProperty]
    private bool _isBuilt;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private string _exePath = string.Empty;

    [ObservableProperty]
    private string _sizeText = string.Empty;

    [ObservableProperty]
    private bool _isBuilding;

    /// <summary>
    /// Why a built executable trails its source (dev checkouts only), so the row
    /// can say an update is available instead of leaving the reason in the log.
    /// </summary>
    [ObservableProperty]
    private string? _staleNotice;

    /// <summary>True when a newer sidecar build exists than the one on disk.</summary>
    public bool IsStale => StaleNotice is not null;

    public string StaleText => StaleNotice is null ? string.Empty : $"Update available \u2014 {StaleNotice}";

    /// <summary>
    /// Set by the startup GitHub check: a newer prebuilt exists on the release
    /// page for this RID. The row then points at the existing Download/Update
    /// button - the alternative to spending minutes on a local build.
    /// </summary>
    [ObservableProperty]
    private string? _gitHubUpdateText;

    public bool IsGitHubUpdate => GitHubUpdateText is not null;

    /// <summary>Live build completion, 0-100, driven by the real pipeline.</summary>
    [ObservableProperty]
    private double _buildPercent;

    /// <summary>What the build is doing right now, e.g. "Packaging: archive assembled".</summary>
    [ObservableProperty]
    private string _buildStage = string.Empty;

    /// <summary>False until the pipeline reports its first stage, so the bar can spin first.</summary>
    [ObservableProperty]
    private bool _hasProgress;

    public bool IsBuildIndeterminate => !HasProgress;

    /// <summary>True when the build scripts exist and no build is currently running.</summary>
    public bool CanBuild
    {
        get => _canBuild;
        set
        {
            if (SetProperty(ref _canBuild, value)) NotifyCommands();
        }
    }

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
    /// trails the sidecar source or the GitHub release - exactly the states in
    /// which the panel offers the two ways forward (Build and Download) and
    /// withholds Update.
    /// </summary>
    public bool IsMissingOrOutdated => !IsBuilt || IsStale || IsGitHubUpdate;

    /// <summary>Offered whenever the variant still needs getting: a missing
    /// variant is built, an outdated one is rebuilt over what is there.</summary>
    public bool IsBuildButtonVisible => CanBuild && IsMissingOrOutdated && !IsBusy;

    /// <summary>The alternative to building: fetch what the latest release
    /// already published, replacing an outdated executable when there is one.</summary>
    public bool IsDownloadButtonVisible => CanDownload && IsMissingOrOutdated && !IsBusy;

    /// <summary>
    /// The no-action state: the executable exists and neither the source nor
    /// the GitHub release has anything newer for it. Rendered as a disabled
    /// "Up to date" button, so the row still has the shape of the ones that do
    /// offer actions but cannot be pressed.
    /// </summary>
    public bool IsUpToDateVisible => IsBuilt && !IsMissingOrOutdated && !IsBusy;

    /// <summary>One progress bar serves all operations, so they must never overlap.</summary>
    public bool IsBusy => IsBuilding || IsDownloading;

    public string StatusText => IsBuilding ? "Building\u2026"
        : IsDownloading ? "Downloading\u2026"
        : IsBuilt ? (SizeText.Length > 0 ? $"Built ({SizeText})" : "Built")
        : "Not built";

    /// <summary>Status dot: green once this variant exists on disk.</summary>
    public IBrush StatusBrush => IsBuilt ? Brushes.ForestGreen : Brushes.Gray;

    /// <summary>
    /// Applies the on-disk state detected for this variant, including whether
    /// the executable it found now trails the sidecar source.
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
        BuildCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsMissingOrOutdated));
        OnPropertyChanged(nameof(IsBuildButtonVisible));
        OnPropertyChanged(nameof(IsDownloadButtonVisible));
        OnPropertyChanged(nameof(IsUpToDateVisible));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
    }

    /// <summary>Clears the bar so a fresh build starts from zero.</summary>
    public void ResetBuildProgress()
    {
        BuildPercent = 0;
        BuildStage = string.Empty;
        HasProgress = false;
    }

    /// <summary>Applies a stage/percentage update streamed from the build pipeline.</summary>
    public void ApplyProgress(SidecarBuildProgress progress)
    {
        void Apply()
        {
            BuildPercent = progress.Percent;
            BuildStage = progress.Stage;
            HasProgress = true;
        }

        // Reports arrive on pipeline threads; the bar is bound on the UI thread.
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    // Can-execute mirrors the button visibility exactly: a command may never
    // run in a state its button is hidden in (and vice versa).
    private bool CanBuildNow => CanBuild && IsMissingOrOutdated && !IsBusy;

    private bool CanDownloadNow => CanDownload && IsMissingOrOutdated && !IsBusy;

    partial void OnIsBuiltChanged(bool value) => NotifyCommands();

    partial void OnIsBuildingChanged(bool value) => NotifyCommands();

    partial void OnIsDownloadingChanged(bool value) => NotifyCommands();

    partial void OnStaleNoticeChanged(string? value)
    {
        OnPropertyChanged(nameof(IsStale));
        OnPropertyChanged(nameof(StaleText));
        // Staleness flips which pair of buttons the row shows.
        NotifyCommands();
    }

    partial void OnGitHubUpdateTextChanged(string? value)
    {
        OnPropertyChanged(nameof(IsGitHubUpdate));
        // The startup GitHub check flips the row from Update to Build+Download.
        NotifyCommands();
    }

    partial void OnHasProgressChanged(bool value) => OnPropertyChanged(nameof(IsBuildIndeterminate));

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

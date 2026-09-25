using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Avalonia.Services;

namespace Synapic.Avalonia.ViewModels;

/// <summary>
/// One sidecar variant (CPU or CUDA) as shown in the startup setup panel:
/// whether it exists on disk, where it lives, and how to get it - build it from
/// source, fetch the executable the GitHub release already published, or replace
/// one that is already there.
/// The sidecar is the pivotal part of the app - nothing can be tagged without
/// it - so the panel is the only interactive surface until one variant exists.
/// </summary>
public partial class SidecarVariantViewModel : ViewModelBase
{
    private readonly Func<SidecarVariantViewModel, CancellationToken, Task> _buildAsync;
    private readonly Func<SidecarVariantViewModel, CancellationToken, Task> _downloadAsync;
    private readonly Func<SidecarVariantViewModel, CancellationToken, Task> _updateAsync;
    private bool _canBuild;
    private bool _canDownload = true;
    private bool _canUpdate = true;

    public SidecarVariantViewModel(
        string rid,
        string detail,
        bool canBuild,
        Func<SidecarVariantViewModel, CancellationToken, Task> buildAsync,
        Func<SidecarVariantViewModel, CancellationToken, Task> downloadAsync,
        Func<SidecarVariantViewModel, CancellationToken, Task> updateAsync)
    {
        Rid = rid;
        DisplayName = InferenceSidecarService.VariantDisplayName(rid);
        Detail = detail;
        _canBuild = canBuild;
        _buildAsync = buildAsync;
        _downloadAsync = downloadAsync;
        _updateAsync = updateAsync;
        BuildCommand = new AsyncRelayCommand(ct => _buildAsync(this, ct), () => CanBuildNow);
        DownloadCommand = new AsyncRelayCommand(ct => _downloadAsync(this, ct), () => CanDownloadNow);
        UpdateCommand = new AsyncRelayCommand(ct => _updateAsync(this, ct), () => CanUpdateNow);
    }

    public string Rid { get; }

    public string DisplayName { get; }

    /// <summary>One-line guidance on when to choose this variant.</summary>
    public string Detail { get; }

    public AsyncRelayCommand BuildCommand { get; }

    /// <summary>Fetches the prebuilt executable from the GitHub release instead of compiling it.</summary>
    public AsyncRelayCommand DownloadCommand { get; }

    /// <summary>
    /// Replaces an executable that already exists: the only action offered once a
    /// variant is built, because Build and Download both hide themselves then -
    /// which left a sidecar that trails its source with no way back in.
    /// </summary>
    public AsyncRelayCommand UpdateCommand { get; }

    [ObservableProperty]
    private bool _isBuilt;

    [ObservableProperty]
    private bool _isDownloading;

    /// <summary>True while an existing executable is being replaced.</summary>
    [ObservableProperty]
    private bool _isUpdating;

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

    /// <summary>False while another row is busy - only one operation runs at a time.</summary>
    public bool CanUpdate
    {
        get => _canUpdate;
        set
        {
            if (SetProperty(ref _canUpdate, value)) NotifyCommands();
        }
    }

    /// <summary>Shown only for a variant that still needs building.</summary>
    public bool IsBuildButtonVisible => CanBuild && !IsBuilt && !IsBusy;

    /// <summary>The alternative to building: fetch what the latest release already published.</summary>
    public bool IsDownloadButtonVisible => CanDownload && !IsBuilt && !IsBusy;

    /// <summary>The only action offered for a variant that is already built.</summary>
    public bool IsUpdateButtonVisible => CanUpdate && IsBuilt && !IsBusy;

    /// <summary>One progress bar serves all three operations, so they must never overlap.</summary>
    public bool IsBusy => IsBuilding || IsDownloading || IsUpdating;

    public string StatusText => IsUpdating
        ? "Updating\u2026"
        : IsBuilding ? "Building\u2026"
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
        UpdateCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsBuildButtonVisible));
        OnPropertyChanged(nameof(IsDownloadButtonVisible));
        OnPropertyChanged(nameof(IsUpdateButtonVisible));
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

    private bool CanBuildNow => CanBuild && !IsBuilt && !IsBusy;

    private bool CanDownloadNow => CanDownload && !IsBuilt && !IsBusy;

    private bool CanUpdateNow => CanUpdate && IsBuilt && !IsBusy;

    partial void OnIsBuiltChanged(bool value) => NotifyCommands();

    partial void OnIsBuildingChanged(bool value) => NotifyCommands();

    partial void OnIsDownloadingChanged(bool value) => NotifyCommands();

    partial void OnIsUpdatingChanged(bool value) => NotifyCommands();

    partial void OnStaleNoticeChanged(string? value)
    {
        OnPropertyChanged(nameof(IsStale));
        OnPropertyChanged(nameof(StaleText));
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

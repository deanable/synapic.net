using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Avalonia.Services;

namespace Synapic.Avalonia.ViewModels;

/// <summary>
/// One buildable sidecar variant (CPU or CUDA) as shown in the startup setup
/// panel: whether it exists on disk, where it lives, and a command to build it.
/// The sidecar is the pivotal part of the app - nothing can be tagged without
/// it - so the panel is the only interactive surface until one variant exists.
/// </summary>
public partial class SidecarVariantViewModel : ViewModelBase
{
    private readonly Func<SidecarVariantViewModel, CancellationToken, Task> _buildAsync;
    private bool _canBuild;

    public SidecarVariantViewModel(
        string rid,
        string detail,
        bool canBuild,
        Func<SidecarVariantViewModel, CancellationToken, Task> buildAsync)
    {
        Rid = rid;
        DisplayName = InferenceSidecarService.VariantDisplayName(rid);
        Detail = detail;
        _canBuild = canBuild;
        _buildAsync = buildAsync;
        BuildCommand = new AsyncRelayCommand(ct => _buildAsync(this, ct), () => CanBuildNow);
    }

    public string Rid { get; }

    public string DisplayName { get; }

    /// <summary>One-line guidance on when to choose this variant.</summary>
    public string Detail { get; }

    public AsyncRelayCommand BuildCommand { get; }

    [ObservableProperty]
    private bool _isBuilt;

    [ObservableProperty]
    private string _exePath = string.Empty;

    [ObservableProperty]
    private string _sizeText = string.Empty;

    [ObservableProperty]
    private bool _isBuilding;

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

    /// <summary>Shown only for a variant that still needs building.</summary>
    public bool IsBuildButtonVisible => CanBuild && !IsBuilt && !IsBuilding;

    public string StatusText => IsBuilt
        ? (SizeText.Length > 0 ? $"Built ({SizeText})" : "Built")
        : "Not built";

    /// <summary>Status dot: green once this variant exists on disk.</summary>
    public IBrush StatusBrush => IsBuilt ? Brushes.ForestGreen : Brushes.Gray;

    /// <summary>Applies the on-disk state detected for this variant.</summary>
    public void ApplyBuildState(string? exePath)
    {
        ExePath = exePath ?? string.Empty;
        // Set the size before IsBuilt so StatusText sees it when it re-reads.
        SizeText = exePath is null ? string.Empty : TryFormatSize(exePath);
        IsBuilt = exePath is not null;
        NotifyCommands();
    }

    public void NotifyCommands()
    {
        BuildCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsBuildButtonVisible));
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

    private bool CanBuildNow => CanBuild && !IsBuilt && !IsBuilding;

    partial void OnIsBuiltChanged(bool value) => NotifyCommands();

    partial void OnIsBuildingChanged(bool value) => NotifyCommands();

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

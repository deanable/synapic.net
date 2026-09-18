using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.Services.Processing;

namespace Synapic.Avalonia.ViewModels.Steps;

/// <summary>
/// Dedup wizard step (port of step_dedup.py): pick algorithm + threshold,
/// scan a folder for duplicate groups, review and apply bulk actions.
/// </summary>
public partial class StepDedupViewModel : ViewModelBase
{
    private readonly IDedupService _dedup;

    public StepDedupViewModel(IDedupService? dedup = null)
    {
        _dedup = dedup ?? new DedupService();
    }

    [ObservableProperty]
    private string _folderPath = "";

    [ObservableProperty]
    private int _selectedAlgorithm; // index into Algorithms

    [ObservableProperty]
    private double _threshold = 0.90;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _scanSummary = "";

    [ObservableProperty]
    private DuplicateGroup? _selectedGroup;

    public ObservableCollection<DuplicateGroup> Groups { get; } = new();

    public string[] Algorithms { get; } = { "PHash", "DHash", "AHash", "ColorMoment" };
    public string[] Actions { get; } = { "Tag", "Move", "Delete" };

    [ObservableProperty]
    private int _selectedAction; // index into Actions

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync(CancellationToken ct)
    {
        IsScanning = true;
        Groups.Clear();
        ScanSummary = "Scanning…";
        try
        {
            var files = System.IO.Directory.EnumerateFiles(
                    FolderPath, "*.*", System.IO.SearchOption.AllDirectories)
                .Where(f =>
                {
                    var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                    return ext is ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff";
                })
                .ToArray();

            var progress = new Progress<DedupProgress>(p =>
                ScanSummary = $"Hashed {p.FilesHashed}/{p.TotalFiles}…");

            var result = await _dedup.FindDuplicatesAsync(
                files,
                new DedupOptions(
                    (HashAlgorithm)SelectedAlgorithm,
                    Threshold,
                    MaxDimension: 512),
                progress,
                ct);

            foreach (var g in result.Groups) Groups.Add(g);
            var dupCount = Groups.Sum(g => g.Items.Length - 1);
            ScanSummary = $"{files.Length} files scanned — {Groups.Count} groups, {dupCount} duplicates";
            SynapicLog.Info(nameof(StepDedupViewModel), ScanSummary);
        }
        catch (Exception e)
        {
            ScanSummary = $"Scan failed: {e.Message}";
            SynapicLog.Error(nameof(StepDedupViewModel), ScanSummary);
        }
        finally
        {
            IsScanning = false;
            ScanCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanScan() => !IsScanning && !string.IsNullOrWhiteSpace(FolderPath);

    partial void OnFolderPathChanged(string value) => ScanCommand.NotifyCanExecuteChanged();
    partial void OnIsScanningChanged(bool value) => ScanCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync(CancellationToken ct)
    {
        if (SelectedGroup is null) return;

        var result = new DedupResult();
        result.Groups.Add(SelectedGroup);
        var action = (DedupAction)SelectedAction;
        var ok = await _dedup.ApplyActionsAsync(result, action, ct);
        ScanSummary = ok
            ? $"{action} applied to group ({SelectedGroup.Items.Length - 1} duplicates)"
            : $"{action} completed with errors — see log";

        Groups.Remove(SelectedGroup);
        SelectedGroup = null;
    }

    private bool CanApply() => SelectedGroup is not null;

    partial void OnSelectedGroupChanged(DuplicateGroup? value) => ApplyCommand.NotifyCanExecuteChanged();
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.Services.Processing;

namespace Synapic.Avalonia.ViewModels.Steps;

/// <summary>
/// One image inside a duplicate group. The checkbox is the keep-set: a checked
/// item survives, unchecked items are what Apply acts on (Tag/Move/Delete for a
/// local source, catalog delete for Daminion). The box can be toggled by hand
/// or driven automatically by the step's oldest/newest/smallest/largest rule
/// checkboxes (rule changes recompute the whole selection — hand tweaks stick
/// until a rule is toggled again).
/// </summary>
public partial class DedupItemViewModel : ObservableObject
{
    private bool _isChecked;
    private bool _suppressRule;

    public DedupItemViewModel(string key, string displayName)
    {
        Key = key;
        DisplayName = displayName;
    }

    /// <summary>Stable identity: local path, or "daminion:{id}" for catalog items.</summary>
    public string Key { get; }

    public string DisplayName { get; }

    /// <summary>File size in bytes when known (local FileInfo or downloaded original).</summary>
    public long? SizeBytes { get; init; }

    /// <summary>Best available date: EXIF capture date, else file creation date.</summary>
    public DateTime? DateTaken { get; init; }

    /// <summary>Catalog item id when the source is Daminion.</summary>
    public int? DaminionId { get; init; }

    public string SizeText => SizeBytes is { } b ? FormatSize(b) : "—";

    public string DateText => DateTaken is { } d ? d.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—";

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (!SetProperty(ref _isChecked, value)) return;
            if (!_suppressRule) SelectionChanged?.Invoke();
        }
    }

    /// <summary>Raised after any checkbox change (rule-driven or manual); the group
    /// forwards it so the step refreshes its counts and command state.</summary>
    public event Action? SelectionChanged;

    /// <summary>Programmatic (rule/scan) checkbox write used by the auto-select recompute.</summary>
    internal void SetCheckedByRule(bool checkedState)
    {
        _suppressRule = true;
        IsChecked = checkedState;
        _suppressRule = false;
        SelectionChanged?.Invoke();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.##} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.#} KB",
        _ => $"{bytes} B",
    };
}

/// <summary>One duplicate group, rendered as a card in the vertical group list.</summary>
public partial class DuplicateGroupViewModel : ObservableObject
{
    public DuplicateGroupViewModel(IEnumerable<DedupItemViewModel> items, string hashType, double threshold)
    {
        Items = new ObservableCollection<DedupItemViewModel>(items);
        HashType = hashType;
        Threshold = threshold;
        foreach (var item in Items)
            item.SelectionChanged += OnItemSelectionChanged;
    }

    public ObservableCollection<DedupItemViewModel> Items { get; }

    public string HashType { get; }

    public double Threshold { get; }

    public string Header => $"{Items.Count} similar images";

    public string Summary => $"{Items.Count(i => !i.IsChecked)} selected for the action — {Items.Count(i => i.IsChecked)} kept";

    private void OnItemSelectionChanged()
    {
        OnPropertyChanged(nameof(Summary));
        SelectionChanged?.Invoke();
    }

    /// <summary>Forwarded from any item checkbox change.</summary>
    public event Action? SelectionChanged;
}

/// <summary>
/// Dedup wizard step (port of step_dedup.py): pick algorithm + threshold, choose
/// a source — a local folder or the Step 1 Daminion scope (exactly like tagging
/// does) — scan for duplicate groups, review them as a vertical list of group
/// cards with per-item keep checkboxes, drive those checkboxes from the
/// oldest/newest/smallest/largest auto-select set, and apply a bulk action to
/// the unchecked duplicates.
/// </summary>
public partial class StepDedupViewModel : ViewModelBase
{
    private readonly IDedupService _dedup;
    private readonly Step1DatasourceViewModel? _step1;
    private readonly IInferenceSidecar? _sidecar;

    /// <param name="dedup">Injectable dedup engine (tests).</param>
    /// <param name="step1">Step 1 provides the Daminion source (connection +
    /// scope collection), mirroring how tagging gets its datasource.</param>
    /// <param name="sidecar">Needed only to build the fetch orchestrator for a
    /// Daminion source; local scans never touch it.</param>
    /// <param name="confirm">Modal confirmation for the catalog delete; the dedup
    /// view wires the real dialog, tests inject answers. Null = cancel (fail closed).</param>
    public StepDedupViewModel(
        IDedupService? dedup = null,
        Step1DatasourceViewModel? step1 = null,
        IInferenceSidecar? sidecar = null,
        Func<string, Task<bool>>? confirm = null)
    {
        _dedup = dedup ?? new DedupService();
        _step1 = step1;
        _sidecar = sidecar;
        ConfirmAction = confirm;

        // Step 1 connects / re-scopes while the dedup tab may be showing:
        // re-evaluate Scan/Apply and the scope summary on any Step 1 change.
        if (_step1 is not null)
            _step1.PropertyChanged += (_, _) =>
            {
                ScanCommand.NotifyCanExecuteChanged();
                ApplyCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(DaminionScopeSummary));
            };
    }

    /// <summary>Modal confirmation hook (message → confirmed?). Set by the view;
    /// null cancels every destructive action (Daminion catalog delete and local
    /// file delete alike) — fail closed.</summary>
    public Func<string, Task<bool>>? ConfirmAction { get; set; }

    // ── Source (local vs Daminion — same radio pattern as Step 1) ───────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocal))]
    [NotifyPropertyChangedFor(nameof(IsDaminion))]
    [NotifyPropertyChangedFor(nameof(IsLocalSelected))]
    [NotifyPropertyChangedFor(nameof(IsDaminionSelected))]
    private string _datasourceType = "local";

    public bool IsLocal => DatasourceType == "local";

    public bool IsDaminion => DatasourceType == "daminion";

    /// <summary>Settable radio bindings (getter-only ones bind dead — see Step 1).</summary>
    public bool IsLocalSelected
    {
        get => IsLocal;
        set
        {
            if (value) DatasourceType = "local";
        }
    }

    public bool IsDaminionSelected
    {
        get => IsDaminion;
        set
        {
            if (value) DatasourceType = "daminion";
        }
    }

    partial void OnDatasourceTypeChanged(string value)
    {
        SelectedAction = 0;
        OnPropertyChanged(nameof(Actions));
        OnPropertyChanged(nameof(DaminionScopeSummary));
        ScanCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Which Step 1 scope collection feeds the Daminion scan (read-only mirror).</summary>
    public string DaminionScopeSummary => _step1 is null
        ? "No datasource step available"
        : $"Source: {_step1.Scope switch
        {
            "collection" => $"Shared collection #{_step1.CollectionId}",
            "saved_search" => $"Saved search #{_step1.SavedSearchId}",
            "search" => $"Keyword search \"{_step1.SearchTerm}\"",
            _ => "Entire catalog",
        }} — configured in Step 1";

    private bool DaminionReady => IsDaminion && _step1?.ConnectedClient is not null;

    // ── Scan settings ───────────────────────────────────────────────────────

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

    public string[] Algorithms { get; } = { "PHash", "DHash", "AHash", "ColorMoment" };

    public string[] LocalActions { get; } = { "Tag", "Move", "Delete" };

    /// <summary>Actions offered per source: Daminion only supports deleting catalog entries.</summary>
    public string[] Actions => IsDaminion ? new[] { "Delete from catalog" } : LocalActions;

    [ObservableProperty]
    private int _selectedAction; // index into Actions

    partial void OnSelectedActionChanged(int value) => UpdateSummary();

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = new();

    /// <summary>Total items flagged as duplicates (unchecked) across all groups.</summary>
    public int SelectedForActionCount => Groups.Sum(g => g.Items.Count(i => !i.IsChecked));

    /// <summary>Header count text above the group list.</summary>
    public string SelectionSummary => Groups.Count == 0
        ? ""
        : $"{Groups.Count} duplicate group(s) — {SelectedForActionCount} selected for the action";

    // ── Auto-select rule checkboxes (oldest / newest / smallest / largest) ──

    [ObservableProperty]
    private bool _selectOldest;

    [ObservableProperty]
    private bool _selectNewest;

    [ObservableProperty]
    private bool _selectSmallest;

    [ObservableProperty]
    private bool _selectLargest;

    partial void OnSelectOldestChanged(bool value) => ApplyAutoSelection();

    partial void OnSelectNewestChanged(bool value) => ApplyAutoSelection();

    partial void OnSelectSmallestChanged(bool value) => ApplyAutoSelection();

    partial void OnSelectLargestChanged(bool value) => ApplyAutoSelection();

    /// <summary>
    /// Recompute every group's checkboxes from the active rule set:
    /// checked = union of the picks of all active rules (oldest by date, newest
    /// by date, smallest/largest by size — ties resolve to the group's earlier
    /// item). With no rule active the keep-first default is restored (first
    /// item checked). Items whose value is unknown to a rule (no date / no
    /// size) are never picked by it; if no rule matches anything in a group,
    /// that group falls back to the keep-first default. Toggling a rule
    /// overwrites hand tweaks — that is the "automatic" part; tweak again after.
    /// </summary>
    internal void ApplyAutoSelection()
    {
        var picks = new List<Func<DuplicateGroupViewModel, DedupItemViewModel?>>();
        if (SelectOldest) picks.Add(g => PickBy(g, i => i.DateTaken, ascending: true));
        if (SelectNewest) picks.Add(g => PickBy(g, i => i.DateTaken, ascending: false));
        if (SelectSmallest) picks.Add(g => PickBy(g, i => i.SizeBytes, ascending: true));
        if (SelectLargest) picks.Add(g => PickBy(g, i => i.SizeBytes, ascending: false));

        foreach (var group in Groups)
        {
            if (picks.Count == 0)
            {
                SetDefaultSelection(group);
                continue;
            }

            var checkedSet = new HashSet<DedupItemViewModel>();
            foreach (var pick in picks)
            {
                var target = pick(group);
                if (target is not null) checkedSet.Add(target);
            }

            // Rules matched nothing here (all values unknown) → keep-first.
            if (checkedSet.Count == 0 && group.Items.Count > 0)
                checkedSet.Add(group.Items[0]);

            foreach (var item in group.Items)
                item.SetCheckedByRule(checkedSet.Contains(item));
        }
        UpdateSummary();
    }

    /// <summary>Keep-first default: the first item of the group is checked.</summary>
    private static void SetDefaultSelection(DuplicateGroupViewModel group)
    {
        for (var i = 0; i < group.Items.Count; i++)
            group.Items[i].SetCheckedByRule(i == 0);
    }

    private static DedupItemViewModel? PickBy(
        DuplicateGroupViewModel group,
        Func<DedupItemViewModel, object?> selector,
        bool ascending)
    {
        // LINQ OrderBy is stable, so equal values resolve to the group's earlier
        // item — the same pick the rule chose last time stays put across toggles.
        var dated = group.Items
            .Where(i => selector(i) is not null)
            .ToList();
        if (dated.Count == 0) return null;

        return ascending
            ? dated.OrderBy(selector).First()
            : dated.OrderByDescending(selector).First();
    }

    // ── Scan ────────────────────────────────────────────────────────────────

    private bool CanScan() => !IsScanning && (IsLocal ? !string.IsNullOrWhiteSpace(FolderPath) : DaminionReady);

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync(CancellationToken ct)
    {
        IsScanning = true;
        Groups.Clear();
        ScanSummary = IsDaminion ? "Fetching items from Daminion…" : "Scanning…";
        try
        {
            var opts = new DedupOptions(
                (HashAlgorithm)SelectedAlgorithm,
                Threshold,
                MaxDimension: 512);

            var result = IsDaminion
                ? await ScanDaminionAsync(opts, ct)
                : await ScanLocalAsync(opts, ct);

            AttachGroups(result);
            var dupCount = Groups.Sum(g => g.Items.Count - 1);
            ScanSummary = $"{result.TotalFiles} files scanned — {Groups.Count} groups, {dupCount} duplicates";
            SynapicLog.Info(nameof(StepDedupViewModel), ScanSummary);
            TelemetryService.Shared.RecordDedup(dupCount);

            // Re-apply any active auto-select rules to the fresh groups.
            ApplyAutoSelection();
        }
        catch (OperationCanceledException)
        {
            ScanSummary = "Scan cancelled";
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
            ApplyCommand.NotifyCanExecuteChanged();
            UpdateSummary();
        }
    }

    private async Task<DedupResult> ScanLocalAsync(DedupOptions opts, CancellationToken ct)
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

        return await _dedup.FindDuplicatesAsync(files, opts, progress, ct);
    }

    /// <summary>
    /// Daminion scan: fetch the Step 1 scope's items through the same orchestrator
    /// tagging uses, download each original to a temp file, hash it, record size +
    /// EXIF/file date, and delete the temp again — one at a time so a large
    /// collection never lands on disk whole.
    /// </summary>
    private async Task<DedupResult> ScanDaminionAsync(DedupOptions opts, CancellationToken ct)
    {
        var step1 = _step1 ?? throw new InvalidOperationException("No datasource step available");
        var client = step1.ConnectedClient ?? throw new InvalidOperationException("Not connected to Daminion");
        if (_sidecar is null) throw new InvalidOperationException("Inference sidecar unavailable");

        var ds = step1.ToSelectionForProcessing(client);
        var orchestrator = new ProcessingOrchestrator(_sidecar);
        var items = await orchestrator.FetchItemsAsync(ds, ct);

        var hashes = new Dictionary<string, ulong>(items.Count);
        _pendingDaminionItems = new Dictionary<string, DedupItemViewModel>(items.Count);
        var index = 0;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.DaminionId is not { } id) continue;
            index++;
            ScanSummary = $"Downloading {index}/{items.Count}: {item.FileName}";

            var temp = await client.DownloadOriginalAsync(id, ct);
            if (string.IsNullOrEmpty(temp) || !System.IO.File.Exists(temp)) continue;

            try
            {
                var info = new System.IO.FileInfo(temp);
                var hash = await Task.Run(() => _dedup.ComputeHash(temp, opts), ct);
                if (hash is null) continue;

                var key = $"daminion:{id}";
                hashes[key] = hash.Value;
                _pendingDaminionItems[key] = new DedupItemViewModel(key, item.FileName ?? $"Item {id}")
                {
                    SizeBytes = info.Length,
                    // The temp file's own timestamps are download-time; the EXIF
                    // date inside the original is the only meaningful one.
                    DateTaken = DedupService.ReadImageDateUtc(temp) ?? info.CreationTimeUtc,
                    DaminionId = id,
                };
            }
            finally
            {
                try { System.IO.File.Delete(temp); }
                catch { /* temp cleanup is best effort */ }
            }
        }

        return _dedup.GroupFromHashes(hashes, opts);
    }

    /// <summary>Daminion item metadata keyed by "daminion:{id}" for the current scan.</summary>
    private Dictionary<string, DedupItemViewModel>? _pendingDaminionItems;

    private void AttachGroups(DedupResult result)
    {
        foreach (var g in result.Groups)
        {
            var items = g.Items.Select(key =>
            {
                if (_pendingDaminionItems is not null && _pendingDaminionItems.TryGetValue(key, out var vm))
                    return vm;

                var info = new System.IO.FileInfo(key);
                return new DedupItemViewModel(key, System.IO.Path.GetFileName(key))
                {
                    SizeBytes = info.Exists ? info.Length : null,
                    DateTaken = info.Exists ? DedupService.ReadImageDateUtc(key) ?? info.CreationTimeUtc : null,
                };
            });

            var group = new DuplicateGroupViewModel(items, g.HashType, result.Threshold);
            SetDefaultSelection(group); // keep-first baseline
            group.SelectionChanged += OnGroupSelectionChanged;
            Groups.Add(group);
        }
        _pendingDaminionItems = null;
    }

    private void OnGroupSelectionChanged() => UpdateSummary();

    private void UpdateSummary()
    {
        OnPropertyChanged(nameof(SelectedForActionCount));
        OnPropertyChanged(nameof(SelectionSummary));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    // ── Apply ───────────────────────────────────────────────────────────────

    private bool CanApply() =>
        !IsScanning &&
        Groups.Count > 0 &&
        SelectedForActionCount > 0 &&
        (IsLocal || DaminionReady);

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync(CancellationToken ct)
    {
        var targets = Groups
            .SelectMany(g => g.Items.Where(i => !i.IsChecked))
            .ToList();
        if (targets.Count == 0) return;

        bool ok;
        string summary;

        if (IsDaminion)
        {
            var client = _step1?.ConnectedClient;
            if (client is null) return;

            // Fail closed: no confirmation hook wired = no catalog delete.
            if (ConfirmAction is null ||
                !await ConfirmAction($"Delete {targets.Count} item(s) from the Daminion catalog? This cannot be undone."))
            {
                ScanSummary = "Catalog delete cancelled";
                SynapicLog.Info(nameof(StepDedupViewModel), "Daminion catalog delete cancelled");
                return;
            }

            var ids = targets.Where(t => t.DaminionId is not null).Select(t => t.DaminionId!.Value).ToList();
            ok = await client.DeleteItemsAsync(ids, ct);
            summary = ok
                ? $"Deleted {ids.Count} item(s) from the Daminion catalog"
                : "Catalog delete failed — see log";
        }
        else
        {
            var action = (DedupAction)Math.Clamp(SelectedAction, 0, LocalActions.Length - 1);

            // Local delete is as permanent as the catalog one (no Recycle Bin),
            // so it gets the same fail-closed gate: no prompt wired = no delete.
            // Tag and Move are reversible and never prompt.
            if (action == DedupAction.Delete &&
                (ConfirmAction is null ||
                 !await ConfirmAction($"Permanently delete {targets.Count} duplicate file(s)? This cannot be undone.")))
            {
                ScanSummary = "Delete cancelled";
                SynapicLog.Info(nameof(StepDedupViewModel), "Local duplicate delete cancelled");
                return;
            }

            ok = await _dedup.ApplyToPathsAsync(targets.Select(t => t.Key), action, ct);
            summary = ok
                ? $"{action} applied to {targets.Count} duplicate(s)"
                : $"{action} completed with errors — see log";
        }

        ScanSummary = summary;
        SynapicLog.Info(nameof(StepDedupViewModel), summary);

        if (ok)
        {
            // Groups that had something acted on leave the list; groups where the
            // user kept everything remain (nothing happened there).
            foreach (var group in Groups.Where(g => g.Items.Any(i => !i.IsChecked)).ToList())
            {
                group.SelectionChanged -= OnGroupSelectionChanged;
                Groups.Remove(group);
            }
        }
        UpdateSummary();
    }

    partial void OnFolderPathChanged(string value) => ScanCommand.NotifyCanExecuteChanged();

    partial void OnIsScanningChanged(bool value)
    {
        ScanCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
    }
}

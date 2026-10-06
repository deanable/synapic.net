using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.Services.Daminion;
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

    private Bitmap? _thumb;

    /// <summary>In-memory preview of the image, decoded once when the scan
    /// finishes (local files are scaled with libvips, catalog items come from
    /// the thumbnail API) — never written to a cache. Null when no preview
    /// could be produced; the row simply hides its image column.</summary>
    public Bitmap? Thumb
    {
        get => _thumb;
        set
        {
            if (!SetProperty(ref _thumb, value)) return;
            OnPropertyChanged(nameof(HasThumb));
        }
    }

    /// <summary>Drives the image column's visibility so rows without a
    /// preview collapse to text instead of showing an empty box.</summary>
    public bool HasThumb => _thumb is not null;

    /// <summary>Detach and dispose the preview (a fresh scan or an applied
    /// action drops the rows that own it).</summary>
    internal void DisposeThumb()
    {
        var old = _thumb;
        if (old is null) return;
        _thumb = null;
        OnPropertyChanged(nameof(Thumb));
        old.Dispose();
    }

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

    /// <summary>Detach and dispose every row preview — the group is leaving
    /// the list (fresh scan or applied action).</summary>
    internal void DisposeThumbs()
    {
        foreach (var item in Items) item.DisposeThumb();
    }

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

    /// <param name="dedup">Injectable dedup engine (tests).</param>
    /// <param name="step1">Step 1 provides the Daminion source (connection +
    /// scope collection), mirroring how tagging gets its datasource.</param>
    /// <param name="confirm">Modal confirmation for the catalog delete; the dedup
    /// view wires the real dialog, tests inject answers. Null = cancel (fail closed).</param>
    public StepDedupViewModel(
        IDedupService? dedup = null,
        Step1DatasourceViewModel? step1 = null,
        Func<string, Task<bool>>? confirm = null)
    {
        _dedup = dedup ?? new DedupService();
        _step1 = step1;
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
        : $"Source: {_step1.ScopeDescription} — configured in Step 1";

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
        // Old previews are native bitmaps: detach them from their rows (so no
        // control keeps a disposed source) and free them before the rescan.
        foreach (var group in Groups) group.DisposeThumbs();
        Groups.Clear();
        ScanSummary = IsDaminion ? "Fetching items from Daminion…" : "Scanning…";
        try
        {
            var opts = new DedupOptions(
                (HashAlgorithm)SelectedAlgorithm,
                Threshold,
                MaxDimension: 512);

            // Everything a failed scan needs to be diagnosed after the fact:
            // which source, which algorithm/threshold, and which scope id.
            SynapicLog.Info(nameof(StepDedupViewModel),
                $"Scan starting — source={(IsDaminion ? $"Daminion catalog, {ScopeIdForLog()}" : $"local folder '{FolderPath}'")}, " +
                $"algorithm={opts.Algorithm}, threshold={opts.Threshold:0.###}, maxDimension={opts.MaxDimension}");

            // Before pulling whole originals, drop anything a previous crash
            // left in the download folder — a scan is the only flow that can
            // put megabytes per item there.
            var (staleFiles, staleBytes) = DaminionApiClient.CleanupStaleDownloads();
            if (staleFiles > 0)
                SynapicLog.Info(nameof(StepDedupViewModel),
                    $"Cleared {staleFiles} leftover download(s) ({staleBytes:N0} bytes) from {DaminionApiClient.DefaultTempDirectory}");

            var result = IsDaminion
                ? await ScanDaminionAsync(opts, ct)
                : await ScanLocalAsync(opts, ct);

            SynapicLog.Info(nameof(StepDedupViewModel),
                $"Hashing finished — {result.TotalFiles} file(s) hashed, {result.Groups.Count} duplicate group(s) at threshold {result.Threshold:0.###}");

            // Nothing is cached, so this should read 0 files / 0 bytes after
            // every scan; anything else is a leak worth seeing in the log.
            var (leftFiles, leftBytes) = DaminionApiClient.TempDirectoryUsage();
            SynapicLog.Info(nameof(StepDedupViewModel),
                $"Download folder after the scan: {DaminionApiClient.DefaultTempDirectory} holds {leftFiles} file(s), {leftBytes:N0} bytes");

            // Preview thumbnails for the rows about to be shown — in-memory
            // only, and best effort: a missing preview hides the image column,
            // it never fails the scan.
            var thumbs = await LoadThumbsAsync(result, ct);
            AttachGroups(result, thumbs);
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

        // An empty folder looks exactly like "no duplicates found" in the UI,
        // so say which one it was before the hashing starts.
        SynapicLog.Info(nameof(StepDedupViewModel),
            files.Length == 0
                ? $"No .jpg/.jpeg/.png/.tif/.tiff files found under '{FolderPath}' (recursive) — nothing to compare"
                : $"Enumerated {files.Length} image file(s) under '{FolderPath}' (recursive)");

        var progress = new Progress<DedupProgress>(p =>
            ScanSummary = $"Hashed {p.FilesHashed}/{p.TotalFiles}…");

        return await _dedup.FindDuplicatesAsync(files, opts, progress, ct);
    }

    /// <summary>Scope id (saved search / collection / search term) for the scan's log line.</summary>
    private string ScopeIdForLog() => _step1?.Scope switch
    {
        "saved_search" => $"saved search #{_step1.SavedSearchId}",
        "collection" => $"collection #{_step1.CollectionId}",
        "search" => $"term '{_step1.SearchTerm}'",
        _ => "entire catalog",
    };

    /// <summary>
    /// Daminion scan: fetch the Step 1 scope's items through the shared
    /// workflow kernel (same fetch, progress and per-item isolation as tagging
    /// and upscaling), with this scan's per-item operation being
    /// <see cref="DedupScanHandler"/>. Items that carry a server-computed
    /// hashCode are grouped directly from that hash — no download, no
    /// algorithmic hash. Items without a server hash (older server builds, or
    /// scoped queries that omit it) fall back to downloading the original and
    /// hashing it the old way, one at a time so a large scope never lands on
    /// disk whole.
    /// </summary>
    private async Task<DedupResult> ScanDaminionAsync(DedupOptions opts, CancellationToken ct)
    {
        var step1 = _step1 ?? throw new InvalidOperationException("No datasource step available");
        var client = step1.ConnectedClient ?? throw new InvalidOperationException("Not connected to Daminion");

        var ds = step1.ToSelectionForProcessing(client);
        var items = await WorkflowRunner.FetchItemsAsync(ds, ct);
        var serverHashCount = items.Count(i => i.ServerHashCode != 0);
        SynapicLog.Info(nameof(StepDedupViewModel),
            $"Dedup fetch: {items.Count} item(s) in scope ({ScopeIdForLog()}) — " +
            (serverHashCount > 0
                ? $"{serverHashCount} carry a server hash (grouped without download), " +
                  $"{items.Count - serverHashCount} need a download to hash"
                : "no server hashes available — downloading each original to hash it"));

        var handler = new DedupScanHandler(client, opts, _dedup);
        var workflow = new WorkflowDefinition(
            Key: "dedup",
            Title: "Duplicate scan",
            MaxDegreeOfParallelism: 1, // one item at a time: a large scope never lands on disk whole
            Description: serverHashCount > 0
                ? $"grouping {serverHashCount} item(s) from server hash, {opts.Algorithm} at threshold {opts.Threshold:0.###} for the rest"
                : $"hashing {opts.Algorithm} at threshold {opts.Threshold:0.###}");

        // Live scan text. When the server already computed a content hash for an
        // item it is grouped without a download, so the progress line says
        // "grouping" rather than "downloading" for those.
        var progress = new Progress<ProcessProgress>(p =>
            ScanSummary = serverHashCount > 0
                ? $"Grouping {Math.Min(p.Processed + 1, Math.Max(p.Total, 1))}/{p.Total}: {p.CurrentFile}"
                : $"Downloading {Math.Min(p.Processed + 1, Math.Max(p.Total, 1))}/{p.Total}: {p.CurrentFile}");

        await new WorkflowRunner().RunItemsAsync(workflow, items, handler, progress, log: null, pause: null, ct);

        SynapicLog.Info(nameof(StepDedupViewModel),
            $"Dedup scan done: {handler.Hashes.Count} of {items.Count} item(s) hashed " +
            $"({handler.ServerHashGrouped} from server hash, " +
            $"{handler.DownloadFailures} download failure(s), {handler.HashFailures} unhashable file(s))");

        // Diagnostic summary: log detailed information about what happened under the hood.
        // This is essential for the live-server verification of whether the server
        // hashCode is perceptual or exact. Look for the "=== Dedup Diagnostic Summary ==="
        // section in the log after the scan completes.
        handler.LogDiagnosticSummary($"Daminion catalog, {ScopeIdForLog()}");

        // Row data for the review list, built from what the scan recorded.
        _pendingDaminionItems = handler.Records.Values.ToDictionary(
            r => r.Key,
            r => new DedupItemViewModel(r.Key, r.FileName)
            {
                // 0 means "unknown" (no download and no server fileSize) — map it
                // to null so the row shows "—" instead of a bogus "0 B".
                SizeBytes = r.SizeBytes > 0 ? r.SizeBytes : null,
                DateTaken = r.DateTakenUtc, // may be null when the server hash path was used without a download
                DaminionId = r.DaminionId,
            });

        return _dedup.GroupFromHashes(handler.Hashes, opts);
    }

    /// <summary>Daminion item metadata keyed by "daminion:{id}" for the current scan.</summary>
    private Dictionary<string, DedupItemViewModel>? _pendingDaminionItems;

    /// <summary>Preview bitmaps for the keys about to be attached, decoded
    /// once here and handed to the rows. Local files are scaled in memory with
    /// libvips; catalog items pull the server's own thumbnail over the API and
    /// the temp file is deleted as soon as its bytes are read — nothing is
    /// written to a thumbnail cache. Failures are logged per item and leave a
    /// row without its image; only cancellation propagates.</summary>
    private async Task<Dictionary<string, Bitmap>> LoadThumbsAsync(DedupResult result, CancellationToken ct)
    {
        var keys = result.Groups.SelectMany(g => g.Items).Distinct().ToArray();
        var thumbs = new Dictionary<string, Bitmap>(keys.Length);
        if (keys.Length == 0) return thumbs;

        try
        {
            if (IsDaminion)
            {
                var client = _step1?.ConnectedClient;
                if (client is null) return thumbs;

                var index = 0;
                foreach (var key in keys)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!key.StartsWith("daminion:", StringComparison.Ordinal) ||
                        !int.TryParse(key.AsSpan("daminion:".Length), out var id))
                        continue;

                    index++;
                    var name = _pendingDaminionItems?.GetValueOrDefault(key)?.DisplayName ?? key;
                    ScanSummary = $"Fetching preview {index}/{keys.Length}: {name}";

                    try
                    {
                        var temp = await client.DownloadThumbnailAsync(id, ThumbSize, ThumbSize, ct);
                        if (string.IsNullOrEmpty(temp))
                        {
                            SynapicLog.Warning(nameof(StepDedupViewModel),
                                $"No preview downloaded for {name} — the row shows without an image");
                            continue;
                        }
                        try
                        {
                            var bytes = System.IO.File.ReadAllBytes(temp);
                            if (DecodeThumb(bytes) is { } bitmap) thumbs[key] = bitmap;
                        }
                        finally
                        {
                            try { System.IO.File.Delete(temp); }
                            catch { /* temp cleanup is best effort */ }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e)
                    {
                        SynapicLog.Warning(nameof(StepDedupViewModel), $"Preview unavailable for {name}: {e.Message}");
                    }
                }
            }
            else
            {
                ScanSummary = "Rendering previews…";
                var decoded = await Task.Run(() =>
                {
                    var found = new Dictionary<string, Bitmap>(keys.Length);
                    try
                    {
                        foreach (var key in keys)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (DedupService.CreateThumbnail(key, ThumbSize) is { } bytes &&
                                DecodeThumb(bytes) is { } bitmap)
                                found[key] = bitmap;
                        }
                        return found;
                    }
                    catch
                    {
                        // Cancelled mid-loop: the caller only disposes what it
                        // received, so drop the partial set here.
                        foreach (var bitmap in found.Values) bitmap.Dispose();
                        throw;
                    }
                }, ct);

                foreach (var pair in decoded) thumbs[pair.Key] = pair.Value;
            }

            return thumbs;
        }
        catch
        {
            // Cancelled mid-load: don't strand the bitmaps already decoded.
            foreach (var bitmap in thumbs.Values) bitmap.Dispose();
            throw;
        }
    }

    /// <summary>JPEG bytes → bitmap; null (logged) when the bytes won't decode.</summary>
    private static Bitmap? DecodeThumb(byte[] bytes)
    {
        try
        {
            return new Bitmap(new System.IO.MemoryStream(bytes));
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(StepDedupViewModel), $"Preview decode failed: {e.Message}");
            return null;
        }
    }

    /// <summary>Side length of the fetched/scaled previews — 2–3× the 48 px
    /// row box so they stay sharp on high-DPI displays.</summary>
    private const int ThumbSize = 128;

    private void AttachGroups(DedupResult result, IReadOnlyDictionary<string, Bitmap> thumbs)
    {
        foreach (var g in result.Groups)
        {
            var items = g.Items.Select(key =>
            {
                thumbs.TryGetValue(key, out var thumb);

                if (_pendingDaminionItems is not null && _pendingDaminionItems.TryGetValue(key, out var vm))
                {
                    vm.Thumb = thumb;
                    return vm;
                }

                var info = new System.IO.FileInfo(key);
                return new DedupItemViewModel(key, System.IO.Path.GetFileName(key))
                {
                    SizeBytes = info.Exists ? info.Length : null,
                    DateTaken = info.Exists ? DedupService.ReadImageDateUtc(key) ?? info.CreationTimeUtc : null,
                    Thumb = thumb,
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

        // Say what is about to happen before any destructive call goes out —
        // the summary line after Apply only reports what the call claimed.
        SynapicLog.Info(nameof(StepDedupViewModel),
            IsDaminion
                ? $"Apply requested — remove {targets.Count} item(s) from the catalog: " +
                  $"ids [{string.Join(", ", targets.Where(t => t.DaminionId is not null).Select(t => t.DaminionId))}]"
                : $"Apply requested — {SelectedActionLabel()} on {targets.Count} duplicate file(s)");

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
                group.DisposeThumbs();
            }
        }
        UpdateSummary();
    }

    /// <summary>Human label of the local action for log lines (Tag/Move/Delete).</summary>
    private string SelectedActionLabel() =>
        LocalActions[Math.Clamp(SelectedAction, 0, LocalActions.Length - 1)];

    partial void OnFolderPathChanged(string value) => ScanCommand.NotifyCanExecuteChanged();

    partial void OnIsScanningChanged(bool value)
    {
        ScanCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
    }
}

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Main.Services;
using Synapic.Main.Services.Daminion;
using Synapic.Main.Services.Processing;
using Synapic.Main.ViewModels.Operations;

namespace Synapic.Main.ViewModels.Steps;

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
        _suppressRule = true;        IsChecked = checkedState;
        _suppressRule = false;
        SelectionChanged?.Invoke();
    }

    // ── Quick keep/exclude controls (right-click menu, double-click) ────────

    /// <summary>Keep this image: it drops out of the apply batch (its
    /// checkbox goes on). The right-click menu's top action.
    /// </summary>
    [RelayCommand]
    private void KeepItem() => IsChecked = true;

    /// <summary>Select this image for the action: the checkbox goes off and
    /// Apply includes it again.
    /// </summary>
    [RelayCommand]
    private void SelectForAction() => IsChecked = false;

    /// <summary>The double-click toggle: easy exclusion without opening the menu.</summary>
    public void ToggleKeep() => IsChecked = !IsChecked;

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

    /// <summary>
    /// Drop the acted-on rows (an applied "Reject in catalog") and refresh the
    /// count-derived texts the card shows. Rows the group keeps keep their
    /// previews; the detached ones lose theirs. Internal so the step's apply
    /// flow drives it and tests can too.
    /// </summary>
    internal void RemoveActedItems(HashSet<string> actedKeys)
    {
        foreach (var item in Items.Where(i => actedKeys.Contains(i.Key)).ToList())
        {
            item.SelectionChanged -= OnItemSelectionChanged;
            Items.Remove(item);
            item.DisposeThumb();
        }
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(Summary));
        SelectionChanged?.Invoke();
    }

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
/// Which image-matching search a <see cref="StepDedupViewModel"/> runs. The two
/// are the same mechanism — a 64-bit perceptual fingerprint compared at a
/// hamming threshold, grouped by union-find, reviewed as keep/unchecked rows and
/// acted on in bulk — and differ only in what they mean by "the same":
/// duplicates are near-identical copies, similar images are the visibly alike
/// ones a duplicate scan deliberately keeps apart. Each profile is its own
/// operation with its own route, settings tab, help topic and registry keys, so
/// loosening one search never loosens the other.
/// </summary>
public enum ImageMatchProfile
{
    /// <summary>Near-identical copies — the Deduplication operation.</summary>
    Duplicates,

    /// <summary>Visibly alike images — the Similar images operation.</summary>
    Similar,
}

/// <summary>
/// Dedup wizard step (port of step_dedup.py): pick algorithm + threshold, choose
/// a source — a local folder or the Step 1 Daminion scope (exactly like tagging
/// does) — scan for duplicate groups, review them as a vertical list of group
/// cards with per-item keep checkboxes, drive those checkboxes from the
/// oldest/newest/smallest/largest auto-select set, and apply a bulk action to
/// the unchecked duplicates.
///
/// One implementation serves both <see cref="ImageMatchProfile"/>s: the scan,
/// the review and the apply are identical, and only the threshold the profile
/// starts at, the wording, the persisted keys and the operation identity
/// (key/title/help topic) are chosen per profile. A second copy of the scan
/// would have had to re-derive the catalog server-hash shortcut, the temp-file
/// lifecycle, the preview loading and the fail-closed confirmation gates — the
/// parts worth having exactly one of.
/// </summary>
public partial class StepDedupViewModel : RunStateViewModel
{
    /// <summary>
    /// The threshold a profile starts at for the given fingerprint, before
    /// anything is restored from the registry. pHash — the default algorithm —
    /// ships strict for duplicates (0.90: at most six of the 64 hash bits differ)
    /// and loose enough for the similarity search to catch re-encodes, crops and
    /// light edits (0.85: at most ten bits). The other two fingerprints carry
    /// their own numbers, because the scale is not shared; see
    /// <see cref="AppSettingsRegistryStore.DedupThresholdDefaultFor"/> for the
    /// measurements behind them. An absent algorithm resolves to pHash's.
    /// </summary>
    public static double DefaultThresholdFor(ImageMatchProfile profile, string? algorithm = null) =>
        profile == ImageMatchProfile.Similar
            ? AppSettingsRegistryStore.SimilarThresholdDefaultFor(algorithm)
            : AppSettingsRegistryStore.DedupThresholdDefaultFor(algorithm);

    private readonly IDedupService _dedup;
    private readonly Step1DatasourceViewModel? _step1;
    private readonly AppSettingsRegistryStore? _appSettingsStore;
    private readonly DedupFingerprintCache? _fingerprintCache;
    private readonly Func<string>? _retrievalReportPathProvider;

    /// <param name="dedup">Injectable dedup engine (tests).</param>
    /// <param name="step1">Step 1 provides the Daminion source (connection +
    /// scope collection), mirroring how tagging gets its datasource.</param>
    /// <param name="confirm">Modal confirmation for the catalog delete; the dedup
    /// view wires the real dialog, tests inject answers. Null = cancel (fail closed).</param>
    /// <param name="appSettingsStore">Registry-backed general settings; when
    /// given, the scan rules, keep-set checkboxes, selected action and the move
    /// destination are restored from it and persisted to it on change.</param>
    /// <param name="profile">Which search this instance runs (Deduplication or
    /// Similar images). It selects the registry keys, the starting threshold and
    /// the wording; the scanning itself is the same code either way.</param>
    /// <param name="fingerprintCache">On-disk store for the last scan per source.
    /// When given, a scan saves its fingerprints and row metadata under this
    /// operation and source, and a new instance restores them so the review list
    /// and its threshold slider are live before anything is hashed again. Null =
    /// memory only (the standalone/test host). The shell passes the real store.</param>
    /// <param name="retrievalReportPathProvider">Where "Export report" writes the
    /// last walk's numbers. Null = a timestamped text file in Documents (tests
    /// inject a temp path).</param>
    public StepDedupViewModel(
        IDedupService? dedup = null,
        Step1DatasourceViewModel? step1 = null,
        Func<string, Task<bool>>? confirm = null,
        AppSettingsRegistryStore? appSettingsStore = null,
        ImageMatchProfile profile = ImageMatchProfile.Duplicates,
        DedupFingerprintCache? fingerprintCache = null,
        Func<string>? retrievalReportPathProvider = null)
    {
        _dedup = dedup ?? new DedupService();
        _step1 = step1;
        _appSettingsStore = appSettingsStore;
        _fingerprintCache = fingerprintCache;
        _retrievalReportPathProvider = retrievalReportPathProvider;
        Profile = profile;
        ConfirmAction = confirm;
        // Straight to the backing field: the generated setter persists, and a
        // default must not be written back before the saved one is read.
        _threshold = DefaultThresholdFor(profile);
        LoadPersistedState();

        // Step 1 owns the source while the shell hosts both: anything that moves
        // there (type, folder, connection, scope) moves this step's own gate and
        // read-backs too.
        if (_step1 is not null)
        {
            _step1.PropertyChanged += (_, e) => OnSharedSourceChanged(e.PropertyName);
            _step1.PropertyChanged += (_, e) => OnSharedFilterChanged(e.PropertyName);
        }

        // A restart should land on the last scan's results, not an empty review
        // list: the fingerprints and rows were saved for exactly this source, and
        // re-grouping them arms the threshold slider without hashing anything.
        ScheduleRestore();
    }

    /// <summary>
    /// Mirror a shared-source change into this step's gate and read-backs, so a
    /// source configured anywhere is the source the scan would use (D-02).
    /// </summary>
    private void OnSharedSourceChanged(string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(Step1DatasourceViewModel.DatasourceType):
                OnDatasourceTypeChanged();
                break;
            case nameof(Step1DatasourceViewModel.LocalPath):
                OnPropertyChanged(nameof(FolderPath));
                break;
        }
        ScanCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(DaminionScopeSummary));
        // Retained fingerprints describe the source they came from: a folder or
        // scope change leaves the old groups on screen but takes the slider away.
        OnPropertyChanged(nameof(CanRetune));
        // …and the newly configured source may have a saved scan of its own.
        ScheduleRestore();
    }

    /// <summary>Filter settings also change which catalog items a saved fingerprint set describes.</summary>
    private void OnSharedFilterChanged(string? propertyName)
    {
        if (propertyName is not (nameof(Step1DatasourceViewModel.StatusFilterIndex)
            or nameof(Step1DatasourceViewModel.UntaggedKeywords)
            or nameof(Step1DatasourceViewModel.UntaggedCategories)
            or nameof(Step1DatasourceViewModel.UntaggedDescription)
            or nameof(Step1DatasourceViewModel.SearchTerm)
            or nameof(Step1DatasourceViewModel.SavedSearchId)
            or nameof(Step1DatasourceViewModel.CollectionId)
            or nameof(Step1DatasourceViewModel.ScopeIndex))) return;

        OnPropertyChanged(nameof(CanRetune));
        ScheduleRestore();
    }

    // ── Which search this is (profile identity) ─────────────────────────────

    /// <summary>Which search this instance runs.</summary>
    public ImageMatchProfile Profile { get; }

    /// <summary>True for the Similar images operation, false for Deduplication.</summary>
    public bool IsSimilar => Profile == ImageMatchProfile.Similar;

    /// <summary>The shell's operation key this instance is opened and persisted under.</summary>
    public string OperationKey =>
        IsSimilar ? OperationShellViewModel.SimilarKey : OperationShellViewModel.DedupKey;

    /// <summary>The operation's display name, as its route title and nav row read it.</summary>
    public string OperationTitle => IsSimilar ? "Similar images" : "Deduplication";

    /// <summary>What a group holds, in this profile's words ("3 duplicate groups").</summary>
    public string MatchNoun => IsSimilar ? "similar" : "duplicate";

    /// <summary>The plural of <see cref="MatchNoun"/> for the summary lines.</summary>
    public string MatchNounPlural => IsSimilar ? "similar images" : "duplicates";

    /// <summary>
    /// The heading of the scan card on this mode's run page, so the Similar
    /// images view never reads as the deduplication view with the same form.
    /// </summary>
    public string ScanCardTitle => IsSimilar ? "Search for similar images" : "Scan for duplicates";

    /// <summary>The run page's rule-line hint for this profile.</summary>
    public string RunPageHint => IsSimilar
        ? "The similarity settings and the keep-set rule live in the Settings view's Similar tab; the search, the review and the apply happen here."
        : "The scan rules and the keep-set rule live in the Settings view's Dedup section; the scan, the review and the apply happen here.";

    /// <summary>The apply card's heading for this profile.</summary>
    public string ApplyCardTitle => IsSimilar
        ? "Apply to the unchecked near-matches"
        : "Apply to the unchecked duplicates";

    /// <summary>The caveat under the apply controls, in this profile's words.</summary>
    public string ApplyHint => IsSimilar
        ? "Checked items are kept; the action runs on every unchecked near-match across all groups — each destructive action asks first."
        : "Checked items are kept; the action runs on every unchecked duplicate across all groups — each destructive action asks first.";

    /// <summary>The progress line shown while a destructive call is in flight.</summary>
    public string ApplyingHint => IsSimilar
        ? "Applying to the unchecked near-matches — Stop cancels the rest of the batch."
        : "Applying to the unchecked duplicates — Stop cancels the rest of the batch.";

    /// <summary>What this profile's threshold means, shown beside the picker.</summary>
    public string ThresholdHint => IsSimilar
        ? "How close two images have to be to count as similar (0.85 = clearly alike; lower groups more loosely)."
        : "How close two images have to be to count as duplicates (0.90 = near-identical).";

    /// <summary>The keep-set explanation under the rule checkboxes, in this profile's words.</summary>
    public string KeepSetHint => IsSimilar
        ? "Ticking a rule drives every group's keep checkboxes: checked items are kept, the unchecked near-matches get the action."
        : "Ticking a rule drives every group's keep checkboxes: checked items are kept, the unchecked duplicates get the action.";

    /// <summary>Modal confirmation hook (message → confirmed?). Set by the view;
    /// null cancels every destructive action (Daminion catalog delete and local
    /// file delete alike) — fail closed.</summary>
    public Func<string, Task<bool>>? ConfirmAction { get; set; }

    // ── Persisted dedup settings (registry) ─────────────────────────────────

    /// <summary>
    /// Restore what this form remembers: algorithm, threshold, server-hash
    /// match, keep-set rules, selected action and the move destination. Best
    /// effort — a bad registry read falls back to the shipped defaults. The
    /// profile decides which half of the store is read, so the two searches
    /// never inherit each other's rules.
    /// </summary>
    private void LoadPersistedState()
    {
        if (_appSettingsStore?.Load() is not { } s) return;
        _hydratingDedup = true;
        try
        {
            var algorithm = IsSimilar ? s.SimilarAlgorithm : s.DedupAlgorithm;
            var threshold = IsSimilar ? s.SimilarThreshold : s.DedupThreshold;
            var serverHash = IsSimilar ? s.SimilarServerHashMatch : s.DedupServerHashMatch;

            var algoIndex = Array.IndexOf(Algorithms, algorithm);
            SelectedAlgorithm = algoIndex >= 0 ? algoIndex : 0;
            // The restored algorithm decides the fallback: a store that has no
            // threshold for this profile still knows which fingerprint is
            // selected, and the default belongs to that algorithm, not to pHash.
            Threshold = threshold is > 0.5 and <= 1
                ? threshold
                : DefaultThresholdFor(Profile, algorithm);
            var matchIndex = Array.IndexOf(ServerHashMatches, serverHash);
            SelectedServerHashMatch = matchIndex >= 0 ? matchIndex : 0;
            SelectOldest = IsSimilar ? s.SimilarSelectOldest : s.DedupSelectOldest;
            SelectNewest = IsSimilar ? s.SimilarSelectNewest : s.DedupSelectNewest;
            SelectSmallest = IsSimilar ? s.SimilarSelectSmallest : s.DedupSelectSmallest;
            SelectLargest = IsSimilar ? s.SimilarSelectLargest : s.DedupSelectLargest;
            MoveDestinationFolder = IsSimilar ? s.SimilarMoveDestination : s.DedupMoveDestination;
        }
        finally
        {
            _hydratingDedup = false;
        }
    }

    private bool _hydratingDedup;

    /// <summary>Write the form's current rules back to the registry store.
    /// Called after anything the store records changes (not during hydration).</summary>
    private void PersistState()
    {
        if (_appSettingsStore is null || _hydratingDedup) return;
        try
        {
            var existing = _appSettingsStore.Load() ?? _appSettingsStore.Defaults;
            var algorithm = Algorithms[Math.Clamp(SelectedAlgorithm, 0, Algorithms.Length - 1)];
            var serverHash = ServerHashMatches[Math.Clamp(SelectedServerHashMatch, 0, ServerHashMatches.Length - 1)];
            var action = Math.Clamp(SelectedAction, 0, Math.Max(Actions.Length - 1, 0));

            // The profile picks the half of the state this form owns: writing the
            // other half too would let the similar search overwrite the duplicate
            // rules (and the reverse) every time either form was touched.
            _appSettingsStore.Save(IsSimilar
                ? existing with
                {
                    SimilarAlgorithm = algorithm,
                    SimilarThreshold = Threshold,
                    SimilarServerHashMatch = serverHash,
                    SimilarSelectOldest = SelectOldest,
                    SimilarSelectNewest = SelectNewest,
                    SimilarSelectSmallest = SelectSmallest,
                    SimilarSelectLargest = SelectLargest,
                    SimilarSelectedAction = action,
                    SimilarMoveDestination = MoveDestinationFolder,
                }
                : existing with
                {
                    DedupAlgorithm = algorithm,
                    DedupThreshold = Threshold,
                    DedupServerHashMatch = serverHash,
                    DedupSelectOldest = SelectOldest,
                    DedupSelectNewest = SelectNewest,
                    DedupSelectSmallest = SelectSmallest,
                    DedupSelectLargest = SelectLargest,
                    DedupSelectedAction = action,
                    DedupMoveDestination = MoveDestinationFolder,
                });
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(StepDedupViewModel), $"Failed to persist {OperationKey} settings: {e.Message}");
        }
    }

    // ── Source (local vs Daminion) ──────────────────────────────────────────
    // The source is shared, not dedup's own: with a Step 1 in the shell every
    // read and write below goes through that one instance (D-02 — one source of
    // truth, the same one the Region A strip shows). The private fields here are
    // only the standalone fallback for hosts that have no Step 1 (unit tests).

    private string _datasourceType = "local";

    /// <summary>The shared source's type; writes go through to Step 1.</summary>
    public string DatasourceType
    {
        get => _step1?.DatasourceType ?? _datasourceType;
        set
        {
            if (_step1 is not null)
            {
                // Step 1's own change notification runs OnSharedSourceChanged.
                _step1.DatasourceType = value;
                return;
            }
            if (_datasourceType == value) return;
            _datasourceType = value;
            OnDatasourceTypeChanged();
        }
    }

    public bool IsLocal => DatasourceType == "local";

    public bool IsDaminion => DatasourceType == "daminion";

    /// <summary>
    /// Whether the form offers the catalog's server-hash rule. Only the duplicate
    /// scan can use a content hash: the similarity profile hashes pixels, so
    /// showing the setting there would offer a control that changes nothing.
    /// </summary>
    public bool ShowsServerHashRule => IsDaminion && !IsSimilar;

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

    /// <summary>Everything that depends on which source the scan would read.</summary>
    private void OnDatasourceTypeChanged()
    {
        OnPropertyChanged(nameof(DatasourceType));
        OnPropertyChanged(nameof(IsLocal));
        OnPropertyChanged(nameof(IsDaminion));
        OnPropertyChanged(nameof(ShowsServerHashRule));
        OnPropertyChanged(nameof(IsLocalSelected));
        OnPropertyChanged(nameof(IsDaminionSelected));
        SelectedAction = 0;
        OnPropertyChanged(nameof(Actions));
        OnPropertyChanged(nameof(DaminionScopeSummary));
        OnPropertyChanged(nameof(ScanSettingsSummary));
        // The retrieval numbers are catalog-only: switching to a local source
        // hides the block without discarding the last catalog walk's record.
        OnPropertyChanged(nameof(HasRetrievalDiagnostics));
        OnPropertyChanged(nameof(HasRetrievalMismatch));
        ReWalkUsingPageTotalsCommand.NotifyCanExecuteChanged();
        ExportRetrievalReportCommand.NotifyCanExecuteChanged();
        ScanCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        // The other source may have a saved scan of its own.
        ScheduleRestore();
    }

    /// <summary>Which Step 1 scope collection feeds the Daminion scan (read-only mirror).</summary>
    public string DaminionScopeSummary => _step1 is null
        ? "No datasource step available"
        : $"Source: {_step1.ScopeDescription} — configured in Step 1";

    private bool DaminionReady => IsDaminion && _step1?.ConnectedClient is not null;

    // ── Scan settings ───────────────────────────────────────────────────────

    private string _folderPath = "";

    /// <summary>The shared folder (Step 1's local path); writes go through to it.</summary>
    public string FolderPath
    {
        get => _step1?.LocalPath ?? _folderPath;
        set
        {
            if (_step1 is not null)
            {
                // Step 1's own change notification runs OnSharedSourceChanged.
                _step1.LocalPath = value;
                return;
            }
            if (_folderPath == value) return;
            _folderPath = value;
            OnPropertyChanged(nameof(FolderPath));
            OnPropertyChanged(nameof(CanRetune));
            ScanCommand.NotifyCanExecuteChanged();
            ScheduleRestore();
        }
    }

    [ObservableProperty]
    private int _selectedAlgorithm; // index into Algorithms

    /// <summary>
    /// How close two fingerprints must be to count as the same image. The
    /// starting value comes from the profile (see
    /// <see cref="DefaultThresholdFor"/>); the saved value, when there is one,
    /// replaces it in <see cref="LoadPersistedState"/>.
    /// </summary>
    [ObservableProperty]
    private double _threshold;

    /// <summary>
    /// Scan-in-progress flag the dedup view binds. The state itself lives on the
    /// shared base (<see cref="RunStateViewModel.IsRunning"/>) so all three modes
    /// expose one run shape — the base's change hook raises this property too.
    /// </summary>
    public bool IsScanning
    {
        get => IsRunning;
        set => IsRunning = value;
    }

    [ObservableProperty]
    private string _scanSummary = "";

    // ── Catalog retrieval diagnostics ───────────────────────────────────────

    private CatalogRetrievalDiagnostics? _lastRetrieval;

    /// <summary>One-shot mode for the re-walk action: ignore the scope count as
    /// the walk's denominator and follow the pages' own total.</summary>
    private bool _preferPageTotalForScan;

    /// <summary>
    /// The last whole-scope catalog walk's retrieval numbers, or null before one
    /// has completed. Read-only: it reports what the walk did, it never steers
    /// it. The view renders the numbers side by side so a count that disagrees
    /// with the pages (or pages that disagree with what was kept) is visible
    /// without opening the log file.
    /// </summary>
    public CatalogRetrievalDiagnostics? LastRetrieval => _lastRetrieval;

    /// <summary>The diagnostics block is catalog-only and appears after a walk.</summary>
    public bool HasRetrievalDiagnostics => IsDaminion && _lastRetrieval is not null;

    /// <summary>Which scope the numbers describe, and when the walk finished.</summary>
    public string RetrievalScopeText => _lastRetrieval is { } r
        ? $"{DescribeRetrievalScope(r.Scope)} · {r.ReportedUtc.ToLocalTime():HH:mm:ss}"
        : "";

    public string RetrievalCountText => FormatRetrievalNumber(_lastRetrieval?.ScopeCount);

    public string RetrievalPageTotalText => FormatRetrievalNumber(_lastRetrieval?.PageTotal);

    public string RetrievalPagesText => FormatRetrievalNumber(_lastRetrieval?.Pages);

    public string RetrievalScannedText => FormatRetrievalNumber(_lastRetrieval?.Scanned);

    public string RetrievalKeptText => FormatRetrievalNumber(_lastRetrieval?.Kept);

    /// <summary>
    /// One sentence comparing the three totals. A count that differs from what
    /// was kept is the retrieval problem the view exists to expose; for
    /// collection/saved-search scopes a difference is expected only when this
    /// side dropped records (scanned &gt; kept). A whole-page walk that still ends
    /// short is called what it is: a short read.
    /// </summary>
    public string RetrievalComparisonText
    {
        get
        {
            if (_lastRetrieval is not { } r) return "";
            var parts = new List<string>();

            if (r.ScopeCount is { } counted)
            {
                var difference = counted - r.Kept;
                parts.Add(difference == 0
                    ? $"Counted and kept agree at {counted:N0}."
                    : $"Counted {counted:N0} vs {r.Kept:N0} kept " +
                      $"({Math.Abs(difference):N0} {(difference > 0 ? "missing" : "extra")}).");
            }

            if (r.PageTotal is { } pageTotal && pageTotal != r.ScopeCount)
                parts.Add($"The pages reported {pageTotal:N0}.");

            // A collection/saved-search difference is only "expected" when this
            // side actually dropped records. When every page was returned whole
            // and the walk still ends short, the difference is a retrieval
            // failure — excusing it here is what made "500 of 6,704" look normal.
            if (r.Scope is ("collection" or "saved_search") && r.Scanned > r.Kept)
                parts.Add($"{r.Scanned - r.Kept:N0} record(s) were filtered locally, so a difference is expected.");
            else if (r.ScopeCount is { } inScope && r.Kept < inScope)
                parts.Add($"The walk read {r.Kept:N0} of {inScope:N0} — an empty page ended it early, " +
                          "so this is a short read, not a local filter.");

            // A re-walk on an all/keyword scope deliberately ignores the count, so
            // the two totals disagree by design; the sentence names the total that
            // steered it. (A collection or saved search never uses the count as its
            // denominator, so saying this there would explain nothing.)
            if (!r.CountDrivesTotal && r.Scope is ("all" or "search"))
                parts.Add("This walk followed the pages' total instead of the count.");

            return parts.Count > 0
                ? string.Join(" ", parts)
                : $"{r.Kept:N0} item(s) kept; the server reported no total.";
        }
    }

    /// <summary>
    /// Record the walk's one-shot diagnostics report. Internal so the view's
    /// formatting and visibility can be tested without a server; the scan passes
    /// a <see cref="Progress{T}"/> that lands here on the UI thread.
    /// </summary>
    internal void ApplyRetrievalDiagnostics(CatalogRetrievalDiagnostics diagnostics)
    {
        _lastRetrieval = diagnostics;
        RetrievalActionMessage = "";
        OnPropertyChanged(nameof(LastRetrieval));
        OnPropertyChanged(nameof(HasRetrievalDiagnostics));
        OnPropertyChanged(nameof(HasRetrievalMismatch));
        OnPropertyChanged(nameof(RetrievalScopeText));
        OnPropertyChanged(nameof(RetrievalCountText));
        OnPropertyChanged(nameof(RetrievalPageTotalText));
        OnPropertyChanged(nameof(RetrievalPagesText));
        OnPropertyChanged(nameof(RetrievalScannedText));
        OnPropertyChanged(nameof(RetrievalKeptText));
        OnPropertyChanged(nameof(RetrievalComparisonText));
        ReWalkUsingPageTotalsCommand.NotifyCanExecuteChanged();
        ExportRetrievalReportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// True when the count and the kept items disagree on an all/keyword scope —
    /// the case the re-walk action exists for. Collection and saved-search scopes
    /// filter locally, so their difference is not a retrieval failure.
    /// </summary>
    public bool HasRetrievalMismatch =>
        HasRetrievalDiagnostics &&
        _lastRetrieval is { } r &&
        r.ScopeCount is { } counted &&
        counted != r.Kept &&
        r.Scope is ("all" or "search");

    /// <summary>A one-shot message after an action (re-walking, report written).</summary>
    public string RetrievalActionMessage
    {
        get => _retrievalActionMessage;
        private set
        {
            if (!SetProperty(ref _retrievalActionMessage, value)) return;
            OnPropertyChanged(nameof(HasRetrievalActionMessage));
        }
    }

    private string _retrievalActionMessage = "";

    public bool HasRetrievalActionMessage => !string.IsNullOrWhiteSpace(RetrievalActionMessage);

    /// <summary>The re-walk is a scan, so it needs a runnable source and no run in flight.</summary>
    private bool CanReWalkUsingPageTotals() => HasRetrievalMismatch && CanScan();

    /// <summary>
    /// Re-run the catalog walk with the scope count ignored as the denominator:
    /// the pages' own total steers the bar and the report. It is the diagnostic
    /// response to "the count says more than the walk kept" — a transient page or
    /// filter problem can disappear on a second pass, and the new numbers say so.
    /// It always re-walks (never the retained-fingerprint fast path, whose whole
    /// point is to avoid walking), and the shared Stop still cancels it because
    /// the body runs on its own cancellation source.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReWalkUsingPageTotals))]
    private async Task ReWalkUsingPageTotalsAsync()
    {
        if (IsScanning) return;
        const string message = "Re-walking with page totals…";
        _preferPageTotalForScan = true;
        RetrievalActionMessage = message;
        try
        {
            await RunFullScanAsync(CancellationToken.None);
        }
        finally
        {
            _preferPageTotalForScan = false;
            // A completed walk replaced the message through its report; a
            // cancelled/failed one must not leave "Re-walking…" on screen.
            if (RetrievalActionMessage == message) RetrievalActionMessage = "";
        }
    }

    /// <summary>Export is available whenever the block shows a completed walk.</summary>
    private bool CanExportRetrievalReport() => HasRetrievalDiagnostics;

    /// <summary>
    /// Write the last walk's report beside the app's other exports: all the
    /// numbers, which total the walk followed, the comparison sentence and where
    /// the page-level detail lives. It never modifies the scan or the catalog.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportRetrievalReport))]
    private async Task ExportRetrievalReportAsync()
    {
        if (_lastRetrieval is not { } report) return;
        var path = (_retrievalReportPathProvider ?? DefaultRetrievalReportPath)();
        try
        {
            var text = BuildRetrievalReport(
                report, OperationTitle, RetrievalComparisonText, SynapicLog.LogFilePath);
            await File.WriteAllTextAsync(path, text, Encoding.UTF8);
            RetrievalActionMessage = $"Report written to {path}";
            SynapicLog.Info(nameof(StepDedupViewModel), $"Retrieval report written to {path}");
        }
        catch (Exception e)
        {
            RetrievalActionMessage = $"Could not write the report: {e.Message}";
            SynapicLog.Warning(nameof(StepDedupViewModel), RetrievalActionMessage);
        }
    }

    private static string DefaultRetrievalReportPath()
    {
        var name = $"synapic_retrieval_{DateTime.Now:yyyyMMdd_HHmmss_fff}.txt";
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), name);
    }

    /// <summary>
    /// The exported report. Internal so its fields can be asserted without
    /// touching the user's Documents folder.
    /// </summary>
    internal static string BuildRetrievalReport(
        CatalogRetrievalDiagnostics report,
        string operationTitle,
        string comparison,
        string? logPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Synapic retrieval report");
        sb.AppendLine($"Operation: {operationTitle}");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Scope: {DescribeRetrievalScope(report.Scope)} ({report.Scope})");
        sb.AppendLine($"Walk finished (UTC): {report.ReportedUtc:yyyy-MM-dd'T'HH:mm:ss'Z'}");
        sb.AppendLine($"Counted: {FormatReportNumber(report.ScopeCount)}");
        sb.AppendLine($"Page total: {FormatReportNumber(report.PageTotal)}");
        sb.AppendLine($"Pages: {report.Pages}");
        sb.AppendLine($"Raw scanned: {report.Scanned:N0}");
        sb.AppendLine($"Kept: {report.Kept:N0}");
        sb.AppendLine($"Total followed: " +
                      (report.CountDrivesTotal
                          ? "scope count"
                          : report.PageTotal is not null
                              ? "page total"
                              : "none reported"));
        sb.AppendLine();
        sb.AppendLine(comparison);
        sb.AppendLine();
        sb.AppendLine($"Full page-level detail: " +
                      (string.IsNullOrWhiteSpace(logPath) ? "logs/synapic.log" : logPath));
        return sb.ToString();
    }

    /// <summary>A report-friendly number: "unknown" reads better than the UI's dash.</summary>
    private static string FormatReportNumber(int? value) => value is { } v ? v.ToString("N0") : "unknown";

    private static string DescribeRetrievalScope(string scope) => scope switch
    {
        "all" => "entire catalog",
        "search" => "keyword search",
        "collection" => "collection",
        "saved_search" => "saved search",
        _ => scope,
    };

    private static string FormatRetrievalNumber(int? value) => value is { } v ? v.ToString("N0") : "—";

    /// <summary>
    /// The selectable fingerprints. Each maps to a real engine algorithm; a
    /// removed "ColorMoment" entry used to sit here while dispatching to AHash.
    /// </summary>
    public string[] Algorithms { get; } = { "PHash", "DHash", "AHash" };

    /// <summary>
    /// How the Daminion server's own content hashes are compared, in dropdown
    /// order — index = the enum value. Exact is the default because the API does
    /// not say whether <c>hashCode</c> is byte-exact; switch to Hamming once a
    /// live scan shows it behaves perceptually. Either way server hashes are
    /// only compared with each other, never with algorithmic hashes.
    /// </summary>
    public string[] ServerHashMatches { get; } = { "Exact", "Hamming" };

    [ObservableProperty]
    private int _selectedServerHashMatch; // index into ServerHashMatches

    private ServerHashMatchMode ServerHashMatch =>
        (ServerHashMatchMode)Math.Clamp(SelectedServerHashMatch, 0, ServerHashMatches.Length - 1);

    partial void OnSelectedAlgorithmChanged(int value)
    {
        // A threshold that is still sitting on a shipped default follows the
        // algorithm. It has to: the fingerprints are not on a common scale, so
        // leaving dHash at pHash's 0.90 would let different pictures merge into
        // duplicate groups — and duplicates are moved or deleted. A threshold
        // the user tuned away from every default is left exactly as it is.
        var algorithm = Algorithms[Math.Clamp(value, 0, Algorithms.Length - 1)];
        var algorithmDefault = DefaultThresholdFor(Profile, algorithm);
        if (!_hydratingDedup && IsADefaultThreshold(Threshold) && Math.Abs(Threshold - algorithmDefault) > 1e-9)
            Threshold = algorithmDefault; // its own hook notifies the summary and persists

        OnPropertyChanged(nameof(ScanSettingsSummary));
        // The retained fingerprints belong to the algorithm that produced them:
        // switching algorithms makes them unretunable until the next scan.
        OnPropertyChanged(nameof(CanRetune));
        PersistState();
    }

    /// <summary>
    /// True while <paramref name="threshold"/> is one of this profile's shipped
    /// defaults for some algorithm. The registry cannot say whether a stored
    /// number was chosen or inherited (its loader always supplies a value), so
    /// "still on a default" is the only honest signal that a threshold was
    /// never tuned — and it is what lets the default follow the algorithm.
    /// </summary>
    private bool IsADefaultThreshold(double threshold) =>
        Algorithms.Any(a => Math.Abs(threshold - DefaultThresholdFor(Profile, a)) < 1e-9);

    /// <summary>One-line explanation of the selected rule, shown beside the picker.</summary>
    public string ServerHashMatchHint => ServerHashMatch == ServerHashMatchMode.Exact
        ? "Same hashCode value only — groups byte-identical catalog items."
        : "Hamming distance at the threshold, the way algorithmic hashes compare.";

    /// <summary>
    /// What the similarity search does with a catalog item instead of using the
    /// server's content hash: it hashes pixels. A content hash only answers "is
    /// this the same file", which is the duplicate scan's question.
    /// </summary>
    public string SimilarHashingHint =>
        "Catalog items are downloaded and fingerprinted with the algorithm above; the catalog's own hash is a content hash, so it cannot say two images look alike.";

    partial void OnSelectedServerHashMatchChanged(int value)
    {
        OnPropertyChanged(nameof(ServerHashMatchHint));
        OnPropertyChanged(nameof(ScanSettingsSummary));
        PersistState();
    }

    partial void OnThresholdChanged(double value)
    {
        OnPropertyChanged(nameof(ScanSettingsSummary));
        // Live sensitivity: once a scan has fingerprints, moving the threshold
        // re-groups them instead of asking for a rescan. Debounced in
        // ScheduleRetune so dragging the slider does not start a regroup per pixel.
        if (CanRetune)
        {
            // The settle point persists instead of every pixel: PersistState
            // rewrites nine registry values, which is not something to do 60
            // times a second during a drag. A scan start also persists, so the
            // value cannot be lost by moving the slider and starting a scan.
            ScheduleRetune();
        }
        else
        {
            PersistState();
        }
    }

    /// <summary>
    /// Read-back of the scan rules for the review page: those rules live in the
    /// Parameters region now, and a scan started from a rule line the user cannot
    /// see is a scan nobody can explain afterwards.
    /// </summary>
    public string ScanSettingsSummary
    {
        get
        {
            var algorithm = Algorithms[Math.Clamp(SelectedAlgorithm, 0, Algorithms.Length - 1)];

            var keep = new List<string>();
            if (SelectOldest) keep.Add("oldest");
            if (SelectNewest) keep.Add("newest");
            if (SelectSmallest) keep.Add("smallest");
            if (SelectLargest) keep.Add("largest");
            var keepText = keep.Count == 0 ? "keep-set by hand" : $"keep {string.Join(" + ", keep)}";

            var server = ShowsServerHashRule
                ? $" · server hash: {ServerHashMatches[Math.Clamp(SelectedServerHashMatch, 0, ServerHashMatches.Length - 1)]}"
                : "";

            return $"{algorithm} · threshold {Threshold:0.00} · {keepText}{server}";
        }
    }

    public string[] LocalActions { get; } = { "Tag", "Move to folder…", "Delete from disk" };

    /// <summary>Actions offered per source. Catalog items can go two ways:
    /// deleted from the catalog (destructive, confirmed), or marked rejected so
    /// the identified files can be gone through by hand in Daminion first —
    /// a reversible flag write, no confirmation. Local files get tag/move/delete.
    /// </summary>
    public string[] Actions => IsDaminion
        ? ["Delete from catalog", "Reject in catalog"]
        : LocalActions;

    [ObservableProperty]
    private int _selectedAction; // index into Actions

    /// <summary>Destination folder for the local "Move to folder…" action.
    /// Persisted through the app config so a run on Monday reuses the folder
    /// chosen on Friday. Empty = the action is not usable yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMoveActionAvailable))]
    private string _moveDestinationFolder = "";

    public bool IsMoveActionAvailable => IsLocal && SelectedAction == 1 && !string.IsNullOrWhiteSpace(MoveDestinationFolder);

    /// <summary>Whether the action combo currently has "Move to folder…" selected
    /// (local source only) — drives the destination picker's visibility.</summary>
    public bool IsMoveActionSelected => IsLocal && SelectedAction == 1;

    partial void OnSelectedActionChanged(int value)
    {
        OnPropertyChanged(nameof(IsMoveActionAvailable));
        UpdateSummary();
        PersistState();
    }

    partial void OnMoveDestinationFolderChanged(string value)
    {
        OnPropertyChanged(nameof(IsMoveActionAvailable));
        PersistState();
    }

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = new();

    /// <summary>Total items flagged as duplicates (unchecked) across all groups.</summary>
    public int SelectedForActionCount => Groups.Sum(g => g.Items.Count(i => !i.IsChecked));

    /// <summary>Header count text above the group list.</summary>
    public string SelectionSummary => Groups.Count == 0
        ? ""
        : $"{Groups.Count} {MatchNoun} group(s) — {SelectedForActionCount} selected for the action";

    // ── Auto-select rule checkboxes (oldest / newest / smallest / largest) ──

    [ObservableProperty]
    private bool _selectOldest;

    [ObservableProperty]
    private bool _selectNewest;

    [ObservableProperty]
    private bool _selectSmallest;

    [ObservableProperty]
    private bool _selectLargest;

    partial void OnSelectOldestChanged(bool value)
    {
        RuleChanged();
        PersistState();
    }

    partial void OnSelectNewestChanged(bool value)
    {
        RuleChanged();
        PersistState();
    }

    partial void OnSelectSmallestChanged(bool value)
    {
        RuleChanged();
        PersistState();
    }

    partial void OnSelectLargestChanged(bool value)
    {
        RuleChanged();
        PersistState();
    }

    /// <summary>A keep-set rule moved: recompute the checkboxes and the read-back.</summary>
    private void RuleChanged()
    {
        ApplyAutoSelection();
        OnPropertyChanged(nameof(ScanSettingsSummary));
    }

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

    /// <summary>
    /// The scan's parameters as the dedup engine wants them (clamps included),
    /// mirroring StepUpscaleViewModel.BuildOptions. Public for tests.
    /// </summary>
    /// <remarks>
    /// The similarity profile does not use the catalog's own content hash. That
    /// hash answers "is this the same file", so grouping it (exactly, or even by
    /// hamming distance at a threshold meant for a 64-bit perceptual
    /// fingerprint) is not a similarity search — and on Daminion 11.x every item
    /// carries one, so the profile's algorithm and threshold never applied to a
    /// single image. It hashes pixels instead.
    /// </remarks>
    public DedupOptions BuildOptions() => new(
        (HashAlgorithm)Math.Clamp(SelectedAlgorithm, 0, Algorithms.Length - 1),
        Threshold,
        MaxDimension: 512,
        ServerHashMatch: ServerHashMatch,
        UseServerHashes: !IsSimilar);

    private bool CanScan() => !IsScanning && (IsLocal ? !string.IsNullOrWhiteSpace(FolderPath) : DaminionReady);

    // ── Persisted scan (fingerprint cache) ──────────────────────────────────

    /// <summary>The source the displayed fingerprints came from; null when no
    /// source is configured. Both halves are part of it, because the same folder
    /// and the same catalog scope produce different hashes.</summary>
    private string? CurrentSourceKey()
    {
        if (IsDaminion)
        {
            if (_step1 is null) return null;
            // A cache includes only the retained source's data, not today's
            // filters or threshold options. Every value that changes the
            // returned item set belongs in this identity, or a fast restore
            // would silently show stale/incorrect groups.
            return $"daminion|{(_step1.DaminionUrl ?? "").Trim()}|{_step1.ActiveCatalog ?? ""}|{_step1.Scope}|{_step1.SavedSearchId}|"
                   + $"{_step1.CollectionId}|{(_step1.SearchTerm ?? "").Trim()}|{_step1.StatusFilter}|"
                   + $"{_step1.UntaggedKeywords},{_step1.UntaggedCategories},{_step1.UntaggedDescription}";
        }

        var folder = FolderPath?.Trim();
        if (string.IsNullOrEmpty(folder)) return null;
        try { folder = System.IO.Path.GetFullPath(folder); }
        catch { /* keep the text as typed — it still names one source */ }
        return "local|" + folder;
    }

    /// <summary>The selected fingerprint, lowercase — the same spelling
    /// <see cref="DedupResult.Algorithm"/> and the cache file use.</summary>
    private string SelectedAlgorithmName() =>
        Algorithms[Math.Clamp(SelectedAlgorithm, 0, Algorithms.Length - 1)].ToLowerInvariant();

    /// <summary>Completes when the startup restore has finished (or decided there
    /// was nothing to restore). Tests await it instead of sleeping.</summary>
    internal Task RestoreCompletion => _restoreCompletion;

    private Task _restoreCompletion = Task.CompletedTask;
    private bool _restoreInFlight;

    /// <summary>
    /// Offer the saved scan for the source now configured, unless this instance
    /// already has results (a scan won, or a restore already ran). Called at
    /// construction and whenever the source moves, so a folder or scope that is
    /// picked after construction — the tests' object initializer, or a source
    /// switch in the form — still gets its saved results.
    /// </summary>
    private void ScheduleRestore()
    {
        if (_fingerprintCache is null || _restoreInFlight) return;
        if (IsScanning || IsApplying || _lastScan is not null || Groups.Count > 0) return;
        if (CurrentSourceKey() is null) return;
        // A folder typed one character at a time is not a source yet; only a real
        // directory is worth a cache read.
        if (IsLocal && !System.IO.Directory.Exists(FolderPath)) return;
        _restoreCompletion = RestoreCachedScanAsync();
    }

    /// <summary>The source key the current results (or restored ones) belong to.
    /// Null while nothing has been scanned here.</summary>
    private string? _scanSourceKey;

    /// <summary>
    /// Load this operation's saved scan for the current source and render it, so
    /// a restart opens on the same review list and a live slider. Everything is
    /// checked before use: a cache for another source, another fingerprint, a
    /// newer format or a scan that has since happened is ignored rather than
    /// merged. Restored rows are the ones the groups need; local metadata is read
    /// from the files then, and catalog rows carry their ids so Apply still works
    /// after reconnecting.
    /// </summary>
    private async Task RestoreCachedScanAsync()
    {
        if (_fingerprintCache is null) return;
        var sourceKey = CurrentSourceKey();
        if (sourceKey is null) return;
        _restoreInFlight = true;
        try
        {
            await RestoreCoreAsync(sourceKey);
        }
        finally
        {
            _restoreInFlight = false;
            // The source moved while this read was in flight and the new one was
            // turned away by the in-flight guard: give it its own read now.
            if (_lastScan is null && Groups.Count == 0 && !IsScanning && !IsApplying &&
                !string.Equals(CurrentSourceKey(), sourceKey, StringComparison.Ordinal))
                ScheduleRestore();
        }
    }

    /// <summary>
    /// True when a saved scan cannot describe this operation: a Similar result
    /// built entirely from the catalog's content hashes. Those fingerprints
    /// answer "is this the same file", which is the duplicate scan's question;
    /// the similarity search hashes pixels. Internal so the rule is testable
    /// without a catalog.
    /// </summary>
    internal static bool IsStaleSimilarCache(CachedDedupScan cached, bool isSimilar) =>
        isSimilar && cached.PerceptualHashes.Count == 0 && cached.ExactHashes.Count > 0;

    private async Task RestoreCoreAsync(string sourceKey)
    {
        if (_fingerprintCache is null) return;
        CachedDedupScan? cached;
        try
        {
            cached = await _fingerprintCache.LoadAsync(OperationKey, sourceKey);
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(StepDedupViewModel),
                $"Could not restore the last {OperationTitle} scan: {e.Message}");
            return;
        }

        if (cached is null || cached.Items.Length == 0) return;

        // Hashes of one fingerprint are meaningless to another. The cache is per
        // source, not per algorithm, so an algorithm changed between runs drops
        // the restore and the next scan replaces the file.
        var algorithm = SelectedAlgorithmName();
        if (!string.Equals(cached.Algorithm, algorithm, StringComparison.OrdinalIgnoreCase))
        {
            SynapicLog.Info(nameof(StepDedupViewModel),
                $"Saved {OperationTitle} scan skipped: it used {cached.Algorithm}, the current fingerprint is {algorithm}");
            return;
        }

        // A similarity result with no perceptual fingerprints came from the old
        // path that grouped the catalog's content hash. Restoring it would show
        // the exact-match groups this operation no longer produces — and make a
        // fixed build look unchanged on the next launch — so it is dropped and
        // the source rescanned instead.
        if (IsStaleSimilarCache(cached, IsSimilar))
        {
            SynapicLog.Info(nameof(StepDedupViewModel),
                $"Saved {OperationTitle} scan skipped: it holds only catalog content hashes, not fingerprints");
            return;
        }

        // The load is asynchronous: by now the user may have scanned, applied, or
        // pointed the source somewhere else. A restore must never overwrite work
        // that happened while it was reading.
        if (IsScanning || IsApplying || _lastScan is not null || Groups.Count > 0) return;
        if (!string.Equals(CurrentSourceKey(), sourceKey, StringComparison.Ordinal)) return;

        if (IsDaminion)
        {
            // Every saved catalog row, id or not: a key without a row would fall
            // back to a FileInfo read on "daminion:{id}" if a retune showed it.
            _pendingDaminionItems = cached.Items
                .ToDictionary(i => i.Key, i => new DedupItemViewModel(i.Key, i.FileName)
                {
                    SizeBytes = i.SizeBytes > 0 ? i.SizeBytes : null,
                    DateTaken = i.DateTakenUtc,
                    DaminionId = i.DaminionId,
                });
        }

        var opts = BuildOptions();
        var result = await Task.Run(
            () => _dedup.GroupFromHashes(cached.PerceptualHashes, cached.ExactHashes, opts));

        // Previews are best effort and in-memory, exactly as after a scan: a
        // catalog that is not reconnected yet simply restores rows without images.
        var thumbs = await LoadThumbsAsync(result, CancellationToken.None);

        // A scan can start while the previews load; its rows own the list from
        // then on, and the bitmaps built here are dropped.
        if (IsScanning || IsApplying || _lastScan is not null || Groups.Count > 0)
        {
            foreach (var bitmap in thumbs.Values) bitmap.Dispose();
            return;
        }
        // The source may have moved during the read as well.
        if (!string.Equals(CurrentSourceKey(), sourceKey, StringComparison.Ordinal))
        {
            foreach (var bitmap in thumbs.Values) bitmap.Dispose();
            return;
        }
        _scanSourceKey = sourceKey;
        AttachGroups(result, thumbs);
        ApplyAutoSelection();

        var dupCount = Groups.Sum(g => g.Items.Count - 1);
        ScanSummary = Groups.Count == 0
            ? $"{result.TotalFiles} files from the last scan — no {MatchNounPlural} at {opts.Threshold:0.###}. Rescan to refresh."
            : $"{result.TotalFiles} files from the last scan — {Groups.Count} groups, {dupCount} {MatchNounPlural} "
              + $"at {opts.Threshold:0.###}. Rescan to refresh.";
        SynapicLog.Info(nameof(StepDedupViewModel),
            $"Restored the last {OperationTitle} scan for this source: {result.TotalFiles} item(s), "
            + $"{Groups.Count} group(s) at {opts.Threshold:0.###} (no hashing)");
    }

    /// <summary>Save the completed scan's fingerprints and row metadata for this
    /// source. Best effort: a failure only costs the next launch a rescan.</summary>
    private async Task SaveCachedScanAsync(DedupResult result, CancellationToken ct)
    {
        if (_fingerprintCache is null || _scanSourceKey is null) return;
        var keys = result.PerceptualHashes.Keys.Concat(result.ExactHashes.Keys).Distinct().ToArray();
        if (keys.Length == 0) return;
        if (keys.Length > DedupFingerprintCache.MaxItems)
        {
            SynapicLog.Info(nameof(StepDedupViewModel),
                $"Not caching this scan: {keys.Length:N0} items exceeds the {DedupFingerprintCache.MaxItems:N0}-item limit");
            return;
        }

        var items = new CachedDedupItem[keys.Length];
        for (var index = 0; index < keys.Length; index++)
        {
            var key = keys[index];
            var row = CachedItem(key);
            items[index] = new CachedDedupItem(
                key,
                row?.DisplayName ?? System.IO.Path.GetFileName(key),
                row?.SizeBytes ?? 0,
                row?.DateTaken,
                row?.DaminionId);
        }

        try
        {
            await _fingerprintCache.SaveAsync(OperationKey, new CachedDedupScan(
                DedupFingerprintCache.CurrentVersion,
                _scanSourceKey,
                result.Algorithm,
                result.Threshold,
                result.TotalFiles,
                items,
                new Dictionary<string, ulong>(result.PerceptualHashes),
                new Dictionary<string, ulong>(result.ExactHashes)), ct).ConfigureAwait(false);
            SynapicLog.Info(nameof(StepDedupViewModel),
                $"Cached {items.Length:N0} fingerprint(s) for the next launch ({_fingerprintCache.DirectoryPath})");
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(StepDedupViewModel),
                $"Could not cache this scan ({e.Message}); the next launch will rescan");
        }
    }

    /// <summary>
    /// The run bar's primary action. When this source already has retained
    /// fingerprints (a finished scan, or the saved scan a restart restored),
    /// pressing it re-groups instantly instead of hashing everything again —
    /// the same fast method the automatic restore uses — because re-grouping
    /// is free now, and the user paid for the hashing once. Walking and
    /// hashing every file again is the explicit "Re-hash from disk" action
    /// (and the natural next step when files changed).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync(CancellationToken ct)
    {
        if (CanRetune)
        {
            var version = Interlocked.Increment(ref _retuneVersion);
            await RetuneAsync(CancellationToken.None, version);
            if (!string.IsNullOrEmpty(ScanSummary))
                ScanSummary +=
                    " — fingerprints reused, nothing re-hashed. Use Re-hash from disk to pick up new or changed files.";
            return;
        }

        await RunFullScanAsync(ct);
    }

    /// <summary>The explicit full scan: walk the source and hash every file
    /// again, no matter what fingerprints are on hand.</summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task HashAgainAsync(CancellationToken ct)
        => await RunFullScanAsync(ct);

    /// <summary>The scan body in full: fetch, hash, group, render and cache.
    /// Runs on its own cancellation source so the shared bar's Stop cancels it
    /// whichever entry point started it (the page Scan command used to own the
    /// token, which left a bar-started scan unstoppable).
    /// </summary>
    private async Task RunFullScanAsync(CancellationToken commandToken)
    {
        BeginRun();
        var ct = Cts!.Token;
        _ = commandToken; // the command token is superseded by the run's own source
        // A threshold moved while results were on screen is persisted by the
        // recalculation it scheduled; if a scan supersedes that, save it here.
        PersistState();
        // Remember which source these fingerprints will describe, before the
        // rows are dropped — the cache is keyed by it and Apply invalidates it.
        _scanSourceKey = CurrentSourceKey();
        ClearScanResults();
        // A rescan starts from zero on the shared run bar: the previous scan's
        // percentage, file name and ETA would otherwise describe work already done.
        ProgressPercent = 0;
        ProgressText = IsDaminion ? "Fetching items from Daminion…" : "Scanning…";
        CurrentFile = "";
        EtaText = "";
        ScanSummary = IsDaminion ? "Fetching items from Daminion…" : "Scanning…";
        try
        {
            var opts = BuildOptions();

            // Everything a failed scan needs to be diagnosed after the fact:
            // which source, which algorithm/threshold, and which scope id.
            SynapicLog.Info(nameof(StepDedupViewModel),
                $"Scan starting — source={(IsDaminion ? $"Daminion catalog, {ScopeIdForLog()}" : $"local folder '{FolderPath}'")}, " +
                $"algorithm={opts.Algorithm}, threshold={opts.Threshold:0.###}, maxDimension={opts.MaxDimension}" +
                (IsDaminion
                    ? opts.UseServerHashes
                        ? $", serverHashMatch={opts.ServerHashMatch}"
                        : ", hashing pixels (server hashes not used for similarity)"
                    : ""));

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
                $"Hashing finished — {result.TotalFiles} file(s) hashed, {result.Groups.Count} {MatchNoun} group(s) at threshold {result.Threshold:0.###}");

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
            ScanSummary = Groups.Count == 0
                ? result.TotalFiles == 0
                    ? "No images found in this source — choose a folder or scope with images, then scan again."
                    : $"No {MatchNounPlural} found among {result.TotalFiles} images — try a lower threshold or another algorithm."
                : $"{result.TotalFiles} files scanned — {Groups.Count} groups, {dupCount} {MatchNounPlural}";
            // "500 files scanned" reads as the whole scope. When the walk ended
            // before the scope's own count, say so next to the result instead of
            // leaving it to the diagnostics block below.
            if (IsDaminion && _lastRetrieval is { } walk && walk.ScopeCount is { } inScope && walk.Kept < inScope)
                ScanSummary += $" The scope holds {inScope:N0}; the walk read {walk.Kept:N0}.";
            SynapicLog.Info(nameof(StepDedupViewModel), ScanSummary);
            TelemetryService.Shared.RecordDedup(dupCount);

            // Re-apply any active auto-select rules to the fresh groups.
            ApplyAutoSelection();

            // Persist for the next launch. CancellationToken.None: the hashing is
            // done, and a Stop pressed during the save must not turn a finished
            // scan into "Scan cancelled" (the write itself is atomic).
            await SaveCachedScanAsync(result, CancellationToken.None);
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
            // A cancelled or failed scan must not leave an estimate running on
            // the bar — it would promise work that is no longer happening.
            EndRun();
            EtaText = "";
            // The threshold may have moved while the scan ran (the settings tab
            // is reachable during a long fetch). Those results were grouped at
            // the value captured when the scan started, so bring them in line
            // with the slider now that a retune is allowed again.
            if (_lastScan is { } scanned && HasRetunableScan &&
                Math.Abs(scanned.Threshold - Threshold) > 1e-9)
                ScheduleRetune();
            ScanCommand.NotifyCanExecuteChanged();
            ApplyCommand.NotifyCanExecuteChanged();
            UpdateSummary();
        }
    }

    /// <summary>
    /// Drop the previous scan's rows, previews and retained fingerprints. Item
    /// view models outlive a re-group (the retune slider reuses their previews),
    /// so the caches have to be emptied explicitly — disposing only the rows in
    /// <see cref="Groups"/> would leak the ones a strict threshold filtered out.
    /// </summary>
    private void ClearScanResults()
    {
        _retuneCts?.Cancel();
        _retuneVersion++;
        IsRetuning = false;
        foreach (var item in AllScannedItems()) item.DisposeThumb();
        Groups.Clear();
        _localItems.Clear();
        _pendingDaminionItems = null;
        _lastScan = null;
        OnPropertyChanged(nameof(CanRetune));
    }

    private IEnumerable<DedupItemViewModel> AllScannedItems() =>
        Groups.SelectMany(g => g.Items)
            .Concat(_localItems.Values)
            .Concat(_pendingDaminionItems?.Values ?? Enumerable.Empty<DedupItemViewModel>())
            .Distinct();

    /// <summary>
    /// Put a catalog page boundary on the shared run bar: raw records walked of
    /// the scope total, the page just fetched, and an ETA from the average page
    /// cost. Raw records — not the eligible subset — because the server's total
    /// counts records, and a filtered page still advanced the cursor. Internal
    /// so the bar's wording and its estimate can be tested through the view model
    /// the bar binds.
    /// </summary>
    internal void ApplyCatalogFetchProgress(CatalogFetchProgress p, TimeSpan elapsed)
    {
        var (eta, perPage) = WorkflowRunner.EstimateCatalogFetchProgress(elapsed, p.Pages, p.Scanned, p.Total);
        ProgressPercent = p.Total is { } total && total > 0
            ? Math.Clamp(100.0 * p.Scanned / total, 0, 100)
            : 0;
        ProgressText = p.Total is { } scopeTotal
            ? $"Fetching catalog: {p.Scanned:N0}/{scopeTotal:N0} items"
            : $"Fetching catalog: {p.Scanned:N0} items so far";
        CurrentFile = $"catalog page {p.Pages}";
        EtaText = eta is { } remaining && remaining > TimeSpan.Zero
            ? $"ETA {ProcessProgress.FormatDuration(remaining)} remaining — " +
              $"{ProcessProgress.FormatDuration(perPage)} per page"
            : "";
    }

    /// <summary>
    /// Local hashing on the same bar: files hashed of the folder's images, with
    /// an estimate derived from completed hashes (one hash at a time, so the
    /// average is honest).
    /// </summary>
    internal void ApplyDedupProgress(DedupProgress p, TimeSpan elapsed)
    {
        ProgressPercent = p.TotalFiles > 0
            ? Math.Clamp(100.0 * p.FilesHashed / p.TotalFiles, 0, 100)
            : 0;
        ProgressText = p.TotalFiles > 0
            ? $"{p.FilesHashed}/{p.TotalFiles} hashed"
            : "No images to hash";
        CurrentFile = "";
        if (p.FilesHashed > 0 && p.FilesHashed < p.TotalFiles)
        {
            var (eta, perItem) = WorkflowRunner.EstimateProgress(elapsed, p.FilesHashed, p.TotalFiles);
            EtaText = eta is { } remaining && remaining > TimeSpan.Zero
                ? $"ETA {ProcessProgress.FormatDuration(remaining)} remaining — " +
                  $"{ProcessProgress.FormatDuration(perItem)} per image"
                : "";
        }
        else
        {
            EtaText = "";
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
        if (files.Length == 0)
            ScanSummary = "No images found in this folder — choose a folder with supported images, then scan again.";

        SynapicLog.Info(nameof(StepDedupViewModel),
            files.Length == 0
                ? $"No .jpg/.jpeg/.png/.tif/.tiff files found under '{FolderPath}' (recursive) — nothing to compare"
                : $"Enumerated {files.Length} image file(s) under '{FolderPath}' (recursive)");

        // Local hashing drives the same run bar as the catalog walk: a scan of a
        // folder used to leave the bar reading "Idle" for its whole duration.
        var started = Stopwatch.StartNew();
        var progress = new Progress<DedupProgress>(p =>
        {
            ApplyDedupProgress(p, started.Elapsed);
            ScanSummary = $"Hashed {p.FilesHashed}/{p.TotalFiles}…";
        });

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

        // Whole scope, always: a duplicate/similarity group is only correct over
        // the complete result set. Step 1's "max items" ceiling exists for
        // tagging (a partial batch is meaningful there), and the catalog API
        // returns at most 500 records per call — so this walks every page until
        // the server returns an empty one, reporting each boundary so a large
        // catalog visibly makes progress instead of looking hung.
        var fetchStarted = Stopwatch.StartNew();
        var fetchProgress = new Progress<CatalogFetchProgress>(p =>
        {
            ApplyCatalogFetchProgress(p, fetchStarted.Elapsed);
            ScanSummary = p.Total is { } total
                ? $"Fetching items from Daminion: {p.Scanned:N0} of {total:N0}…"
                : $"Fetching items from Daminion: {p.Scanned:N0} so far…";
        });
        // One report at the end of the walk, kept for the read-only diagnostics
        // block: the count, the page total, the pages, the raw cursor and what
        // was actually kept, all together. Progress<T> marshals it to the UI
        // thread, like the per-page progress above.
        var retrieval = new Progress<CatalogRetrievalDiagnostics>(ApplyRetrievalDiagnostics);
        var items = await WorkflowRunner.FetchEntireScopeAsync(
            ds, fetchProgress, ct, retrieval, ignoreScopeCount: _preferPageTotalForScan);

        // The walk is over; hashing owns the bar from here. Drop the fetch ETA —
        // it names the wrong phase, and its last value would otherwise sit there
        // until the first item completed and reported an estimate of its own.
        fetchStarted.Stop();
        ProgressPercent = 0;
        ProgressText = $"Scanning {items.Count:N0} item(s)…";
        CurrentFile = "";
        EtaText = "";
        var serverHashCount = items.Count(i => i.ServerHashCode != 0);            SynapicLog.Info(nameof(StepDedupViewModel),
                $"{OperationTitle} fetch: {items.Count} item(s) in scope ({ScopeIdForLog()})" +
                (_preferPageTotalForScan ? " [re-walk: page totals steer the bar]" : "") + " — " +
            (serverHashCount > 0
                ? $"{serverHashCount} carry a server hash (grouped without download), " +
                  $"{items.Count - serverHashCount} need a download to hash"
                : "no server hashes available — downloading each original to hash it"));

        var handler = new DedupScanHandler(client, opts, _dedup);
        var workflow = new WorkflowDefinition(
            Key: OperationKey,
            Title: IsSimilar ? "Similarity scan" : "Duplicate scan",
            MaxDegreeOfParallelism: 1, // one item at a time: a large scope never lands on disk whole
            Description: serverHashCount > 0
                ? $"grouping {serverHashCount} item(s) from server hash, {opts.Algorithm} at threshold {opts.Threshold:0.###} for the rest"
                : $"hashing {opts.Algorithm} at threshold {opts.Threshold:0.###}");

        // Live scan text. When the server already computed a content hash for an
        // item it is grouped without a download, so the progress line says
        // "grouping" rather than "downloading" for those.
        var progress = new Progress<ProcessProgress>(p =>
        {
            ApplyProgress(p);
            ScanSummary = serverHashCount > 0
                ? $"Grouping {Math.Min(p.Processed + 1, Math.Max(p.Total, 1))}/{p.Total}: {p.CurrentFile}"
                : $"Downloading {Math.Min(p.Processed + 1, Math.Max(p.Total, 1))}/{p.Total}: {p.CurrentFile}";
        });

        await new WorkflowRunner().RunItemsAsync(workflow, items, handler, progress, log: null, pause: null, ct);

        SynapicLog.Info(nameof(StepDedupViewModel),
            $"{OperationTitle} scan done: {handler.ExactHashes.Count + handler.PerceptualHashes.Count} of {items.Count} item(s) hashed " +
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

        return _dedup.GroupFromHashes(handler.PerceptualHashes, handler.ExactHashes, opts);
    }

    // ── Live retune (the results page's threshold slider) ───────────────────

    /// <summary>How long the slider must rest before a recalculation starts.
    /// Dragging fires a value change per pixel; each one would otherwise pay the
    /// O(n²) grouping cost. 300 ms is below the threshold of "feels immediate".</summary>
    internal const int RetuneDebounceMs = 300;

    /// <summary>The debounce + regroup currently scheduled or running — tests
    /// await this instead of sleeping, and a new slider move supersedes it.</summary>
    internal Task RetuneCompletion => _retuneCompletion;

    /// <summary>
    /// True when the current results can be re-filtered in place: a scan has
    /// produced fingerprints, that scan used the algorithm still selected (the
    /// hashes of one algorithm are meaningless to another), and no other run is
    /// touching the source. The results page's slider binds its visibility.
    /// </summary>
    public bool CanRetune => !IsScanning && !IsApplying && HasRetunableScan;

    private bool HasRetunableScan => _lastScan is { } last &&
        string.Equals(last.Algorithm, SelectedAlgorithmName(), StringComparison.Ordinal) &&
        // The fingerprints describe one source; a folder/scope change leaves the
        // old groups on screen but must not re-filter them as if they were new.
        string.Equals(_scanSourceKey, CurrentSourceKey(), StringComparison.Ordinal);

    /// <summary>The line under the slider: what moving it costs, and what it does.</summary>
    public string RetuneHint => IsSimilar
        ? "Sensitivity of this result set: lower catches more edits and more false matches. Groups rebuild from the fingerprints already computed — no image is hashed again; only an item that newly enters a group needs a preview."
        : "Sensitivity of this result set: lower groups more loosely. Groups rebuild from the fingerprints already computed — no image is hashed again; only an item that newly enters a group needs a preview.";

    private DedupResult? _lastScan;
    private readonly Dictionary<string, DedupItemViewModel> _localItems = new();
    private CancellationTokenSource? _retuneCts;
    private Task _retuneCompletion = Task.CompletedTask;
    private int _retuneVersion;

    [ObservableProperty]
    private bool _isRetuning;

    partial void OnIsRetuningChanged(bool value) => ApplyCommand.NotifyCanExecuteChanged();

    private void ScheduleRetune()
    {
        if (_hydratingDedup || IsScanning || IsApplying || !HasRetunableScan) return;

        _retuneCts?.Cancel();
        var cts = new CancellationTokenSource();
        _retuneCts = cts;
        var version = ++_retuneVersion;
        _retuneCompletion = DebounceAndRetuneAsync(cts, version);
    }

    private async Task DebounceAndRetuneAsync(CancellationTokenSource cts, int version)
    {
        try
        {
            await Task.Delay(RetuneDebounceMs, cts.Token);
            if (version != _retuneVersion || IsScanning || IsApplying) return;
            await RetuneAsync(cts.Token, version);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer slider value or by a new scan.
        }
        catch (Exception e)
        {
            ScanSummary = $"Recalculation failed: {e.Message}";
            SynapicLog.Error(nameof(StepDedupViewModel), ScanSummary);
        }
        finally
        {
            if (ReferenceEquals(_retuneCts, cts)) _retuneCts = null;
            cts.Dispose();
        }
    }

    /// <summary>
    /// Re-group the fingerprints retained by the last scan at the current
    /// threshold — no hashing, no downloads, no file reads. Internal so the
    /// recalculation can be driven directly by tests; the view goes through
    /// <see cref="ScheduleRetune"/> and its debounce.
    /// </summary>
    /// <remarks>
    /// Grouping compares every fingerprint with every other, which is why this
    /// runs on the thread pool: measured release-build costs are ~22 ms for
    /// 10,000 items, ~90 ms for 20,000 and ~0.6 s for 50,000. The UI stays live
    /// and shows the busy line; a newer slider value supersedes this run.
    /// </remarks>
    internal async Task RetuneAsync(CancellationToken ct, int version)
    {
        if (_lastScan is not { } last || IsScanning) return;

        var opts = BuildOptions();
        IsRetuning = true;
        ScanSummary = $"Re-grouping {last.TotalFiles:N0} item(s) at {opts.Threshold:0.###}…";
        try
        {
            var result = await Task.Run(
                () => _dedup.GroupFromHashes(last.PerceptualHashes, last.ExactHashes, opts), ct);
            ct.ThrowIfCancellationRequested();
            if (version != _retuneVersion) return;

            // Only items that just entered a group lack a preview; the ones that
            // were already visible keep the bitmap their row cache holds.
            var thumbs = await LoadThumbsAsync(result, ct);
            if (version != _retuneVersion)
            {
                foreach (var bitmap in thumbs.Values) bitmap.Dispose();
                return;
            }

            AttachGroups(result, thumbs, preserveSelection: true);
            PersistState();
            var dupCount = Groups.Sum(g => g.Items.Count - 1);
            ScanSummary = Groups.Count == 0
                ? $"{result.TotalFiles} files at {opts.Threshold:0.###} — no {MatchNounPlural} found"
                : $"{result.TotalFiles} files at {opts.Threshold:0.###} — {Groups.Count} groups, {dupCount} {MatchNounPlural}";
        }
        finally
        {
            if (version == _retuneVersion) IsRetuning = false;
        }
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
        // A retune only renders the items that just entered a group; anything a
        // previous grouping already showed still holds its bitmap in the row cache.
        var keys = result.Groups.SelectMany(g => g.Items).Distinct()
            .Where(key => CachedItem(key)?.Thumb is null)
            .ToArray();
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

    /// <summary>
    /// Build the group cards from a grouping result. On a fresh scan the keep-set
    /// falls back to the rules (keep-first); on a retune the checkboxes the user
    /// already set are carried over by item key, because re-filtering must not
    /// silently re-check the file they chose to act on.
    /// </summary>
    private void AttachGroups(
        DedupResult result,
        IReadOnlyDictionary<string, Bitmap> thumbs,
        bool preserveSelection = false)
    {
        var priorChecked = preserveSelection
            ? Groups.SelectMany(g => g.Items).ToDictionary(i => i.Key, i => i.IsChecked)
            : null;

        Groups.Clear();
        foreach (var g in result.Groups)
        {
            var items = g.Items.Select(key => GetOrCreateItem(key, thumbs)).ToArray();

            var group = new DuplicateGroupViewModel(items, g.HashType, result.Threshold);
            for (var index = 0; index < group.Items.Count; index++)
            {
                var item = group.Items[index];
                var keep = priorChecked is null
                    ? index == 0                                   // keep-first baseline
                    : priorChecked.TryGetValue(item.Key, out var was) ? was : index == 0;
                item.SetCheckedByRule(keep);
            }
            group.SelectionChanged += OnGroupSelectionChanged;
            Groups.Add(group);
        }

        // The slider's offer is only honest while the fingerprints on hand are
        // the ones for the current results.
        _lastScan = result;
        OnPropertyChanged(nameof(CanRetune));
        UpdateSummary();
    }

    /// <summary>One row per scanned item, reused across retunes so a preview is
    /// decoded or downloaded once per scan rather than once per slider move.</summary>
    private DedupItemViewModel GetOrCreateItem(string key, IReadOnlyDictionary<string, Bitmap> thumbs)
    {
        if (_pendingDaminionItems is not null && _pendingDaminionItems.TryGetValue(key, out var catalog))
        {
            if (thumbs.TryGetValue(key, out var catalogThumb)) catalog.Thumb = catalogThumb;
            return catalog;
        }
        if (_localItems.TryGetValue(key, out var cached))
        {
            if (thumbs.TryGetValue(key, out var cachedThumb)) cached.Thumb = cachedThumb;
            return cached;
        }

        var info = new System.IO.FileInfo(key);
        var created = new DedupItemViewModel(key, System.IO.Path.GetFileName(key))
        {
            SizeBytes = info.Exists ? info.Length : null,
            DateTaken = info.Exists ? DedupService.ReadImageDateUtc(key) ?? info.CreationTimeUtc : null,
        };
        if (thumbs.TryGetValue(key, out var thumb)) created.Thumb = thumb;
        _localItems[key] = created;
        return created;
    }

    /// <summary>The row already cached for a key, if any (used to skip previews
    /// that a previous grouping already rendered).</summary>
    private DedupItemViewModel? CachedItem(string key) =>
        _pendingDaminionItems?.GetValueOrDefault(key) ?? _localItems.GetValueOrDefault(key);

    private void OnGroupSelectionChanged() => UpdateSummary();

    private void UpdateSummary()
    {
        OnPropertyChanged(nameof(SelectedForActionCount));
        OnPropertyChanged(nameof(SelectionSummary));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    // ── Apply ───────────────────────────────────────────────────────────────

    /// <summary>
    /// True for exactly as long as the destructive call is in flight (UI-REVIEW
    /// carried-forward finding #1, ui-design §10 criterion 6): the apply view
    /// shows an indeterminate bar off it, disables Apply and offers Stop, so a
    /// multi-minute catalog delete is never a silently greyed button. Set before
    /// the operation's first await and cleared in a <c>finally</c>, so success,
    /// cancellation and a thrown error all settle it.
    /// </summary>
    [ObservableProperty]
    private bool _isApplying;

    partial void OnIsApplyingChanged(bool value)
    {
        ApplyCommand.NotifyCanExecuteChanged();
        StopApplyCommand.NotifyCanExecuteChanged();
        // Retuning while an Apply is deleting/moving files would re-group rows
        // whose files are disappearing under it.
        OnPropertyChanged(nameof(CanRetune));
    }

    private bool CanApply() =>
        !IsApplying &&
        !IsScanning &&
        !IsRetuning &&
        Groups.Count > 0 &&
        SelectedForActionCount > 0 &&
        (IsDaminion ? DaminionReady : IsMoveActionUsable);

    /// <summary>
    /// The local Apply's extra gate: "Move to folder…" needs a destination, and
    /// it must not be inside the scanned source folder (moving a copy of a
    /// duplicate into the very folder the scan reads would grow the scan). Tag
    /// and Delete need nothing more.
    /// </summary>
    private bool IsMoveActionUsable =>
        SelectedAction switch
        {
            1 => !string.IsNullOrWhiteSpace(MoveDestinationFolder) &&
                 Directory.Exists(MoveDestinationFolder) &&
                 !IsSameOrSubPath(MoveDestinationFolder, FolderPath),
            _ => true,
        };

    internal static bool IsSameOrSubPath(string? candidate, string? root)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var fullCandidate = Path.GetFullPath(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var fullRoot = Path.GetFullPath(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return fullCandidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
                   fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }


    /// <summary>Cancels the Apply in flight; enabled exactly while it is running.</summary>
    [RelayCommand(CanExecute = nameof(IsApplying))]
    private void StopApply() => ApplyCommand.Cancel();

    /// <summary>
    /// Cancels the scan in flight, whichever entry point started it: the page's
    /// Scan command or the shared run bar's Start (both run the same scan body on
    /// their own token). UI-REVIEW #1's scan half — a long scan had no Stop.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void StopScan()
    {
        ScanCommand.Cancel();
        if (IsRunning) CancelRun();
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync(CancellationToken ct)
    {
        var targets = Groups
            .SelectMany(g => g.Items.Where(i => !i.IsChecked))
            .ToList();
        if (targets.Count == 0) return;

        var client = IsDaminion ? _step1?.ConnectedClient : null;
        if (IsDaminion && client is null) return;

        // Index into Actions: catalog item index 1 is "Reject in catalog" (and
        // is only offered on a Daminion source), local index 1 is the move-to
        // folder action. Resolved here because both the pre-apply log and the
        // confirmation gate below name the chosen action before anything runs.
        var rejecting = client is not null && SelectedAction == 1;

        // Say what is about to happen before any destructive call goes out —
        // the summary line after Apply only reports what the call claimed.
        SynapicLog.Info(nameof(StepDedupViewModel),
            client is not null
                ? $"Apply requested — {(rejecting ? "mark rejected" : "remove")} {targets.Count} item(s) in the catalog: "
                  + $"ids [{string.Join(", ", targets.Where(t => t.DaminionId is not null).Select(t => t.DaminionId))}]"
                : $"Apply requested — {SelectedActionLabel()} on {targets.Count} {MatchNoun} file(s)");

        var ids = client is not null
            ? targets.Where(t => t.DaminionId is not null).Select(t => t.DaminionId!.Value).ToList()
            : new List<int>();
        var action = (DedupAction)Math.Clamp(SelectedAction, 0, LocalActions.Length - 1);

        // The prompts are the user's decision, not the operation: they settle
        // before the busy state starts, so the bar measures the work itself (and
        // a declined prompt never flashes one).
        if (client is not null && !rejecting)
        {
            // Fail closed: no confirmation hook wired = no catalog delete.
            if (ConfirmAction is null ||
                !await ConfirmAction($"Delete {targets.Count} item(s) from the Daminion catalog? This cannot be undone."))
            {
                ScanSummary = "Catalog delete cancelled";
                SynapicLog.Info(nameof(StepDedupViewModel), "Daminion catalog delete cancelled");
                return;
            }
        }
        // Local delete is as permanent as the catalog one (no Recycle Bin), so it
        // gets the same fail-closed gate: no prompt wired = no delete. Tag and
        // Move are reversible and never prompt.
        else if (action == DedupAction.Delete &&
                 (ConfirmAction is null ||
                  !await ConfirmAction($"Permanently delete {targets.Count} duplicate file(s)? This cannot be undone.")))
        {
            ScanSummary = "Delete cancelled";
            SynapicLog.Info(nameof(StepDedupViewModel), "Local duplicate delete cancelled");
            return;
        }        bool ok;
        string summary;
        // The catalog reject reports which ids the read-back confirmed, so a
        // partially applied batch is visible instead of failing wholesale.
        DaminionFlagWriteResult? flagWrite = null;

        // An Apply and a recalculation must not overlap: one is acting on the
        // rows the other is rebuilding. CanApply already excludes IsRetuning;
        // this cancels a recalculation that slipped in before the click.
        _retuneCts?.Cancel();
        _retuneVersion++;
        IsRetuning = false;

        IsApplying = true;
        try
        {
            if (client is not null)
            {
                if (rejecting)
                {
                    flagWrite = await client.SetItemsFlagDetailedAsync(ids, DaminionFlagValue.Rejected, ct);
                    ok = flagWrite.Success;
                    summary = ok
                        ? $"Rejected {flagWrite.Verified} item(s) in the Daminion catalog"
                        : flagWrite.Verified == 0
                            ? $"Catalog reject did not stick — the server marked none of {flagWrite.Requested} item(s) Rejected (see log)"
                            : $"Only {flagWrite.Verified} of {flagWrite.Requested} item(s) were marked Rejected — the rest left the review list untouched; re-run Apply for them (see log)";
                }
                else
                {
                    ok = await client.DeleteItemsAsync(ids, ct);
                    summary = ok
                        ? $"Deleted {ids.Count} item(s) from the Daminion catalog"
                        : "Catalog delete failed — see log";
                }
            }
            else
            {
                if (action == DedupAction.Move)
                {
                    if (string.IsNullOrWhiteSpace(MoveDestinationFolder))
                    {
                        ScanSummary = "Move cancelled — no destination folder chosen.";
                        SynapicLog.Info(nameof(StepDedupViewModel), "Local move cancelled: no destination folder");
                        return;
                    }
                    Directory.CreateDirectory(MoveDestinationFolder);
                }

                ok = await _dedup.ApplyToPathsAsync(targets.Select(t => t.Key), action, MoveDestinationFolder, ct);
                summary = ok
                    ? action == DedupAction.Move
                        ? $"Moved {targets.Count} duplicate(s) to {MoveDestinationFolder}"
                        : $"{action} applied to {targets.Count} duplicate(s)"
                    : $"{action} completed with errors — see log";
            }
        }
        finally
        {
            IsApplying = false;
        }

        ScanSummary = summary;
        SynapicLog.Info(nameof(StepDedupViewModel), summary);

        // Only rows the write actually confirmed leave the review list: after a
        // partial reject, the unmarked ones stay so re-running Apply retries them.
        var clearTargets = ok && !rejecting || flagWrite is { Verified: > 0 };
        if (clearTargets)
        {
            if (rejecting)
            {
                // Rejection keeps the files, so the fingerprints still describe
                // the source: the confirmed rows leave the review list (find them
                // by key — checkboxes may have changed while the call ran),
                // groups reduced to a single image stop being matches, but the
                // retained results and the saved scan stay armed for the slider
                // and a restart. What was rejected can be re-reviewed (and
                // un-rejected) in Daminion itself.
                var confirmedIds = flagWrite!.VerifiedIds.ToHashSet();
                var acted = new HashSet<string>(
                    targets.Where(t => t.DaminionId is { } id && confirmedIds.Contains(id))
                        .Select(t => t.Key));
                foreach (var group in Groups.ToList())
                {
                    if (!group.Items.Any(i => acted.Contains(i.Key))) continue;
                    group.RemoveActedItems(acted);

                    if (group.Items.Count > 1) continue; // still a match group
                    // A group reduced to a single image has no match left to
                    // review — it goes the way the acted-on rows did.
                    group.SelectionChanged -= OnGroupSelectionChanged;
                    Groups.Remove(group);
                    group.DisposeThumbs();
                }
            }
            else
            {
                // Groups that had something acted on leave the list; groups where the
                // user kept everything remain (nothing happened there).
                foreach (var group in Groups.Where(g => g.Items.Any(i => !i.IsChecked)).ToList())
                {
                    group.SelectionChanged -= OnGroupSelectionChanged;
                    Groups.Remove(group);
                    group.DisposeThumbs();
                }

                // The acted-on files are gone or moved, so the retained fingerprints
                // no longer describe the source: the slider must not re-group what is
                // no longer there. The next scan re-arms it — and the saved copy is
                // dropped too, or a restart would offer to re-group deleted files.
                _lastScan = null;
                if (_scanSourceKey is not null) _fingerprintCache?.Delete(OperationKey, _scanSourceKey);
                OnPropertyChanged(nameof(CanRetune));
            }
        }
        UpdateSummary();
    }

    /// <summary>Human label of the local action for log lines (Tag/Move/Delete).</summary>
    private string SelectedActionLabel() =>
        LocalActions[Math.Clamp(SelectedAction, 0, LocalActions.Length - 1)];

    /// <summary>
    /// The dedup view's run controls: the Scan command (also this mode's primary
    /// run for Phase 2's operation adapter) and Apply, which needs no scan running.
    /// </summary>
    protected override void NotifyRunCommandsCanExecuteChanged()
    {
        OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(CanRetune));
        StartCommand.NotifyCanExecuteChanged();
        ScanCommand.NotifyCanExecuteChanged();
        StopScanCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        StopApplyCommand.NotifyCanExecuteChanged();
        ReWalkUsingPageTotalsCommand.NotifyCanExecuteChanged();
        ExportRetrievalReportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>This mode's primary run is the scan (CONTEXT D-05).</summary>
    protected override bool CanStart() => CanScan();

    /// <summary>The bar's primary action is this mode's scan, under its own name.</summary>
    public override string RunActionLabel => "Scan";

    /// <summary>Exposed for tests: the retained-fingerprint fast rescan used the
    /// automatic restore's method without touching the source.</summary>
    public bool HasRetainedScan => _lastScan is not null && HasRetunableScan;

    /// <summary>
    /// The scan's Stop, on the shared bar. It cancels whichever entry point
    /// started the scan (this one, or the page's Scan when it had one), so the
    /// bar is the one place a long scan is called off.
    /// </summary>
    public override IRelayCommand? StopAction => StopScanCommand;

    protected override Task StartCoreAsync(CancellationToken ct) => ScanAsync(ct);
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.Services.Daminion;
using Synapic.Avalonia.Services.Processing;

namespace Synapic.Avalonia.ViewModels.Steps;

/// <summary>
/// Step 1: Datasource (port of step1_datasource.py) — local folder browser or
/// Daminion connection with scope (all/search/collection/saved search),
/// untagged-field filters, status filter, item count preview, and live
/// saved-search / shared-collection pickers loaded from the server.
/// </summary>
public partial class Step1DatasourceViewModel : ViewModelBase
{
    private readonly Session _session;

    public Step1DatasourceViewModel(Session session)
    {
        _session = session;
        HydrateFromSession();
    }

    private void HydrateFromSession()
    {
        var ds = _session.Datasource;
        LocalPath = ds.LocalPath;
        LocalRecursive = ds.LocalRecursive;
        DaminionUrl = ds.DaminionUrl;
        DaminionUser = ds.DaminionUser;
        DaminionPass = ds.DaminionPass;
        DaminionCatalogId = ds.DaminionCatalogId;
        _hydrating = true;
        ScopeIndex = ScopeStringToIndex(ds.DaminionScope);
        StatusFilterIndex = StatusStringToIndex(ds.StatusFilter);
        ResizeScaleIndex = ds.ResizeScale switch { 75 => 1, 50 => 2, 25 => 3, _ => 0 };
        SavedSearchId = ds.SavedSearchId;
        CollectionId = ds.CollectionId;
        _hydrating = false;
        UntaggedKeywords = ds.UntaggedKeywords;
        UntaggedCategories = ds.UntaggedCategories;
        UntaggedDescription = ds.UntaggedDescription;
        MaxItems = ds.MaxItems;
        UseThumbnailOverride = ds.UseThumbnailOverride;
    }

    private bool _hydrating;

    // ── Local ───────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocal))]
    private string _localPath = "";

    [ObservableProperty]
    private bool _localRecursive;

    partial void OnLocalPathChanged(string value) => _session.Datasource.LocalPath = value;

    partial void OnLocalRecursiveChanged(bool value) => _session.Datasource.LocalRecursive = value;

    // ── Daminion connection ─────────────────────────────────────────────────

    [ObservableProperty]
    private string _daminionUrl = "";

    [ObservableProperty]
    private string _daminionUser = "";

    [ObservableProperty]
    private string _daminionPass = "";

    [ObservableProperty]
    private string _daminionCatalogId = "";

    [ObservableProperty]
    private bool _isDaminionConnected;

    [ObservableProperty]
    private string? _connectionMessage;

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private string _datasourceType = "local";

    public bool IsLocal => DatasourceType == "local";
    public bool IsDaminion => DatasourceType == "daminion";

    partial void OnDatasourceTypeChanged(string value)
    {
        _session.Datasource.Type = value;
        OnPropertyChanged(nameof(IsLocal));
        OnPropertyChanged(nameof(IsDaminion));
    }

    public DaminionApiClient? ConnectedClient { get; private set; }

    partial void OnDaminionUrlChanged(string value) => _session.Datasource.DaminionUrl = value;
    partial void OnDaminionUserChanged(string value) => _session.Datasource.DaminionUser = value;
    partial void OnDaminionPassChanged(string value) => _session.Datasource.DaminionPass = value;
    partial void OnDaminionCatalogIdChanged(string value) => _session.Datasource.DaminionCatalogId = value;

    // ── Scope (index-bound so the ComboBox actually selects) ────────────────

    public string[] ScopeOptions { get; } = { "Entire catalog", "Keyword search", "Shared collection", "Saved search" };

    [ObservableProperty]
    private int _scopeIndex;

    public string Scope => ScopeIndexToString(ScopeIndex);

    partial void OnScopeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Scope));
        _session.Datasource.DaminionScope = Scope;
        OnPropertyChanged(nameof(IsSearchScope));
        OnPropertyChanged(nameof(IsCollectionScope));
        OnPropertyChanged(nameof(IsSavedSearchScope));
    }

    public static string ScopeIndexToString(int i) => i switch
    {
        1 => "search",
        2 => "collection",
        3 => "saved_search",
        _ => "all",
    };

    public static int ScopeStringToIndex(string s) => s switch
    {
        "search" => 1,
        "collection" => 2,
        "saved_search" => 3,
        _ => 0,
    };

    public bool IsSearchScope => ScopeIndex == 1;
    public bool IsCollectionScope => ScopeIndex == 2;
    public bool IsSavedSearchScope => ScopeIndex == 3;

    [ObservableProperty]
    private string _searchTerm = "";

    partial void OnSearchTermChanged(string value) => _session.Datasource.SearchTerm = value;

    // ── Saved searches & collections (loaded from the server on connect) ────

    public ObservableCollection<DaminionSavedSearch> SavedSearches { get; } = new();

    [ObservableProperty]
    private DaminionSavedSearch? _selectedSavedSearch;

    public ObservableCollection<DaminionCollection> Collections { get; } = new();

    [ObservableProperty]
    private DaminionCollection? _selectedCollection;

    [ObservableProperty]
    private string _savedSearchId = "";

    [ObservableProperty]
    private string _collectionId = "";

    [ObservableProperty]
    private bool _isLoadingCatalog;

    partial void OnSelectedSavedSearchChanged(DaminionSavedSearch? value)
    {
        if (value is null || _hydrating) return;
        _hydrating = true;
        SavedSearchId = value.Id.ToString();
        _hydrating = false;
        _session.Datasource.SavedSearchId = SavedSearchId;
    }

    partial void OnSelectedCollectionChanged(DaminionCollection? value)
    {
        if (value is null || _hydrating) return;
        _hydrating = true;
        CollectionId = value.Id.ToString();
        _hydrating = false;
        _session.Datasource.CollectionId = CollectionId;
    }

    partial void OnSavedSearchIdChanged(string value)
    {
        _session.Datasource.SavedSearchId = value;
        if (!_hydrating)
        {
            _hydrating = true;
            SelectedSavedSearch = SavedSearches.FirstOrDefault(s => s.Id.ToString() == value);
            _hydrating = false;
        }
    }

    partial void OnCollectionIdChanged(string value)
    {
        _session.Datasource.CollectionId = value;
        if (!_hydrating)
        {
            _hydrating = true;
            SelectedCollection = Collections.FirstOrDefault(c => c.Id.ToString() == value);
            _hydrating = false;
        }
    }

    // ── Filters ─────────────────────────────────────────────────────────────

    public string[] StatusFilterOptions { get; } = { "All", "Approved", "Rejected", "Unassigned" };

    [ObservableProperty]
    private int _statusFilterIndex;

    public string StatusFilter => StatusIndexToString(StatusFilterIndex);

    partial void OnStatusFilterIndexChanged(int value)
    {
        OnPropertyChanged(nameof(StatusFilter));
        _session.Datasource.StatusFilter = StatusFilter;
    }

    public static string StatusIndexToString(int i) => i switch
    {
        1 => "approved",
        2 => "rejected",
        3 => "unassigned",
        _ => "all",
    };

    public static int StatusStringToIndex(string s) => s switch
    {
        "approved" => 1,
        "rejected" => 2,
        "unassigned" => 3,
        _ => 0,
    };

    [ObservableProperty]
    private bool _untaggedKeywords;

    [ObservableProperty]
    private bool _untaggedCategories;

    [ObservableProperty]
    private bool _untaggedDescription;

    partial void OnUntaggedKeywordsChanged(bool value) => _session.Datasource.UntaggedKeywords = value;
    partial void OnUntaggedCategoriesChanged(bool value) => _session.Datasource.UntaggedCategories = value;
    partial void OnUntaggedDescriptionChanged(bool value) => _session.Datasource.UntaggedDescription = value;

    [RelayCommand]
    private void SelectAllUntagged()
    {
        UntaggedKeywords = true;
        UntaggedCategories = true;
        UntaggedDescription = true;
    }

    // ── Processing limits ───────────────────────────────────────────────────

    public string[] ResizeScaleOptions { get; } = { "100%", "75%", "50%", "25%" };

    [ObservableProperty]
    private int _resizeScaleIndex;

    public int ResizeScale => ResizeScaleIndexToString(ResizeScaleIndex);

    partial void OnResizeScaleIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ResizeScale));
        _session.Datasource.ResizeScale = ResizeScale;
    }

    public static int ResizeScaleIndexToString(int i) => i switch
    {
        1 => 75,
        2 => 50,
        3 => 25,
        _ => 100,
    };

    [ObservableProperty]
    private int _maxItems = 100;

    [ObservableProperty]
    private bool _useThumbnailOverride;

    partial void OnMaxItemsChanged(int value) => _session.Datasource.MaxItems = value;
    partial void OnUseThumbnailOverrideChanged(bool value) => _session.Datasource.UseThumbnailOverride = value;

    // ── Commands ────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync(CancellationToken ct)
    {
        IsConnecting = true;
        ConnectionMessage = null;
        try
        {
            var client = new DaminionApiClient(
                DaminionUrl, DaminionUser, DaminionPass,
                string.IsNullOrWhiteSpace(DaminionCatalogId) ? null : DaminionCatalogId);
            await client.AuthenticateAsync(ct);
            ConnectedClient = client;
            IsDaminionConnected = true;
            ConnectionMessage = $"Connected to {DaminionUrl}";
            SynapicLog.Info(nameof(Step1DatasourceViewModel), $"Daminion connected: {DaminionUrl}");
            await LoadCatalogDataAsync(ct);
        }
        catch (Exception e)
        {
            IsDaminionConnected = false;
            ConnectionMessage = $"Connection failed: {e.Message}";
            SynapicLog.Warning(nameof(Step1DatasourceViewModel), ConnectionMessage);
        }
        finally
        {
            IsConnecting = false;
            ConnectCommand.NotifyCanExecuteChanged();
            CountCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanConnect() =>
        !IsConnecting &&
        !string.IsNullOrWhiteSpace(DaminionUrl) &&
        !string.IsNullOrWhiteSpace(DaminionUser) &&
        !string.IsNullOrWhiteSpace(DaminionPass);

    [RelayCommand]
    private void Disconnect()
    {
        try { ConnectedClient?.LogoutAsync(); } catch { /* best effort */ }
        ConnectedClient = null;
        IsDaminionConnected = false;
        SavedSearches.Clear();
        Collections.Clear();
        ConnectionMessage = "Disconnected";
        CountCommand.NotifyCanExecuteChanged();
        SynapicLog.Info(nameof(Step1DatasourceViewModel), "Daminion disconnected");
    }

    /// <summary>Load the saved-search and shared-collection pickers (server round trips).</summary>
    private async Task LoadCatalogDataAsync(CancellationToken ct)
    {
        if (ConnectedClient is null) return;
        IsLoadingCatalog = true;
        try
        {
            var searches = await ConnectedClient.GetSavedSearchesAsync(ct);
            var collections = await ConnectedClient.GetSharedCollectionsAsync(ct);
            _hydrating = true;
            SavedSearches.Clear();
            foreach (var s in searches) SavedSearches.Add(s);
            Collections.Clear();
            foreach (var c in collections) Collections.Add(c);
            // Restore the previously persisted id if it exists on this server.
            SelectedSavedSearch = SavedSearches.FirstOrDefault(s => s.Id.ToString() == SavedSearchId);
            SelectedCollection = Collections.FirstOrDefault(c => c.Id.ToString() == CollectionId);
            _hydrating = false;
            ConnectionMessage = $"Connected to {DaminionUrl} — {searches.Count} saved searches, {collections.Count} collections";
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(Step1DatasourceViewModel), $"Catalog data load failed (pickers stay manual): {e.Message}");
            ConnectionMessage = $"Connected to {DaminionUrl} (saved searches/collections unavailable: {e.Message})";
        }
        finally
        {
            IsLoadingCatalog = false;
        }
    }

    [ObservableProperty]
    private string? _countText;

    [ObservableProperty]
    private bool _isCounting;

    [RelayCommand(CanExecute = nameof(CanCount))]
    private async Task CountAsync(CancellationToken ct)
    {
        if (ConnectedClient is null) return;
        IsCounting = true;
        try
        {
            var count = await ConnectedClient.GetFilteredItemCountAsync(
                scope: Scope,
                savedSearchId: int.TryParse(SavedSearchId, out var ss) ? ss : null,
                collectionId: int.TryParse(CollectionId, out var col) ? col : null,
                searchTerm: string.IsNullOrWhiteSpace(SearchTerm) ? null : SearchTerm,
                untaggedFields: _session.Datasource.UntaggedFields(),
                statusFilter: StatusFilter);
            CountText = $"{count:N0} items match the current filters";
            SynapicLog.Info(nameof(Step1DatasourceViewModel), $"Item count: {count}");
        }
        catch (Exception e)
        {
            CountText = $"Count failed: {e.Message}";
            SynapicLog.Warning(nameof(Step1DatasourceViewModel), CountText);
        }
        finally
        {
            IsCounting = false;
        }
    }

    private bool CanCount() => IsDaminionConnected && ConnectedClient is not null && !IsCounting;

    /// <summary>Build a DatasourceSelection for the processing orchestrator (shares the connected client).</summary>
    public DatasourceSelection ToSelectionForProcessing(DaminionApiClient? sharedClient) =>
        _session.Datasource.ToSelection(ConnectedClient ?? sharedClient);
}

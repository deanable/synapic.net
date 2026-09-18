using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.Services.Daminion;
using Synapic.Avalonia.Services.Processing;

namespace Synapic.Avalonia.ViewModels.Steps;

/// <summary>
/// Step 1: Datasource (port of step1_datasource.py) — local folder browser or
/// Daminion connection with scope/filters and Test Connection.
/// </summary>
public partial class Step1DatasourceViewModel : ViewModelBase
{
    private readonly Session _session;

    public Step1DatasourceViewModel(Session session)
    {
        _session = session;
    }

    // ── Local ───────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocal))]
    private string _localPath = "";

    [ObservableProperty]
    private bool _localRecursive;

    partial void OnLocalPathChanged(string value)
    {
        _session.Datasource.LocalPath = value;
    }

    partial void OnLocalRecursiveChanged(bool value)
    {
        _session.Datasource.LocalRecursive = value;
    }

    // ── Daminion ────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDaminion))]
    private string _daminionUrl = "";

    [ObservableProperty]
    private string _daminionUser = "";

    [ObservableProperty]
    private string _daminionPass = "";

    [ObservableProperty]
    private string _daminionCatalogId = "";

    [ObservableProperty]
    private string _scope = "all"; // all | search | collection | saved_search

    [ObservableProperty]
    private string _savedSearchId = "";

    [ObservableProperty]
    private string _collectionId = "";

    [ObservableProperty]
    private string _searchTerm = "";

    [ObservableProperty]
    private bool _untaggedKeywords;

    [ObservableProperty]
    private bool _untaggedCategories;

    [ObservableProperty]
    private bool _untaggedDescription;

    [ObservableProperty]
    private string _statusFilter = "all"; // all | approved | rejected | unassigned

    [ObservableProperty]
    private int _maxItems = 100;

    [ObservableProperty]
    private int _resizeScale = 100;

    [ObservableProperty]
    private bool _useThumbnailOverride;

    [ObservableProperty]
    private bool _isDaminionConnected;

    [ObservableProperty]
    private string? _connectionMessage;

    [ObservableProperty]
    private bool _isConnecting;

    public DaminionApiClient? ConnectedClient { get; private set; }

    public bool IsLocal
    {
        get
        {
            // DatasourceType drives which panel is visible; computed from both.
            return DatasourceType == "local";
        }
    }

    public bool IsDaminion => DatasourceType == "daminion";

    [ObservableProperty]
    private string _datasourceType = "local";

    partial void OnDatasourceTypeChanged(string value)
    {
        _session.Datasource.Type = value;
        OnPropertyChanged(nameof(IsLocal));
        OnPropertyChanged(nameof(IsDaminion));
    }

    partial void OnDaminionUrlChanged(string value) => _session.Datasource.DaminionUrl = value;
    partial void OnDaminionUserChanged(string value) => _session.Datasource.DaminionUser = value;
    partial void OnDaminionPassChanged(string value) => _session.Datasource.DaminionPass = value;
    partial void OnScopeChanged(string value) => _session.Datasource.DaminionScope = value;
    partial void OnSavedSearchIdChanged(string value) => _session.Datasource.SavedSearchId = value;
    partial void OnCollectionIdChanged(string value) => _session.Datasource.CollectionId = value;
    partial void OnSearchTermChanged(string value) => _session.Datasource.SearchTerm = value;
    partial void OnUntaggedKeywordsChanged(bool value) => _session.Datasource.UntaggedKeywords = value;
    partial void OnUntaggedCategoriesChanged(bool value) => _session.Datasource.UntaggedCategories = value;
    partial void OnUntaggedDescriptionChanged(bool value) => _session.Datasource.UntaggedDescription = value;
    partial void OnStatusFilterChanged(string value) => _session.Datasource.StatusFilter = value;
    partial void OnMaxItemsChanged(int value) => _session.Datasource.MaxItems = value;
    partial void OnResizeScaleChanged(int value) => _session.Datasource.ResizeScale = value;
    partial void OnUseThumbnailOverrideChanged(bool value) => _session.Datasource.UseThumbnailOverride = value;

    [RelayCommand]
    private void BrowseFolder()
    {
        // StorageProvider API is wired in the view (code-behind); VM holds the result.
    }

    /// <summary>Build a DatasourceSelection for the processing orchestrator (shared Daminion client when connected).</summary>
    public DatasourceSelection ToSelectionForProcessing(DaminionApiClient? sharedClient)
    {
        var selection = _session.Datasource.ToSelection(sharedClient);
        if (DatasourceType == "daminion" && sharedClient is null && selection.DaminionClient is null)
        {
            // UI-created connection when Step 3 runs without a shared client.
            selection = new DatasourceSelection
            {
                IsDaminion = true,
                LocalPath = selection.LocalPath,
                LocalRecursive = selection.LocalRecursive,
                DaminionClient = new DaminionApiClient(DaminionUrl, DaminionUser, DaminionPass),
                Scope = selection.Scope,
                SavedSearchId = selection.SavedSearchId,
                CollectionId = selection.CollectionId,
                SearchTerm = selection.SearchTerm,
                UntaggedFields = selection.UntaggedFields,
                StatusFilter = selection.StatusFilter,
                MaxItems = selection.MaxItems,
                AutoPaginate = selection.AutoPaginate,
                ResizeScale = selection.ResizeScale,
                UseThumbnailOverride = selection.UseThumbnailOverride,
            };
        }
        return selection;
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync(CancellationToken ct)
    {
        IsConnecting = true;
        ConnectionMessage = null;
        try
        {
            var client = new DaminionApiClient(DaminionUrl, DaminionUser, DaminionPass, string.IsNullOrEmpty(DaminionCatalogId) ? null : DaminionCatalogId);
            await client.AuthenticateAsync(ct);
            ConnectedClient = client;
            IsDaminionConnected = true;
            ConnectionMessage = $"Connected to {DaminionUrl}";
            SynapicLog.Info(nameof(Step1DatasourceViewModel), $"Daminion connected: {DaminionUrl}");
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
        }
    }

    private bool CanConnect() =>
        !IsConnecting &&
        !string.IsNullOrWhiteSpace(DaminionUrl) &&
        !string.IsNullOrWhiteSpace(DaminionUser) &&
        !string.IsNullOrWhiteSpace(DaminionPass);
}

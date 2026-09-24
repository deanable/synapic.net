using Synapic.Avalonia.Services.Daminion;
using Synapic.Avalonia.Services.Processing;

namespace Synapic.Avalonia.Models;

/// <summary>
/// Wizard session state (port of src/core/session.py): datasource selection,
/// engine settings, batch counters and per-item results shared by the wizard
/// steps. Step viewmodels hydrate from and push back into this object; it is
/// the single source of truth handed to validation and the orchestrator.
/// </summary>
public sealed class Session
{
    public DatasourceState Datasource { get; } = new();
    public EngineState Engine { get; } = new();

    /// <summary>Per-item outcomes of the last batch (Step 4 grid source).</summary>
    public List<ProcessItemResult> Results { get; } = new();

    public int TotalItems { get; set; }
    public int ProcessedItems { get; set; }
    public int FailedItems { get; set; }

    /// <summary>Clear batch counters and results (start of a new run).</summary>
    public void ResetStats()
    {
        TotalItems = 0;
        ProcessedItems = 0;
        FailedItems = 0;
        Results.Clear();
    }

    /// <summary>
    /// Gate for entering Step 2 (datasource must be usable): a local folder
    /// must be chosen, or a Daminion server URL configured.
    /// </summary>
    public (bool Valid, string? Error) ValidateForStep2()
    {
        if (Datasource.Type == "daminion")
        {
            if (string.IsNullOrWhiteSpace(Datasource.DaminionUrl))
                return (false, "Enter the Daminion server URL before continuing.");
        }
        else if (string.IsNullOrWhiteSpace(Datasource.LocalPath))
        {
            return (false, "Select a folder to process first.");
        }
        return (true, null);
    }

    /// <summary>
    /// Gate for entering Step 3 (processing): a model must be chosen and the
    /// datasource must be ready — for Daminion this requires a live connection.
    /// </summary>
    public (bool Valid, string? Error) ValidateForStep3(bool daminionConnected)
    {
        if (string.IsNullOrWhiteSpace(Engine.ModelId))
            return (false, "Choose a model in Step 2 before processing.");

        if (!Engine.HasSelectedTagField)
            return (false, "Select at least one field to tag (Keywords, Categories or Description).");

        if (Datasource.Type == "daminion")
        {
            if (!daminionConnected)
                return (false, "Connect to Daminion in Step 1 before processing.");
        }
        else if (string.IsNullOrWhiteSpace(Datasource.LocalPath))
        {
            return (false, "Select a folder to process first.");
        }
        return (true, null);
    }
}

/// <summary>Step 1 state: local folder or Daminion connection, scope and filters.</summary>
public sealed class DatasourceState
{
    public string Type { get; set; } = "local";
    public string LocalPath { get; set; } = "";
    public bool LocalRecursive { get; set; }

    public string DaminionUrl { get; set; } = "";
    public string DaminionUser { get; set; } = "";
    public string DaminionPass { get; set; } = "";
    public string DaminionScope { get; set; } = "all";

    public string SearchTerm { get; set; } = "";
    public string SavedSearchId { get; set; } = "";
    public string CollectionId { get; set; } = "";

    public bool UntaggedKeywords { get; set; }
    public bool UntaggedCategories { get; set; }
    public bool UntaggedDescription { get; set; }

    public string StatusFilter { get; set; } = "all";
    public int MaxItems { get; set; } = 100;

    /// <summary>Ignore <see cref="MaxItems"/> and page until the server returns nothing.</summary>
    public bool ProcessAll { get; set; }
    public int ResizeScale { get; set; } = 100;
    public bool UseThumbnailOverride { get; set; }

    /// <summary>Untagged-field filter names derived from the checkboxes (keywords/categories/description).</summary>
    public string[] UntaggedFields()
    {
        var fields = new List<string>(3);
        if (UntaggedKeywords) fields.Add("keywords");
        if (UntaggedCategories) fields.Add("categories");
        if (UntaggedDescription) fields.Add("description");
        return fields.ToArray();
    }

    /// <summary>
    /// Build the orchestrator's <see cref="DatasourceSelection"/> from this
    /// state, optionally sharing the Step 1 authenticated Daminion client —
    /// a fresh connection would fail every fetch and metadata write.
    /// </summary>
    public DatasourceSelection ToSelection(DaminionApiClient? client) => new()
    {
        IsDaminion = Type == "daminion",
        LocalPath = LocalPath,
        LocalRecursive = LocalRecursive,
        DaminionClient = client,
        Scope = DaminionScope,
        SavedSearchId = int.TryParse(SavedSearchId, out var savedSearchId) ? savedSearchId : null,
        CollectionId = int.TryParse(CollectionId, out var collectionId) ? collectionId : null,
        SearchTerm = string.IsNullOrWhiteSpace(SearchTerm) ? null : SearchTerm,
        UntaggedFields = UntaggedFields(),
        StatusFilter = StatusFilter,
        MaxItems = MaxItems,
        ProcessAll = ProcessAll,
        ResizeScale = ResizeScale,
        UseThumbnailOverride = UseThumbnailOverride,
    };
}

/// <summary>Step 2 state: local sidecar engine settings (defaults mirror EngineSettings in config).</summary>
public sealed class EngineState
{
    public string ModelId { get; set; } = "LiquidAI/LFM2.5-VL-450M";
    public string Task { get; set; } = "image-text-to-text";
    public string Device { get; set; } = "cpu";
    public double ConfidenceThreshold { get; set; } = 0.3;
    public string ProbabilityMode { get; set; } = "both";
    public double ProbabilityThreshold { get; set; } = 0.5;
    public string[] ProbabilityCandidates { get; set; } = [];
    public string SystemPrompt { get; set; } = "";

    /// <summary>
    /// Custom tag instruction sent as /tag's <c>user_prompt</c>. Empty = use the
    /// sidecar's built-in instruction, which is the default and the safe state to
    /// return to: the built-in one is what asks for parseable JSON.
    /// </summary>
    public string UserPrompt { get; set; } = "";

    public bool EmbeddingRescueEnabled { get; set; }

    // Which returned fields to write (original Step 2 checkboxes). The LFM
    // prompt always produces all three in one JSON payload; these decide the
    // permutation that actually gets tagged.
    public bool TagKeywords { get; set; } = true;
    public bool TagCategories { get; set; } = true;
    public bool TagDescription { get; set; } = true;

    /// <summary>A batch is only meaningful when at least one field will be tagged.</summary>
    public bool HasSelectedTagField => TagKeywords || TagCategories || TagDescription;

    /// <summary>
    /// True when a run would drop one of the three fields the model returns. The
    /// write step honours this silently, so it is worth saying out loud.
    /// </summary>
    public bool HasPartialTagFieldSelection =>
        HasSelectedTagField && !ToTagFieldSelection().IsAll;

    public TagFieldSelection ToTagFieldSelection() =>
        new(TagCategories, TagKeywords, TagDescription);
}

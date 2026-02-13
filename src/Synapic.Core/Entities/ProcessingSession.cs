namespace Synapic.Core.Entities;

/// <summary>
/// Represents a processing session with configuration and statistics
/// </summary>
public class ProcessingSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    
    // Data source configuration
    public DataSourceConfig DataSource { get; set; } = new();
    
    // Engine configuration
    public EngineConfig Engine { get; set; } = new();
    
    // Runtime statistics
    public int TotalItems { get; set; }
    public int ProcessedItems { get; set; }
    public int FailedItems { get; set; }
    public bool IsProcessing { get; set; }
    
    // Results
    public List<ProcessingResult> Results { get; set; } = new();
}

/// <summary>
/// Configuration for data source
/// </summary>
public class DataSourceConfig
{
    public DataSourceType Type { get; set; } = DataSourceType.Local;
    
    // Local file system
    public string LocalPath { get; set; } = string.Empty;
    public bool LocalRecursive { get; set; }
    
    // Daminion DAMS
    public string DaminionUrl { get; set; } = string.Empty;
    public string DaminionUser { get; set; } = string.Empty;
    public string DaminionPassword { get; set; } = string.Empty;
    public DaminionScope Scope { get; set; } = DaminionScope.All;
    public int? SavedSearchId { get; set; }
    public int? CollectionId { get; set; }
    public string SearchTerm { get; set; } = string.Empty;
    
    // Filters
    public StatusFilter StatusFilter { get; set; } = StatusFilter.All;
    public bool UntaggedKeywords { get; set; }
    public bool UntaggedCategories { get; set; }
    public bool UntaggedDescription { get; set; }
    public int MaxItems { get; set; } = 100;
}

/// <summary>
/// Configuration for AI engine
/// </summary>
public class EngineConfig
{
    public EngineProvider Provider { get; set; } = EngineProvider.Local;
    public string ModelId { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public string? SystemPrompt { get; set; }
    public ModelTask Task { get; set; } = ModelTask.ImageToText;
    public int DeviceId { get; set; } = -1; // -1 for CPU, 0+ for GPU
}

/// <summary>
/// Result of processing a single item
/// </summary>
public class ProcessingResult
{
    public int ItemId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Category { get; set; }
    public List<string> Keywords { get; set; } = new();
    public string? Description { get; set; }
    public DateTime ProcessedDate { get; set; } = DateTime.UtcNow;
    public TimeSpan ProcessingDuration { get; set; }
}

public enum DataSourceType
{
    Local,
    Daminion
}

public enum DaminionScope
{
    All,
    SavedSearch,
    SharedCollection,
    Search
}

public enum StatusFilter
{
    All,
    Flagged,
    Unflagged,
    Rejected
}

public enum EngineProvider
{
    Local,
    HuggingFace,
    OpenRouter
}

public enum ModelTask
{
    ImageToText,
    ImageClassification,
    ObjectDetection,
    ZeroShotImageClassification
}

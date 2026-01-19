using Synapic.Core.Entities;

namespace Synapic.Core.Interfaces;

/// <summary>
/// Interface for data source providers (local files, Daminion, etc.)
/// </summary>
public interface IDataSourceProvider
{
    /// <summary>
    /// Connect to the data source
    /// </summary>
    Task<bool> ConnectAsync(DataSourceConfig config, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Disconnect from the data source
    /// </summary>
    Task DisconnectAsync();
    
    /// <summary>
    /// Get count of items matching the filter criteria
    /// </summary>
    Task<int> GetItemCountAsync(DataSourceConfig config, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Retrieve items matching the filter criteria
    /// </summary>
    Task<IEnumerable<MediaItem>> GetItemsAsync(
        DataSourceConfig config, 
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Download thumbnail for an item
    /// </summary>
    Task<string?> DownloadThumbnailAsync(int itemId, int width = 300, int height = 300, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Update metadata for an item
    /// </summary>
    Task<bool> UpdateItemMetadataAsync(int itemId, string? category, List<string>? keywords, string? description, CancellationToken cancellationToken = default);
}

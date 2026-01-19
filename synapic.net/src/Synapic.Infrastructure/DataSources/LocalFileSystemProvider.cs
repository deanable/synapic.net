using Microsoft.Extensions.Logging;
using Synapic.Core.Entities;
using Synapic.Core.Interfaces;

namespace Synapic.Infrastructure.DataSources;

/// <summary>
/// Data source provider for local file system
/// </summary>
public class LocalFileSystemProvider : IDataSourceProvider
{
    private readonly ILogger<LocalFileSystemProvider> _logger;
    private readonly IImageMetadataService _imageService;
    private static readonly string[] SupportedExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff" };

    public LocalFileSystemProvider(
        ILogger<LocalFileSystemProvider> logger,
        IImageMetadataService imageService)
    {
        _logger = logger;
        _imageService = imageService;
    }

    public Task<bool> ConnectAsync(DataSourceConfig config, CancellationToken cancellationToken = default)
    {
        // For local file system, just validate the path exists
        if (string.IsNullOrEmpty(config.LocalPath))
        {
            _logger.LogError("Local path is not configured");
            return Task.FromResult(false);
        }

        if (!Directory.Exists(config.LocalPath))
        {
            _logger.LogError("Directory does not exist: {Path}", config.LocalPath);
            return Task.FromResult(false);
        }

        _logger.LogInformation("Connected to local file system: {Path}", config.LocalPath);
        return Task.FromResult(true);
    }

    public Task DisconnectAsync()
    {
        // Nothing to disconnect for local file system
        return Task.CompletedTask;
    }

    public async Task<int> GetItemCountAsync(DataSourceConfig config, CancellationToken cancellationToken = default)
    {
        try
        {
            var files = await GetImageFilesAsync(config, cancellationToken);
            var count = files.Count();
            
            _logger.LogInformation("Found {Count} images in {Path}", count, config.LocalPath);
            return count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error counting items in {Path}", config.LocalPath);
            return 0;
        }
    }

    public async Task<IEnumerable<MediaItem>> GetItemsAsync(
        DataSourceConfig config, 
        IProgress<int>? progress = null, 
        CancellationToken cancellationToken = default)
    {
        try
        {
            var files = await GetImageFilesAsync(config, cancellationToken);
            var items = new List<MediaItem>();
            int processedCount = 0;

            foreach (var file in files.Take(config.MaxItems))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var item = await CreateMediaItemAsync(file, processedCount);
                
                // Apply filters
                if (PassesFilters(item, config))
                {
                    items.Add(item);
                }

                processedCount++;
                progress?.Report(processedCount);
            }

            _logger.LogInformation("Retrieved {Count} items from local file system", items.Count);
            return items;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving items from {Path}", config.LocalPath);
            throw;
        }
    }

    public Task<string?> DownloadThumbnailAsync(
        int itemId, 
        int width = 300, 
        int height = 300, 
        CancellationToken cancellationToken = default)
    {
        // For local files, we don't need to download thumbnails
        // The file path itself is the thumbnail source
        return Task.FromResult<string?>(null);
    }

    public async Task<bool> UpdateItemMetadataAsync(
        int itemId, 
        string? category, 
        List<string>? keywords, 
        string? description, 
        CancellationToken cancellationToken = default)
    {
        // For local files, metadata is written directly to the file
        // This would be called by the processing manager
        _logger.LogDebug("UpdateItemMetadata called for item {ItemId} (handled by ImageMetadataService)", itemId);
        return await Task.FromResult(true);
    }

    private async Task<IEnumerable<string>> GetImageFilesAsync(
        DataSourceConfig config, 
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            var searchOption = config.LocalRecursive 
                ? SearchOption.AllDirectories 
                : SearchOption.TopDirectoryOnly;

            return Directory.EnumerateFiles(config.LocalPath, "*.*", searchOption)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();
        }, cancellationToken);
    }

    private async Task<MediaItem> CreateMediaItemAsync(string filePath, int id)
    {
        var fileInfo = new FileInfo(filePath);
        
        // Read existing metadata
        var (category, keywords, description) = await _imageService.ReadMetadataAsync(filePath);

        return new MediaItem
        {
            Id = id,
            FilePath = filePath,
            CreatedDate = fileInfo.CreationTimeUtc,
            ModifiedDate = fileInfo.LastWriteTimeUtc,
            Category = category,
            Keywords = keywords,
            Description = description
        };
    }

    private bool PassesFilters(MediaItem item, DataSourceConfig config)
    {
        // Status filter
        if (config.StatusFilter != StatusFilter.All)
        {
            switch (config.StatusFilter)
            {
                case StatusFilter.Flagged when !item.IsFlagged:
                case StatusFilter.Unflagged when item.IsFlagged:
                case StatusFilter.Rejected when !item.IsRejected:
                    return false;
            }
        }

        // Untagged filters
        if (config.UntaggedKeywords && item.Keywords.Any())
            return false;

        if (config.UntaggedCategories && !string.IsNullOrEmpty(item.Category))
            return false;

        if (config.UntaggedDescription && !string.IsNullOrEmpty(item.Description))
            return false;

        return true;
    }
}

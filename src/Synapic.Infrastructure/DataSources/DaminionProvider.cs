using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Synapic.Core.Entities;
using Synapic.Core.Interfaces;

namespace Synapic.Infrastructure.DataSources;

/// <summary>
/// Data source provider for Daminion DAMS
/// </summary>
public class DaminionProvider : IDataSourceProvider
{
    private readonly ILogger<DaminionProvider> _logger;
    private readonly IImageMetadataService _imageService;
    private readonly HttpClient _httpClient;
    private string? _authToken;
    private string? _baseUrl;
    private Dictionary<int, string> _tagSchema = new();

    public DaminionProvider(
        ILogger<DaminionProvider> logger,
        IImageMetadataService imageService,
        HttpClient httpClient)
    {
        _logger = logger;
        _imageService = imageService;
        _httpClient = httpClient;
    }

    public async Task<bool> ConnectAsync(DataSourceConfig config, CancellationToken cancellationToken = default)
    {
        try
        {
            _baseUrl = config.DaminionUrl.TrimEnd('/');
            _logger.LogInformation("Connecting to Daminion server: {Url}", _baseUrl);

            // Authenticate
            var authPayload = new
            {
                userName = config.DaminionUser,
                password = config.DaminionPassword
            };

            var response = await _httpClient.PostAsJsonAsync(
                $"{_baseUrl}/api/Authentication/Authenticate",
                authPayload,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Authentication failed: {StatusCode}", response.StatusCode);
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<AuthResponse>(cancellationToken: cancellationToken);
            _authToken = result?.Token;

            if (string.IsNullOrEmpty(_authToken))
            {
                _logger.LogError("Authentication succeeded but no token received");
                return false;
            }

            // Set authorization header for future requests
            _httpClient.DefaultRequestHeaders.Authorization = 
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _authToken);

            _logger.LogInformation("Successfully authenticated with Daminion");

            // Load tag schema
            await LoadTagSchemaAsync(cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error connecting to Daminion");
            return false;
        }
    }

    public Task DisconnectAsync()
    {
        _authToken = null;
        _httpClient.DefaultRequestHeaders.Authorization = null;
        _logger.LogInformation("Disconnected from Daminion");
        return Task.CompletedTask;
    }

    private async Task LoadTagSchemaAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync(
                $"{_baseUrl}/api/Tags/GetLayout",
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var layout = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
                ExtractTagsFromLayout(layout);
                _logger.LogInformation("Loaded {Count} tags from schema", _tagSchema.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load tag schema");
        }
    }

    private void ExtractTagsFromLayout(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("propertyId", out var propId) && 
                element.TryGetProperty("name", out var name))
            {
                if (propId.ValueKind == JsonValueKind.Number && 
                    name.ValueKind == JsonValueKind.String)
                {
                    _tagSchema[propId.GetInt32()] = name.GetString() ?? "";
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                ExtractTagsFromLayout(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ExtractTagsFromLayout(item);
            }
        }
    }

    public async Task<int> GetItemCountAsync(DataSourceConfig config, CancellationToken cancellationToken = default)
    {
        try
        {
            var query = BuildSearchQuery(config);
            
            var response = await _httpClient.PostAsJsonAsync(
                $"{_baseUrl}/api/MediaItems/GetCount",
                query,
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<CountResponse>(cancellationToken: cancellationToken);
                _logger.LogInformation("Daminion item count: {Count}", result?.Count ?? 0);
                return result?.Count ?? 0;
            }

            _logger.LogWarning("Failed to get item count: {StatusCode}", response.StatusCode);
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting item count from Daminion");
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
            var items = new List<MediaItem>();
            var query = BuildSearchQuery(config);
            query["index"] = 0;
            query["pageSize"] = Math.Min(config.MaxItems, 200);

            var response = await _httpClient.PostAsJsonAsync(
                $"{_baseUrl}/api/MediaItems/Get",
                query,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to get items: {StatusCode}", response.StatusCode);
                return items;
            }

            var result = await response.Content.ReadFromJsonAsync<SearchResponse>(cancellationToken: cancellationToken);
            
            if (result?.Items != null)
            {
                for (int i = 0; i < result.Items.Count; i++)
                {
                    var damItem = result.Items[i];
                    var mediaItem = await ConvertToMediaItemAsync(damItem, i, cancellationToken);
                    items.Add(mediaItem);
                    progress?.Report(i + 1);
                }
            }

            _logger.LogInformation("Retrieved {Count} items from Daminion", items.Count);
            return items;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving items from Daminion");
            throw;
        }
    }

    public async Task<string?> DownloadThumbnailAsync(
        int itemId, 
        int width = 300, 
        int height = 300, 
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync(
                $"{_baseUrl}/api/Thumbnail/Get/{itemId}?width={width}&height={height}",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var tempPath = Path.Combine(Path.GetTempPath(), $"daminion_thumb_{itemId}.jpg");
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);

            return tempPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading thumbnail for item {ItemId}", itemId);
            return null;
        }
    }

    public async Task<bool> UpdateItemMetadataAsync(
        int itemId, 
        string? category, 
        List<string>? keywords, 
        string? description, 
        CancellationToken cancellationToken = default)
    {
        try
        {
            var updates = new Dictionary<string, object>();

            if (!string.IsNullOrEmpty(category))
            {
                updates["Category"] = new[] { category };
            }

            if (keywords != null && keywords.Any())
            {
                updates["Keywords"] = keywords.ToArray();
            }

            if (!string.IsNullOrEmpty(description))
            {
                updates["Description"] = description;
            }

            var payload = new
            {
                itemIds = new[] { itemId },
                tags = updates
            };

            var response = await _httpClient.PostAsJsonAsync(
                $"{_baseUrl}/api/ItemData/BatchUpdate",
                payload,
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Updated metadata for item {ItemId}", itemId);
                return true;
            }

            _logger.LogWarning("Failed to update metadata for item {ItemId}: {StatusCode}", 
                itemId, response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating metadata for item {ItemId}", itemId);
            return false;
        }
    }

    private Dictionary<string, object> BuildSearchQuery(DataSourceConfig config)
    {
        var query = new Dictionary<string, object>();

        switch (config.Scope)
        {
            case DaminionScope.SavedSearch:
                if (config.SavedSearchId.HasValue)
                {
                    query["savedSearchId"] = config.SavedSearchId.Value;
                }
                break;

            case DaminionScope.SharedCollection:
                if (config.CollectionId.HasValue)
                {
                    query["collectionId"] = config.CollectionId.Value;
                }
                break;

            case DaminionScope.Search:
                if (!string.IsNullOrEmpty(config.SearchTerm))
                {
                    query["searchTerm"] = config.SearchTerm;
                }
                break;
        }

        return query;
    }

    private async Task<MediaItem> ConvertToMediaItemAsync(
        DaminionItem damItem, 
        int index, 
        CancellationToken cancellationToken)
    {
        var item = new MediaItem
        {
            Id = damItem.Id,
            FilePath = damItem.FilePath ?? "",
            Category = damItem.Category,
            Keywords = damItem.Keywords?.ToList() ?? new List<string>(),
            Description = damItem.Description,
            IsFlagged = damItem.IsFlagged,
            IsRejected = damItem.IsRejected
        };

        return await Task.FromResult(item);
    }

    // DTOs for Daminion API
    private class AuthResponse
    {
        public string? Token { get; set; }
    }

    private class CountResponse
    {
        public int Count { get; set; }
    }

    private class SearchResponse
    {
        public List<DaminionItem>? Items { get; set; }
    }

    private class DaminionItem
    {
        public int Id { get; set; }
        public string? FilePath { get; set; }
        public string? Category { get; set; }
        public string[]? Keywords { get; set; }
        public string? Description { get; set; }
        public bool IsFlagged { get; set; }
        public bool IsRejected { get; set; }
    }
}

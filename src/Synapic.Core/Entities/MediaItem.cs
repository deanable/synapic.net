namespace Synapic.Core.Entities;

/// <summary>
/// Represents a media item (image) to be processed
/// </summary>
public class MediaItem
{
    public int Id { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string? ThumbnailPath { get; set; }
    public DateTime? CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    
    // Metadata fields
    public string? Category { get; set; }
    public List<string> Keywords { get; set; } = new();
    public string? Description { get; set; }
    
    // Status flags
    public bool IsFlagged { get; set; }
    public bool IsRejected { get; set; }
    public bool IsProcessed { get; set; }
    
    // Processing results
    public string? ProcessingError { get; set; }
    public DateTime? ProcessedDate { get; set; }
}

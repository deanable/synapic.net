using SixLabors.ImageSharp;

namespace Synapic.Core.Interfaces;

/// <summary>
/// Interface for image metadata operations
/// </summary>
public interface IImageMetadataService
{
    /// <summary>
    /// Read metadata from an image file
    /// </summary>
    Task<(string? category, List<string> keywords, string? description)> ReadMetadataAsync(string imagePath);
    
    /// <summary>
    /// Write metadata to an image file
    /// </summary>
    Task<bool> WriteMetadataAsync(
        string imagePath, 
        string? category, 
        List<string>? keywords, 
        string? description,
        int maxRetries = 3);
    
    /// <summary>
    /// Validate that an image can be processed
    /// </summary>
    Task<(bool isValid, string? error)> ValidateImageAsync(string imagePath);
    
    /// <summary>
    /// Load and preprocess image for model inference
    /// </summary>
    Task<Image> LoadImageAsync(string imagePath);
}

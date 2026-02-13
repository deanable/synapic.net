using Microsoft.Extensions.Logging;
using Synapic.Core.Interfaces;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Metadata.Profiles.Iptc;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Iptc;

namespace Synapic.Infrastructure.Services;

/// <summary>
/// Service for reading and writing image metadata (EXIF, IPTC)
/// </summary>
public class ImageMetadataService : IImageMetadataService
{
    private readonly ILogger<ImageMetadataService> _logger;

    public ImageMetadataService(ILogger<ImageMetadataService> logger)
    {
        _logger = logger;
    }

    public async Task<(string? category, List<string> keywords, string? description)> ReadMetadataAsync(string imagePath)
    {
        return await Task.Run<(string?, List<string>, string?)>(() =>
        {
            try
            {
                var directories = ImageMetadataReader.ReadMetadata(imagePath);
                
                string? category = null;
                var keywords = new List<string>();
                string? description = null;

                // Read IPTC metadata
                var iptcDirectory = directories.OfType<IptcDirectory>().FirstOrDefault();
                if (iptcDirectory != null)
                {
                    // Category
                    category = iptcDirectory.GetString(IptcDirectory.TagCategory);
                    
                    // Keywords
                    var keywordTags = iptcDirectory.GetStringArray(IptcDirectory.TagKeywords);
                    if (keywordTags != null)
                    {
                        keywords.AddRange(keywordTags);
                    }
                    
                    // Description/Caption
                    description = iptcDirectory.GetString(IptcDirectory.TagCaption);
                }

                // Fallback to EXIF if IPTC not available
                if (string.IsNullOrEmpty(description))
                {
                    var exifDirectory = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
                    description = exifDirectory?.GetString(ExifDirectoryBase.TagImageDescription);
                }

                _logger.LogDebug("Read metadata from {ImagePath}: Category={Category}, Keywords={KeywordCount}, Description={HasDescription}",
                    imagePath, category, keywords.Count, !string.IsNullOrEmpty(description));

                return (category, keywords, description);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading metadata from {ImagePath}", imagePath);
                return ((string?)null, new List<string>(), (string?)null);
            }
        });
    }

    public async Task<bool> WriteMetadataAsync(
        string imagePath, 
        string? category, 
        List<string>? keywords, 
        string? description, 
        int maxRetries = 3)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await Task.Run(() =>
                {
                    using var image = Image.Load(imagePath);
                    
                    // Get or create IPTC profile
                    var iptcProfile = image.Metadata.IptcProfile ?? new IptcProfile();

                    // Write category
                    if (!string.IsNullOrEmpty(category))
                    {
                        iptcProfile.SetValue(IptcTag.Category, category);
                    }

                    // Write keywords
                    if (keywords != null && keywords.Any())
                    {
                        foreach (var keyword in keywords)
                        {
                            iptcProfile.SetValue(IptcTag.Keywords, keyword);
                        }
                    }

                    // Write description
                    if (!string.IsNullOrEmpty(description))
                    {
                        iptcProfile.SetValue(IptcTag.Caption, description);
                    }

                    image.Metadata.IptcProfile = iptcProfile;

                    // Also write to EXIF
                    var exifProfile = image.Metadata.ExifProfile ?? new ExifProfile();
                    if (!string.IsNullOrEmpty(description))
                    {
                        exifProfile.SetValue(ExifTag.ImageDescription, description);
                    }
                    image.Metadata.ExifProfile = exifProfile;

                    // Save the image
                    image.Save(imagePath);
                });

                _logger.LogDebug("Wrote metadata to {ImagePath} on attempt {Attempt}", imagePath, attempt);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write metadata to {ImagePath} on attempt {Attempt}/{MaxRetries}", 
                    imagePath, attempt, maxRetries);
                
                if (attempt < maxRetries)
                {
                    await Task.Delay(500 * attempt); // Exponential backoff
                }
                else
                {
                    _logger.LogError(ex, "Failed to write metadata to {ImagePath} after {MaxRetries} attempts", 
                        imagePath, maxRetries);
                    return false;
                }
            }
        }

        return false;
    }

    public async Task<(bool isValid, string? error)> ValidateImageAsync(string imagePath)
    {
        return await Task.Run<(bool, string?)>(() =>
        {
            try
            {
                if (!File.Exists(imagePath))
                {
                    return (false, "File does not exist");
                }

                // Try to load the image
                using var image = Image.Load(imagePath);
                
                if (image.Width == 0 || image.Height == 0)
                {
                    return (false, "Invalid image dimensions");
                }

                return (true, (string?)null);
            }
            catch (UnknownImageFormatException)
            {
                return (false, "Unknown or unsupported image format");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating image {ImagePath}", imagePath);
                return (false, ex.Message);
            }
        });
    }

    public async Task<Image> LoadImageAsync(string imagePath)
    {
        return await Task.Run(() =>
        {
            try
            {
                return Image.Load(imagePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading image {ImagePath}", imagePath);
                throw;
            }
        });
    }
}

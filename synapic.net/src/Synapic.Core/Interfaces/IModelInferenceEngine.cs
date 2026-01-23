using Synapic.Core.Entities;

namespace Synapic.Core.Interfaces;

/// <summary>
/// Interface for AI model inference engines
/// 
/// This interface defines the contract for all AI inference engines that can be used
/// to analyze images and extract metadata such as categories, keywords, and descriptions.
/// Implementations may include local TorchSharp engines, cloud-based APIs,
/// or other AI frameworks.
/// </summary>
public interface IModelInferenceEngine : IAsyncDisposable
{
    /// <summary>
    /// Initialize the model with the given configuration
    /// </summary>
    /// <param name="config">Engine configuration including model ID, task type, and device settings</param>
    /// <param name="progress">Optional progress reporter for initialization status updates</param>
    /// <param name="cancellationToken">Cancellation token for stopping initialization</param>
    /// <returns>Task representing the asynchronous initialization operation</returns>
    /// <exception cref="ArgumentException">Thrown when configuration is invalid</exception>
    /// <exception cref="InvalidOperationException">Thrown when model fails to load</exception>
    /// <exception cref="FileNotFoundException">Thrown when model files are not found</exception>
    Task InitializeAsync(EngineConfig config, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Process a single image and extract AI-generated metadata
    /// </summary>
    /// <param name="imagePath">Full path to the image file to process</param>
    /// <param name="cancellationToken">Cancellation token for stopping processing</param>
    /// <returns>
    /// Tuple containing:
    /// - category: Primary image classification (e.g., "dog", "landscape")
    /// - keywords: List of descriptive keywords (e.g., ["pet", "animal", "brown"])
    /// - description: Natural language description of the image content
    /// </returns>
    /// <exception cref="FileNotFoundException">Thrown when image file doesn't exist</exception>
    /// <exception cref="UnsupportedImageFormatException">Thrown when image format is not supported</exception>
    /// <exception cref="InvalidOperationException">Thrown when engine is not initialized</exception>
    Task<(string? category, List<string> keywords, string? description)> ProcessImageAsync(
        string imagePath, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Check if the engine is initialized and ready for processing
    /// </summary>
    /// <returns>True if model is loaded and ready, false otherwise</returns>
    bool IsInitialized { get; }
}

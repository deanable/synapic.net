using Synapic.Core.Entities;

namespace Synapic.Core.Interfaces;

/// <summary>
/// Interface for AI model inference engines
/// </summary>
public interface IModelInferenceEngine
{
    /// <summary>
    /// Initialize the model with the given configuration
    /// </summary>
    Task InitializeAsync(EngineConfig config, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Process a single image and extract tags
    /// </summary>
    Task<(string? category, List<string> keywords, string? description)> ProcessImageAsync(
        string imagePath, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Check if the engine is initialized and ready
    /// </summary>
    bool IsInitialized { get; }
    
/// <summary>
    /// Dispose of model resources
    /// </summary>
    ValueTask DisposeAsync();
}

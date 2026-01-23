using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Synapic.Core.Interfaces;
using Synapic.Infrastructure.AI;
using Synapic.Infrastructure.DataSources;
using Synapic.Infrastructure.Persistence;
using Synapic.Infrastructure.Services;
using Synapic.Application.Services;
using Synapic.Core.Entities;

namespace Synapic.Application.Configuration;

/// <summary>
/// Dependency injection configuration for Synapic services
/// 
/// This extension method configures all the services required for the Synapic.NET application.
/// It sets up proper dependency injection, logging, and service lifetimes.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add Synapic services to the dependency injection container
    /// </summary>
    /// <param name="services">The service collection to configure</param>
    /// <param name="configureOptions">Optional action to configure Synapic options</param>
    /// <returns>The configured service collection for chaining</returns>
    /// <exception cref="ArgumentNullException">Thrown when services is null</exception>
    /// <example>
    /// <code>
    /// var services = new ServiceCollection();
    /// services.AddSynapicServices(options => {
    ///     options.ModelCachePath = @"C:\Models";
    ///     options.DefaultDeviceId = 0; // Use GPU
    /// });
    /// var serviceProvider = services.BuildServiceProvider();
    /// </code>
    /// </example>
    public static IServiceCollection AddSynapicServices(
        this IServiceCollection services, 
        Action<SynapicOptions>? configureOptions = null)
    {
        // Configure options
        var options = new SynapicOptions();
        configureOptions?.Invoke(options);
        services.AddSingleton(options);

        // Add logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.AddDebug();
        });

        // Core services
        services.AddSingleton<IImageMetadataService, ImageMetadataService>();
        services.AddSingleton<ISessionRepository, JsonSessionRepository>();
        
        // Data source providers (registered as transient to allow multiple instances)
        services.AddTransient<IDataSourceProvider, LocalFileSystemProvider>();
        services.AddTransient<IDataSourceProvider, DaminionProvider>();
        
        // AI inference engine
        services.AddTransient<IModelInferenceEngine, TorchSharpInferenceEngine>();
        
        // Application services
        services.AddScoped<ProcessingManager>();
        
        // HTTP client for infrastructure
        services.AddHttpClient();
        
        return services;
    }
}

/// <summary>
/// Configuration options for Synapic services
/// </summary>
public class SynapicOptions
{
    /// <summary>
    /// Default model cache directory
    /// </summary>
    public string? ModelCachePath { get; set; }
    
    /// <summary>
    /// Default session storage directory
    /// </summary>
    public string? SessionStoragePath { get; set; }
    
    /// <summary>
    /// Default device for AI inference (-1 for CPU, 0+ for GPU)
    /// </summary>
    public int DefaultDeviceId { get; set; } = -1;
    
    /// <summary>
    /// Enable CUDA GPU acceleration if available
    /// </summary>
    public bool EnableCuda { get; set; } = true;
    
    /// <summary>
    /// Maximum number of concurrent image processing threads
    /// </summary>
    public int MaxConcurrency { get; set; } = Environment.ProcessorCount;
    
    /// <summary>
    /// Default model for image classification
    /// </summary>
    public string DefaultClassificationModel { get; set; } = "resnet50";
    
    /// <summary>
    /// Default model for image-to-text
    /// </summary>
    public string DefaultImageToTextModel { get; set; } = "blip-base";
    
    /// <summary>
    /// Default model for zero-shot classification
    /// </summary>
    public string DefaultZeroShotModel { get; set; } = "clip-vit-base-patch32";
}
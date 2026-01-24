using System.Text.Json;
using Microsoft.Extensions.Logging;
using Synapic.Core.Entities;
using Synapic.Core.Interfaces;

namespace Synapic.Infrastructure.AI;

public class LocalModelRepository : IModelRepository
{
    private readonly ILogger<LocalModelRepository> _logger;
    private readonly string _modelsDirectory;
    private List<ModelInfo> _cachedModels = new();

    public LocalModelRepository(ILogger<LocalModelRepository> logger)
    {
        _logger = logger;
        _modelsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models");
        
        // Scan on initialization
        ScanModels();
    }

    private void ScanModels()
    {
        _cachedModels.Clear();
        
        if (!Directory.Exists(_modelsDirectory))
        {
            _logger.LogWarning("Models directory not found at {ModelsDirectory}", _modelsDirectory);
            return;
        }

        foreach (var dir in Directory.GetDirectories(_modelsDirectory))
        {
            var configPath = Path.Combine(dir, "config.json");
            if (File.Exists(configPath))
            {
                try
                {
                    var json = File.ReadAllText(configPath);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement; // Assuming config.json structure for now
                    
                    // Simple parsing logic - adapt based on actual config.json structure
                    // For sidecar.py generated configs, we might need specific property names
                    
                    var modelName = Path.GetFileName(dir); // Default to directory name
                    if (root.TryGetProperty("name", out var nameProp)) modelName = nameProp.GetString() ?? modelName;
                    
                    // Default task if not specified
                    var task = ModelTask.ImageToText; 
                    if (root.TryGetProperty("task", out var taskProp) && Enum.TryParse<ModelTask>(taskProp.GetString(), true, out var parsedTask))
                    {
                        task = parsedTask;
                    }

                    _cachedModels.Add(new ModelInfo
                    {
                        Id = Path.GetFileName(dir),
                        Name = modelName,
                        Description = $"{task} Model",
                        Task = task,
                        Path = dir
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error reading config for model at {Dir}", dir);
                }
            }
        }
        
        _logger.LogInformation("Found {Count} models in {ModelsDirectory}", _cachedModels.Count, _modelsDirectory);
    }

    public IEnumerable<ModelInfo> GetAvailableModels()
    {
        return _cachedModels;
    }

    public ModelInfo? GetModel(string id)
    {
        return _cachedModels.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }
}

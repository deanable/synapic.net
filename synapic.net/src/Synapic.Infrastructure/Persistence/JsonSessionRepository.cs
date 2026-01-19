using System.Text.Json;
using Microsoft.Extensions.Logging;
using Synapic.Core.Entities;
using Synapic.Core.Interfaces;

namespace Synapic.Infrastructure.Persistence;

/// <summary>
/// JSON-based session repository for persisting configuration
/// </summary>
public class JsonSessionRepository : ISessionRepository
{
    private readonly ILogger<JsonSessionRepository> _logger;
    private readonly string _configDirectory;
    private readonly string _configFilePath;

    public JsonSessionRepository(ILogger<JsonSessionRepository> logger)
    {
        _logger = logger;
        
        // Store config in user's AppData
        _configDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Synapic");
        
        _configFilePath = Path.Combine(_configDirectory, "session.json");
    }

    public async Task SaveSessionAsync(ProcessingSession session)
    {
        try
        {
            // Ensure directory exists
            Directory.CreateDirectory(_configDirectory);

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var json = JsonSerializer.Serialize(session, options);
            await File.WriteAllTextAsync(_configFilePath, json);

            _logger.LogInformation("Session saved to {Path}", _configFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving session to {Path}", _configFilePath);
            throw;
        }
    }

    public async Task<ProcessingSession?> LoadSessionAsync()
    {
        try
        {
            if (!File.Exists(_configFilePath))
            {
                _logger.LogInformation("No saved session found at {Path}", _configFilePath);
                return null;
            }

            var json = await File.ReadAllTextAsync(_configFilePath);
            
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var session = JsonSerializer.Deserialize<ProcessingSession>(json, options);
            
            _logger.LogInformation("Session loaded from {Path}", _configFilePath);
            return session;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading session from {Path}", _configFilePath);
            return null;
        }
    }

    public async Task ClearSessionAsync()
    {
        try
        {
            if (File.Exists(_configFilePath))
            {
                File.Delete(_configFilePath);
                _logger.LogInformation("Session cleared from {Path}", _configFilePath);
            }
            
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing session from {Path}", _configFilePath);
            throw;
        }
    }
}

using Synapic.Core.Entities;

namespace Synapic.Core.Interfaces;

/// <summary>
/// Interface for session persistence
/// </summary>
public interface ISessionRepository
{
    /// <summary>
    /// Save session configuration
    /// </summary>
    Task SaveSessionAsync(ProcessingSession session);
    
    /// <summary>
    /// Load last saved session
    /// </summary>
    Task<ProcessingSession?> LoadSessionAsync();
    
    /// <summary>
    /// Clear saved session
    /// </summary>
    Task ClearSessionAsync();
}

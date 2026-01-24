using Synapic.Core.Entities;

namespace Synapic.Core.Interfaces;

public interface IModelRepository
{
    IEnumerable<ModelInfo> GetAvailableModels();
    ModelInfo? GetModel(string id);
}


namespace Synapic.Core.Entities;

public class ModelInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ModelTask Task { get; set; }
    public string Path { get; set; } = string.Empty;

    public override string ToString() => Name;
}

using Synapic.Core.Services;

namespace Synapic.Main.Tests;

/// <summary>
/// Metadata-writer stub that records what it was asked to write. The orchestrator
/// tests that use it moved to Synapic.Core.Tests, which keeps its own copy; the
/// two test assemblies must not reference each other just to share a stub.
/// </summary>
internal sealed class CapturingMetadataWriter : IMetadataWriter
{
    private readonly List<TagResult> _writes = new();
    public IReadOnlyList<TagResult> Writes { get { lock (_writes) return _writes.ToArray(); } }

    public Task<bool> WriteAsync(string filePath, TagResult tags, CancellationToken ct)
    {
        lock (_writes) _writes.Add(tags);
        return Task.FromResult(true);
    }

    public Task<TagResult?> ReadAsync(string filePath, CancellationToken ct) => Task.FromResult<TagResult?>(null);
}

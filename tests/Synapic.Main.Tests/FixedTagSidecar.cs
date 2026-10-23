using Synapic.Core.Services;
using Synapic.Shared.Contracts;

namespace Synapic.Main.Tests;

/// <summary>
/// Deterministic tagging fake used by the desktop tests that exercise the
/// kernel through a UI view model. The Core test project has its own copy: the
/// two test assemblies must not reference each other just to share a stub.
/// </summary>
internal class FixedTagSidecar : IInferenceSidecar
{
    public SidecarStatus CurrentStatus => SidecarStatus.Ready;
    public int SidecarPort => 0;
    public bool IsRunning => true;

#pragma warning disable CS0067 // events unused by this fake
    public event EventHandler<SidecarStatusChangedEventArgs>? StatusChanged;
    public event Action<string>? LogReceived;
#pragma warning restore CS0067

    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(TimeSpan? gracefulTimeout = null) => Task.CompletedTask;

    public virtual Task<TagResponse> TagAsync(TagRequest request, CancellationToken ct = default)
        => Task.FromResult(new TagResponse
        {
            Category = "Nature",
            Keywords = ["tree", "forest"],
            Description = "A forest at dawn",
        });

    public Task<ModelInfo[]> ListModelsAsync(CancellationToken ct = default) => Task.FromResult(Array.Empty<ModelInfo>());
    public Task DownloadModelAsync(string modelId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<HealthResponse> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new HealthResponse());
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

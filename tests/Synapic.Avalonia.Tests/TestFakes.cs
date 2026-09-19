using Synapic.Avalonia.Services;
using Synapic.Shared.Contracts;

namespace Synapic.Avalonia.Tests;

/// <summary>In-memory IInferenceSidecar for view-model tests.</summary>
internal sealed class FakeSidecar : IInferenceSidecar
{
    public SidecarStatus CurrentStatus { get; set; } = SidecarStatus.Stopped;
    public int SidecarPort { get; set; }
    public bool IsRunning { get; set; }
    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }

    public event EventHandler<SidecarStatusChangedEventArgs>? StatusChanged;
    public event Action<string>? LogReceived;

    public Task StartAsync(CancellationToken ct = default)
    {
        StartCalls++;
        RaiseStatus(SidecarStatus.Ready);
        return Task.CompletedTask;
    }

    public Task StopAsync(TimeSpan? gracefulTimeout = null)
    {
        StopCalls++;
        RaiseStatus(SidecarStatus.Stopped);
        return Task.CompletedTask;
    }

    public Task<TagResponse> TagAsync(TagRequest request, CancellationToken ct = default)
        => Task.FromResult(new TagResponse());

    public Task<ModelInfo[]> ListModelsAsync(CancellationToken ct = default)
        => Task.FromResult(Array.Empty<ModelInfo>());

    public Task DownloadModelAsync(string modelId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<HealthResponse> GetHealthAsync(CancellationToken ct = default)
        => Task.FromResult(new HealthResponse());

    public void RaiseStatus(SidecarStatus status, string? message = null)
    {
        CurrentStatus = status;
        StatusChanged?.Invoke(this, new SidecarStatusChangedEventArgs(status, message));
    }

    public void RaiseLog(string line) => LogReceived?.Invoke(line);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>In-memory ISidecarBuildService for view-model tests.</summary>
internal sealed class FakeBuildService : ISidecarBuildService
{
    public bool CanBuild { get; set; } = true;
    public bool IsBuilding { get; set; }
    public Func<Action<string>, CancellationToken, Task>? OnBuild { get; set; }
    public int BuildCalls { get; private set; }

    public Task BuildAsync(Action<string> log, CancellationToken ct)
    {
        BuildCalls++;
        return OnBuild is { } callback ? callback(log, ct) : Task.CompletedTask;
    }

    public void Cancel() { }
}

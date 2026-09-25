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

    /// <summary>Stand-in for the sidecar's built-in instruction.</summary>
    public string PromptDefaults { get; set; } = "BUILT-IN INSTRUCTION";

    /// <summary>How often the built-in instruction was requested (caching check).</summary>
    public int PromptFetchCount { get; private set; }

    public Task<PromptDefaultsDto> GetPromptDefaultsAsync(CancellationToken ct = default)
    {
        PromptFetchCount++;
        return Task.FromResult(new PromptDefaultsDto { DefaultUserPrompt = PromptDefaults });
    }

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

    /// <summary>(rid, log, progress, ct) — returns the task the fake build awaits.</summary>
    public Func<string, Action<string>, IProgress<SidecarBuildProgress>, CancellationToken, Task>? OnBuild { get; set; }

    /// <summary>Every RID a build was requested for, in order.</summary>
    public List<string> BuiltRids { get; } = new();

    public int BuildCalls => BuiltRids.Count;

    public Task BuildAsync(string rid, Action<string> log, IProgress<SidecarBuildProgress> progress, CancellationToken ct)
    {
        BuiltRids.Add(rid);
        return OnBuild is { } callback ? callback(rid, log, progress, ct) : Task.CompletedTask;
    }

    public void Cancel() { }
}

/// <summary>In-memory ISidecarDownloadService for view-model tests.</summary>
internal sealed class FakeDownloadService : ISidecarDownloadService
{
    /// <summary>Every RID a download was requested for, in order.</summary>
    public List<string> DownloadedRids { get; } = new();

    /// <summary>Destinations the download was asked to write, in order.</summary>
    public List<string> Destinations { get; } = new();

    /// <summary>(rid, destination, progress, ct) — returns the task the fake awaits.</summary>
    public Func<string, string, IProgress<SidecarDownloadProgress>, CancellationToken, Task>? OnDownload { get; set; }

    public Task DownloadAsync(
        string rid, string destinationPath, IProgress<SidecarDownloadProgress> progress, CancellationToken ct = default)
    {
        DownloadedRids.Add(rid);
        Destinations.Add(destinationPath);
        return OnDownload is { } callback ? callback(rid, destinationPath, progress, ct) : Task.CompletedTask;
    }
}

/// <summary>In-memory IHelpService for view-model tests.</summary>
internal sealed class FakeHelpService : IHelpService
{
    /// <summary>Every topic Open was asked for, in order (null = the home page).</summary>
    public List<string?> OpenedTopics { get; } = new();

    public bool IsAvailable { get; set; } = true;

    /// <summary>What Open reports; false models a machine with no help payload.</summary>
    public bool OpenResult { get; set; } = true;

    public bool Open(string? topic = null)
    {
        OpenedTopics.Add(topic);
        return OpenResult;
    }
}

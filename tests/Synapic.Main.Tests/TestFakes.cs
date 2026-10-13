using Synapic.Main.Services;
using Synapic.Shared.Contracts;

namespace Synapic.Main.Tests;

/// <summary>In-memory IInferenceSidecar for view-model tests.</summary>
internal class FakeSidecar : IInferenceSidecar
{
    public SidecarStatus CurrentStatus { get; set; } = SidecarStatus.Stopped;
    public int SidecarPort { get; set; }
    public bool IsRunning { get; set; }
    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }
    public int HealthCalls { get; private set; }
    public Func<CancellationToken, Task<HealthResponse>>? HealthHandler { get; set; }
    public Func<TagRequest, CancellationToken, Task<TagResponse>>? TagHandler { get; set; }

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
        => TagHandler is { } handler
            ? handler(request, ct)
            : Task.FromResult(new TagResponse());

    public virtual Task<ModelInfo[]> ListModelsAsync(CancellationToken ct = default)
        => Task.FromResult(Array.Empty<ModelInfo>());

    public Task DownloadModelAsync(string modelId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<HealthResponse> GetHealthAsync(CancellationToken ct = default)
    {
        HealthCalls++;
        return HealthHandler is { } handler
            ? handler(ct)
            : Task.FromResult(new HealthResponse());
    }

    /// <summary>Every device pushed through SetDeviceAsync, in order.</summary>
    public List<string?> PushedDevices { get; } = new();

    public Task SetDeviceAsync(string? device, CancellationToken ct = default)
    {
        PushedDevices.Add(device);
        return Task.CompletedTask;
    }

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

/// <summary>In-memory ISidecarDownloadService for view-model tests.</summary>
internal sealed class FakeDownloadService : ISidecarDownloadService
{
    /// <summary>Every RID a download was requested for, in order.</summary>
    public List<string> DownloadedRids { get; } = new();

    /// <summary>Destinations the download was asked to write, in order.</summary>
    public List<string> Destinations { get; } = new();

    /// <summary>(rid, destination, progress, ct) — returns the task the fake awaits.</summary>
    public Func<string, string, IProgress<SidecarDownloadProgress>, CancellationToken, Task>? OnDownload { get; set; }    public Task DownloadAsync(
        string rid,
        string destinationPath,
        IProgress<SidecarDownloadProgress> progress,
        CancellationToken ct = default)
    {
        DownloadedRids.Add(rid);
        Destinations.Add(destinationPath);
        return OnDownload is { } callback ? callback(rid, destinationPath, progress, ct) : Task.CompletedTask;
    }

    /// <summary>What the startup GitHub check reports; null = nothing newer
    /// (the default keeps every test hermetic and quiet).</summary>
    public SidecarUpdateInfo? UpdateInfo { get; set; }

    /// <summary>Every RID the update check asked about, in order.</summary>
    public List<string> CheckedRids { get; } = new();

    public Task<SidecarUpdateInfo?> CheckForUpdateAsync(string rid, string? localExePath, CancellationToken ct = default)
    {
        CheckedRids.Add(rid);
        return Task.FromResult(UpdateInfo);
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

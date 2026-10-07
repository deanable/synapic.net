using Synapic.Avalonia.Services;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// Local-only opt-in telemetry (P6.1, spec §11 Q5): counters accrue in a JSON
/// file only when enabled; when disabled the service is a no-op that never
/// writes anything. The file holds aggregate counts only — version, OS family,
/// model usage — no paths, no file names, no network.
/// </summary>
public class TelemetryServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "synapic-usage-tests-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "synapic-usage.json");

    public TelemetryServiceTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Disabled_service_never_writes_anything()
    {
        var svc = new TelemetryService(enabled: false, filePath: FilePath);
        svc.RecordAppLaunch();
        svc.RecordBatch(10, 2, "model-a", 5.0);
        svc.RecordDedup(3);
        svc.RecordMetadataWrite(7);

        Assert.False(File.Exists(FilePath));
        Assert.False(svc.Enabled);
        Assert.Equal(0, svc.Snapshot().Batches);
    }

    [Fact]
    public void Enabled_service_accumulates_counters_and_persists()
    {
        var svc = new TelemetryService(enabled: true, filePath: FilePath, now: () => new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc));
        svc.RecordAppLaunch();
        svc.RecordBatch(itemsProcessed: 8, itemsFailed: 2, modelId: "LiquidAI/LFM2.5-VL-1.6B", durationSeconds: 12.5);
        svc.RecordBatch(itemsProcessed: 5, itemsFailed: 0, modelId: "LiquidAI/LFM2.5-VL-1.6B", durationSeconds: 7.5);
        svc.RecordDedup(duplicatesFound: 4);
        svc.RecordMetadataWrite(13);

        Assert.True(File.Exists(FilePath));

        // A fresh instance reloads the persisted file (simulates next launch).
        var reloaded = new TelemetryService(enabled: true, filePath: FilePath);
        var snap = reloaded.Snapshot();
        Assert.Equal(1, snap.Launches);
        Assert.Equal(2, snap.Batches);
        Assert.Equal(13, snap.ItemsProcessed);
        Assert.Equal(2, snap.ItemsFailed);
        Assert.Equal(20.0, snap.TotalInferenceSeconds, precision: 1);
        Assert.Equal(1, snap.DedupRuns);
        Assert.Equal(4, snap.DuplicatesFound);
        Assert.Equal(13, snap.MetadataWrites);
        Assert.Equal(2, snap.ModelUsage["LiquidAI/LFM2.5-VL-1.6B"]);
        Assert.Equal(new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc), snap.LastUpdatedUtc);
    }

    [Fact]
    public void Environment_metadata_is_stamped()
    {
        var svc = new TelemetryService(enabled: true, filePath: FilePath,
            appVersion: "1.0.0", osFamily: "Windows");
        svc.RecordAppLaunch();

        var content = File.ReadAllText(FilePath);
        Assert.Contains("\"appVersion\": \"1.0.0\"", content);
        Assert.Contains("\"osFamily\": \"Windows\"", content);
    }

    [Fact]
    public void Corrupt_file_starts_fresh()
    {
        File.WriteAllText(FilePath, "{ not valid json !!");
        var svc = new TelemetryService(enabled: true, filePath: FilePath);
        svc.RecordAppLaunch();

        Assert.Equal(1, svc.Snapshot().Launches);
        Assert.True(File.Exists(FilePath));
    }

    [Fact]
    public void Shared_defaults_to_disabled_noop()
    {
        // App startup replaces this; tests and pre-startup code must be safe.
        Assert.False(TelemetryService.Shared.Enabled);
        TelemetryService.Shared.RecordBatch(1, 1, "m", 1);
        Assert.Equal(0, TelemetryService.Shared.Snapshot().Batches);
    }

    [Fact]
    public void Negative_inputs_are_clamped()
    {
        var svc = new TelemetryService(enabled: true, filePath: FilePath);
        svc.RecordBatch(-5, -2, null, -10);
        svc.RecordMetadataWrite(-3);

        var snap = svc.Snapshot();
        Assert.Equal(1, snap.Batches);
        Assert.Equal(0, snap.ItemsProcessed);
        Assert.Equal(0, snap.ItemsFailed);
        Assert.Equal(0, snap.MetadataWrites);
        Assert.Empty(snap.ModelUsage);
    }
}

using Synapic.Core.Services.Daminion;
using Synapic.Shared.Contracts;

using Synapic.Core.Services;
namespace Synapic.Core.Services.Processing;

/// <summary>
/// The upscaling workflow's per-item operation — a port of the original app's
/// <c>StepUpscale._process_single_item</c> (checkout → download → upscale →
/// check-in) for a Daminion source, and the same enhance without the version
/// dance for local files, where the output is written beside the original as
/// <c>{name}_upscaled{ext}</c>. All three workflows share the runner; this
/// class is the part that changes — plus, of course, the
/// <see cref="UpscaleOptions"/> parameters themselves.
/// </summary>
public sealed class UpscaleItemHandler : IWorkflowItemHandler
{
    private static readonly TimeSpan RollbackTimeout = TimeSpan.FromSeconds(15);

    private readonly DaminionApiClient? _client;
    private readonly IInferenceSidecar _sidecar;
    private readonly UpscaleOptions _options;
    private readonly Func<string, Task> _log;

    /// <param name="client">Catalog client; required only for Daminion items.</param>
    /// <param name="sidecar">Runs the actual algorithm (POST /upscale).</param>
    /// <param name="options">The run's parameters — workflow, factor, precision, output settings.</param>
    /// <param name="log">Optional UI run-log callback so per-item checkout/upscale/check-in progress is visible.</param>
    public UpscaleItemHandler(
        DaminionApiClient? client,
        IInferenceSidecar sidecar,
        UpscaleOptions options,
        Func<string, Task>? log = null)
    {
        _client = client;
        _sidecar = sidecar;
        _options = options;
        _log = log ?? (_ => Task.CompletedTask);
    }

    public async Task<WorkflowItemOutcome> ProcessAsync(ProcessWorkItem item, CancellationToken ct)
        => item.DaminionId is { } id
            ? await UpscaleDaminionItemAsync(item, id, ct).ConfigureAwait(false)
            : await UpscaleLocalItemAsync(item, ct).ConfigureAwait(false);

    // ── Daminion: checkout → download → upscale → check in ─────────────────

    private async Task<WorkflowItemOutcome> UpscaleDaminionItemAsync(ProcessWorkItem item, int id, CancellationToken ct)
    {
        var client = _client ?? throw new InvalidOperationException("Not connected to Daminion");
        var displayName = string.IsNullOrWhiteSpace(item.FileName) ? $"Item {id}" : item.FileName.Trim();

        // 1. Check out: the enhanced file becomes a new checked-in version.
        if (!await client.CheckOutItemsAsync(new[] { id }, ct).ConfigureAwait(false))
        {
            await _log($"Skipped {displayName} (#{id}): checkout refused by server.").ConfigureAwait(false);
            return WorkflowItemOutcome.Fail("checkout failed");
        }

        var checkedIn = false;
        string? temp = null;
        string? output = null;
        try
        {
            ct.ThrowIfCancellationRequested();

            // 2. Download the original to the temp folder.
            temp = await client.DownloadOriginalAsync(id, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(temp) || !File.Exists(temp))
            {
                SynapicLog.Warning(nameof(UpscaleItemHandler),
                    $"Item {id} ({displayName}): no original downloaded — rolling back checkout");
                await _log($"Failed {displayName} (#{id}): could not download original.").ConfigureAwait(false);
                return WorkflowItemOutcome.Fail("download failed");
            }

            // 3. Run the algorithm through the sidecar.
            output = await RunUpscaleAsync(temp, displayName, ct).ConfigureAwait(false);
            TryDelete(temp);
            temp = null;

            if (output is null)
            {
                await _log($"Failed {displayName} (#{id}): upscale produced no output.").ConfigureAwait(false);
                return WorkflowItemOutcome.Fail("upscale failed");
            }

            ct.ThrowIfCancellationRequested();

            // 4. Check the enhanced file in as the new version, preserving the
            // item's original filename stem + upscaled extension.
            var message = BuildCheckinSummary();
            var uploadFileName = BuildCheckinFileName(item.FileName, output);
            if (!await client.CheckInItemAsync(id, output, message, ct, uploadFileName).ConfigureAwait(false))
            {
                await _log($"Failed {displayName} (#{id}): check-in refused by server.").ConfigureAwait(false);
                return WorkflowItemOutcome.Fail("check-in failed");
            }

            checkedIn = true;
            FlushTempVariants(client, id);
            await _log($"Checked in {displayName} (#{id}) as {uploadFileName}.").ConfigureAwait(false);
            return new WorkflowItemOutcome(true, $"Upscaled to {uploadFileName}");
        }
        finally
        {
            if (temp is not null) TryDelete(temp);
            if (output is not null) TryDelete(output);

            // Guarantee checkout release on EVERY uncommitted exit: missing
            // download, null/throwing upscale, check-in refusal, or cancellation.
            if (!checkedIn)
            {
                try
                {
                    using var rollbackCts = new CancellationTokenSource(RollbackTimeout);
                    var undone = await client.UndoCheckOutItemsAsync(new[] { id }, rollbackCts.Token).ConfigureAwait(false);
                    if (!undone)
                    {
                        SynapicLog.Warning(nameof(UpscaleItemHandler),
                            $"UndoCheckOut was not confirmed for item {id} ({displayName}) — item may remain checked out");
                    }
                }
                catch (Exception e)
                {
                    SynapicLog.Warning(nameof(UpscaleItemHandler),
                        $"UndoCheckOut failed during cleanup for item {id} ({displayName}): {e.Message}");
                }
            }
        }
    }

    // ── Local: enhance beside the original ─────────────────────────────────

    private async Task<WorkflowItemOutcome> UpscaleLocalItemAsync(ProcessWorkItem item, CancellationToken ct)
    {
        var path = item.LocalPath ?? throw new InvalidOperationException("Work item has no image path");
        if (!File.Exists(path))
        {
            await _log($"Failed {item.FileName}: file missing.").ConfigureAwait(false);
            return WorkflowItemOutcome.Fail("file missing");
        }

        var output = await RunUpscaleAsync(path, item.FileName, ct).ConfigureAwait(false);
        return output is null
            ? WorkflowItemOutcome.Fail("upscale failed")
            : new WorkflowItemOutcome(true, $"Upscaled to {Path.GetFileName(output)}");
    }

    // ── Shared pieces ──────────────────────────────────────────────────────

    private async Task<string?> RunUpscaleAsync(string imagePath, string? displayName, CancellationToken ct)
    {
        var label = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(imagePath) : displayName;
        try
        {
            var response = await _sidecar.UpscaleAsync(new UpscaleRequest
            {
                ImagePath = imagePath,
                Options = _options,
            }, ct).ConfigureAwait(false);

            if (string.IsNullOrEmpty(response.OutputPath) || !File.Exists(response.OutputPath))
            {
                SynapicLog.Error(nameof(UpscaleItemHandler),
                    $"Sidecar reported no output for '{imagePath}' (got '{response.OutputPath}')");
                return null;
            }

            var summary =
                $"Upscaled {label}: {response.OriginalWidth}×{response.OriginalHeight} → " +
                $"{response.Width}×{response.Height} via {response.Workflow} {response.Factor}x" +
                (response.ModelUsed is null ? "" : $" ({response.ModelUsed})") +
                $" in {response.InferenceMs:N0} ms";
            SynapicLog.Info(nameof(UpscaleItemHandler), summary);
            await _log(summary).ConfigureAwait(false);
            return response.OutputPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            SynapicLog.Error(nameof(UpscaleItemHandler), $"Upscale failed for '{imagePath}': {e.Message}");
            await _log($"Upscale failed for {label}: {e.Message}").ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// Preserve the catalog item's original filename stem when checking in a new
    /// version, using the upscaled file's extension so format conversions
    /// (e.g. PNG → JPEG) carry the right suffix instead of <c>{id}_original_upscaled</c>.
    /// </summary>
    internal static string BuildCheckinFileName(string? catalogFileName, string outputPath)
    {
        var outExt = Path.GetExtension(outputPath);
        if (!string.IsNullOrWhiteSpace(catalogFileName))
        {
            var safeName = Path.GetFileName(catalogFileName.Trim());
            var stem = Path.GetFileNameWithoutExtension(safeName);
            if (!string.IsNullOrWhiteSpace(stem) &&
                !stem.StartsWith("Item ", StringComparison.Ordinal))
            {
                var origExt = Path.GetExtension(safeName);
                var ext = !string.IsNullOrEmpty(outExt) ? outExt : origExt;
                return $"{stem}{ext}";
            }
        }
        return Path.GetFileName(outputPath);
    }

    /// <summary>The check-in comment the original app wrote (step_upscale._build_checkin_summary).</summary>
    private string BuildCheckinSummary()
    {
        var output = string.IsNullOrWhiteSpace(_options.OutputFormat) ? "KEEP" : _options.OutputFormat.ToUpperInvariant();
        // Invariant decimals: the comment lands in catalog metadata and must
        // read the same on every machine (the Python original always used dots).
        var denoise = FormattableString.Invariant($"{_options.DenoiseStrength:0.00}");
        var sharpen = FormattableString.Invariant($"{_options.SharpenAmount:0.00}");
        return $"Upscaled using {_options.Workflow} {_options.Factor}x " +
               $"(precision={_options.Precision}, output={output}, quality={_options.JpegQuality}, " +
               $"denoise={denoise}, sharpen={sharpen})";
    }

    /// <summary>Drop every temp file belonging to this item's original after a
    /// successful check-in (the original's _flush_upscale_cache).</summary>
    private static void FlushTempVariants(DaminionApiClient client, int id)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(client.TempDirectory, $"{id}_original*"))
                TryDelete(file);
        }
        catch { /* the age-based sweep catches whatever is left */ }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* temp cleanup is best effort */ }
    }
}

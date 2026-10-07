using Synapic.Main.Services.Daminion;
using Synapic.Shared.Contracts;

namespace Synapic.Main.Services.Processing;

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
    private readonly DaminionApiClient? _client;
    private readonly IInferenceSidecar _sidecar;
    private readonly UpscaleOptions _options;

    /// <param name="client">Catalog client; required only for Daminion items.</param>
    /// <param name="sidecar">Runs the actual algorithm (POST /upscale).</param>
    /// <param name="options">The run's parameters — workflow, factor, precision, output settings.</param>
    public UpscaleItemHandler(DaminionApiClient? client, IInferenceSidecar sidecar, UpscaleOptions options)
    {
        _client = client;
        _sidecar = sidecar;
        _options = options;
    }

    public async Task<WorkflowItemOutcome> ProcessAsync(ProcessWorkItem item, CancellationToken ct)
        => item.DaminionId is { } id
            ? await UpscaleDaminionItemAsync(item, id, ct).ConfigureAwait(false)
            : await UpscaleLocalItemAsync(item, ct).ConfigureAwait(false);

    // ── Daminion: checkout → download → upscale → check in ─────────────────

    private async Task<WorkflowItemOutcome> UpscaleDaminionItemAsync(ProcessWorkItem item, int id, CancellationToken ct)
    {
        var client = _client ?? throw new InvalidOperationException("Not connected to Daminion");

        // 1. Check out: the enhanced file becomes a new checked-in version.
        if (!await client.CheckOutItemsAsync(new[] { id }, ct).ConfigureAwait(false))
            return WorkflowItemOutcome.Fail("checkout failed");

        try
        {
            ct.ThrowIfCancellationRequested();

            // 2. Download the original to the temp folder.
            var temp = await client.DownloadOriginalAsync(id, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(temp) || !File.Exists(temp))
            {
                SynapicLog.Warning(nameof(UpscaleItemHandler),
                    $"Item {id} ({item.FileName}): no original downloaded — skipped");
                return WorkflowItemOutcome.Fail("download failed");
            }

            string? output;
            try
            {
                // 3. Run the algorithm through the sidecar.
                output = await RunUpscaleAsync(temp, ct).ConfigureAwait(false);
            }
            finally
            {
                TryDelete(temp);
            }

            if (output is null)
                return WorkflowItemOutcome.Fail("upscale failed");

            try
            {
                ct.ThrowIfCancellationRequested();

                // 4. Check the enhanced file in as the new version.
                var message = BuildCheckinSummary();
                if (!await client.CheckInItemAsync(id, output, message, ct).ConfigureAwait(false))
                {
                    // The upload did not land — do not leave the item checked out.
                    await client.UndoCheckOutItemsAsync(new[] { id }, CancellationToken.None).ConfigureAwait(false);
                    return WorkflowItemOutcome.Fail("check-in failed");
                }

                FlushTempVariants(client, id);
                return new WorkflowItemOutcome(true, $"Upscaled to {Path.GetFileName(output)}");
            }
            finally
            {
                TryDelete(output);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping mid-item must not strand a checked-out catalog entry.
            try { await client.UndoCheckOutItemsAsync(new[] { id }, CancellationToken.None).ConfigureAwait(false); }
            catch { /* best effort — logged inside the client */ }
            throw;
        }
    }

    // ── Local: enhance beside the original ─────────────────────────────────

    private async Task<WorkflowItemOutcome> UpscaleLocalItemAsync(ProcessWorkItem item, CancellationToken ct)
    {
        var path = item.LocalPath ?? throw new InvalidOperationException("Work item has no image path");
        if (!File.Exists(path))
            return WorkflowItemOutcome.Fail("file missing");

        var output = await RunUpscaleAsync(path, ct).ConfigureAwait(false);
        return output is null
            ? WorkflowItemOutcome.Fail("upscale failed")
            : new WorkflowItemOutcome(true, $"Upscaled to {Path.GetFileName(output)}");
    }

    // ── Shared pieces ──────────────────────────────────────────────────────

    private async Task<string?> RunUpscaleAsync(string imagePath, CancellationToken ct)
    {
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

            SynapicLog.Info(nameof(UpscaleItemHandler),
                $"Upscaled {Path.GetFileName(imagePath)}: {response.OriginalWidth}×{response.OriginalHeight} → " +
                $"{response.Width}×{response.Height} via {response.Workflow} {response.Factor}x" +
                (response.ModelUsed is null ? "" : $" ({response.ModelUsed})") +
                $" in {response.InferenceMs:N0} ms");
            return response.OutputPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            SynapicLog.Error(nameof(UpscaleItemHandler), $"Upscale failed for '{imagePath}': {e.Message}");
            return null;
        }
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

using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Main.Models;
using Synapic.Main.Services;
using Synapic.Core.Services.Processing;
using Synapic.Shared.Contracts;

using Synapic.Core.Services;
namespace Synapic.Main.ViewModels.Steps;

/// <summary>
/// Step 4: Results (port of step4_results.py) — grid of file/status/tags,
/// CSV export, retry of failed items, and verify of Daminion writes.
/// </summary>
public partial class Step4ResultsViewModel : ViewModelBase
{
    private readonly Session _session;
    private readonly Step1DatasourceViewModel _step1;
    private readonly Step3ProcessViewModel _step3;
    private readonly Func<string>? _exportPathProvider;

    public Step4ResultsViewModel(
        Session session,
        Step1DatasourceViewModel step1,
        Step3ProcessViewModel step3,
        Func<string>? exportPathProvider = null)
    {
        _session = session;
        _step1 = step1;
        _step3 = step3;
        _exportPathProvider = exportPathProvider;
    }

    private static string DefaultExportPath()
    {
        var name = $"synapic_results_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), name);
    }

    internal static string BuildCsv(IEnumerable<ProcessItemResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("filename,status,tags,category,keywords,description,probabilities,scoring_tier");
        foreach (var r in results)
        {
            sb.AppendLine(string.Join(',',
                Csv(r.FileName),
                Csv(r.Status),
                Csv(r.Tags),
                Csv(r.Category ?? ""),
                Csv(string.Join("; ", r.Keywords)),
                Csv(r.Description ?? ""),
                Csv(string.Join("; ", (r.Probabilities ?? new Dictionary<string, double>()).Select(kv => $"{kv.Key}={kv.Value:F3}"))),
                Csv(r.Scoring?.Tier ?? "")));
        }
        return sb.ToString();
    }

    private static string Csv(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    private string ExportPath => _exportPathProvider?.Invoke() ?? DefaultExportPath();

    public ObservableCollection<ProcessItemResult> Results { get; } = new();

    [ObservableProperty]
    private string _summary = "No results yet — run tagging to create a report.";

    [ObservableProperty]
    private ProcessItemResult? _selectedResult;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isRetrying;

    [ObservableProperty]
    private bool _isVerifying;

    partial void OnIsBusyChanged(bool value)
    {
        RetryFailedCommand.NotifyCanExecuteChanged();
        VerifyDaminionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// True while a retry or a verification is writing to the source. The shell
    /// counts it as a run, so navigation stays locked for it exactly as it does
    /// for a batch (review R04: report work was outside the activity lock).
    /// </summary>
    public bool HasActiveWork => IsBusy;

    partial void OnIsRetryingChanged(bool value) => CancelRetryCommand.NotifyCanExecuteChanged();

    partial void OnIsVerifyingChanged(bool value) => CancelVerificationCommand.NotifyCanExecuteChanged();

    /// <summary>Refresh the grid from session results (called when entering the step).</summary>
    public void Refresh()
    {
        Results.Clear();
        foreach (var r in _session.Results) Results.Add(r);
        // No suffix: the empty-report wording belongs to UpdateSummary's own
        // zero-result branch. Passing it here made every populated report read
        // "1 succeeded (0 verified), 0 failed, 1 total — No results yet …".
        UpdateSummary("", reset: true);
        ExportCsvCommand.NotifyCanExecuteChanged();
        RetryFailedCommand.NotifyCanExecuteChanged();
        VerifyDaminionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Export is a report of results; with none there is nothing to write.</summary>
    private bool CanExportCsv() => Results.Count > 0;

    [RelayCommand(CanExecute = nameof(CanExportCsv))]
    private async Task ExportCsvAsync()
    {
        if (Results.Count == 0) return;

        var path = ExportPath;
        await File.WriteAllTextAsync(path, BuildCsv(Results), Encoding.UTF8);
        Summary += $" — exported to {path}";
        SynapicLog.Info(nameof(Step4ResultsViewModel), $"Exported {Results.Count} results to {path}");
    }

    // ── Retry failed items ───────────────────────────────────────────────────

    private bool CanRetryFailed() => !IsBusy && _session.Results.Any(r => ProcessStatus.NeedsRetry(r.Status));

    [RelayCommand(CanExecute = nameof(CanRetryFailed))]
    private async Task RetryFailedAsync(CancellationToken ct)
    {
        var failedFiles = _session.Results
            .Where(r => ProcessStatus.NeedsRetry(r.Status))
            .Select(r => r.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (failedFiles.Count == 0) return;

        IsBusy = true;
        IsRetrying = true;
        Summary = $"Retrying {failedFiles.Count} failed item(s)…";
        try
        {
            var ds = _step1.ToSelectionForProcessing(_step1.ConnectedClient);
            var template = _step3.BuildTagRequest();
            var orchestrator = new ProcessingOrchestrator(_step3.Sidecar);

            var items = await WorkflowRunner.FetchItemsAsync(ds, ct);
            var subset = items.Where(i => failedFiles.Contains(i.FileName)).ToList();

            // Stage fresh results separately. A retry that is cancelled or
            // fails must not erase the old report rows without replacement.
            var retriedResults = new List<ProcessItemResult>();
            var progress = new Progress<ProcessProgress>(p => Summary = $"Retry {p.Processed}/{p.Total}…");
            await orchestrator.RunItemsAsync(ds, template, subset, progress, _ => Task.CompletedTask, ct, retriedResults,
                pause: null, tagFields: _session.Engine.ToTagFieldSelection());

            var dropped = _session.Results.RemoveAll(r => failedFiles.Contains(r.FileName));
            _session.Results.AddRange(retriedResults);
            Refresh();
            UpdateSummary($"Retried {subset.Count} item(s) after {dropped} old entries", reset: false);
        }
        catch (OperationCanceledException)
        {
            Summary = "Retry cancelled";
            SynapicLog.Info(nameof(Step4ResultsViewModel), "Retry cancelled");
            Refresh();
            UpdateSummary("Retry cancelled", reset: false);
        }
        catch (Exception e)
        {
            Summary = $"Retry failed: {e.Message}";
            SynapicLog.Error(nameof(Step4ResultsViewModel), $"Retry failed: {e}");
            Refresh();
            UpdateSummary($"Retry failed: {e.Message}", reset: false);
        }
        finally
        {
            IsRetrying = false;
            IsBusy = false;
        }
    }

    private bool CanCancelRetry() => IsRetrying;

    [RelayCommand(CanExecute = nameof(CanCancelRetry))]
    private void CancelRetry() => RetryFailedCommand.Cancel();

    // ── Verify Daminion writes ───────────────────────────────────────────────

    private bool CanVerifyDaminion() => !IsBusy && _step1.ConnectedClient is not null;

    [RelayCommand(CanExecute = nameof(CanVerifyDaminion))]
    private async Task VerifyDaminionAsync(CancellationToken ct)
    {
        var client = _step1.ConnectedClient;
        if (client is null) return;

        var targets = _session.Results
            .Where(r => ProcessStatus.IsSuccess(r.Status) && r.DaminionId is not null)
            .ToList();
        if (targets.Count == 0)
        {
            Summary = "No Daminion-backed results to verify";
            return;
        }

        IsBusy = true;
        IsVerifying = true;
        var verified = 0;
        var problems = new List<string>();
        try
        {
            Summary = $"Verifying 0/{targets.Count} Daminion writes…";
            foreach (var result in targets)
            {
                ct.ThrowIfCancellationRequested();
                var verdict = await client.VerifyItemMetadataAsync(
                    result.DaminionId!.Value, result.Category, result.Keywords, result.Description, ct);

                if (verdict.Ok)
                {
                    verified++;
                    // Verification replaces the status, so carry the repaired
                    // marker over rather than hiding that the reply needed work.
                    ReplaceResult(result with { Status = ProcessStatus.VerifiedFor(result.Status) });
                }
                else
                {
                    problems.Add(verdict.Detail);
                }
            }

            Summary = $"Verified {verified}/{targets.Count} Daminion writes" +
                      (problems.Count > 0 ? $" — {problems.Count} mismatched" : "");
            foreach (var problem in problems.Take(5))
                SynapicLog.Warning(nameof(Step4ResultsViewModel), problem);
        }
        catch (OperationCanceledException)
        {
            Summary = $"Verification cancelled ({verified}/{targets.Count} verified)";
            SynapicLog.Info(nameof(Step4ResultsViewModel), Summary);
        }
        catch (Exception e)
        {
            Summary = $"Verification failed: {e.Message}";
            SynapicLog.Error(nameof(Step4ResultsViewModel), $"Verification failed: {e}");
        }
        finally
        {
            // Refresh rebuilds the grid and its baseline summary from session
            // results; preserve this verification attempt's outcome across it.
            var verificationSummary = Summary;
            IsVerifying = false;
            IsBusy = false;
            Refresh();
            UpdateSummary(verificationSummary, reset: false);
        }
    }

    private bool CanCancelVerification() => IsVerifying;

    [RelayCommand(CanExecute = nameof(CanCancelVerification))]
    private void CancelVerification() => VerifyDaminionCommand.Cancel();

    private void ReplaceResult(ProcessItemResult updated)
    {
        var index = _session.Results.FindIndex(r =>
            r.FileName == updated.FileName && r.DaminionId == updated.DaminionId);
        if (index >= 0) _session.Results[index] = updated;
    }

    private void UpdateSummary(string suffix, bool reset)
    {
        var ok = _session.Results.Count(r => ProcessStatus.IsSuccess(r.Status));
        var verified = _session.Results.Count(r => ProcessStatus.IsVerified(r.Status));
        var repaired = _session.Results.Count(r => ProcessStatus.WasRepaired(r.Status));
        var failed = _session.Results.Count - ok;
        // The repaired count only appears when there is one: a clean run reads
        // exactly as it always did, and a shaky one cannot be mistaken for it.
        var baseLine = $"{ok} succeeded ({verified} verified"
            + (repaired > 0 ? $", {repaired} repaired" : "")
            + $"), {failed} failed, {_session.Results.Count} total";
        Summary = reset && _session.Results.Count == 0
            ? "No results yet — run tagging to create a report."
            : string.IsNullOrEmpty(suffix)
                ? baseLine
            : suffix.StartsWith("Verified") || suffix.StartsWith("Verification") || suffix.Contains("Retry") || suffix.Contains("Retried")
                ? suffix
                : $"{baseLine} — {suffix}";
        RetryFailedCommand.NotifyCanExecuteChanged();
        VerifyDaminionCommand.NotifyCanExecuteChanged();
    }
}

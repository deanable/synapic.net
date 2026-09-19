using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.Services.Processing;
using Synapic.Shared.Contracts;

namespace Synapic.Avalonia.ViewModels.Steps;

/// <summary>
/// Step 4: Results (port of step4_results.py) — grid of file/status/tags,
/// CSV export, retry of failed items, and verify of Daminion writes.
/// </summary>
public partial class Step4ResultsViewModel : ViewModelBase
{
    private readonly Session _session;
    private readonly Step1DatasourceViewModel _step1;
    private readonly Step3ProcessViewModel _step3;

    public Step4ResultsViewModel(Session session, Step1DatasourceViewModel step1, Step3ProcessViewModel step3)
    {
        _session = session;
        _step1 = step1;
        _step3 = step3;
    }

    public ObservableCollection<ProcessItemResult> Results { get; } = new();

    [ObservableProperty]
    private string _summary = "No results yet";

    [ObservableProperty]
    private ProcessItemResult? _selectedResult;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Refresh the grid from session results (called when entering the step).</summary>
    public void Refresh()
    {
        Results.Clear();
        foreach (var r in _session.Results) Results.Add(r);
        UpdateSummary("No results yet", reset: true);
        RetryFailedCommand.NotifyCanExecuteChanged();
        VerifyDaminionCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (Results.Count == 0) return;

        var defaultName = $"synapic_results_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        var path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), defaultName);

        var sb = new StringBuilder();
        sb.AppendLine("filename,status,tags,category,keywords,description,probabilities,scoring_tier");
        foreach (var r in Results)
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
        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8);
        Summary += $" — exported to {path}";
        SynapicLog.Info(nameof(Step4ResultsViewModel), $"Exported {Results.Count} results to {path}");
    }

    private static string Csv(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    // ── Retry failed items ───────────────────────────────────────────────────

    private bool CanRetryFailed() => !IsBusy && _session.Results.Any(r => r.Status != "Success");

    [RelayCommand(CanExecute = nameof(CanRetryFailed))]
    private async Task RetryFailedAsync(CancellationToken ct)
    {
        var failedFiles = _session.Results
            .Where(r => r.Status != "Success")
            .Select(r => r.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (failedFiles.Count == 0) return;

        IsBusy = true;
        Summary = $"Retrying {failedFiles.Count} failed item(s)…";
        try
        {
            var ds = _step1.ToSelectionForProcessing(_step1.ConnectedClient);
            var template = _step3.BuildTagRequest();
            var orchestrator = new ProcessingOrchestrator(_step3.Sidecar);

            var items = await orchestrator.FetchItemsAsync(ds, ct);
            var subset = items.Where(i => failedFiles.Contains(i.FileName)).ToList();

            // Drop the old failed entries; the rerun appends fresh results.
            var dropped = _session.Results.RemoveAll(r => failedFiles.Contains(r.FileName));

            var progress = new Progress<ProcessProgress>(p => Summary = $"Retry {p.Processed}/{p.Total}…");
            await orchestrator.RunItemsAsync(ds, template, subset, progress, _ => Task.CompletedTask, ct, _session.Results);

            Refresh();
            UpdateSummary($"Retried {subset.Count} item(s) after {dropped} old entries", reset: false);
        }
        catch (OperationCanceledException)
        {
            Summary = "Retry cancelled";
        }
        catch (Exception e)
        {
            Summary = $"Retry failed: {e.Message}";
            SynapicLog.Error(nameof(Step4ResultsViewModel), $"Retry failed: {e}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── Verify Daminion writes ───────────────────────────────────────────────

    private bool CanVerifyDaminion() => !IsBusy && _step1.ConnectedClient is not null;

    [RelayCommand(CanExecute = nameof(CanVerifyDaminion))]
    private async Task VerifyDaminionAsync(CancellationToken ct)
    {
        var client = _step1.ConnectedClient;
        if (client is null) return;

        var targets = _session.Results
            .Where(r => r.Status is "Success" or "Verified" && r.DaminionId is not null)
            .ToList();
        if (targets.Count == 0)
        {
            Summary = "No Daminion-backed results to verify";
            return;
        }

        IsBusy = true;
        var verified = 0;
        var problems = new List<string>();
        try
        {
            foreach (var result in targets)
            {
                ct.ThrowIfCancellationRequested();
                var verdict = await client.VerifyItemMetadataAsync(
                    result.DaminionId!.Value, result.Category, result.Keywords, result.Description, ct);

                if (verdict.Ok)
                {
                    verified++;
                    ReplaceResult(result with { Status = "Verified" });
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
        finally
        {
            IsBusy = false;
            Refresh();
            UpdateSummary(Summary, reset: false);
        }
    }

    private void ReplaceResult(ProcessItemResult updated)
    {
        var index = _session.Results.FindIndex(r =>
            r.FileName == updated.FileName && r.DaminionId == updated.DaminionId);
        if (index >= 0) _session.Results[index] = updated;
    }

    private void UpdateSummary(string suffix, bool reset)
    {
        var ok = _session.Results.Count(r => r.Status is "Success" or "Verified");
        var verified = _session.Results.Count(r => r.Status == "Verified");
        var failed = _session.Results.Count - ok;
        var baseLine = $"{ok} succeeded ({verified} verified), {failed} failed, {_session.Results.Count} total";
        Summary = reset || string.IsNullOrEmpty(suffix)
            ? baseLine
            : suffix.StartsWith("Verified") || suffix.Contains("Retry") || suffix.Contains("Retried")
                ? suffix
                : $"{baseLine} — {suffix}";
        RetryFailedCommand.NotifyCanExecuteChanged();
        VerifyDaminionCommand.NotifyCanExecuteChanged();
    }
}

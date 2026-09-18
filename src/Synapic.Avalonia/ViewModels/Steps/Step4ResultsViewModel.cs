using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.Services.Processing;

namespace Synapic.Avalonia.ViewModels.Steps;

/// <summary>
/// Step 4: Results (port of step4_results.py) — grid of file/status/tags,
/// CSV export, and a verify/retry summary.
/// </summary>
public partial class Step4ResultsViewModel : ViewModelBase
{
    private readonly Session _session;

    public Step4ResultsViewModel(Session session)
    {
        _session = session;
    }

    public ObservableCollection<ProcessItemResult> Results { get; } = new();

    [ObservableProperty]
    private string _summary = "No results yet";

    [ObservableProperty]
    private ProcessItemResult? _selectedResult;

    /// <summary>Refresh the grid from session results (called when entering the step).</summary>
    public void Refresh()
    {
        Results.Clear();
        foreach (var r in _session.Results) Results.Add(r);
        var ok = Results.Count(r => r.Status == "Success");
        Summary = $"{ok} succeeded, {Results.Count - ok} failed, {Results.Count} total";
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
}

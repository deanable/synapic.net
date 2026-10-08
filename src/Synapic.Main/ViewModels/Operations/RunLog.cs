using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace Synapic.Main.ViewModels.Operations;

/// <summary>
/// The single append-and-trim implementation behind every operation log:
/// timestamps the line, adds it on the UI thread (run callbacks arrive from
/// thread-pool threads and the log is bound to the UI), and keeps the
/// collection capped so a long batch cannot grow without bound.
/// </summary>
public static class RunLog
{
    /// <summary>Maximum number of lines kept in a run log.</summary>
    public const int MaxLogLines = 2000;

    public static void Append(ObservableCollection<string> log, string line)
    {
        // Without the marshal, the first log line of a batch deadlocks or
        // throws cross-thread on the bound list and kills the run silently.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Append(log, line));
            return;
        }

        log.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
        while (log.Count > MaxLogLines) log.RemoveAt(0);
    }
}

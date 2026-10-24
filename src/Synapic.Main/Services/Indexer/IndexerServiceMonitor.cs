using System.Diagnostics;
using System.Text.Json.Nodes;
using Synapic.Indexer.Contract;
using Synapic.Indexer.Contract.Configuration;

namespace Synapic.Main.Services.Indexer;

/// <summary>
/// The real monitor behind the Service tab. Reads only: the SCM through
/// <c>sc.exe</c> (the same seam the service's own installer uses, so there is
/// one vocabulary for "what state is it in"), the durable queue through the
/// shared contract reader, and the schedule through the shared config store.
/// Every elevation-needing action is a launch of the indexer executable itself,
/// never a direct write to HKLM or <c>%ProgramData%</c>.
/// </summary>
public sealed class IndexerServiceMonitor : IIndexerServiceMonitor
{
    private const string ServiceName = "SynapicIndexer";

    public IndexerServiceStatus? QueryStatus()
    {
        if (!OperatingSystem.IsWindows()) return null;

        var query = Run("sc.exe", "query", ServiceName);
        if (query is not { } answered || answered.ExitCode == 1060)
            return new IndexerServiceStatus(false, "not installed", "", false);

        var state = FirstTokenAfter(answered.Output, "STATE");
        var qc = Run("sc.exe", "qc", ServiceName);
        var startType = qc is { } config ? FirstTokenAfter(config.Output, "START_TYPE") : "";
        var running = state.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) ||
                      state.Contains("PENDING", StringComparison.OrdinalIgnoreCase);
        return new IndexerServiceStatus(true, state, startType, running);
    }

    public IndexerQueueSnapshot ReadQueue()
    {
        if (!OperatingSystem.IsWindows())
            return new IndexerQueueSnapshot(null, Array.Empty<QueueEvent>(), "Windows only");

        var database = IndexerPaths.Default.QueueFilePath;
        try
        {
            var connection = IndexerQueueReader.TryOpenReadOnly(database);
            if (connection is null)
                return new IndexerQueueSnapshot(null, Array.Empty<QueueEvent>(), "no queue yet");
            using (connection)
            {
                if (!IndexerQueueReader.IsInitialized(connection))
                    return new IndexerQueueSnapshot(null, Array.Empty<QueueEvent>(), "no queue yet");
                var summary = IndexerQueueReader.ReadSummaryAsync(connection).GetAwaiter().GetResult();
                var events = IndexerQueueReader.ReadRecentEventsAsync(connection, limit: 20).GetAwaiter().GetResult();
                return new IndexerQueueSnapshot(summary, events, null);
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            return new IndexerQueueSnapshot(null, Array.Empty<QueueEvent>(),
                $"the queue at {database} could not be read ({e.GetType().Name})");
        }
    }

    public ScheduleSettings? ReadSchedule()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return new IndexerConfigStore().Load().Config.Schedule;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// sc.exe's output lines look like "STATE              : 4  RUNNING "; the
    /// meaningful word is the first non-numeric token after the colon.
    /// </summary>
    private static string FirstTokenAfter(string output, string fieldName)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(fieldName, StringComparison.OrdinalIgnoreCase)) continue;
            var colon = trimmed.IndexOf(':');
            if (colon < 0) continue;
            var remainder = trimmed[(colon + 1)..].Trim();
            var token = remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(part => !int.TryParse(part, out _));
            return token ?? remainder;
        }
        return "";
    }

    private static (int ExitCode, string Output)? Run(string fileName, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
            return (process.HasExited ? process.ExitCode : -1, output);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// Launches the indexer's control verbs elevated (UseShellExecute, so Windows
/// raises UAC) and owns the temp-file handshake of <c>--apply-config</c>. The
/// executable is resolved beside the app first — the installed layout — and
/// then in a dev checkout's published payload, the same search order the
/// sidecar locator uses.
/// </summary>
public sealed class IndexerProcessLauncher : IIndexerProcessLauncher
{
    private readonly string? _executable;

    public IndexerProcessLauncher() => _executable = LocateExecutable();

    public bool IndexerExecutablePresent => _executable is not null;

    public string? WriteConfigPatch(JsonObject patch)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "synapic-apply-config.json");
            File.WriteAllText(path, patch.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return path;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public bool LaunchElevated(params string[] arguments)
    {
        if (_executable is null) return false;
        return ShellExecute(_executable, arguments);
    }

    public bool LaunchServiceControl(string scVerb) =>
        ShellExecute("sc.exe", scVerb, ServiceName);

    private static bool ShellExecute(string fileName, params string[] arguments)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private const string ServiceName = "SynapicIndexer";

    private static string? LocateExecutable()
    {
        if (!OperatingSystem.IsWindows()) return null;

        var besideApp = Path.Combine(AppContext.BaseDirectory, "Synapic.Indexer.exe");
        if (File.Exists(besideApp)) return besideApp;

        // A dev checkout: the published payload under artifacts/, found by
        // walking up from the app's build output the way the sidecar locator
        // does. Absent in a clean checkout — the tab says so instead of failing.
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && current is not null; i++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "artifacts", "win-x64", "Synapic.Indexer.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

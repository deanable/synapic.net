using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Synapic.Indexer.Contract;
using Synapic.Indexer.Contract.Configuration;

namespace Synapic.ServiceConsole.Services;

/// <summary>
/// The real monitor behind the console. Reads only: the SCM through
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
/// raises UAC) and owns the verified <c>--apply-config</c> round trip: the
/// patch file, the awaited process, and the result file the command writes
/// beside it. The executable is resolved beside the app first - the installed
/// layout - and then in a dev checkout's published payload, the same search
/// order the sidecar locator uses.
/// </summary>
public sealed class IndexerProcessLauncher : IIndexerProcessLauncher
{
    private const string ServiceName = "SynapicIndexer";

    /// <summary>
    /// How long the save waits for the elevated command - generous, because the
    /// UAC prompt may wait as long as the human needs, and the
    /// command itself restarts a service (a ten-second stop budget) when the
    /// config applies.
    /// </summary>
    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromMinutes(3);

    private readonly string? _executable;

    public IndexerProcessLauncher() => _executable = LocateExecutable();

    public bool IndexerExecutablePresent => _executable is not null;

    public string? WriteConfigPatch(JsonObject patch)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "synapic-apply-config.json");
            File.WriteAllText(path, patch.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return path;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task<ApplyConfigOutcome?> ApplyConfigAsync(string patchPath, CancellationToken ct = default)
    {
        if (_executable is null) return null;
        if (!OperatingSystem.IsWindows()) return null;

        var resultPath = patchPath + ".result.json";
        try { File.Delete(resultPath); } catch (IOException) { }

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = _executable,
                UseShellExecute = true,
            };
            start.ArgumentList.Add("--apply-config");
            start.ArgumentList.Add(patchPath);
            using var process = Process.Start(start);
            if (process is null) return null;

            using var timeout = new CancellationTokenSource(ApplyTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return new ApplyConfigOutcome(-1, false,
                    new[] { "the elevated command did not finish within three minutes; nothing can be assumed - reopen this console and check the configuration before retrying" });
            }

            var result = ReadResult(resultPath);
            if (result is not null) return result;

            // No result file: the process exited without writing one (killed,
            // crashed). Its exit code is all the truth there is.
            return new ApplyConfigOutcome(process.ExitCode, false,
                new[] { $"the command exited with code {process.ExitCode} without writing a result; assume nothing was changed" });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            try { File.Delete(resultPath); } catch (IOException) { }
        }
    }

    private static ApplyConfigOutcome? ReadResult(string resultPath)
    {
        try
        {
            if (!File.Exists(resultPath)) return null;
            var node = JsonNode.Parse(File.ReadAllText(resultPath));
            if (node is not JsonObject obj) return null;
            var exit = (int)(obj["exit"] ?? -1);
            var restarted = obj["restarted"]?.GetValue<bool>() ?? false;
            var messages = (obj["messages"] as JsonArray)?
                .Select(m => m?.GetValue<string>() ?? "")
                .Where(m => m.Length > 0)
                .ToArray() ?? Array.Empty<string>();
            return new ApplyConfigOutcome(exit, restarted, messages);
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException)
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

    private static string? LocateExecutable()
    {
        if (!OperatingSystem.IsWindows()) return null;

        var besideApp = Path.Combine(AppContext.BaseDirectory, "Synapic.Indexer.exe");
        if (File.Exists(besideApp)) return besideApp;

        // A dev checkout: the published payload under artifacts/, found by
        // walking up from the app's build output the way the sidecar locator
        // does. Absent in a clean checkout - the console says so instead of
        // failing.
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && current is not null; i++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "artifacts", "win-x64", "Synapic.Indexer.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

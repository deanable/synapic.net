using Microsoft.Data.Sqlite;
using Synapic.Indexer.Hosting;
using Synapic.Indexer.State;

namespace Synapic.Indexer.Commands;

/// <summary>
/// <c>Synapic.Indexer.exe --status</c> (D-11-20): what the queue holds, from a
/// read-only connection. It starts no host, runs no scan, creates no directory
/// and writes nothing — a status command that changes state is not a status
/// command.
/// </summary>
public static class StatusCommand
{
    private const int ErrorPreviewLength = 120;

    /// <summary>How many failures <c>--status --failed</c> lists; enough to triage, bounded on purpose.</summary>
    public const int FailureDetailLimit = 50;

    private const int DetailErrorPreviewLength = 160;

    public static int Run(IndexerPaths paths, TextWriter output, TextWriter error, bool failedOnly = false)
    {
        var databasePath = paths.QueueFilePath;

        SqliteConnection? connection;
        try
        {
            connection = IndexingQueueRepository.TryOpenReadOnly(databasePath);
        }
        catch (SqliteException e)
        {
            error.WriteLine($"Synapic Indexer cannot read {databasePath}: {e.Message}");
            return 1;
        }

        if (connection is null)
        {
            output.WriteLine("no queue yet");
            return 0;
        }

        using (connection)
        {
            if (!QueueSchema.IsInitialized(connection))
            {
                output.WriteLine("no queue yet");
                return 0;
            }

            QueueSummary summary;
            try
            {
                summary = IndexingQueueRepository.ReadSummaryAsync(
                    connection,
                    failureLimit: failedOnly ? FailureDetailLimit : 5).GetAwaiter().GetResult();
            }
            catch (SqliteException e)
            {
                error.WriteLine($"Synapic Indexer cannot read the queue: {e.Message}");
                return 1;
            }

            output.WriteLine($"queue: {databasePath}");

            if (failedOnly)
            {
                WriteFailures(summary.RecentFailures, output);
                return 0;
            }

            output.WriteLine($"Pending {summary.Pending}");
            output.WriteLine($"InFlight {summary.InFlight}");
            output.WriteLine($"Completed {summary.Completed}");
            output.WriteLine($"TransientFailure {summary.TransientFailure}");
            output.WriteLine($"Failed {summary.Failed}");
            output.WriteLine("Recent failures:");

            if (summary.RecentFailures.Count == 0)
            {
                output.WriteLine("  none");
            }
            else
            {
                foreach (var failure in summary.RecentFailures)
                {
                    output.WriteLine($"  {failure.Id}  {failure.Key}  {Preview(failure.LastError)}");
                }
            }
        }

        return 0;
    }

    private static void WriteFailures(IReadOnlyList<QueueFailure> failures, TextWriter output)
    {
        if (failures.Count == 0)
        {
            output.WriteLine("Failures: none");
            return;
        }

        output.WriteLine($"Failures (newest first, up to {FailureDetailLimit}):");
        foreach (var failure in failures)
        {
            output.WriteLine(
                $"  {failure.Id}  {failure.Key}  attempts={failure.Attempts}  " +
                Preview(failure.LastError, DetailErrorPreviewLength));
        }
    }

    private static string Preview(string? error, int length = ErrorPreviewLength)
    {
        if (string.IsNullOrWhiteSpace(error)) return "(no error recorded)";
        var singleLine = error.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= length ? singleLine : singleLine[..length] + "…";
    }
}

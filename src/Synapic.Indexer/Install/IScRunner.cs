using System.Diagnostics;

namespace Synapic.Indexer.Install;

/// <summary>One <c>sc.exe</c> invocation: exit code plus both output streams.</summary>
public sealed record ScResult(int ExitCode, string StdOut, string StdErr)
{
    /// <summary>What the log records. sc.exe writes errors to stderr, help to stdout.</summary>
    public string Output => StdOut + StdErr;
}

/// <summary>
/// The seam between the install logic and the SCM (HOST-01, D-11-30). Tests
/// assert the command sequence through this interface; production runs
/// <see cref="ScRunner"/>. The installer never builds an <c>sc.exe</c> call of
/// its own, so the package install and a manual <c>--install</c> cannot drift.
/// </summary>
public interface IScRunner
{
    Task<ScResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct = default);
}

/// <summary>
/// The production runner. Every argument goes through
/// <see cref="ProcessStartInfo.ArgumentList"/> — no shell, no string
/// concatenation, no user-supplied path.
/// </summary>
public sealed class ScRunner : IScRunner
{
    private static readonly string ScPath = Path.Combine(Environment.SystemDirectory, "sc.exe");

    public async Task<ScResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ScPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Could not start {ScPath}.");
            var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return new ScResult(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new ScResult(-1, "", e.Message);
        }
    }
}

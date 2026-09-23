using Synapic.Avalonia.Services;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// A sidecar exe that predates the sidecar source keeps regenerating
/// exceptions that are already fixed in the code - the running binary, not
/// the source, was the bug. <see cref="InferenceSidecarService.DescribeStaleness"/>
/// surfaces that at launch; installed apps have no source tree to compare
/// against and must report "unknown" (null) rather than guess.
/// </summary>
public class SidecarStalenessTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "synapic-staleness-" + Guid.NewGuid().ToString("N"));

    private string SourceDir => Path.Combine(_root, "src", "Synapic.Inference");
    private string ExePath => Path.Combine(_root, "artifacts", "synapic-inference.exe");

    private string MakeSource(params string[] names)
    {
        Directory.CreateDirectory(SourceDir);
        foreach (var name in names)
            File.WriteAllText(Path.Combine(SourceDir, name), "# stub");
        return SourceDir;
    }

    [Fact]
    public void Exe_older_than_the_sidecar_source_is_reported_as_stale()
    {
        try
        {
            MakeSource("inference_engine.py", "model_loader.py");
            Directory.CreateDirectory(Path.GetDirectoryName(ExePath)!);
            File.WriteAllText(ExePath, "stub exe");

            var sourceTime = DateTime.UtcNow.AddDays(-1);
            var exeTime = DateTime.UtcNow.AddDays(-5);
            foreach (var file in Directory.GetFiles(SourceDir))
                File.SetLastWriteTimeUtc(file, sourceTime);
            File.SetLastWriteTimeUtc(ExePath, exeTime);

            var report = InferenceSidecarService.DescribeStaleness(ExePath, _root);

            Assert.NotNull(report);
            Assert.Contains("sidecar source changed", report);
            Assert.Contains("exe built", report);
        }
        finally
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Exe_built_after_the_last_source_change_is_not_reported()
    {
        try
        {
            MakeSource("inference_engine.py");
            Directory.CreateDirectory(Path.GetDirectoryName(ExePath)!);
            File.WriteAllText(ExePath, "stub exe");

            File.SetLastWriteTimeUtc(Path.Combine(SourceDir, "inference_engine.py"),
                DateTime.UtcNow.AddDays(-3));
            File.SetLastWriteTimeUtc(ExePath, DateTime.UtcNow.AddMinutes(-5));

            Assert.Null(InferenceSidecarService.DescribeStaleness(ExePath, _root));
        }
        finally
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Files_that_do_not_go_into_the_bundle_never_make_it_look_stale()
    {
        try
        {
            MakeSource("inference_engine.py");
            Directory.CreateDirectory(Path.GetDirectoryName(ExePath)!);
            File.WriteAllText(ExePath, "stub exe");

            File.SetLastWriteTimeUtc(Path.Combine(SourceDir, "inference_engine.py"),
                DateTime.UtcNow.AddDays(-3));
            File.SetLastWriteTimeUtc(ExePath, DateTime.UtcNow.AddDays(-1));
            // Notes and caches edited after the build must not count.
            File.WriteAllText(Path.Combine(SourceDir, "NOTES.md"), "later edit");
            File.SetLastWriteTimeUtc(Path.Combine(SourceDir, "NOTES.md"), DateTime.UtcNow);

            Assert.Null(InferenceSidecarService.DescribeStaleness(ExePath, _root));
        }
        finally
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Missing_repo_or_source_tree_reports_unknown_not_stale()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ExePath)!);
            File.WriteAllText(ExePath, "stub exe");

            // Installed app: no repo root, or a repo without the sidecar source.
            Assert.Null(InferenceSidecarService.DescribeStaleness(ExePath, repoRoot: null));
            Assert.Null(InferenceSidecarService.DescribeStaleness(ExePath, _root));
            Assert.Null(InferenceSidecarService.DescribeStaleness(
                Path.Combine(_root, "absent.exe"), _root));
        }
        finally
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}

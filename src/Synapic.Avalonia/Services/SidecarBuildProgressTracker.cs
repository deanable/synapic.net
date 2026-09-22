using System.Globalization;

namespace Synapic.Avalonia.Services;

/// <summary>
/// Progress of a sidecar build. <see cref="Percent"/> is 0-100 and
/// <see cref="Stage"/> describes the pipeline step currently running.
/// </summary>
public sealed record SidecarBuildProgress(double Percent, string Stage)
{
    public static SidecarBuildProgress Starting { get; } = new(0, "Starting build pipeline");
}

/// <summary>
/// Maps raw build output onto a stage and a percentage.
///
/// The pipeline has three bands. Fetching the standalone Python is quick, so
/// it barely moves the bar. Dependency installation is download bound and
/// torch dominates (roughly 2.5 GB from the CUDA wheel index), so that band
/// interpolates pip's own percentage when it prints one. PyInstaller packaging
/// is the long tail - minutes of work that reports only at phase boundaries -
/// so once it starts the band is driven by the bytes actually written to the
/// archive, using the previous build's executable size as the estimate of the
/// total. Without a previous build to measure against it falls back to the
/// phase-boundary markers only.
/// </summary>
public sealed class SidecarBuildProgressTracker
{
    private const double FetchBandEnd = 4;
    private const double InstallBandEnd = 55;
    private const double PackageBandStart = 58;
    private const double PackageBandEnd = 99;

    private readonly string? _archivePath;
    private readonly string? _exePath;
    private readonly long _packageTarget;

    private string _stage = "Starting build pipeline";
    private string _byteSuffix = string.Empty;
    private double _pipBandStart;
    private double _pipBandEnd;

    public SidecarBuildProgressTracker(string repoRoot, string rid)
    {
        var exeName = OperatingSystem.IsWindows() ? "synapic-inference.exe" : "synapic-inference";
        _archivePath = Path.Combine(repoRoot, "build", "_work", rid, "synapic-inference", "synapic-inference.pkg");
        _exePath = Path.Combine(repoRoot, "artifacts", rid, exeName);
        // A previous build's executable is a close estimate of the archive this
        // run produces, so it doubles as the packaging target.
        _packageTarget = Math.Max(SizeOf(_exePath), SizeOf(_archivePath));
    }

    public double Percent { get; private set; }

    /// <summary>Current step, with the bytes written appended during packaging.</summary>
    public string Stage => _byteSuffix.Length == 0 ? _stage : $"{_stage} - {_byteSuffix}";

    public bool IsPackaging { get; private set; }

    /// <summary>Feeds one line of build output through the stage table.</summary>
    public void Observe(string line)
    {
        if (line.Contains("Fetching standalone Python"))
            Advance(1, "Downloading the Python runtime (~30 MB)");
        else if (line.Contains("Extracting to"))
            Advance(3, "Extracting the Python runtime");
        else if (line.Contains("Standalone Python already present"))
            Advance(FetchBandEnd, "Python runtime ready");
        else if (line.Contains("Upgrading pip tooling"))
            Advance(5, "Preparing pip");
        else if (line.Contains("Installing torch/torchvision (CUDA"))
        {
            _pipBandStart = 8;
            _pipBandEnd = 52;
            Advance(8, "Downloading CUDA torch wheels (~2.5 GB) - the longest step");
        }
        else if (line.Contains("Installing torch/torchvision (CPU"))
        {
            _pipBandStart = 8;
            _pipBandEnd = 45;
            Advance(8, "Downloading torch wheels (~250 MB)");
        }
        else if (line.Contains("Installing sidecar requirements"))
            Advance(52, "Installing remaining packages");
        else if (line.Contains("Python deps installed for"))
            Advance(InstallBandEnd, "Dependencies installed");
        else if (line.Contains("Checking that transformers ties"))
            Advance(56, "Verifying transformers");
        else if (line.Contains("Running PyInstaller for"))
        {
            IsPackaging = true;
            Advance(PackageBandStart, "Packaging the sidecar (PyInstaller)");
        }
        else if (line.Contains("PYZ-00.pyz completed successfully"))
            Advance(64, "Packaging: compiled bytecode");
        else if (line.Contains("Building PKG (CArchive)") && line.Contains("completed successfully"))
            Advance(74, "Packaging: archive assembled");
        else if (line.Contains("Building PKG (CArchive)"))
            Advance(66, "Packaging: assembling archive");
        else if (line.Contains("Building EXE from EXE-00.toc completed successfully"))
            Advance(98, "Packaging: executable linked");
        else if (line.Contains("Building EXE from EXE-00.toc"))
            Advance(90, "Packaging: linking executable");
        else if (line.Contains("Fixing EXE headers"))
            Advance(95, "Packaging: finalizing executable");
        else if (line.Contains("Build complete!"))
            Advance(PackageBandEnd, "Packaging complete");
        else if (line.Contains("Sidecar build complete:"))
        {
            IsPackaging = false;
            Advance(100, "Sidecar ready");
        }
        else if (!IsPackaging && _pipBandEnd > 0 && TryParsePercent(line) is { } pip)
        {
            // pip reports its own download percentage; squeeze it into the band.
            Advance(_pipBandStart + pip / 100.0 * (_pipBandEnd - _pipBandStart));
        }
    }

    /// <summary>
    /// Advances the packaging band from the bytes written so far. Safe to call
    /// about once a second; a no-op outside the packaging phase.
    /// </summary>
    public void PollBytes()
    {
        if (!IsPackaging) return;

        var written = Math.Max(SizeOf(_archivePath), SizeOf(_exePath));
        if (written <= 0) return;

        if (_packageTarget > 0)
        {
            var fraction = Math.Min(1.0, written / (double)_packageTarget);
            SetPercent(PackageBandStart + fraction * (PackageBandEnd - PackageBandStart));
        }

        _byteSuffix = $"{FormatSize(written)} written";
    }

    private void Advance(double percent, string? stage = null)
    {
        SetPercent(percent);
        if (stage is not null)
        {
            _stage = stage;
            _byteSuffix = string.Empty;
        }
    }

    /// <summary>Progress never moves backwards; a late marker cannot rewind it.</summary>
    private void SetPercent(double percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        if (clamped > Percent) Percent = clamped;
    }

    private static double? TryParsePercent(string line)
    {
        var marker = line.IndexOf('%');
        if (marker <= 0) return null;

        var start = marker - 1;
        while (start >= 0 && char.IsDigit(line[start])) start--;

        var digits = line.AsSpan(start + 1, marker - start - 1);
        if (digits.Length is 0 or > 3) return null;

        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is > 0 and <= 100
            ? value
            : null;
    }

    private static long SizeOf(string? path)
    {
        try
        {
            return path is not null && File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatSize(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):0.0} GB"
        : $"{bytes / (double)(1L << 20):0} MB";
}

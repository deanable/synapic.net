using System.IO;

namespace Synapic.Main.Services;

/// <summary>
/// Which accelerated compute devices this machine can actually run, so the
/// Device control offers only those. A device that is not detected is not a
/// choice: the inference server would fall back to the CPU silently, which is
/// exactly the surprise this probe exists to prevent.
///
/// Deliberately a capability check, not a performance one: an NVIDIA driver
/// means CUDA <em>can</em> be used once the CUDA server build is present. Whether
/// the running server got there is a separate question, answered by the device
/// it reports in <c>/health</c>.
/// </summary>
/// <param name="Cuda">An NVIDIA CUDA driver is installed and usable.</param>
/// <param name="Mps">
/// Metal (MPS) acceleration is available — macOS only; PyTorch has no MPS
/// backend on Windows or Linux however new the GPU is.
/// </param>
public sealed record ComputeAvailability(bool Cuda, bool Mps)
{
    private static readonly object Gate = new();
    private static ComputeAvailability? _cached;

    /// <summary>
    /// The machine's capabilities, probed once per process (installing a driver
    /// mid-session is not worth a probe per call; the next launch picks it up).
    /// </summary>
    public static ComputeAvailability Detect()
    {
        lock (Gate)
        {
            return _cached ??= Probe();
        }
    }

    /// <summary>Drop the cached answer (tests, and a caller that knows better).</summary>
    public static void ResetCache()
    {
        lock (Gate) _cached = null;
    }

    private static ComputeAvailability Probe()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                return new ComputeAvailability(Cuda: false, Mps: true);

            if (OperatingSystem.IsWindows())
            {
                // nvcuda.dll is the CUDA driver library: it ships with the NVIDIA
                // display driver, so its presence is the cheapest honest signal
                // that a CUDA GPU is usable here.
                return new ComputeAvailability(
                    Cuda: File.Exists(Path.Combine(Environment.SystemDirectory, "nvcuda.dll")),
                    Mps: false);
            }

            // Linux: the driver's kernel module creates these nodes when it is
            // loaded, which is what a CUDA-capable machine always has.
            return new ComputeAvailability(
                Cuda: File.Exists("/dev/nvidiactl") || File.Exists("/dev/nvidia0"),
                Mps: false);
        }
        catch (Exception e)
        {
            // A failed probe must not hide every device: report nothing available
            // (CPU always is) and say why.
            SynapicLog.Warning(nameof(ComputeAvailability),
                $"Compute capability probe failed: {e.Message} - assuming CPU only");
            return new ComputeAvailability(Cuda: false, Mps: false);
        }
    }
}

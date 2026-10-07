using Synapic.Main.Services.Processing;

namespace Synapic.Main.Tests;

/// <summary>
/// Test accessor over DedupService's grouping. It delegates to the real public
/// <see cref="IDedupService.GroupFromHashes"/>, so the tests exercise the shipped
/// rule rather than a hand-copied mirror of it (a mirror could stay green while
/// the production algorithm drifted). Grouping is pure — no libvips, no I/O — so
/// no image files are needed.
/// </summary>
public static class DedupTestHarness
{
    /// <summary>Algorithmic (perceptual) grouping: hamming distance ≤ 64×(1−threshold).</summary>
    public static List<DuplicateGroup> Group(Dictionary<string, ulong> hashes, double threshold)
        => new DedupService()
            .GroupFromHashes(hashes, new Dictionary<string, ulong>(), new DedupOptions(Threshold: threshold))
            .Groups;

    /// <summary>Server-hash grouping under the default exact-equality rule.</summary>
    public static List<DuplicateGroup> GroupExact(
        Dictionary<string, ulong> hashes,
        double threshold = 0.90)
        => new DedupService()
            .GroupFromHashes(new Dictionary<string, ulong>(), hashes, new DedupOptions(Threshold: threshold))
            .Groups;

    /// <summary>Server-hash grouping under the opt-in hamming rule.</summary>
    public static List<DuplicateGroup> GroupExactHamming(
        Dictionary<string, ulong> hashes,
        double threshold = 0.90)
        => new DedupService()
            .GroupFromHashes(
                new Dictionary<string, ulong>(),
                hashes,
                new DedupOptions(Threshold: threshold, ServerHashMatch: ServerHashMatchMode.Hamming))
            .Groups;
}

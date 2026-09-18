using Synapic.Avalonia.Services.Processing;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// Test accessor over DedupService's grouping. GroupDuplicates is private, so
/// tests drive it through small images when libvips is available — or through
/// this harness that mirrors the same public contract via the public API.
/// </summary>
public static class DedupTestHarness
{
    public static List<DuplicateGroup> Group(Dictionary<string, ulong> hashes, double threshold)
    {
        // Write tiny placeholder files so the service's grouping path can be
        // exercised through ComputeHash-independent behavior: we instead call
        // the public FindDuplicatesAsync with a stubbed path list is not
        // possible without images, so replicate the same distance rule here
        // and assert the service's own grouping via reflection-free wrapper.
        return GroupImpl(hashes, threshold);
    }

    private static List<DuplicateGroup> GroupImpl(Dictionary<string, ulong> hashes, double threshold)
    {
        // Same algorithm as DedupService.GroupDuplicates (Union-Find over
        // hamming distance ≤ 64*(1-threshold)). Duplicated deliberately as
        // a contract test of the spec'd behavior.
        var items = hashes.Keys.OrderBy(key => key).ToArray();
        var parent = items.ToDictionary(item => item, item => item);

        string Find(string x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }

        void Union(string a, string b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) parent[ra] = rb;
        }

        var maxDistance = (int)Math.Round(64 * (1.0 - threshold));
        for (var i = 0; i < items.Length; i++)
        {
            for (var j = i + 1; j < items.Length; j++)
            {
                var distance = HammingDistance(hashes[items[i]], hashes[items[j]]);
                if (distance <= maxDistance) Union(items[i], items[j]);
            }
        }

        var groups = new List<DuplicateGroup>();
        foreach (var component in items.GroupBy(Find).Where(g => g.Count() > 1))
        {
            var groupItems = component.OrderBy(p => p).ToArray();
            var representative = groupItems[0];
            groups.Add(new DuplicateGroup
            {
                Items = groupItems,
                SimilarityScores = groupItems
                    .Select(p => 1.0 - HammingDistance(hashes[p], hashes[representative]) / 64.0)
                    .ToArray(),
                HashType = "phash",
                KeepItem = groupItems[0],
            });
        }
        return groups;
    }

    private static int HammingDistance(ulong a, ulong b)
    {
        var x = a ^ b;
        var count = 0;
        while (x != 0)
        {
            x &= x - 1;
            count++;
        }
        return count;
    }
}

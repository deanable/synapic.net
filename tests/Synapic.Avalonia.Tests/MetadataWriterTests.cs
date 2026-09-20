using Synapic.Avalonia.Services;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// Unit-level coverage for the metadata write→read round trip. The e2e test
/// only proves "some metadata appeared" after a real inference run; these
/// tests pin the exact fields so a regression in the XMP packet (write) or the
/// XMP parse (read) fails without needing the sidecar exe.
/// </summary>
public class MetadataWriterTests
{
    private static string? FindSampleImage()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Synapic.Avalonia.Tests", "TestData", "sample.jpg");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string CopySample()
    {
        var sample = FindSampleImage();
        Assert.NotNull(sample);
        var tmp = Path.Combine(Path.GetTempPath(), $"synapic-meta-{Guid.NewGuid():N}.jpg");
        File.Copy(sample!, tmp);
        return tmp;
    }

    [Fact]
    public async Task Jpeg_WriteThenRead_RoundTripsAllFields()
    {
        var path = CopySample();
        try
        {
            var writer = new MetadataWriterService();
            var tags = new TagResult("Nature", new[] { "forest", "tree" }, "A quiet description");

            Assert.True(await writer.WriteAsync(path, tags));

            var read = await writer.ReadAsync(path);
            Assert.NotNull(read);
            Assert.Equal("Nature", read!.Category);
            Assert.Contains("forest", read.Keywords);
            Assert.Contains("tree", read.Keywords);
            Assert.Equal("A quiet description", read.Description);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Jpeg_WriteThenRead_KeywordsOnly()
    {
        var path = CopySample();
        try
        {
            var writer = new MetadataWriterService();
            Assert.True(await writer.WriteAsync(path, new TagResult(null, new[] { "alpha", "beta" }, null)));

            var read = await writer.ReadAsync(path);
            Assert.NotNull(read);
            Assert.Contains("alpha", read!.Keywords);
            Assert.Contains("beta", read.Keywords);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

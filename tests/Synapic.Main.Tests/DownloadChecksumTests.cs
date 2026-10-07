using System.Security.Cryptography;
using Synapic.Main.Services;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// The app executes what SidecarDownloadService downloads, so the SHA256SUMS
/// verification must be strict about format, tolerant about sha256sum
/// conventions (\r\n endings, the '*' binary marker, case).
/// </summary>
public class DownloadChecksumTests
{
    private static readonly string Manifest =
        "1111111111111111111111111111111111111111111111111111111111111111  synapic-inference-win-x64.exe\n" +
        "2222222222222222222222222222222222222222222222222222222222222222 *synapic-inference-win-x64.exe.part1\r\n" +
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  synapic-inference-osx-arm64";

    [Fact]
    public void Finds_entry_ignoring_crlf_binary_marker_and_case()
    {
        Assert.Equal("1111111111111111111111111111111111111111111111111111111111111111",
            SidecarDownloadService.FindExpectedSha256(Manifest, "synapic-inference-win-x64.exe"));
        Assert.Equal("2222222222222222222222222222222222222222222222222222222222222222",
            SidecarDownloadService.FindExpectedSha256(Manifest, "SYNAPIC-INFERENCE-WIN-X64.EXE.PART1"));
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            SidecarDownloadService.FindExpectedSha256(Manifest, "synapic-inference-osx-arm64"));
    }

    [Fact]
    public void Missing_or_malformed_entries_return_null()
    {
        Assert.Null(SidecarDownloadService.FindExpectedSha256(Manifest, "synapic-inference-win-x64-cuda.exe"));
        Assert.Null(SidecarDownloadService.FindExpectedSha256("garbage\nlines\nonly", "synapic-inference-win-x64.exe"));
        Assert.Null(SidecarDownloadService.FindExpectedSha256("", "anything"));
    }

    [Fact]
    public void Verify_accepts_a_matching_hash_and_rejects_a_mismatch()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes("synapic sidecar payload");
        var hash = SHA256.HashData(payload);
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        var manifest = $"{hex}  synapic-inference-win-x64.exe\n";

        SidecarDownloadService.VerifySha256Hex(manifest, "synapic-inference-win-x64.exe", hash);

        var tampered = hash.ToArray();
        tampered[0] ^= 0xFF;
        Assert.Throws<InvalidOperationException>(() =>
            SidecarDownloadService.VerifySha256Hex(manifest, "synapic-inference-win-x64.exe", tampered));
    }
}

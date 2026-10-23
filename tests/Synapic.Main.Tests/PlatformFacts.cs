using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// A test that can only run against the Windows registry (or another Windows-only
/// API). On any other platform it is reported as <b>skipped</b> with the reason
/// instead of passing without asserting anything — an early return used to be
/// reported as a pass, which is exactly the false green review D2 called out.
/// </summary>
/// <remarks>
/// It derives from <see cref="FactAttribute"/> rather than
/// <c>SkippableFact</c>: the skip is a static attribute value (set once at
/// discovery, the platform cannot change mid-run), and the standard discoverer
/// keeps these tests in their class's collection. The skippable discoverer gives
/// each test its own collection, which let registry tests run concurrently with
/// the headless Avalonia collections and made unrelated UI tests fail
/// intermittently on the dispatcher.
/// </remarks>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows registry is not available on this platform";
    }
}

/// <summary>
/// The skip has to be a discovery-time attribute value (xUnit reads it before
/// the test body runs), so the mechanism itself is worth one assertion: on
/// Windows the attribute must not skip anything, and elsewhere it must carry a
/// reason.
/// </summary>
public class PlatformFactAttributeTests
{
    [Fact]
    public void Windows_fact_carries_a_reason_only_off_windows()
    {
        var attribute = new WindowsFactAttribute();
        if (OperatingSystem.IsWindows())
            Assert.Null(attribute.Skip);
        else
            Assert.False(string.IsNullOrWhiteSpace(attribute.Skip));
    }
}

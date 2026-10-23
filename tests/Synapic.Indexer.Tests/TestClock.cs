namespace Synapic.Indexer.Tests;

/// <summary>
/// A settable <see cref="TimeProvider"/>. Debounce, retry and recovery tests move
/// time on purpose instead of sleeping, so a slow machine cannot change a result.
/// </summary>
internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset _utcNow;

    public TestClock(DateTimeOffset? start = null) =>
        _utcNow = start ?? new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan by) => _utcNow += by;

    public void Set(DateTimeOffset value) => _utcNow = value;
}

using Synapic.Indexer.Detection;
using Xunit;

namespace Synapic.Indexer.Tests;

public sealed class DebounceBufferTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);
    private readonly TestClock _clock = new();

    private DebounceBuffer Buffer() => new(Window, _clock);

    [Fact]
    public void A_path_is_released_only_after_the_window()
    {
        var buffer = Buffer();
        buffer.Add("local|c:\\photos\\a.jpg");

        Assert.Empty(buffer.DrainReady());
        _clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Empty(buffer.DrainReady());
        _clock.Advance(TimeSpan.FromSeconds(1));

        var ready = buffer.DrainReady();
        Assert.Single(ready);
        Assert.Equal("local|c:\\photos\\a.jpg", ready[0].Key);
        Assert.Equal(0, ready[0].Attempts);
        Assert.Empty(buffer.DrainReady());
    }

    [Fact]
    public void A_second_event_extends_the_window()
    {
        var buffer = Buffer();
        buffer.Add("local|c:\\a.jpg");
        _clock.Advance(TimeSpan.FromSeconds(3));
        buffer.Add("local|c:\\a.jpg");
        _clock.Advance(TimeSpan.FromSeconds(4));

        Assert.Empty(buffer.DrainReady()); // original due time has passed; the new one has not
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(buffer.DrainReady());
    }

    [Fact]
    public void Two_paths_release_independently()
    {
        var buffer = Buffer();
        buffer.Add("local|c:\\a.jpg");
        _clock.Advance(TimeSpan.FromSeconds(2));
        buffer.Add("local|c:\\b.jpg");
        _clock.Advance(TimeSpan.FromSeconds(3));

        var first = buffer.DrainReady();
        Assert.Single(first);
        Assert.EndsWith("a.jpg", first[0].Key);

        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Single(buffer.DrainReady());
    }

    [Fact]
    public void ReArm_keeps_the_path_and_counts_attempts()
    {
        var buffer = Buffer();
        buffer.Add("local|c:\\a.jpg");
        _clock.Advance(TimeSpan.FromSeconds(5));
        var ready = Assert.Single(buffer.DrainReady());

        buffer.ReArm(ready.Key, TimeSpan.FromSeconds(2));
        Assert.True(buffer.Contains(ready.Key));
        _clock.Advance(TimeSpan.FromSeconds(2));

        var retried = Assert.Single(buffer.DrainReady());
        Assert.Equal(1, retried.Attempts);
    }

    [Fact]
    public void A_new_event_resets_the_probe_attempts()
    {
        var buffer = Buffer();
        buffer.Add("local|c:\\a.jpg");
        buffer.ReArm("local|c:\\a.jpg", TimeSpan.FromSeconds(2));
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, buffer.DrainReady()[0].Attempts);

        buffer.Add("local|c:\\a.jpg");
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(0, buffer.DrainReady()[0].Attempts);
    }

    [Fact]
    public void Keys_are_compared_case_insensitively()
    {
        var buffer = Buffer();
        buffer.Add("local|C:\\Photos\\A.JPG");
        buffer.Add("local|c:\\photos\\a.jpg");

        _clock.Advance(Window);
        var ready = buffer.DrainReady();
        Assert.Single(ready);
        Assert.Equal("local|C:\\Photos\\A.JPG", ready[0].Key);
    }

    [Fact]
    public async Task Concurrent_adds_and_drains_are_safe()
    {
        var buffer = Buffer();
        var adds = Task.Run(() =>
        {
            for (var i = 0; i < 500; i++) buffer.Add($"local|c:\\file{i}.jpg");
        });
        var drains = Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                _clock.Advance(TimeSpan.FromMilliseconds(10));
                buffer.DrainReady();
            }
        });

        await Task.WhenAll(adds, drains);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var ready = buffer.DrainReady();
        Assert.Equal(0, buffer.Count);
        Assert.True(ready.Count <= 500);
    }
}

using Synapic.Avalonia.Services;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// The system-prompt history file. Every test runs against its own temp file so
/// the suite never touches %APPDATA%/Synapic/system-prompts.json.
/// </summary>
public class SystemPromptPresetStoreTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"synapic-presets-{Guid.NewGuid():N}.json");

    private readonly SystemPromptPresetStore _store;

    public SystemPromptPresetStoreTests() => _store = new SystemPromptPresetStore(_path);

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void A_missing_file_is_an_empty_history()
    {
        Assert.False(File.Exists(_path));
        Assert.Empty(_store.Load());
    }

    [Fact]
    public void Load_reads_the_object_shape()
    {
        File.WriteAllText(_path, """{"version":1,"prompts":["describe the image","be terse"]}""");

        Assert.Equal(new[] { "describe the image", "be terse" }, _store.Load());
    }

    [Fact]
    public void Load_also_reads_a_bare_array()
    {
        // The file is plain JSON the user may hand-edit; a bare array is the
        // obvious thing to write, so it has to work.
        File.WriteAllText(_path, """["one","two"]""");

        Assert.Equal(new[] { "one", "two" }, _store.Load());
    }

    [Fact]
    public void A_corrupt_file_is_an_empty_history_and_never_throws()
    {
        File.WriteAllText(_path, "{ this is not json");

        var loaded = _store.Load();

        Assert.Empty(loaded);
    }

    [Fact]
    public void Add_puts_the_newest_prompt_first_and_persists_it()
    {
        _store.Add("first");
        _store.Add("second");

        Assert.Equal(new[] { "second", "first" }, _store.Load());
        // A fresh store over the same file sees the same history.
        Assert.Equal(new[] { "second", "first" }, new SystemPromptPresetStore(_path).Load());
    }

    [Fact]
    public void Add_does_not_duplicate_and_moves_a_reused_prompt_to_the_front()
    {
        _store.Add("a");
        _store.Add("b");
        _store.Add("a");

        Assert.Equal(new[] { "a", "b" }, _store.Load());
    }

    [Fact]
    public void Add_trims_and_ignores_blank_input()
    {
        _store.Add("  spaced out  ");
        _store.Add("   ");
        _store.Add(null);

        Assert.Equal(new[] { "spaced out" }, _store.Load());
    }

    [Fact]
    public void Remove_takes_out_only_the_named_prompt()
    {
        _store.Add("a");
        _store.Add("b");

        var remaining = _store.Remove("a");

        Assert.Equal(new[] { "b" }, remaining);
        Assert.Equal(new[] { "b" }, _store.Load());
    }

    [Fact]
    public void Remove_of_an_unknown_prompt_changes_nothing()
    {
        _store.Add("a");

        Assert.Equal(new[] { "a" }, _store.Remove("nope"));
        Assert.Equal(new[] { "a" }, _store.Load());
    }

    [Fact]
    public void Normalize_drops_blanks_and_duplicates_and_caps_the_history()
    {
        var normalized = SystemPromptPresetStore.Normalize(
            new[] { " a ", "", "a", "   ", "b" });

        Assert.Equal(new[] { "a", "b" }, normalized);

        var many = Enumerable.Range(0, SystemPromptPresetStore.MaxPresets + 10)
            .Select(i => $"prompt {i}");
        Assert.Equal(SystemPromptPresetStore.MaxPresets, SystemPromptPresetStore.Normalize(many).Count);
    }

    [Fact]
    public void Save_then_Load_round_trips_the_whole_list()
    {
        var written = _store.Save(new[] { "one", "two", "one", " ", "three" });

        Assert.Equal(new[] { "one", "two", "three" }, written);
        Assert.Equal(new[] { "one", "two", "three" }, _store.Load());
    }

    [Fact]
    public void Saving_an_empty_list_empties_the_history()
    {
        _store.Add("gone");

        _store.Save(Array.Empty<string>());

        Assert.Empty(_store.Load());
    }
}

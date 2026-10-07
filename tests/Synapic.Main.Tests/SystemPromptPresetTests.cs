using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.ViewModels.Steps;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// Step 2's system-prompt history: every committed prompt is remembered, the
/// highlighted one is removable, and removing the prompt that is in the box
/// clears the box (otherwise the next commit would put it straight back).
/// </summary>
public class SystemPromptPresetTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"synapic-presets-vm-{Guid.NewGuid():N}.json");

    /// <summary>A store over the test's own file - a new one re-reads from disk.</summary>
    private SystemPromptPresetStore Store => new(_path);

    private Step2EngineViewModel Build(Session? session = null) =>
        new(session ?? new Session(), new FakeSidecar(), engineStore: null, presetStore: Store);

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Saved_prompts_load_into_the_combobox_newest_first()
    {
        Store.Add("older wording");
        Store.Add("newer wording");

        var vm = Build();

        Assert.Equal(new[] { "newer wording", "older wording" }, vm.SystemPromptPresets);
    }

    [Fact]
    public void A_typed_prompt_is_remembered_and_selected()
    {
        var vm = Build();
        vm.SystemPrompt = "You are a Daminion tagging engine.";

        vm.RememberSystemPrompt();

        Assert.Equal(new[] { "You are a Daminion tagging engine." }, vm.SystemPromptPresets);
        Assert.Equal(new[] { "You are a Daminion tagging engine." }, Store.Load());
        Assert.Equal("You are a Daminion tagging engine.", vm.SelectedSystemPromptPreset);
    }

    [Fact]
    public void A_blank_prompt_is_never_remembered()
    {
        var vm = Build();
        vm.SystemPrompt = "   ";

        vm.RememberSystemPrompt();

        Assert.Empty(vm.SystemPromptPresets);
        Assert.Empty(Store.Load());
    }

    [Fact]
    public void Remembering_the_same_prompt_twice_keeps_one_entry()
    {
        var vm = Build();
        vm.SystemPrompt = "same";
        vm.RememberSystemPrompt();
        vm.SystemPrompt = "other";
        vm.RememberSystemPrompt();
        vm.SystemPrompt = "same";
        vm.RememberSystemPrompt();

        Assert.Equal(new[] { "other", "same" }, vm.SystemPromptPresets);
    }

    [Fact]
    public void Leaving_the_step_remembers_a_prompt_that_was_only_typed()
    {
        var vm = Build();
        vm.SystemPrompt = "used for this run";

        vm.MakeSelectionValid();

        Assert.Equal(new[] { "used for this run" }, Store.Load());

        var other = Build();
        other.SystemPrompt = "and this one too";
        other.SaveToStore();

        Assert.Contains("and this one too", Store.Load());
    }

    [Fact]
    public void Picking_a_preset_fills_the_box_and_the_session()
    {
        Store.Add("pick me");
        var session = new Session();
        var vm = Build(session);

        vm.SelectedSystemPromptPreset = "pick me";

        Assert.Equal("pick me", vm.SystemPrompt);
        Assert.Equal("pick me", session.Engine.SystemPrompt);
    }

    [Fact]
    public void Editing_the_box_away_from_a_preset_clears_the_highlight()
    {
        Store.Add("saved wording");
        var vm = Build();

        // The box matching a saved prompt highlights it...
        vm.SystemPrompt = "saved wording";
        Assert.Equal("saved wording", vm.SelectedSystemPromptPreset);

        // ...and editing away from it must drop the highlight, because Delete
        // acts on the highlight.
        vm.SystemPrompt = "saved wording, but edited";
        Assert.Null(vm.SelectedSystemPromptPreset);
    }

    [Fact]
    public void Delete_removes_the_highlighted_preset_from_the_history()
    {
        Store.Add("keep me");
        Store.Add("delete me");
        var vm = Build();
        vm.SystemPrompt = "delete me";

        vm.DeleteSystemPromptPresetCommand.Execute(vm.SelectedSystemPromptPreset);

        Assert.Equal(new[] { "keep me" }, vm.SystemPromptPresets);
        Assert.Equal(new[] { "keep me" }, Store.Load());
    }

    [Fact]
    public void Deleting_the_prompt_that_is_in_the_box_clears_the_box()
    {
        Store.Add("delete me");
        var vm = Build();
        vm.SystemPrompt = "delete me";

        vm.DeleteSystemPromptPresetCommand.Execute("delete me");

        Assert.Equal("", vm.SystemPrompt);
        Assert.Null(vm.SelectedSystemPromptPreset);
        Assert.DoesNotContain("delete me", Store.Load());

        // The cleared box means the next commit cannot resurrect the entry.
        vm.RememberSystemPrompt();
        Assert.Empty(Store.Load());
    }

    [Fact]
    public void Deleting_a_different_preset_leaves_the_box_alone()
    {
        Store.Add("keep me");
        Store.Add("delete me");
        var vm = Build();
        vm.SystemPrompt = "keep me";

        vm.DeleteSystemPromptPresetCommand.Execute("delete me");

        Assert.Equal("keep me", vm.SystemPrompt);
        Assert.Equal(new[] { "keep me" }, vm.SystemPromptPresets);
    }

    [Fact]
    public void Deleting_is_persisted_for_the_next_session()
    {
        Store.Add("keep me");
        Store.Add("delete me");
        var vm = Build();

        vm.DeleteSystemPromptPresetCommand.Execute("delete me");

        Assert.Equal(new[] { "keep me" }, Build().SystemPromptPresets);
    }

    [Fact]
    public void Delete_with_nothing_to_delete_explains_itself()
    {
        var vm = Build();

        vm.DeleteSystemPromptPresetCommand.Execute(null);

        Assert.Empty(vm.SystemPromptPresets);
        Assert.NotNull(vm.SystemPromptMessage);
        Assert.Contains("Select a saved system prompt", vm.SystemPromptMessage);
    }

    [Fact]
    public void Delete_of_a_prompt_that_was_never_saved_is_refused()
    {
        var vm = Build();
        vm.SystemPrompt = "typed but never committed";

        vm.DeleteSystemPromptPresetCommand.Execute(null);

        Assert.Equal("typed but never committed", vm.SystemPrompt);
        Assert.Empty(Store.Load());
        Assert.NotNull(vm.SystemPromptMessage);
        Assert.Contains("not one of the saved", vm.SystemPromptMessage);
    }
}

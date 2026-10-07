using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.ViewModels.Steps;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// The tag instruction is user-editable in Step 2 and sent as /tag's
/// <c>user_prompt</c>. Blank means "the sidecar's built-in instruction", which is
/// what makes clearing the box a safe way back to a prompt that yields parseable
/// JSON - so these tests pin that blank stays blank all the way to the request
/// rather than turning into an empty instruction.
/// </summary>
public class TagInstructionTests
{
    private const string BuiltInLoadedMessage =
        "Loaded the built-in instruction - edit it as needed.";

    [Fact]
    public void EditingTheInstruction_PushesToSession()
    {
        var session = new Session();
        var vm = new Step2EngineViewModel(session, new FakeSidecar());

        Assert.Equal("", vm.UserPrompt);
        Assert.False(vm.HasCustomUserPrompt);

        vm.UserPrompt = "Describe the image as JSON.";

        Assert.Equal("Describe the image as JSON.", session.Engine.UserPrompt);
        Assert.True(vm.HasCustomUserPrompt);
    }

    [Fact]
    public async Task UseBuiltInInstruction_LoadsItFromTheSidecarIntoTheBox()
    {
        var session = new Session();
        var sidecar = new FakeSidecar { PromptDefaults = "BUILT-IN: reply with JSON." };
        var vm = new Step2EngineViewModel(session, sidecar);

        await vm.UseBuiltInPromptCommand.ExecuteAsync(null);

        // The box now holds the shipped wording, ready to be edited.
        Assert.Equal("BUILT-IN: reply with JSON.", vm.UserPrompt);
        Assert.Equal("BUILT-IN: reply with JSON.", session.Engine.UserPrompt);
        Assert.True(vm.HasCustomUserPrompt);
        Assert.Equal(BuiltInLoadedMessage, vm.PromptMessage);
    }

    [Fact]
    public async Task UseBuiltInInstruction_FetchesTheSidecarTextOnlyOnce()
    {
        var sidecar = new FakeSidecar { PromptDefaults = "BUILT-IN" };
        var vm = new Step2EngineViewModel(new Session(), sidecar);

        await vm.UseBuiltInPromptCommand.ExecuteAsync(null);
        vm.UserPrompt = "edited";
        await vm.UseBuiltInPromptCommand.ExecuteAsync(null);

        // The second click reuses the cached copy and overwrites the edit, which
        // is what makes the button a reliable way back to the shipped wording.
        Assert.Equal(1, sidecar.PromptFetchCount);
        Assert.Equal("BUILT-IN", vm.UserPrompt);
    }

    [Fact]
    public async Task UseBuiltInInstruction_ReportsAnEmptyAnswerInsteadOfBlankingTheBox()
    {
        // A blanked box would look like a reset and silently discard whatever the
        // user had typed, so an empty reply must be a message, not an overwrite.
        var sidecar = new FakeSidecar { PromptDefaults = "" };
        var vm = new Step2EngineViewModel(new Session(), sidecar) { UserPrompt = "mine" };

        await vm.UseBuiltInPromptCommand.ExecuteAsync(null);

        Assert.Equal("mine", vm.UserPrompt);
        Assert.NotNull(vm.PromptMessage);
    }

    [Fact]
    public async Task UseBuiltInInstruction_ReportsAFailureInsteadOfBlankBox()
    {
        var vm = new Step2EngineViewModel(new Session(), new FailingPromptSidecar())
        {
            UserPrompt = "mine",
        };

        await vm.UseBuiltInPromptCommand.ExecuteAsync(null);

        Assert.Equal("mine", vm.UserPrompt);
        Assert.Contains("Could not read the built-in instruction", vm.PromptMessage);
    }

    [Fact]
    public void Reset_ClearsBackToTheBuiltInInstruction()
    {
        var session = new Session();
        var vm = new Step2EngineViewModel(session, new FakeSidecar()) { UserPrompt = "mine" };

        vm.ResetUserPromptCommand.Execute(null);

        Assert.Equal("", vm.UserPrompt);
        Assert.Equal("", session.Engine.UserPrompt);
        Assert.False(vm.HasCustomUserPrompt);
        Assert.Equal("Using the built-in instruction.", vm.PromptMessage);
    }

    [Fact]
    public void BlankInstruction_StaysNullOnTheWire()
    {
        // Null/absent is the wire spelling of "use the built-in instruction"; an
        // empty string would be indistinguishable from a user who cleared a
        // custom prompt on purpose.
        var session = new Session();
        var vm = new Step3ProcessViewModel(session, new FakeSidecar(), new Step1DatasourceViewModel(session));

        Assert.Null(vm.BuildTagRequest().Options!.UserPrompt);

        session.Engine.UserPrompt = "   ";
        Assert.Null(vm.BuildTagRequest().Options!.UserPrompt);
    }

    [Fact]
    public void CustomInstruction_TravelsOnTheTagRequest()
    {
        var session = new Session();
        session.Engine.UserPrompt = "Reply in JSON with description, category, keywords.";
        var vm = new Step3ProcessViewModel(session, new FakeSidecar(), new Step1DatasourceViewModel(session));

        Assert.Equal("Reply in JSON with description, category, keywords.",
            vm.BuildTagRequest().Options!.UserPrompt);
    }

    [Fact]
    public void StoreRoundTrip_KeepsTheInstruction()
    {
        if (!OperatingSystem.IsWindows()) return;

        var keyPath = $@"Software\Synapic\Tests_TagInstruction_{Guid.NewGuid():N}";
        var store = new EngineSettingsStore(keyPath);

        try
        {
            var vm = new Step2EngineViewModel(new Session(), new FakeSidecar(), store)
            {
                UserPrompt = "custom instruction",
            };
            vm.SaveToStore();

            var reloaded = new Step2EngineViewModel(new Session(), new FakeSidecar(), store);

            Assert.Equal("custom instruction", reloaded.UserPrompt);
        }
        finally
        {
            store.Clear();
        }
    }

    /// <summary>Fake whose prompt endpoint fails, as when the sidecar is down.</summary>
    private sealed class FailingPromptSidecar : IInferenceSidecar
    {
        public SidecarStatus CurrentStatus => SidecarStatus.Stopped;
        public int SidecarPort => 0;
        public bool IsRunning => false;

#pragma warning disable CS0067 // events unused by this fake
        public event EventHandler<SidecarStatusChangedEventArgs>? StatusChanged;
        public event Action<string>? LogReceived;
#pragma warning restore CS0067

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(TimeSpan? gracefulTimeout = null) => Task.CompletedTask;
        public Task<Shared.Contracts.TagResponse> TagAsync(Shared.Contracts.TagRequest request, CancellationToken ct = default)
            => Task.FromResult(new Shared.Contracts.TagResponse());
        public Task<Shared.Contracts.ModelInfo[]> ListModelsAsync(CancellationToken ct = default)
            => Task.FromResult(Array.Empty<Shared.Contracts.ModelInfo>());
        public Task DownloadModelAsync(string modelId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Shared.Contracts.HealthResponse> GetHealthAsync(CancellationToken ct = default)
            => Task.FromResult(new Shared.Contracts.HealthResponse());
        public Task<Shared.Contracts.PromptDefaultsDto> GetPromptDefaultsAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("sidecar is not running");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

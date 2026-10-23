using Synapic.Core.Services.Processing;
using Synapic.Indexer.Configuration;
using Synapic.Shared.Contracts;

namespace Synapic.Indexer.Pipeline;

/// <summary>
/// Builds the <c>/tag</c> request and field selection from the service's
/// configuration. This mirrors <c>Step3ProcessViewModel.BuildTagRequest</c> field
/// for field: one engine, one request shape, so a difference in output can only
/// come from the model or the file — never from the host.
/// </summary>
public static class TagRequestFactory
{
    /// <summary>The sidecar task the desktop sends for tagging.</summary>
    public const string TaggingTask = "image-text-to-text";

    public static TagRequest Create(EngineSettings engine) => new()
    {
        ModelId = string.IsNullOrWhiteSpace(engine.ModelId) ? null : engine.ModelId,
        Task = TaggingTask,
        Options = new TagOptions
        {
            ConfidenceThreshold = engine.ConfidenceThreshold,
            ProbabilityMode = engine.ProbabilityMode,
            ProbabilityThreshold = engine.ProbabilityThreshold,
            // The service has no system-prompt setting; null means the sidecar's
            // built-in instruction, exactly as a blank desktop field does.
            SystemPrompt = null,
            UserPrompt = string.IsNullOrWhiteSpace(engine.UserPrompt) ? null : engine.UserPrompt,
            MaxNewTokens = 512,
        },
    };

    public static TagFieldSelection Fields(TaggingSettings tagging) =>
        new(tagging.WriteCategory, tagging.WriteKeywords, tagging.WriteDescription);
}

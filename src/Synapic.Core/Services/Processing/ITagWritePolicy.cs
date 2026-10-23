using Synapic.Core.Services;

namespace Synapic.Core.Services.Processing;

/// <summary>
/// The one place a host can change <em>what</em> is written without forking the
/// tagging kernel. Called once per item, immediately before the metadata write,
/// with the tags the model produced and the item they belong to.
/// </summary>
/// <remarks>
/// A policy may only drop or rewrite fields; it cannot write metadata itself.
/// Returning <c>null</c> is a contract violation and fails that item. An
/// exception thrown here is an item failure like any other, so a policy that
/// reads existing tags must tolerate a missing/corrupt file by returning the
/// tags unchanged rather than throwing.
/// </remarks>
public interface ITagWritePolicy
{
    Task<TagResult> AdjustAsync(ProcessWorkItem item, TagResult tags, CancellationToken ct);
}

/// <summary>The desktop's behaviour: write exactly what the model returned.</summary>
public sealed class NullTagWritePolicy : ITagWritePolicy
{
    public static NullTagWritePolicy Instance { get; } = new();

    public Task<TagResult> AdjustAsync(ProcessWorkItem item, TagResult tags, CancellationToken ct) =>
        Task.FromResult(tags);
}

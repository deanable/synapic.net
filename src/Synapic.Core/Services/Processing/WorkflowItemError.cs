namespace Synapic.Core.Services.Processing;

/// <summary>
/// Why one item failed, in the terms a queue needs: which item (path or catalog
/// id), what happened, and the exception's type so the indexer can classify it
/// without re-parsing a message.
/// </summary>
/// <remarks>
/// The runner catches per-item exceptions so one bad file cannot end a batch;
/// before this record existed those exceptions reached the log and nowhere else,
/// leaving a caller that owns durable state unable to tell a corrupt image from
/// a locked one.
/// </remarks>
public sealed record WorkflowItemError(
    string FileName,
    string? LocalPath,
    int? DaminionId,
    string Status,
    string Message,
    string ExceptionType);

namespace Nocturne.Core.Models;

/// <summary>What an upsert of one <see cref="StateSpan"/> by its <see cref="StateSpan.OriginalId"/> did.</summary>
public enum StateSpanUpsertOutcome
{
    /// <summary>No span held the original id, so the span was inserted.</summary>
    Inserted,

    /// <summary>The live span holding the original id was updated in place.</summary>
    Updated,

    /// <summary>A span the user deleted holds the original id, so nothing was written.</summary>
    Blocked,
}

/// <summary>
/// One span's upsert: the row it wrote, or for <see cref="StateSpanUpsertOutcome.Blocked"/> the
/// deleted row that refused it, and what the upsert did.
/// </summary>
public sealed record StateSpanUpsert(StateSpan Span, StateSpanUpsertOutcome Outcome);

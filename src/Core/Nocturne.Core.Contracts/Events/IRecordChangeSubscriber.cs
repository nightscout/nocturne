namespace Nocturne.Core.Contracts.Events;

/// <summary>Reacts to a committed write after its commit, at its <see cref="Stage"/>.</summary>
public interface IRecordChangeSubscriber
{
    RecordChangeStage Stage { get; }

    Task OnChangedAsync(RecordsChanged batch, CancellationToken ct);
}

/// <summary>
/// When a subscriber runs. Every <see cref="Invalidate"/> subscriber runs before any
/// <see cref="Announce"/> subscriber, so a client told of a write never re-reads a cache that still
/// serves the rows as they were.
/// </summary>
public enum RecordChangeStage
{
    /// <summary>Clears what the write made stale.</summary>
    Invalidate,

    /// <summary>Tells clients of the write.</summary>
    Announce,
}

namespace Nocturne.Core.Contracts.Events;

/// <summary>Reacts to a committed write after its commit, at its <see cref="Stage"/>.</summary>
public interface IRecordChangeSubscriber
{
    RecordChangeStage Stage { get; }

    Task OnChangedAsync(RecordsChanged batch, CancellationToken ct);
}

/// <summary>
/// When a subscriber runs, in declaration order. Every <see cref="Invalidate"/> subscriber runs before
/// any later one, so neither work handed off nor a client told of a write re-reads a cache that still
/// serves the rows as they were. <see cref="HandOff"/> runs before <see cref="Announce"/>, so a client
/// fan-out that throws cannot lose the work.
/// </summary>
public enum RecordChangeStage
{
    /// <summary>Clears what the write made stale.</summary>
    Invalidate,

    /// <summary>Hands the write to work that runs after it, without waiting for that work.</summary>
    HandOff,

    /// <summary>Tells clients of the write.</summary>
    Announce,
}

namespace Nocturne.Core.Contracts.Jobs;

/// <summary>What a run of an <see cref="IScheduledJob"/> leaves in its ledger row.</summary>
public abstract record JobOutcome
{
    private JobOutcome()
    {
    }

    /// <summary>The job is finished for its row and never comes due again.</summary>
    public static JobOutcome Done { get; } = new Finished();

    /// <summary>The job comes due again at <paramref name="nextDueAt"/> and resumes from <paramref name="cursor"/>.</summary>
    public static JobOutcome Resume(JobCursor? cursor, DateTimeOffset nextDueAt) => new Resumed(cursor, nextDueAt);

    /// <inheritdoc cref="Done"/>
    public sealed record Finished : JobOutcome;

    /// <inheritdoc cref="Resume"/>
    public sealed record Resumed(JobCursor? Cursor, DateTimeOffset NextDueAt) : JobOutcome;
}

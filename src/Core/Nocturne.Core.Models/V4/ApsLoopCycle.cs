namespace Nocturne.Core.Models.V4;

/// <summary>
/// One loop cycle of an <see cref="ApsSnapshot"/>: what the algorithm did and reported, without the
/// prediction curves or the stored commands.
/// </summary>
/// <remarks>
/// The oref fields are read out of the stored suggestion (<see cref="ApsSnapshot.SuggestedJson"/>)
/// by the database, so the suggestion itself never leaves it. oref's limits (<c>max_basal</c>,
/// <c>max_iob</c> and the safety multipliers) are not among them: ingest stores the suggestion as
/// <c>OpenApsSuggested</c> serialises it, which keeps no member it does not name, so they never
/// reach the database. Only the reason says when one bound.
/// </remarks>
public sealed class ApsLoopCycle
{
    /// <summary>The snapshot's id, which <see cref="TempBasal.ApsSnapshotId"/> names.</summary>
    public Guid Id { get; init; }

    public DateTime Timestamp { get; init; }

    public long Mills => new DateTimeOffset(Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();

    public AidAlgorithm AidAlgorithm { get; init; }

    /// <inheritdoc cref="ApsSnapshot.Enacted"/>
    public bool Enacted { get; init; }

    /// <summary>U/h.</summary>
    public double? EnactedRate { get; init; }

    /// <summary>U.</summary>
    public double? Iob { get; init; }

    /// <inheritdoc cref="ApsSnapshot.SensitivityRatio"/>
    public double? SensitivityRatio { get; init; }

    /// <summary>The cycle stored an oref suggestion with a non-empty <c>reason</c>.</summary>
    public bool HasReason { get; init; }

    /// <summary>
    /// The suggestion asked for a temp basal (it carries a <c>duration</c>) or a microbolus (it
    /// carries <c>units</c>); null without a suggestion.
    /// </summary>
    public bool? SuggestedChange { get; init; }

    /// <summary>The suggestion's <c>reason</c>, only where it names a limit (<see cref="NamesALimit"/>).</summary>
    public string? LimitReason { get; init; }

    /// <summary>How an oref reason names determine-basal's cut of a rate to its basal cap.</summary>
    public const string MaxSafeBasalToken = "maxSafeBasal";

    /// <summary>How an oref reason names its IOB limit.</summary>
    public const string MaxIobToken = "max_iob";

    /// <summary>Whether a suggestion's <c>reason</c> names a limit, and so is kept as <see cref="LimitReason"/>.</summary>
    public static bool NamesALimit(string reason) =>
        reason.Contains(MaxSafeBasalToken, StringComparison.Ordinal) || reason.Contains(MaxIobToken, StringComparison.Ordinal);
}

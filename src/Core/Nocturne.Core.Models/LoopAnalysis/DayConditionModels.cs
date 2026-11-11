namespace Nocturne.Core.Models.LoopAnalysis;

/// <summary>What a local day must have had to be kept by a <see cref="DayCondition"/>.</summary>
public enum DayConditionKind
{
    /// <summary>An exercise span overlaps the day.</summary>
    Exercise,

    /// <summary>An illness span overlaps the day.</summary>
    Illness,

    /// <summary>A low, as the glucose statistics count lows, overlaps the day.</summary>
    Low,

    /// <summary>A low overlaps the day's night: local midnight to <see cref="DayCondition.NightEndMinute"/>.</summary>
    LowOvernight,

    /// <summary>The day's carbs are within <see cref="DayCondition.MinCarbs"/> and <see cref="DayCondition.MaxCarbs"/>.</summary>
    Carbs,
}

/// <summary>
/// Keeps the local days of a range that had something happen, or with <see cref="Others"/> every
/// other day. Set by a custom report; the endpoints take none.
/// </summary>
public sealed class DayCondition
{
    /// <summary>The end of a day's night, minutes after local midnight: 06:00.</summary>
    public const int NightEndMinute = 6 * 60;

    public DayConditionKind Kind { get; init; }

    /// <summary>Least carbs in the day, g, inclusive; <see cref="DayConditionKind.Carbs"/> only.</summary>
    public double? MinCarbs { get; init; }

    /// <summary>Most carbs in the day, g, inclusive.</summary>
    public double? MaxCarbs { get; init; }

    /// <summary>Keep the days that did not have it.</summary>
    public bool Others { get; init; }
}

/// <summary>How the days of a range fell under a <see cref="DayCondition"/>.</summary>
/// <param name="Kept">The days kept, local dates.</param>
/// <param name="Matching">The days that had what the condition names.</param>
/// <param name="NotMatching">The days that did not.</param>
/// <param name="Undecided">
/// Days whose data the reader cannot see in full (before the history it may read), which are kept by
/// neither the condition nor its <see cref="DayCondition.Others"/>.
/// </param>
public sealed record DaySelection(IReadOnlyList<DateOnly> Kept, int Matching, int NotMatching, int Undecided);

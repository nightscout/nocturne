using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Models.Identity;

/// <summary>
/// A slice of the people a viewer follows: a search of their names, filters that must all hold, and an order. The API
/// judges every part of it, so the slice is the same wherever it is read.
/// </summary>
/// <param name="Search">Kept are the people whose display name contains it, ignoring case and the spaces around it.</param>
/// <param name="Filters">Kept are the people every filter holds for.</param>
/// <param name="Sort">The order within the flagged people and within the rest; the read's own order when left out.</param>
public sealed record PeopleQuery(string? Search, IReadOnlyList<PeopleFilter>? Filters, PeopleSort? Sort)
{
    public const int MaxSearchLength = 100;

    public const int MaxFilters = 10;

    public static readonly PeopleQuery Empty = new(null, null, null);

    /// <summary>The search to match, trimmed; null when there is none.</summary>
    [JsonIgnore]
    public string? Term => string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();

    [JsonIgnore]
    public IReadOnlyList<PeopleFilter> AllFilters => Filters ?? [];
}

/// <summary>The kinds of <see cref="PeopleFilter"/>. Each names the fields of the filter it reads.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PeopleFilterKind>))]
public enum PeopleFilterKind
{
    /// <summary>
    /// A flag fact compared with a value, as a flag rule compares it, without a level: <see cref="PeopleFilter.Dataset"/>,
    /// <see cref="PeopleFilter.Field"/>, <see cref="PeopleFilter.Days"/>, <see cref="PeopleFilter.Comparison"/> and
    /// <see cref="PeopleFilter.Value"/>. A person whose fact cannot be checked, or is still being checked, is not kept.
    /// </summary>
    Fact,

    /// <summary>At least <see cref="PeopleFilter.Years"/> old in whole years.</summary>
    AgeAtLeast,

    /// <summary>At most <see cref="PeopleFilter.Years"/> old in whole years.</summary>
    AgeAtMost,

    /// <summary>A therapy among <see cref="PeopleFilter.TherapyTypes"/>.</summary>
    Therapy,

    /// <summary>Flagged by the reasons the read gives.</summary>
    Flagged,

    /// <summary>Followed by the viewer directly, by a reason of their own on the person's site.</summary>
    SourceDirect,

    /// <summary>Followed by the viewer through the team <see cref="PeopleFilter.TeamId"/>.</summary>
    SourceTeam,

    /// <summary>In <see cref="PeopleFilter.GroupId"/>, a group the viewer is staff of.</summary>
    InGroup,
}

/// <summary>
/// One filter of a <see cref="PeopleQuery"/>. <see cref="Kind"/> says which of the other fields it reads; every other
/// field is left out.
/// </summary>
/// <param name="Value">For a fact, in the fact's unit: a glucose value in mg/dL.</param>
/// <param name="Days">For a period fact, the last N days it covers; left out for a fact as it is now.</param>
public sealed record PeopleFilter(
    [property: Required] PeopleFilterKind Kind,
    string? Dataset,
    string? Field,
    int? Days,
    FlagRuleComparison? Comparison,
    double? Value,
    int? Years,
    IReadOnlyList<TherapyType>? TherapyTypes,
    Guid? TeamId,
    Guid? GroupId) : IFlagFactFields
{
    public const int MaxYears = 120;
}

/// <summary>The columns a <see cref="PeopleQuery"/> sorts by: the Now lens's, then the Review lens's.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PeopleSortColumn>))]
public enum PeopleSortColumn
{
    Name,

    Glucose,

    /// <summary>The time since the newest reading.</summary>
    ReadingAge,

    /// <summary>How many alerts are active.</summary>
    Alerts,

    Reservoir,

    PumpBattery,

    /// <summary>The percent of the review period in the target range.</summary>
    TimeInRange,

    /// <summary>Time in range less that of the period before, in points.</summary>
    TimeInRangeChange,

    Gmi,

    Cv,

    /// <summary>The percent of the review period the sensor was worn.</summary>
    Wear,
}

[JsonConverter(typeof(JsonStringEnumConverter<PeopleSortDirection>))]
public enum PeopleSortDirection
{
    Ascending,

    Descending,
}

/// <summary>An order of people by one column. A person with no value in it comes last, either way.</summary>
public sealed record PeopleSort(
    [property: Required] PeopleSortColumn Column,
    [property: Required] PeopleSortDirection Direction)
{
    /// <summary>The columns of the Review lens, which only the review read has the figures of.</summary>
    public static readonly IReadOnlySet<PeopleSortColumn> ReviewColumns = new HashSet<PeopleSortColumn>
    {
        PeopleSortColumn.TimeInRange,
        PeopleSortColumn.TimeInRangeChange,
        PeopleSortColumn.Gmi,
        PeopleSortColumn.Cv,
        PeopleSortColumn.Wear,
    };

    /// <summary>The columns of the Now lens.</summary>
    public static readonly IReadOnlySet<PeopleSortColumn> NowColumns = new HashSet<PeopleSortColumn>
    {
        PeopleSortColumn.Glucose,
        PeopleSortColumn.ReadingAge,
        PeopleSortColumn.Alerts,
        PeopleSortColumn.Reservoir,
        PeopleSortColumn.PumpBattery,
    };
}

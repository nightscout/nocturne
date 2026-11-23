using System.Text.Json.Serialization;
using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Multitenancy;

/// <summary>The periods, in days, the cross-tenant review is read for.</summary>
public static class TenantReviewPeriods
{
    public static readonly IReadOnlyList<int> Days = [14, 30, 90];

    public static bool IsSupported(int days) => Days.Contains(days);
}

/// <summary>
/// Period statistics of every person the caller may read glucose for. Both periods end where the
/// next begins: the previous one is the same length and ends at <paramref name="From"/>.
/// </summary>
/// <param name="From">Start of the period the figures cover, inclusive, UTC.</param>
/// <param name="To">End of the period, exclusive, UTC.</param>
/// <param name="PreviousFrom">Start of the period directly before it, inclusive, UTC.</param>
/// <param name="PreviousTo">End of the previous period, exclusive, UTC.</param>
/// <param name="People">One entry per person, in no meaningful order: join on
/// <see cref="TenantReviewPerson.TenantId"/>.</param>
public record TenantReviewResponse(
    DateTime From,
    DateTime To,
    DateTime PreviousFrom,
    DateTime PreviousTo,
    List<TenantReviewPerson> People);

/// <summary>One person's entry in a <see cref="TenantReviewResponse"/>.</summary>
/// <param name="Unavailable">Why the caller has no figures for the person. Null when
/// <paramref name="Statistics"/> is set.</param>
/// <param name="Statistics">Null whenever <paramref name="Unavailable"/> is set.</param>
public record TenantReviewPerson(
    Guid TenantId,
    string Slug,
    string DisplayName,
    TenantReviewUnavailable? Unavailable,
    TenantReviewStatistics? Statistics);

/// <summary>
/// A person's figures for the period, as <c>GET /api/v4/statistics/period-comparison</c> states
/// them. Every figure is null when the period holds no glucose readings.
/// </summary>
/// <param name="TimeInRange">Time in each range.</param>
/// <param name="Gmi">Glucose management indicator, a percentage.</param>
/// <param name="CoefficientOfVariation">Coefficient of variation, a percentage.</param>
/// <param name="CgmActivePercent">Percentage of the period the sensor was worn.</param>
/// <param name="Reliability">Whether the period holds enough data for its figures to be reliable.</param>
/// <param name="TimeInRangeChange">Percentage of time in range less that of the previous period, in
/// points. Null when the previous period has no data or its figures are not reliable.</param>
public record TenantReviewStatistics(
    TimeInRangePercentages? TimeInRange,
    double? Gmi,
    double? CoefficientOfVariation,
    double? CgmActivePercent,
    StatisticReliability? Reliability,
    double? TimeInRangeChange);

/// <summary>Why the caller has no figures for a person.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TenantReviewUnavailable>))]
public enum TenantReviewUnavailable
{
    /// <summary>The membership or the credential limits the caller to the last 24 hours.</summary>
    HistoryLimited,

    /// <summary>The caller may not read reports there.</summary>
    NoReportAccess,

    /// <summary>The figures could not be built.</summary>
    Failed,
}

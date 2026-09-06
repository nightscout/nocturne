using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Mappers;

/// <summary>
/// Builds each record type's <see cref="MatchCriteria"/> from its entity.
/// <para>
/// Both deduplication passes resolve criteria here: the repositories when handing freshly
/// inserted rows to <c>DeduplicateBatchAsync</c>, and <c>DeduplicationService</c> when reloading
/// stored rows for the merge and reconcile passes. The two passes compare the tolerances against
/// each other, so a value defined separately per pass makes them disagree about which records are
/// the same event rather than merely drift out of style.
/// </para>
/// </summary>
public static class MatchCriteriaMapper
{
    /// <summary>
    /// Glucose agreement window. Wide enough to absorb a source that stores mmol/L to one decimal
    /// (a convert-and-round trip moves a mg/dL value by up to ~0.9), and no wider: consecutive CGM
    /// readings genuinely differ by a few mg/dL, so a larger window merges distinct readings.
    /// </summary>
    private const double GlucoseTolerance = 1.0;

    private const double InsulinTolerance = 0.05;
    private const double CarbsTolerance = 1.0;
    private const double RateTolerance = 0.05;

    /// <summary>Criteria for a sensor glucose reading.</summary>
    public static MatchCriteria From(SensorGlucoseEntity entity) =>
        new() { GlucoseValue = entity.Mgdl, GlucoseTolerance = GlucoseTolerance };

    /// <summary>Criteria for a finger-stick glucose check.</summary>
    public static MatchCriteria From(BGCheckEntity entity) =>
        new() { GlucoseValue = entity.Glucose, GlucoseTolerance = GlucoseTolerance };

    /// <summary>Criteria for a bolus.</summary>
    public static MatchCriteria From(BolusEntity entity) =>
        new() { Insulin = entity.Insulin, InsulinTolerance = InsulinTolerance };

    /// <summary>Criteria for a carb intake.</summary>
    public static MatchCriteria From(CarbIntakeEntity entity) =>
        new() { Carbs = entity.Carbs, CarbsTolerance = CarbsTolerance };

    /// <summary>
    /// Criteria for a bolus calculation. A correction-only calculation carries no carb input and is
    /// reported as zero; <c>DeduplicationService.CriteriaMatch</c> refuses to wide-match those.
    /// </summary>
    public static MatchCriteria From(BolusCalculationEntity entity) =>
        new() { Carbs = entity.CarbInput ?? 0, CarbsTolerance = CarbsTolerance };

    /// <summary>Criteria for a device event.</summary>
    public static MatchCriteria From(DeviceEventEntity entity) =>
        new() { EventType = entity.EventType };

    /// <summary>
    /// Criteria for a temp basal. Duration is derived from the interval rather than stored, and only
    /// the exact comparison reads it.
    /// </summary>
    public static MatchCriteria From(TempBasalEntity entity) =>
        new()
        {
            Rate = entity.Rate,
            RateTolerance = RateTolerance,
            Duration = entity.EndTimestamp.HasValue
                ? entity.EndTimestamp.Value - entity.StartTimestamp
                : null
        };

    /// <summary>
    /// Criteria for a state span. An unrecognised category yields a null
    /// <see cref="MatchCriteria.Category"/> rather than throwing.
    /// </summary>
    public static MatchCriteria From(StateSpanEntity entity) =>
        new()
        {
            Category = CategoriesByName.TryGetValue(entity.Category, out var category) ? category : null,
            State = entity.State
        };

    /// <summary>
    /// Categories keyed by name. A name lookup rather than <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/>,
    /// which also accepts the numeric form and would read a column holding "3" as a real category.
    /// </summary>
    private static readonly Dictionary<string, StateSpanCategory> CategoriesByName =
        Enum.GetValues<StateSpanCategory>().ToDictionary(c => c.ToString(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Criteria for a note, which matches on its time window alone.</summary>
    public static MatchCriteria ForNote() => new();
}

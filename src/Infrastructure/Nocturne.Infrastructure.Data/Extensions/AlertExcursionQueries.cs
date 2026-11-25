using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// The alert rows that are episodes of a rule's condition. A test fire and a notice about a rule
/// (<see cref="AlertExcursionEntity.UnevaluableConditionVersion"/>: that it cannot be evaluated, or,
/// with <see cref="AlertExcursionEntity.HeldSince"/>, that its hold reached the engine's bound) each
/// have an excursion and an instance so that their deliveries are recorded, and none is a time the
/// condition was true.
/// </summary>
/// <remarks>
/// A standing notice ends at <see cref="AlertExcursionEntity.NoticeStandingEnd"/>, which a reader of
/// "not ended" or "ends in the future" would take for a live alert, so every query over
/// <c>alert_excursions</c> or <c>alert_instances</c> goes through <see cref="OfCondition(IQueryable{AlertExcursionEntity}, bool)"/>
/// or the notice queries here, or is allowlisted with a reason in <c>AlertRowQueryGuardTests</c>.
/// </remarks>
public static class AlertExcursionQueries
{
    /// <summary>
    /// Excursions of a rule's condition: never a notice, and a test fire (an excursion with a test
    /// instance) only when <paramref name="includeTest"/>.
    /// </summary>
    public static IQueryable<AlertExcursionEntity> OfCondition(
        this IQueryable<AlertExcursionEntity> excursions, bool includeTest = false)
    {
        var ofCondition = excursions.Where(e => e.UnevaluableConditionVersion == null);
        return includeTest ? ofCondition : ofCondition.Where(e => !e.Instances.Any(i => i.IsTest));
    }

    /// <summary>
    /// Instances raised by a rule's condition: never a notice's, and a test fire's only when
    /// <paramref name="includeTest"/>.
    /// </summary>
    public static IQueryable<AlertInstanceEntity> OfCondition(
        this IQueryable<AlertInstanceEntity> instances, bool includeTest = false)
    {
        var ofCondition = instances.Where(i => i.AlertExcursion!.UnevaluableConditionVersion == null);
        return includeTest ? ofCondition : ofCondition.Where(i => !i.IsTest);
    }

    /// <summary>Notices about a rule, of either kind, that still stand: their instance is unresolved.</summary>
    public static IQueryable<AlertExcursionEntity> StandingNotices(this IQueryable<AlertExcursionEntity> excursions) =>
        excursions.Where(e => e.UnevaluableConditionVersion != null && e.Instances.Any(i => i.ResolvedAt == null));

    /// <summary>The unresolved instances of notices about a rule, of either kind.</summary>
    public static IQueryable<AlertInstanceEntity> OfStandingNotices(this IQueryable<AlertInstanceEntity> instances) =>
        instances.Where(i => i.ResolvedAt == null && i.AlertExcursion!.UnevaluableConditionVersion != null);
}

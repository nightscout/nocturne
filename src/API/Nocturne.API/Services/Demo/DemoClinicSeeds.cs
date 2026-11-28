using Nocturne.API.Reports;
using Nocturne.API.Reports.Datasets;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Demo;

/// <summary>
/// The saved views and personal flag rules the demo clinic's persona holds, each chosen to pick out one of the archetypes
/// in <see cref="Nocturne.Core.Models.Demo.DemoClinic.People"/>, and the writes that give the persona them.
/// </summary>
internal static class DemoClinicSeeds
{
    private const string Statistics = GlucoseStatisticsParameters.Name;

    private static readonly PeopleFilter LowInTheLastDay = new(
        PeopleFilterKind.Fact, Statistics, "timeBelow", 1, FlagRuleComparison.Above, 0, null, null, null, null);

    private static readonly PeopleFilter Flagged = new(
        PeopleFilterKind.Flagged, null, null, null, null, null, null, null, null, null);

    public static readonly IReadOnlyList<PeopleViewDraft> Views =
    [
        new("Needs a call", new PeopleQuery(null, [Flagged], new PeopleSort(PeopleSortColumn.Glucose, PeopleSortDirection.Ascending)),
            PeopleLens.Now, null),
        new("Low in the last day", new PeopleQuery(null, [LowInTheLastDay], new PeopleSort(PeopleSortColumn.Name, PeopleSortDirection.Ascending)),
            PeopleLens.Now, null),
        new("Two-week review", new PeopleQuery(null, null, new PeopleSort(PeopleSortColumn.TimeInRange, PeopleSortDirection.Ascending)),
            PeopleLens.Review, 14),
    ];

    public static readonly IReadOnlyList<FlagRuleDraft> Rules =
    [
        new(Statistics, "timeBelow", 1, FlagRuleComparison.Above, 0, NotificationUrgency.Hazard),
        new(Statistics, "timeInRangePercent", 14, FlagRuleComparison.Below, 70, NotificationUrgency.Warn),
        new(CurrentStatusParameters.Name, CurrentStatusDataset.HoursSinceReading, null, FlagRuleComparison.Above, 2, NotificationUrgency.Warn),
    ];

    /// <summary>
    /// Gives <paramref name="subjectId"/> each seeded view whose name they do not hold, and each seeded rule they do not
    /// hold, leaving what they hold as it is.
    /// </summary>
    public static async Task SeedAsync(
        IPeopleViewRepository views, IFlagRuleRepository rules, Guid subjectId, CancellationToken ct)
    {
        var heldViews = await views.GetOfSubjectAsync(subjectId, ct);
        foreach (var view in Views.Where(v => !heldViews.Any(h => string.Equals(h.Name, v.Name, StringComparison.OrdinalIgnoreCase))))
            await views.CreateAsync(subjectId, view, ct);

        var heldRules = await rules.GetOfSubjectAsync(subjectId, ct);
        foreach (var rule in Rules.Where(r => !heldRules.Any(h => Same(h, r))))
            await rules.CreateAsync(subjectId, rule, ct);
    }

    /// <summary>
    /// Deletes every view and personal rule <paramref name="subjectId"/> holds, then gives them the seeded ones
    /// (<see cref="SeedAsync"/>), so an edited seed goes back to how it was seeded.
    /// </summary>
    public static async Task ReplaceAsync(
        IPeopleViewRepository views, IFlagRuleRepository rules, Guid subjectId, CancellationToken ct)
    {
        foreach (var view in await views.GetOfSubjectAsync(subjectId, ct))
            await views.DeleteAsync(subjectId, view.Id, ct);
        foreach (var rule in await rules.GetOfSubjectAsync(subjectId, ct))
            await rules.DeleteAsync(subjectId, rule.Id, ct);

        await SeedAsync(views, rules, subjectId, ct);
    }

    private static bool Same(FlagRule held, FlagRuleDraft seed) =>
        held.Dataset == seed.Dataset
        && held.Field == seed.Field
        && held.Days == seed.Days
        && held.Comparison == seed.Comparison
        && held.Value.Equals(seed.Value);
}

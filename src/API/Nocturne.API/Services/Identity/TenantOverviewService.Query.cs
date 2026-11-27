using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Identity;

/// <remarks>
/// A <see cref="PeopleQuery"/> is judged in two steps. <see cref="NarrowAsync"/> keeps the people whose search and
/// person facts hold before any figure is judged, so nothing of anyone else is read for the query. The fact filters are
/// then judged with the flag rules by one judging (<see cref="IViewerFlagRules"/>), and the flagged filter on the reasons
/// that gives (<see cref="KeptItemsAsync"/>).
/// </remarks>
public partial class TenantOverviewService
{
    /// <summary>The kept items of one read, in order, how many people are still being checked, and the teams whose rules it judged.</summary>
    private sealed record KeptItems(
        List<TenantOverviewItem> Items, int Pending, IReadOnlyList<TenantOverviewRuleTeam> RuleTeams);

    /// <summary>
    /// The items of the people of <paramref name="glucoseReadTenants"/> <paramref name="query"/> keeps, in its order
    /// (<see cref="PeopleOrder.Compare"/>). A person still being checked for a fact filter is left out and counted. The
    /// read never waits on a figure (<see cref="IViewerFlagRuleRun"/>).
    /// </summary>
    private async Task<KeptItems> KeptItemsAsync(
        Guid subjectId,
        IReadOnlyList<GlucoseReadTenant> glucoseReadTenants,
        OverviewRuleSet rules,
        PeopleQuery query,
        bool credentialLimitTo24Hours,
        CancellationToken ct)
    {
        var people = await NarrowAsync(subjectId, glucoseReadTenants, query, credentialLimitTo24Hours, ct);
        var run = await BeginRulesAsync(subjectId, rules, query, people, credentialLimitTo24Hours, ct);
        var hasFactFilters = query.AllFilters.Any(f => f.Kind == PeopleFilterKind.Fact);
        var built = await BuildItemsAsync(people, run, hasFactFilters, credentialLimitTo24Hours, ct);
        run?.Finish();

        var flaggedOnly = query.AllFilters.Any(f => f.Kind == PeopleFilterKind.Flagged);
        var now = _time.GetUtcNow().UtcDateTime;
        var items = built
            .Where(b => b.MatchesFilters && (!flaggedOnly || PeopleOrder.IsFlagged(b.Item)))
            .Select(b => b.Item)
            .ToList();
        items.Sort((a, b) => PeopleOrder.Compare(a, b, query.Sort, now));
        return new KeptItems(items, run?.PeopleWithRulesPending ?? 0, run?.Teams ?? []);
    }

    /// <summary>
    /// The people of <paramref name="people"/> the search and the person facts of <paramref name="query"/> keep, in
    /// their order: the search on the display name, where each comes from (<see cref="ITenantMembershipRepository.ListSourcesOfSubjectAsync"/>),
    /// the groups the subject is staff of, then age and therapy, read only for the people still kept
    /// (<see cref="HoldsRecordFactsAsync"/>).
    /// </summary>
    private async Task<IReadOnlyList<GlucoseReadTenant>> NarrowAsync(
        Guid subjectId,
        IReadOnlyList<GlucoseReadTenant> people,
        PeopleQuery query,
        bool credentialLimitTo24Hours,
        CancellationToken ct)
    {
        var filters = query.AllFilters;
        var now = _time.GetUtcNow().UtcDateTime;
        List<GlucoseReadTenant> kept = query.Term is { } term
            ? [.. people.Where(p => p.Tenant.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase))]
            : [.. people];

        var sourced = filters.Where(f => f.Kind is PeopleFilterKind.SourceDirect or PeopleFilterKind.SourceTeam).ToList();
        if (sourced.Count > 0 && kept.Count > 0)
        {
            var sources = await _members.ListSourcesOfSubjectAsync(subjectId, now, ct);
            kept = [.. kept.Where(p => sources.TryGetValue(p.Tenant.Id, out var source)
                && sourced.All(f => f.Kind == PeopleFilterKind.SourceDirect
                    ? source.Direct
                    : f.TeamId is { } team && source.Teams.Contains(team)))];
        }

        foreach (var groupId in filters.Where(f => f.Kind == PeopleFilterKind.InGroup).Select(f => f.GroupId).Distinct())
        {
            if (kept.Count == 0)
                break;
            var group = groupId is { } id ? await _teams.GetPeopleOfStaffGroupAsync(id, subjectId, now, ct) : null;
            kept = [.. kept.Where(p => group?.Contains(p.Tenant.Id) == true)];
        }

        var recorded = filters.Where(f => f.Kind is PeopleFilterKind.AgeAtLeast or PeopleFilterKind.AgeAtMost or PeopleFilterKind.Therapy).ToList();
        if (recorded.Count == 0)
            return kept;

        var holding = new List<GlucoseReadTenant>();
        foreach (var person in kept)
            if (await HoldsRecordFactsAsync(person, recorded, credentialLimitTo24Hours, now, ct))
                holding.Add(person);
        return holding;
    }

    /// <summary>
    /// Whether every age and therapy filter of <paramref name="filters"/> holds for <paramref name="person"/>, read once
    /// for them all in the person's scope (<see cref="IPatientRecordRepository.GetFactsAsync"/>). The record is
    /// therapy's to read, and the devices devices'; a caller without a scope a filter needs there is not read, and kept
    /// by none. Neither is a person whose record does not say, or whose read failed.
    /// </summary>
    private async Task<bool> HoldsRecordFactsAsync(
        GlucoseReadTenant person,
        IReadOnlyList<PeopleFilter> filters,
        bool credentialLimitTo24Hours,
        DateTime now,
        CancellationToken ct)
    {
        var withTherapy = filters.Any(f => f.Kind == PeopleFilterKind.Therapy);
        if (!Scope.Satisfies(person.AllowedScopes, Scope.TherapyRead)
            || (withTherapy && !Scope.Satisfies(person.AllowedScopes, Scope.DevicesRead)))
            return false;

        var access = ItemAccess.Of(person, credentialLimitTo24Hours);
        var facts = await TryBuildAsync<PatientFacts?>(
            person.Tenant,
            async () =>
            {
                await using var scope = OpenTenantScope(person.Tenant, access.HistoryClamped);
                return await scope.ServiceProvider.GetRequiredService<IPatientRecordRepository>().GetFactsAsync(withTherapy, ct);
            },
            () => null);
        if (facts is null)
            return false;

        var age = facts.DateOfBirth is { } born ? PatientAge.InYears(born, DateOnly.FromDateTime(now)) : (int?)null;
        var therapy = facts.Therapy;
        return filters.All(f => f.Kind switch
        {
            PeopleFilterKind.AgeAtLeast => age >= f.Years,
            PeopleFilterKind.AgeAtMost => age <= f.Years,
            PeopleFilterKind.Therapy => therapy is { } type && f.TherapyTypes?.Contains(type) == true,
            _ => false,
        });
    }
}

/// <summary>
/// The order of a read's people. Flagged people come first, and a sort orders the people within the flagged and within
/// the rest; a person with no value in its column comes after those with one, either way, and ties go by display name,
/// then slug. With no sort, or a sort the read has no figures for, the order is <see cref="TenantOverviewService.CompareForDisplay"/>.
/// </summary>
internal static class PeopleOrder
{
    /// <summary>Whether a person has a reason for attention, as the Flagged list holds them.</summary>
    public static bool IsFlagged(TenantOverviewItem item) => item.Attention.Count > 0;

    /// <summary>
    /// Whether the review read needs the overview's items to judge <paramref name="query"/>: for a fact filter, the
    /// flagged filter, or an order, all of which turn on the overview's figures or reasons.
    /// </summary>
    public static bool NeedsItems(PeopleQuery query) =>
        query.Sort is not null || query.AllFilters.Any(f => f.Kind is PeopleFilterKind.Fact or PeopleFilterKind.Flagged);

    /// <summary>The overview's order of two items by <paramref name="sort"/>, at <paramref name="now"/>.</summary>
    public static int Compare(TenantOverviewItem a, TenantOverviewItem b, PeopleSort? sort, DateTime now)
    {
        if (sort is null || PeopleSort.ReviewColumns.Contains(sort.Column))
            return TenantOverviewService.CompareForDisplay(a, b);

        return By(
            (IsFlagged(a), a.DisplayName, a.Slug, NowKey(a, sort.Column, now)),
            (IsFlagged(b), b.DisplayName, b.Slug, NowKey(b, sort.Column, now)),
            sort);
    }

    /// <summary>The review read's order of two people by <paramref name="sort"/>, a review column; <paramref name="flagged"/> holds the flagged people.</summary>
    public static int CompareReview(TenantReviewPerson a, TenantReviewPerson b, PeopleSort sort, IReadOnlySet<Guid> flagged) =>
        By(
            (flagged.Contains(a.TenantId), a.DisplayName, a.Slug, ReviewKey(a, sort.Column)),
            (flagged.Contains(b.TenantId), b.DisplayName, b.Slug, ReviewKey(b, sort.Column)),
            sort);

    private static int By(
        (bool Flagged, string Name, string Slug, double? Key) a,
        (bool Flagged, string Name, string Slug, double? Key) b,
        PeopleSort sort)
    {
        if (a.Flagged != b.Flagged)
            return a.Flagged ? -1 : 1;

        var descending = sort.Direction == PeopleSortDirection.Descending;
        if (sort.Column == PeopleSortColumn.Name)
        {
            var named = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            if (named != 0)
                return descending ? -named : named;
        }
        else if (a.Key != b.Key)
        {
            if (a.Key is not { } first)
                return 1;
            if (b.Key is not { } second)
                return -1;
            return descending ? second.CompareTo(first) : first.CompareTo(second);
        }

        var byName = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        return byName != 0 ? byName : StringComparer.OrdinalIgnoreCase.Compare(a.Slug, b.Slug);
    }

    private static double? NowKey(TenantOverviewItem item, PeopleSortColumn column, DateTime now) => column switch
    {
        PeopleSortColumn.Glucose => item.Latest?.Mgdl,
        PeopleSortColumn.ReadingAge => item.LastReadingAt is { } at ? (now - at).TotalSeconds : null,
        PeopleSortColumn.Alerts => item.ActiveAlertCount,
        PeopleSortColumn.Reservoir => item.Devices?.ReservoirUnits,
        PeopleSortColumn.PumpBattery => item.Devices?.PumpBatteryPercent,
        _ => null,
    };

    private static double? ReviewKey(TenantReviewPerson person, PeopleSortColumn column) => column switch
    {
        PeopleSortColumn.TimeInRange => person.Statistics?.TimeInRange?.Target,
        PeopleSortColumn.TimeInRangeChange => person.Statistics?.TimeInRangeChange,
        PeopleSortColumn.Gmi => person.Statistics?.Gmi,
        PeopleSortColumn.Cv => person.Statistics?.CoefficientOfVariation,
        PeopleSortColumn.Wear => person.Statistics?.CgmActivePercent,
        _ => null,
    };
}

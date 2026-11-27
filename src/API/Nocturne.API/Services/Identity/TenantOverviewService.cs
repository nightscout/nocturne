using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Glucose;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Models.Widget;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Cross-tenant caregiver overview. Enumerates the subject's active memberships through the
/// subject-pinned <see cref="ITenantMembershipRepository.ListMembershipsOfSubjectAsync"/>,
/// then fans out per tenant: every read of a tenant runs in a fresh DI scope opened for it with
/// <see cref="TenantJobScope.OpenFor(IServiceProvider, TenantReader, string)"/>. Never reads as the
/// request's tenant, so the endpoint works from the apex (tenantless) in multi-tenant deployments.
/// </summary>
/// <seealso cref="ITenantOverviewService"/>
public partial class TenantOverviewService : ITenantOverviewService
{
    private const string AuditEndpoint = "service:tenant-overview";

    private static readonly TimeSpan TraceWindow = TimeSpan.FromHours(3);
    private static readonly TimeSpan TraceBucket = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PersonWindow = TimeSpan.FromHours(24);
    private const int PersonEntryLimit = 10;

    private readonly ITenantMembershipRepository _members;
    private readonly ITeamRepository _teams;
    private readonly IServiceProvider _scope;
    private readonly IGlucoseStatusClassifier _classifier;
    private readonly IViewerFlagRules _flagRules;
    private readonly TimeProvider _time;
    private readonly ILogger<TenantOverviewService> _logger;

    public TenantOverviewService(
        ITenantMembershipRepository members,
        ITeamRepository teams,
        IServiceProvider scope,
        IGlucoseStatusClassifier classifier,
        IViewerFlagRules flagRules,
        TimeProvider timeProvider,
        ILogger<TenantOverviewService> logger)
    {
        _members = members;
        _teams = teams;
        _scope = scope;
        _classifier = classifier;
        _flagRules = flagRules;
        _time = timeProvider;
        _logger = logger;
    }

    public async Task<TenantOverviewResponse> GetOverviewAsync(
        Guid subjectId, IReadOnlySet<string> tokenScopes, AuthType authType,
        OverviewRuleSet rules,
        PeopleQuery query,
        bool credentialLimitTo24Hours = false,
        CancellationToken ct = default)
    {
        var (items, hiddenCount, rulesPending, ruleTeams) = await BuildOverviewAsync(
            subjectId, await GetGlucoseReadTenantsAsync(subjectId, tokenScopes, authType, ct), null, rules, query,
            credentialLimitTo24Hours, ct);
        var firstGroups = await _teams.GetFirstGroupOfPeopleAsync(subjectId, _time.GetUtcNow().UtcDateTime, ct);
        var headed = items
            .Select(item => firstGroups.TryGetValue(item.TenantId, out var group) ? item with { FirstGroupId = group } : item)
            .ToList();
        return new(headed, hiddenCount, rulesPending, ruleTeams);
    }

    public Task<IReadOnlyList<StaffGroup>> GetGroupsAsync(Guid subjectId, CancellationToken ct) =>
        _teams.GetGroupsOfStaffAsync(subjectId, _time.GetUtcNow().UtcDateTime, ct);

    public async Task<TenantOverviewResponse?> GetGroupOverviewAsync(
        Guid subjectId, Guid groupId, IReadOnlySet<string> tokenScopes, AuthType authType,
        OverviewRuleSet rules, PeopleQuery query, bool credentialLimitTo24Hours, CancellationToken ct)
    {
        if (await ReadableInGroupAsync(subjectId, groupId, tokenScopes, authType, ct) is not { } group)
            return null;

        return await BuildOverviewAsync(
            subjectId, group.Readable, group.HiddenCount, rules, query, credentialLimitTo24Hours, ct);
    }

    public async Task<TenantReviewResponse?> GetGroupReviewAsync(
        Guid subjectId, Guid groupId, IReadOnlySet<string> tokenScopes, AuthType authType, int days,
        OverviewRuleSet rules, PeopleQuery query, bool credentialLimitTo24Hours, CancellationToken ct) =>
        await ReadableInGroupAsync(subjectId, groupId, tokenScopes, authType, ct) is { } group
            ? await BuildReviewAsync(subjectId, group.Readable, days, rules, query, credentialLimitTo24Hours, ct)
            : null;

    /// <summary>
    /// The tenants of the group that <see cref="GetGlucoseReadTenantsAsync"/> returns, and how many others the
    /// group holds; null when the subject is not in the group as current staff.
    /// </summary>
    private async Task<(IReadOnlyList<GlucoseReadTenant> Readable, int HiddenCount)?> ReadableInGroupAsync(
        Guid subjectId, Guid groupId, IReadOnlySet<string> tokenScopes, AuthType authType, CancellationToken ct)
    {
        var people = await _teams.GetPeopleOfStaffGroupAsync(groupId, subjectId, _time.GetUtcNow().UtcDateTime, ct);
        if (people is null)
            return null;

        var readable = (await GetGlucoseReadTenantsAsync(subjectId, tokenScopes, authType, ct))
            .Where(t => people.Contains(t.Tenant.Id))
            .ToList();
        return (readable, people.Count - readable.Count);
    }

    /// <summary>
    /// The items of <paramref name="glucoseReadTenants"/> <paramref name="query"/> keeps, in its order, with the flag rules
    /// <paramref name="rules"/> names and its fact filters judged for each (<see cref="KeptItemsAsync"/>).
    /// </summary>
    private async Task<TenantOverviewResponse> BuildOverviewAsync(
        Guid subjectId,
        IReadOnlyList<GlucoseReadTenant> glucoseReadTenants,
        int? hiddenCount,
        OverviewRuleSet rules,
        PeopleQuery query,
        bool credentialLimitTo24Hours,
        CancellationToken ct)
    {
        var kept = await KeptItemsAsync(subjectId, glucoseReadTenants, rules, query, credentialLimitTo24Hours, ct);
        return new(kept.Items, hiddenCount, kept.Pending, [.. kept.RuleTeams]);
    }

    /// <summary>
    /// The judging of the flag rules <paramref name="rules"/> names and the fact filters of <paramref name="query"/> for
    /// <paramref name="people"/>; null when there is neither.
    /// </summary>
    private async Task<IViewerFlagRuleRun?> BeginRulesAsync(
        Guid subjectId,
        OverviewRuleSet rules,
        PeopleQuery query,
        IReadOnlyList<GlucoseReadTenant> people,
        bool credentialLimitTo24Hours,
        CancellationToken ct)
    {
        var facts = query.AllFilters.Where(f => f.Kind == PeopleFilterKind.Fact).ToList();
        return (rules != OverviewRuleSet.Fixed || facts.Count > 0) && people.Count > 0
            ? await _flagRules.BeginAsync(subjectId, rules, facts, people, credentialLimitTo24Hours, ct)
            : null;
    }

    /// <summary>Each item, and whether every fact filter of the run matched for it.</summary>
    private async Task<List<(TenantOverviewItem Item, bool MatchesFilters)>> BuildItemsAsync(
        IReadOnlyList<GlucoseReadTenant> glucoseReadTenants,
        IViewerFlagRuleRun? rules,
        bool hasFactFilters,
        bool credentialLimitTo24Hours,
        CancellationToken ct)
    {
        var items = new List<(TenantOverviewItem, bool)>();
        foreach (var readable in glucoseReadTenants)
        {
            var tenant = readable.Tenant;
            var access = ItemAccess.Of(readable, credentialLimitTo24Hours);
            var historyClamped = access.HistoryClamped;

            items.Add(await TryBuildAsync(
                tenant,
                async () =>
                {
                    await using var scope = OpenTenantScope(tenant, historyClamped);
                    return await BuildItemAsync(scope.ServiceProvider, readable, access, rules, ct);
                },
                () =>
                {
                    var nowUtc = _time.GetUtcNow().UtcDateTime;
                    var fallbackLastReadingAt = historyClamped ? null : tenant.LastReadingAt;
                    var fallbackStatus = _classifier.Classify(
                        null, null, fallbackLastReadingAt, _classifier.Defaults, nowUtc);
                    return (new TenantOverviewItem(
                        tenant.Id, tenant.Slug, tenant.DisplayName,
                        fallbackLastReadingAt, Latest: null, fallbackStatus,
                        _classifier.Defaults, ActiveAlertCount: null, HighestActiveSeverity: null,
                        RecentReadings: [], Therapy: null, Devices: null, Trackers: null,
                        Attention: BuildAttention(
                            fallbackStatus, latest: null, fallbackLastReadingAt,
                            activeAlertCount: null, highestSeverity: null, trackers: null,
                            nowUtc),
                        FirstGroupId: null), !hasFactFilters);
                }));
        }

        return items;
    }

    public async Task<TenantReviewResponse> GetReviewAsync(
        Guid? subjectId, IReadOnlySet<string> tokenScopes, AuthType authType, int days,
        OverviewRuleSet rules,
        PeopleQuery query,
        bool credentialLimitTo24Hours = false,
        CancellationToken ct = default) =>
        subjectId is { } subject
            ? await BuildReviewAsync(
                subject, await GetGlucoseReadTenantsAsync(subject, tokenScopes, authType, ct), days, rules, query,
                credentialLimitTo24Hours, ct)
            : await ReadReviewAsync([], days, credentialLimitTo24Hours, ct);

    /// <summary>
    /// The review of the people of <paramref name="glucoseReadTenants"/> <paramref name="query"/> keeps. Ordered by the
    /// query's sort when it is a review column (<see cref="PeopleOrder.CompareReview"/>); otherwise in the overview's
    /// order when a part of the query needs the overview's items to judge (<see cref="PeopleOrder.NeedsItems"/>), and in
    /// no meaningful order when none does.
    /// </summary>
    private async Task<TenantReviewResponse> BuildReviewAsync(
        Guid subjectId,
        IReadOnlyList<GlucoseReadTenant> glucoseReadTenants,
        int days,
        OverviewRuleSet rules,
        PeopleQuery query,
        bool credentialLimitTo24Hours,
        CancellationToken ct)
    {
        IReadOnlyList<GlucoseReadTenant> people;
        IReadOnlySet<Guid> flagged = new HashSet<Guid>();
        var pending = 0;
        if (PeopleOrder.NeedsItems(query))
        {
            var kept = await KeptItemsAsync(subjectId, glucoseReadTenants, rules, query, credentialLimitTo24Hours, ct);
            var byTenant = glucoseReadTenants.ToDictionary(t => t.Tenant.Id);
            people = [.. kept.Items.Select(item => byTenant[item.TenantId])];
            flagged = kept.Items.Where(PeopleOrder.IsFlagged).Select(item => item.TenantId).ToHashSet();
            pending = kept.Pending;
        }
        else
        {
            people = await NarrowAsync(subjectId, glucoseReadTenants, query, credentialLimitTo24Hours, ct);
        }

        var review = await ReadReviewAsync(people, days, credentialLimitTo24Hours, ct);
        if (query.Sort is { } sort && PeopleSort.ReviewColumns.Contains(sort.Column))
            review.People.Sort((a, b) => PeopleOrder.CompareReview(a, b, sort, flagged));
        return review with { PeopleWithRulesPending = pending };
    }

    private async Task<TenantReviewResponse> ReadReviewAsync(
        IReadOnlyList<GlucoseReadTenant> glucoseReadTenants, int days, bool credentialLimitTo24Hours, CancellationToken ct)
    {
        var to = _time.GetUtcNow().UtcDateTime;
        var from = to.AddDays(-days);
        var previousFrom = from.AddDays(-days);

        var people = new List<TenantReviewPerson>();
        foreach (var (tenant, allowed, membershipLimitTo24Hours, effectivePermissions) in glucoseReadTenants)
        {
            TenantReviewPerson Person(TenantReviewUnavailable? unavailable, TenantReviewStatistics? statistics) =>
                new(tenant.Id, tenant.Slug, tenant.DisplayName, unavailable, statistics);

            if (!Scope.Satisfies(allowed, Scope.ReportsRead))
            {
                people.Add(Person(TenantReviewUnavailable.NoReportAccess, null));
            }
            else if (MemberScopeResolver.IsHistoryClamped(
                         credentialLimitTo24Hours, membershipLimitTo24Hours, effectivePermissions))
            {
                people.Add(Person(TenantReviewUnavailable.HistoryLimited, null));
            }
            else
            {
                people.Add(await TryBuildAsync(
                    tenant,
                    async () => Person(null, await BuildReviewStatisticsAsync(tenant, days, from, to, previousFrom, ct)),
                    () => Person(TenantReviewUnavailable.Failed, null)));
            }
        }

        return new TenantReviewResponse(from, to, previousFrom, from, people, 0);
    }

    private async Task<T> TryBuildAsync<T>(TenantRecord tenant, Func<Task<T>> build, Func<T> fallback)
    {
        try
        {
            return await build();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to build the read of tenant {TenantId}", tenant.Id);
            return fallback();
        }
    }

    private async Task<TenantReviewStatistics> BuildReviewStatisticsAsync(
        TenantRecord tenant, int days, DateTime from, DateTime to, DateTime previousFrom, CancellationToken ct)
    {
        await using var scope = OpenTenantScope(tenant, historyClamped: false);
        var services = scope.ServiceProvider;

        return await services.GetRequiredService<ITenantCache>().GetOrSetAsync(
            $"{TenantCacheFamily.Statistics}:review:{days}",
            () => ReadReviewStatisticsAsync(services, from, to, previousFrom, ct),
            TimeSpan.FromMinutes(5),
            ct);
    }

    private static async Task<TenantReviewStatistics> ReadReviewStatisticsAsync(
        IServiceProvider services, DateTime from, DateTime to, DateTime previousFrom, CancellationToken ct)
    {
        var reader = services.GetRequiredService<RangeAnalyticsReader>();
        var current = await reader.ReadAsync(from, to, DiabetesPopulation.Type1Adult, patientDeviceId: null, ct);
        var previous = await reader.ReadAsync(previousFrom, from, DiabetesPopulation.Type1Adult, patientDeviceId: null, ct);

        // The review states no insulin figure, so an empty delivery stands in for the read.
        var noInsulin = new InsulinDeliveryStatistics();
        var comparison = PeriodComparisonCalculator.Build(
            new(previous, noInsulin), new(current, noInsulin), GlucoseUnit.MgDl);

        double? Figure(PeriodComparisonMetric metric) =>
            comparison.Metrics.Single(m => m.Metric == metric).ValueB;

        var timeInRange = comparison.Metrics.Single(m => m.Metric == PeriodComparisonMetric.TirTarget);
        var previousIsReliable = previous.Analysis.Reliability?.MeetsReliabilityCriteria == true;

        return new TenantReviewStatistics(
            comparison.PeriodB.TimeInRange,
            Figure(PeriodComparisonMetric.Gmi),
            Figure(PeriodComparisonMetric.Cv),
            Figure(PeriodComparisonMetric.CgmActivePercent),
            current.Analysis.Reliability,
            previousIsReliable ? timeInRange.Delta : null);
    }

    private AsyncServiceScope OpenTenantScope(TenantRecord tenant, bool historyClamped) =>
        OpenTenantScope(_scope, tenant, historyClamped, AuditEndpoint);

    /// <summary>
    /// A scope that reads <paramref name="tenant"/> as the caller of <paramref name="scope"/>. The caller's request
    /// never passed through MemberScopeMiddleware for this tenant, so its clamp here is the one the membership
    /// decides, and every read goes through the scope.
    /// </summary>
    internal static AsyncServiceScope OpenTenantScope(IServiceProvider scope, TenantRecord tenant, bool historyClamped, string actor)
    {
        var reader = TenantReader.Of(scope)
            .ForTenant(new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, true, tenant.IsDemo))
            .ClampedToDay(historyClamped);
        return TenantJobScope.OpenFor(scope, reader, actor);
    }

    /// <summary>
    /// Display order: people with a reason for attention first, by their most severe reason, then
    /// everyone by name.
    /// </summary>
    internal static int CompareForDisplay(TenantOverviewItem a, TenantOverviewItem b)
    {
        var aSeverity = MostSevere(a);
        var bSeverity = MostSevere(b);
        if (aSeverity.HasValue != bSeverity.HasValue)
            return aSeverity.HasValue ? -1 : 1;
        if (aSeverity.HasValue && aSeverity != bSeverity)
            return bSeverity!.Value.CompareTo(aSeverity.Value);

        var byName = StringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName);
        return byName != 0 ? byName : StringComparer.OrdinalIgnoreCase.Compare(a.Slug, b.Slug);
    }

    private static NotificationUrgency? MostSevere(TenantOverviewItem item) =>
        item.Attention.Count == 0 ? null : item.Attention.Max(r => r.Level);

    public async Task<IReadOnlyList<GlucoseReadTenant>> GetGlucoseReadTenantsAsync(
        Guid subjectId, IReadOnlySet<string> tokenScopes, AuthType authType,
        CancellationToken ct = default)
    {
        var memberships = await _members.ListMembershipsOfSubjectAsync(subjectId, _time.GetUtcNow().UtcDateTime, ct);

        var result = new List<GlucoseReadTenant>();
        foreach (var (tenant, membership) in memberships)
        {
            if (!tenant.IsActive) continue;

            var effectivePermissions = membership.EffectivePermissions().ToHashSet();
            var allowed = MemberScopeResolver.Resolve(effectivePermissions, authType, tokenScopes);
            if (!Scope.Satisfies(allowed, Scope.GlucoseRead)) continue;

            result.Add(new GlucoseReadTenant(
                tenant, allowed, membership.LimitTo24Hours, effectivePermissions));
        }

        return result;
    }

    public async Task<TenantPersonDetail?> GetPersonAsync(
        Guid? subjectId, Guid tenantId, IReadOnlySet<string> tokenScopes, AuthType authType,
        OverviewRuleSet rules,
        bool credentialLimitTo24Hours = false,
        CancellationToken ct = default)
    {
        if (subjectId is not { } subject)
            return null;

        var readable = (await GetGlucoseReadTenantsAsync(subject, tokenScopes, authType, ct))
            .FirstOrDefault(t => t.Tenant.Id == tenantId);
        if (readable is null)
            return null;

        var tenant = readable.Tenant;
        var access = ItemAccess.Of(readable, credentialLimitTo24Hours);
        var to = _time.GetUtcNow().UtcDateTime;
        var from = to - PersonWindow;

        var run = await BeginRulesAsync(subject, rules, PeopleQuery.Empty, [readable], credentialLimitTo24Hours, ct);
        await using var scope = OpenTenantScope(tenant, access.HistoryClamped);
        var services = scope.ServiceProvider;

        var (person, _) = await BuildItemAsync(services, readable, access, run, ct);
        run?.Finish();
        var readings = await services.GetRequiredService<ICanonicalGlucoseService>().GetWindowAsync(from, to, ct);
        var entries = access.Therapy ? await ReadEntriesAsync(services, from, to, ct) : null;

        return new TenantPersonDetail(person, ThinToBuckets(readings), entries, from, to);
    }

    private static async Task<List<TenantPersonEntry>> ReadEntriesAsync(
        IServiceProvider services, DateTime from, DateTime to, CancellationToken ct)
    {
        var boluses = await services.GetRequiredService<IBolusRepository>().GetAsync(
            from, to, device: null, source: null, limit: PersonEntryLimit, kind: BolusKind.Manual, ct: ct);
        var intakes = await services.GetRequiredService<ICarbIntakeRepository>().GetAsync(
            from, to, device: null, source: null, limit: PersonEntryLimit, ct: ct);

        return boluses
            .Select(b => new TenantPersonEntry(TenantPersonEntryKind.Bolus, b.Timestamp, b.Insulin, null))
            .Concat(intakes.Select(c => new TenantPersonEntry(TenantPersonEntryKind.Carbs, c.Timestamp, null, c.Carbs)))
            .OrderByDescending(e => e.Timestamp)
            .Take(PersonEntryLimit)
            .ToList();
    }

    /// <summary>What the caller may see of one tenant's item, and the clamp it is read under.</summary>
    private readonly record struct ItemAccess(bool Alerts, bool Therapy, bool Devices, bool HistoryClamped)
    {
        public static ItemAccess Of(GlucoseReadTenant readable, bool credentialLimitTo24Hours) => new(
            Scope.Satisfies(readable.AllowedScopes, Scope.AlertsRead),
            Scope.Satisfies(readable.AllowedScopes, Scope.TreatmentsRead),
            Scope.Satisfies(readable.AllowedScopes, Scope.DevicesRead),
            MemberScopeResolver.IsHistoryClamped(
                credentialLimitTo24Hours, readable.MembershipLimitTo24Hours, readable.EffectivePermissions));
    }

    /// <summary>The person's item, and whether every fact filter of <paramref name="rules"/> matched for them.</summary>
    private async Task<(TenantOverviewItem Item, bool MatchesFilters)> BuildItemAsync(
        IServiceProvider services, GlucoseReadTenant readable, ItemAccess access, IViewerFlagRuleRun? rules, CancellationToken ct)
    {
        var tenant = readable.Tenant;
        var historyClamped = access.HistoryClamped;
        var nowUtc = _time.GetUtcNow().UtcDateTime;
        var canonical = services.GetRequiredService<ICanonicalGlucoseService>();
        var latest = await canonical.GetLatestAsync(ct);
        var recent = await canonical.GetRecentAsync(nowUtc - TraceWindow, ct);

        await using var db = await services
            .GetRequiredService<INocturneDbContextFactory>()
            .CreateAsync(ct);

        int? activeAlertCount = null;
        AlertRuleSeverity? highestSeverity = null;
        List<V4TrackerStatus>? trackers = null;
        IReadOnlyList<TrackerInstance>? activeTrackers = null;
        if (access.Alerts)
        {
            var activeExcursions = await db.AlertExcursions.AsNoTracking()
                .Include(e => e.AlertRule)
                .OfCondition(includeTest: true)
                .Where(e => e.EndedAt == null)
                .ToListAsync(ct);

            activeAlertCount = activeExcursions.Count;
            // AlertRuleSeverity orders Critical=0 < Warning < Info, so Min is most severe.
            highestSeverity = activeExcursions
                .Where(e => e.AlertRule is not null)
                .Select(e => (AlertRuleSeverity?)AlertRuleMapper.Readable(e.AlertRule!.Severity))
                .Min();

            activeTrackers = await services.GetRequiredService<ITrackerRepository>().GetActiveInstancesAsync(ct);
            trackers = TrackerStatusCondenser.Condense(activeTrackers, nowUtc);
        }

        TenantOverviewTherapy? therapy = null;
        if (access.Therapy)
        {
            var onBoard = await services.GetRequiredService<IIobCobReader>()
                .ReadTotalAsync(new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds(), live: true, ct);
            therapy = new TenantOverviewTherapy(
                onBoard.HasIob ? OnBoardRounding.Iob(onBoard.Iob.Iob) : 0,
                onBoard.HasIob,
                onBoard.HasCob ? OnBoardRounding.Cob(onBoard.Cob!.Cob) : 0,
                onBoard.HasCob);
        }

        TenantOverviewDevices? devices = null;
        PumpSnapshot? pump = null;
        if (access.Devices)
        {
            pump = await services.GetRequiredService<IPumpSnapshotRepository>()
                .GetLatestAsync(asOf: null, ct);
            devices = new TenantOverviewDevices(pump?.Reservoir, pump?.BatteryPercent, pump?.Timestamp);
        }

        var thresholds = _classifier.ResolveThresholds(
            await services.GetRequiredService<IAlertRepository>().GetEnabledRulesAsync(ct));

        var reading = latest is null
            ? null
            : new TenantOverviewReading(latest.Mgdl, latest.Delta, latest.Direction, latest.TrendRate, latest.Timestamp);

        // tenant.LastReadingAt is denormalised from glucose rows RLS never sees, so a clamped
        // caller must not read an older reading's time off it.
        var lastReadingAt = historyClamped ? null : tenant.LastReadingAt;

        var status = _classifier.Classify(
            latest?.Mgdl, latest?.Timestamp, lastReadingAt, thresholds, nowUtc);

        var attention = BuildAttention(status, reading, lastReadingAt, activeAlertCount, highestSeverity, trackers, nowUtc);
        var matchesFilters = true;
        if (rules is not null)
        {
            var judged = await rules.JudgeAsync(readable, services, latest, activeTrackers, pump, ct);
            matchesFilters = judged.MatchesFilters;
            attention = attention
                .Concat(judged.Reasons)
                .OrderByDescending(r => r.Level)
                .ToList();
        }

        return (new TenantOverviewItem(
            tenant.Id, tenant.Slug, tenant.DisplayName,
            latest?.Timestamp ?? lastReadingAt,
            reading, status, thresholds,
            activeAlertCount, highestSeverity,
            ThinToBuckets(recent),
            therapy, devices, trackers,
            attention,
            FirstGroupId: null), matchesFilters);
    }

    /// <summary>Oldest first, the newest reading of each 5-minute bucket.</summary>
    internal static List<TenantOverviewPoint> ThinToBuckets(IEnumerable<SensorGlucose> readings) =>
        readings
            .GroupBy(r => r.Timestamp.Ticks / TraceBucket.Ticks)
            .Select(g => g.MaxBy(r => r.Timestamp)!)
            .OrderBy(r => r.Timestamp)
            .Select(r => new TenantOverviewPoint(r.Timestamp, r.Mgdl))
            .ToList();

    /// <summary>Most severe first. A plain high is not a reason: the alert rules cover it.</summary>
    internal static List<TenantOverviewAttention> BuildAttention(
        GlucoseStatus status,
        TenantOverviewReading? latest,
        DateTime? lastReadingAt,
        int? activeAlertCount,
        AlertRuleSeverity? highestSeverity,
        List<V4TrackerStatus>? trackers,
        DateTime nowUtc)
    {
        var reasons = new List<TenantOverviewAttention>();

        switch (status)
        {
            case GlucoseStatus.UrgentLow:
                reasons.Add(new(TenantOverviewAttentionKind.GlucoseOutOfRange, NotificationUrgency.Urgent));
                break;
            case GlucoseStatus.Low:
            case GlucoseStatus.UrgentHigh:
                reasons.Add(new(TenantOverviewAttentionKind.GlucoseOutOfRange, NotificationUrgency.Hazard));
                break;
        }

        var lastAt = latest?.Timestamp ?? lastReadingAt;
        if (status == GlucoseStatus.Stale || lastAt is null)
        {
            int? minutes = lastAt is { } at
                ? (int)Math.Max(0, Math.Floor((nowUtc - at).TotalMinutes))
                : null;
            reasons.Add(new(TenantOverviewAttentionKind.NoRecentReading, NotificationUrgency.Warn, minutes));
        }

        if (activeAlertCount > 0)
        {
            var level = highestSeverity switch
            {
                AlertRuleSeverity.Critical => NotificationUrgency.Urgent,
                AlertRuleSeverity.Warning => NotificationUrgency.Warn,
                _ => NotificationUrgency.Info,
            };
            reasons.Add(new(TenantOverviewAttentionKind.ActiveAlert, level));
        }

        foreach (var tracker in trackers ?? [])
        {
            if (tracker.Urgency >= NotificationUrgency.Warn)
            {
                reasons.Add(new(
                    TenantOverviewAttentionKind.TrackerDue, tracker.Urgency,
                    TrackerName: tracker.Name, HoursUntilEvent: tracker.HoursUntilEvent));
            }
        }

        return reasons.OrderByDescending(r => r.Level).ToList();
    }
}

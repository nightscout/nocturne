using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;

namespace Nocturne.API.Reports;

/// <summary>What a condition found for one person.</summary>
internal enum FlagOutcome
{
    Matched,

    NotMatched,

    /// <summary>The fact has no value in the range. Never a match: missing data is the fixed set's to flag.</summary>
    NoData,

    /// <summary>
    /// The data covers less than <see cref="DataSufficiencyAssessment.MinimumCompleteness"/> of the range. Judged on
    /// the data there is: <see cref="FlagJudgement.Matches"/> says whether it matched, and the coverage is stated.
    /// </summary>
    TooLittleData,

    /// <summary>The caller may not read the fact for the person (<see cref="FlagUncheckable"/>); nothing was read.</summary>
    CannotBeChecked,
}

/// <summary>Why a condition cannot be checked for a person.</summary>
internal enum FlagUncheckable
{
    /// <summary>The caller reads only the last 24 hours of the person.</summary>
    HistoryLimited,

    /// <summary>The caller lacks a scope the fact needs (<see cref="FlagJudgement.MissingScope"/>).</summary>
    MissingScope,
}

/// <summary>The person's own target range a figure was measured against, in mg/dL.</summary>
/// <param name="VariesOverDay">The range changes over the day, so <paramref name="Low"/> and <paramref name="High"/> are its widest edges.</param>
internal sealed record FlagBound(double Low, double High, bool VariesOverDay);

/// <summary>One condition judged for one person.</summary>
/// <param name="Matches">Whether the figure met the comparison; true only for a match, or too little data that matched.</param>
/// <param name="Value">The figure, in the fact's unit (mg/dL for glucose); null when there is none.</param>
/// <param name="Window">The range a period fact was read over; null for a live fact and where nothing was read.</param>
/// <param name="Bound">For an own-range fact, the range it was measured against.</param>
/// <param name="Coverage">For a fact with a coverage field, the percent of the range the data covers.</param>
internal sealed record FlagJudgement(
    FlagOutcome Outcome,
    bool Matches,
    double? Value,
    ReportWindow? Window,
    FlagBound? Bound,
    double? Coverage,
    FlagUncheckable? Uncheckable,
    string? MissingScope)
{
    public static readonly FlagJudgement HistoryLimited =
        new(FlagOutcome.CannotBeChecked, false, null, null, null, null, FlagUncheckable.HistoryLimited, null);

    public static FlagJudgement WithoutScope(string scope) =>
        new(FlagOutcome.CannotBeChecked, false, null, null, null, null, FlagUncheckable.MissingScope, scope);
}

/// <summary>Every condition judged for one person, in the order the conditions were given.</summary>
internal sealed record FlagPersonJudgements(Guid TenantId, IReadOnlyList<FlagJudgement> Judgements);

/// <summary>
/// Judges flag conditions for the people a caller follows, by the reports engine: each fact is read by its dataset,
/// for the person's tenant, under the caller's scopes there, exactly as a custom report's figures panel reads it, so
/// the two give one figure for one person and range.
/// </summary>
/// <remarks>
/// <para>
/// Conditions that name one dataset query over one range share one read (<see cref="FlagFactCondition.ReadKey"/>), so
/// the glucose of a range is read once however many facts and values are taken from it. A period read's figures row is
/// kept in the person's <see cref="ITenantCache"/> and compared with each condition on every call, so a changed value
/// costs no read. Its key holds the read key, the fields asked for as the dataset reads by them
/// (<see cref="IReportDataset.ReadVariant"/>) and the scopes it was read under, which decide the fields the row holds.
/// A kept row is stale <see cref="KeepLife"/> after its own read by <see cref="TimeProvider"/>. A live fact is read on
/// every call.
/// </para>
/// <para>
/// People are read <see cref="PeopleAtOnce"/> at a time, each in a scope of its own, with its own contexts, cache and
/// read logs. What they share is read only: the request's scope, to open theirs from it and to resolve singletons, and
/// the caller's <see cref="IAuditContext"/>. The request's <see cref="HttpContext"/> is read before the people, and
/// again when the audit rows are written after them, on the caller's own flow.
/// </para>
/// <para>
/// A caller limited to 24 hours for a person, or lacking a fact's scopes (<see cref="FlagFacts.Scopes"/>), is not read
/// at all. A caller gets one read-audit row per person for each keep of figures they are served, read or kept, and one
/// for each live read. Kept figures count as audited for a caller only once their row is enqueued, so a call that fails
/// or is cancelled before a person's row leaves that row owed on the next call.
/// </para>
/// </remarks>
internal sealed class FlagFactEvaluator(
    IReportDatasetRegistry registry,
    ReportSpecValidator validator,
    IServiceProvider scope,
    IAuditContext audit,
    IHttpContextAccessor httpContextAccessor,
    TimeProvider time)
{
    /// <summary>The audit actor of a person's scope, and the read-audit row's endpoint.</summary>
    internal const string AuditEndpoint = "service:flag-facts";

    /// <summary>The longest a period read's figures are kept.</summary>
    internal static readonly TimeSpan PeriodKeep = TimeSpan.FromMinutes(15);

    /// <summary>The most <see cref="KeepLife"/> takes off <see cref="PeriodKeep"/>.</summary>
    internal static readonly TimeSpan KeepSpread = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long the figures of <paramref name="readKey"/> for <paramref name="tenantId"/> are kept: <see cref="PeriodKeep"/>
    /// less an offset under <see cref="KeepSpread"/> from a stable hash of the two.
    /// </summary>
    /// <remarks>
    /// One call reads every person it judges at one instant. Kept for one fixed time, their figures would all run out
    /// together, and the call after would read every person again, every quarter hour. The offset spreads those reads.
    /// </remarks>
    internal static TimeSpan KeepLife(Guid tenantId, string readKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId:N}|{readKey}"));
        var offset = BinaryPrimitives.ReadUInt64LittleEndian(hash) % (ulong)KeepSpread.Ticks;
        return PeriodKeep - TimeSpan.FromTicks((long)offset);
    }

    /// <summary>
    /// The people read at once. Each holds a pooled connection while it reads, and several staff may judge at once, so
    /// the degree stays small.
    /// </summary>
    internal const int PeopleAtOnce = 8;

    /// <inheritdoc cref="FlagFactCondition.Check"/>
    public (FlagFactCondition? Condition, IReadOnlyList<ReportSpecError> Errors) Check(FlagCondition condition) =>
        FlagFactCondition.Check(condition, validator, registry);

    /// <summary>
    /// Every condition for every person in <paramref name="people"/> (the people the caller follows, as
    /// <see cref="ITenantOverviewService.GetGlucoseReadTenantsAsync"/> lists them). A person is limited to 24 hours as
    /// the overview decides it (<see cref="MemberScopeResolver.IsHistoryClamped"/>).
    /// </summary>
    public async Task<IReadOnlyList<FlagPersonJudgements>> JudgeAsync(
        IReadOnlyList<GlucoseReadTenant> people,
        IReadOnlyList<FlagFactCondition> conditions,
        bool credentialLimitTo24Hours,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var http = httpContextAccessor.HttpContext;
        var viewer = $"{audit.SubjectId:N}|{audit.TokenId:N}|{http?.GetAuthContext()?.CredentialFingerprint}";
        var judged = new FlagPersonJudgements[people.Count];
        var unaudited = new IReadOnlyList<ServedRead>[people.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, people.Count),
            new ParallelOptions { MaxDegreeOfParallelism = PeopleAtOnce, CancellationToken = cancellationToken },
            async (i, token) =>
            {
                var (judgements, served) = await JudgePersonAsync(people[i], conditions, credentialLimitTo24Hours, viewer, now, token);
                judged[i] = new FlagPersonJudgements(people[i].Tenant.Id, judgements);
                unaudited[i] = served;
            });

        for (var i = 0; i < people.Count; i++)
            if (unaudited[i].Count > 0)
                await AuditAsync(people[i].Tenant, unaudited[i], http, cancellationToken);
        return judged;
    }

    /// <summary>The person's judgements, and the reads served that <paramref name="viewer"/> has no row for yet.</summary>
    private async Task<(IReadOnlyList<FlagJudgement> Judgements, IReadOnlyList<ServedRead> Unaudited)> JudgePersonAsync(
        GlucoseReadTenant person,
        IReadOnlyList<FlagFactCondition> conditions,
        bool credentialLimitTo24Hours,
        string viewer,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (MemberScopeResolver.IsHistoryClamped(credentialLimitTo24Hours, person.MembershipLimitTo24Hours, person.EffectivePermissions))
            return ([.. conditions.Select(_ => FlagJudgement.HistoryLimited)], []);

        var judgements = new FlagJudgement?[conditions.Count];
        var pending = new List<int>();
        for (var i = 0; i < conditions.Count; i++)
        {
            var needs = FlagFacts.Scopes(conditions[i].Dataset, conditions[i].Field);
            if (needs.FirstOrDefault(s => !Scope.Satisfies(person.AllowedScopes, s)) is { } missing)
                judgements[i] = FlagJudgement.WithoutScope(missing);
            else
                pending.Add(i);
        }
        if (pending.Count == 0)
            return (judgements!, []);

        await using var tenantScope = TenantOverviewService.OpenTenantScope(scope, person.Tenant, historyClamped: false, AuditEndpoint);
        var services = tenantScope.ServiceProvider;
        var cache = services.GetRequiredService<ITenantCache>();
        var unaudited = new List<ServedRead>();
        foreach (var group in pending.GroupBy(i => conditions[i].ReadKey))
        {
            var shared = group.Select(i => conditions[i]).ToList();
            var (figures, keptKey) = await FiguresAsync(person.Tenant.Id, shared, services, cache, now, cancellationToken);
            foreach (var i in group)
                judgements[i] = JudgeOf(conditions[i], figures);
            var mark = keptKey is null ? null : AuditedMark(viewer, keptKey, figures);
            if (mark is null || !cache.TryGetLocal<bool>(mark, out _))
                unaudited.Add(new ServedRead(figures, shared, mark));
        }
        return (judgements!, unaudited);
    }

    /// <summary>
    /// One read of a dataset for one person: its stat tiles row over the window (the last N days, or a live fact's
    /// default range, to the read), when it was read, and what it took from the tenant.
    /// </summary>
    private sealed record Figures(IReadOnlyDictionary<string, object?> Row, ReportWindow Window, DateTimeOffset ReadAt, ReportReadLog ReadLog);

    /// <summary>
    /// Figures a caller was served and has no row for, the conditions they were judged for, and for kept figures the
    /// mark that says the caller's row for them is enqueued (<see cref="AuditedMark"/>).
    /// </summary>
    private sealed record ServedRead(Figures Figures, IReadOnlyList<FlagFactCondition> Conditions, string? Mark);

    /// <summary>
    /// The figures <paramref name="shared"/> are judged on: read now for a live fact, else kept, with the key a kept row
    /// is held under (null for a live read).
    /// </summary>
    private static async Task<(Figures Figures, string? KeptKey)> FiguresAsync(
        Guid tenantId,
        IReadOnlyList<FlagFactCondition> shared,
        IServiceProvider services,
        ITenantCache cache,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var first = shared[0];
        var granted = shared.SelectMany(c => FlagFacts.Scopes(c.Dataset, c.Field)).ToHashSet(StringComparer.Ordinal);
        var shape = new ReportReadShape([.. shared.Select(c => c.Field.Name).Distinct(StringComparer.Ordinal)]);
        Task<Figures> Read() => ReadAsync(first, shape, granted, services, now, cancellationToken);
        if (first.Fact.Kind == ReportFlagFactKind.Live)
            return (await Read(), null);

        var key = $"{TenantCacheFamily.Statistics}:flag-fact:{first.ReadKey}|{first.Dataset.ReadVariant(shape)}|"
            + string.Join(',', granted.Order(StringComparer.Ordinal));
        var life = KeepLife(tenantId, first.ReadKey);
        var kept = await cache.GetOrSetAsync(key, Read, life, cancellationToken);
        if (now - kept.ReadAt < life)
            return (kept, key);

        var fresh = await Read();
        await cache.SetAsync(key, fresh, life, cancellationToken);
        return (fresh, key);
    }

    private static async Task<Figures> ReadAsync(
        FlagFactCondition condition,
        ReportReadShape shape,
        IReadOnlySet<string> granted,
        IServiceProvider services,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var from = condition.Days is { } days ? now.AddDays(-days) : now - condition.Dataset.DefaultRange;
        var readLog = new ReportReadLog();
        var output = await condition.Dataset.RunAsync(
            new ReportDatasetRequest(
                condition.Parameters, ReportPanelKind.StatTiles, from, now, UseMmol: false, granted, services, readLog,
                HistoryClamped: false, Now: now, Fields: shape.Fields),
            cancellationToken);
        var window = output.Window ?? new ReportWindow(from.ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds());
        return new Figures(output.Rows.Count > 0 ? output.Rows[0] : new Dictionary<string, object?>(), window, now, readLog);
    }

    /// <summary>
    /// The local key that says <paramref name="viewer"/>'s row for the figures kept under <paramref name="key"/> is
    /// enqueued. It is held on this instance only, so another instance audits again.
    /// </summary>
    private static string AuditedMark(string viewer, string key, Figures figures) =>
        $"flag-fact-audited:{viewer}:{key}:{figures.ReadAt.UtcTicks}";

    private static FlagJudgement JudgeOf(FlagFactCondition condition, Figures figures)
    {
        var row = figures.Row;
        var window = condition.Fact.Kind == ReportFlagFactKind.Period ? figures.Window : null;
        if (Number(row.GetValueOrDefault(condition.Field.Name)) is not { } value)
            return new FlagJudgement(FlagOutcome.NoData, false, null, window, null, null, null, null);

        var bound = condition.Fact.OwnRange
            && Number(row.GetValueOrDefault(ReportFlagFact.OwnRangeLowField)) is { } low
            && Number(row.GetValueOrDefault(ReportFlagFact.OwnRangeHighField)) is { } high
                ? new FlagBound(low, high, row.GetValueOrDefault(ReportFlagFact.OwnRangeVariesField) is "yes")
                : null;
        var coverage = condition.Fact.CoverageField is { } field ? Number(row.GetValueOrDefault(field)) : null;
        var matches = condition.Matches(value);
        var outcome = coverage < DataSufficiencyAssessment.MinimumCompleteness
            ? FlagOutcome.TooLittleData
            : matches ? FlagOutcome.Matched : FlagOutcome.NotMatched;
        return new FlagJudgement(outcome, matches, value, window, bound, coverage, null, null);
    }

    private static double? Number(object? value) => value switch
    {
        double d => d,
        int i => i,
        long l => l,
        decimal m => (double)m,
        _ => null,
    };

    /// <summary>
    /// The person's read-audit row: what the figures the caller was served, and has no row for, took from the tenant, and
    /// the facts they were for. None when they took nothing. Once the row is enqueued, the kept figures in it are marked.
    /// </summary>
    private async Task AuditAsync(TenantRecord tenant, IReadOnlyList<ServedRead> served, HttpContext? http, CancellationToken cancellationToken)
    {
        var readLog = ReportReadLog.Of(served.Select(s => s.Figures.ReadLog));
        if (readLog.Datasets.Count == 0)
            return;

        var facts = served.SelectMany(s => s.Conditions)
            .Select(c => $"{c.Dataset.Name}.{c.Field.Name}" + (c.Days is { } days ? $" over {days} days" : " now"))
            .Distinct().ToList();
        var json = JsonSerializer.Serialize(new Dictionary<string, object> { ["facts"] = facts });
        var arguments = json.Length <= ReportReadAudit.MaxArgumentsLength
            ? json
            : JsonSerializer.Serialize(new Dictionary<string, object> { ["facts"] = ReportReadAudit.Filtered });
        var enqueued = await ReportReadAudit.WriteAsync(scope, tenant.Id, AuditEndpoint, id => ReportReadAudit.BuildEntry(
            id, AuditEndpoint, arguments, StatusCodes.Status200OK, readLog, audit, http, time.GetUtcNow().UtcDateTime), cancellationToken);
        if (!enqueued || served.All(s => s.Mark is null))
            return;

        await using var tenantScope = TenantOverviewService.OpenTenantScope(scope, tenant, historyClamped: false, AuditEndpoint);
        var cache = tenantScope.ServiceProvider.GetRequiredService<ITenantCache>();
        foreach (var mark in served.Select(s => s.Mark).OfType<string>())
            cache.SetLocal(mark, true, PeriodKeep);
    }
}

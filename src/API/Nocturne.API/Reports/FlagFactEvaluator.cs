using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nocturne.API.Extensions;
using Nocturne.API.Reports.Datasets;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Services;

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

/// <summary>
/// The reads the People overview already made for a person, which the current status facts and the pump's device
/// status facts are judged on.
/// </summary>
/// <param name="ActiveTrackers">The running trackers; null where the caller may not read them.</param>
/// <param name="Pump">The newest pump snapshot; null where there is none or the caller may not read it.</param>
/// <param name="OnBoard">Insulin and carbs on board now; null where the caller may not read treatments.</param>
internal sealed record FlagLiveReads(
    SensorGlucose? Latest, IReadOnlyList<TrackerInstance>? ActiveTrackers, PumpSnapshot? Pump, OnBoardFigures? OnBoard);

/// <summary>What one overview read's judging found for a person.</summary>
internal enum FlagPersonState
{
    /// <summary>Every condition judged (<see cref="FlagPersonJudging.Judgements"/>).</summary>
    Judged,

    /// <summary>A read is not kept, or is past its life. It is read in the background.</summary>
    Pending,

    /// <summary>The kept read failed. The person carries the fixed set until the keep runs out.</summary>
    Failed,
}

/// <summary>
/// A person's judging: every condition that applies to them, in the order <see cref="FlagFactEvaluator.FlagJudging.JudgeAsync"/>
/// was given them, when <see cref="State"/> is judged.
/// </summary>
internal sealed record FlagPersonJudging(FlagPersonState State, IReadOnlyList<FlagJudgement> Judgements)
{
    public static readonly FlagPersonJudging Pending = new(FlagPersonState.Pending, []);

    public static readonly FlagPersonJudging Failed = new(FlagPersonState.Failed, []);
}

/// <summary>
/// One read of a dataset for one person: its stat tiles row over the window (the last N days, or a live fact's default
/// range, to the read), when it was read, and what it took from the tenant.
/// </summary>
internal sealed record FlagFigures(IReadOnlyDictionary<string, object?> Row, ReportWindow Window, DateTimeOffset ReadAt, ReportReadLog ReadLog);

/// <summary>
/// A read as the person's cache keeps it. <see cref="Figures"/> is null when the read failed, so the failure is kept,
/// and logged, once per keep.
/// </summary>
internal sealed record FlagFactKeep(FlagFigures? Figures, DateTimeOffset ReadAt);

/// <summary>
/// Judges flag conditions for the people a caller follows, by the reports engine: each fact is read by its dataset,
/// for the person's tenant, under the caller's scopes there, exactly as a custom report's figures panel reads it, so
/// the two give one figure for one person and range.
/// </summary>
/// <remarks>
/// <para>
/// Conditions that name one dataset query over one range share one read (<see cref="FlagFactCondition.ReadKey"/>), so
/// the glucose of a range is read once however many facts and values are taken from it. A read's figures are kept in
/// the person's local <see cref="ITenantCache"/> tier and compared with each condition on every call, so a changed
/// value costs no read. The local tier, unlike the distributed one, is not cleared by each glucose write, which would
/// leave a person whose data arrives every few minutes never judged. Its key holds the read key, the fields asked for
/// as the dataset reads by them (<see cref="IReportDataset.ReadVariant"/>) and the scopes it was read under, which
/// decide the fields the row holds. A keep serves for <see cref="KeepLife"/> after its own read by
/// <see cref="TimeProvider"/>.
/// </para>
/// <para>
/// A caller never reads a dataset and never waits on a read. The current status facts, and the pump's device status
/// facts when the newest snapshot decides them (<see cref="DeviceStatusDataset.PumpRowOf"/>), are judged on the
/// overview's own reads on every call. Every other fact is served from its keep. A person whose keep is missing or
/// past its life is pending: the read runs in the background (<see cref="FlagFactReads"/>), and the person is judged
/// on a later call. A keep of figures past <see cref="RefreshAfter"/> of its life is served and read again in the
/// background, so a page that polls does not see it run out. A failed keep is read again only once its life is over.
/// </para>
/// <para>
/// A caller limited to 24 hours for a person, or lacking a fact's scopes (<see cref="FlagFacts.Scopes"/>), is not read
/// at all. A caller gets one read-audit row per person for each period keep they are judged on, and for live facts,
/// read by the overview or kept, one per <see cref="PeriodKeep"/>. A pending or failed person gets none. A read counts
/// as audited for a caller only once its row is enqueued.
/// </para>
/// </remarks>
internal sealed class FlagFactEvaluator(
    IReportDatasetRegistry registry,
    ReportSpecValidator validator,
    FlagFactReads reads,
    IServiceProvider scope,
    IAuditContext audit,
    IHttpContextAccessor httpContextAccessor,
    TimeProvider time)
{
    /// <summary>The audit actor of a person's scope, and the read-audit row's endpoint.</summary>
    internal const string AuditEndpoint = "service:flag-facts";

    private TimeProvider Time => time;

    private FlagFactReads Reads => reads;

    /// <summary>The longest a period read's figures are kept.</summary>
    internal static readonly TimeSpan PeriodKeep = TimeSpan.FromMinutes(15);

    /// <summary>The longest the figures of a live fact the overview does not read itself are kept.</summary>
    internal static readonly TimeSpan LiveKeep = TimeSpan.FromMinutes(5);

    /// <summary>The most <see cref="KeepLife"/> takes off the longest keep of a kind, as a share of it.</summary>
    internal const double KeepSpread = 1.0 / 3.0;

    /// <summary>
    /// The share of a keep's life after which a call that serves its figures reads it again in the background. The
    /// shortest live life less this share of it (200 s less 133 s) is longer than the minute between a page's polls, so
    /// a polled keep never runs out.
    /// </summary>
    internal const double RefreshAfter = 2.0 / 3.0;

    /// <summary>
    /// How long the figures of <paramref name="readKey"/> for <paramref name="tenantId"/> are kept: <see cref="PeriodKeep"/>
    /// or <see cref="LiveKeep"/> by <paramref name="kind"/>, less an offset under <see cref="KeepSpread"/> of it from a
    /// stable hash of the two.
    /// </summary>
    /// <remarks>
    /// One call reads every person it judges at one instant. Kept for one fixed time, their figures would all run out
    /// together, and the call after would read every person again at once. The offset spreads those reads.
    /// </remarks>
    internal static TimeSpan KeepLife(Guid tenantId, string readKey, ReportFlagFactKind kind)
    {
        var longest = kind == ReportFlagFactKind.Live ? LiveKeep : PeriodKeep;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId:N}|{readKey}"));
        var offset = BinaryPrimitives.ReadUInt64LittleEndian(hash) % (ulong)(longest.Ticks * KeepSpread);
        return longest - TimeSpan.FromTicks((long)offset);
    }

    /// <inheritdoc cref="FlagFactCondition.Check"/>
    public (FlagFactCondition? Condition, IReadOnlyList<ReportSpecError> Errors) Check(FlagCondition condition) =>
        FlagFactCondition.Check(condition, validator, registry);

    /// <summary>
    /// A flag rule checked as <see cref="Check"/> checks a condition. A rule holds only the dataset's name, and its value
    /// is in the fact's own unit (mg/dL for glucose), so the unit is the fact's.
    /// </summary>
    public (FlagFactCondition? Condition, IReadOnlyList<ReportSpecError> Errors) CheckRule(
        string dataset, string field, int? days, FlagRuleComparison comparison, double value)
    {
        var unit = registry.Find(dataset) is { } named
            ? FlagFacts.Of(named).FirstOrDefault(f => f.Name == field)?.ResolveUnit(useMmol: false).Unit
            : null;
        var condition = new FlagCondition(
            JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["name"] = dataset }),
            field,
            days,
            comparison == FlagRuleComparison.Below ? FlagComparison.Below : FlagComparison.Above,
            value,
            unit);
        return Check(condition);
    }

    /// <summary>
    /// Why <paramref name="condition"/> cannot be checked for <paramref name="person"/>, or null when it can: the person
    /// is limited to 24 hours as the overview decides it (<see cref="MemberScopeResolver.IsHistoryClamped"/>), or the
    /// caller lacks a scope the fact needs there.
    /// </summary>
    public static FlagJudgement? Uncheckable(GlucoseReadTenant person, FlagFactCondition condition, bool credentialLimitTo24Hours)
    {
        if (MemberScopeResolver.IsHistoryClamped(credentialLimitTo24Hours, person.MembershipLimitTo24Hours, person.EffectivePermissions))
            return FlagJudgement.HistoryLimited;
        return FlagFacts.Scopes(condition.Dataset, condition.Field).FirstOrDefault(s => !Scope.Satisfies(person.AllowedScopes, s)) is { } missing
            ? FlagJudgement.WithoutScope(missing)
            : null;
    }

    /// <summary>The judging of <paramref name="conditions"/> for the people of one overview read.</summary>
    public FlagJudging Begin(IReadOnlyList<FlagFactCondition> conditions, bool credentialLimitTo24Hours)
    {
        var http = httpContextAccessor.HttpContext;
        var viewer = $"{audit.SubjectId:N}|{audit.TokenId:N}|{http?.GetAuthContext()?.CredentialFingerprint}";
        return new FlagJudging(this, conditions, credentialLimitTo24Hours, viewer, http);
    }

    /// <summary>
    /// One overview read's judging. Its people are judged one at a time, on the request's own flow, each by the
    /// conditions that apply to them, so conditions that apply to different people still share reads and keeps.
    /// </summary>
    internal sealed class FlagJudging
    {
        private readonly FlagFactEvaluator _evaluator;
        private readonly IReadOnlyList<FlagFactCondition> _conditions;
        private readonly bool _credentialLimitTo24Hours;
        private readonly string _viewer;
        private readonly HttpContext? _http;
        private readonly List<FlagFactReads.Warm> _warms = [];

        internal FlagJudging(
            FlagFactEvaluator evaluator,
            IReadOnlyList<FlagFactCondition> conditions,
            bool credentialLimitTo24Hours,
            string viewer,
            HttpContext? http)
        {
            _evaluator = evaluator;
            _conditions = conditions;
            _credentialLimitTo24Hours = credentialLimitTo24Hours;
            _viewer = viewer;
            _http = http;
        }

        /// <summary>How many people were pending.</summary>
        public int Pending { get; private set; }

        /// <summary>
        /// The conditions <paramref name="applicable"/> indexes for <paramref name="person"/>, read in
        /// <paramref name="personScope"/> (the overview's scope for the person) and on <paramref name="live"/>. A condition
        /// not in <paramref name="applicable"/> is neither judged nor read for the person. Pending while a read is not
        /// kept. The person's read-audit row is enqueued before it returns judged.
        /// </summary>
        public async Task<FlagPersonJudging> JudgeAsync(
            GlucoseReadTenant person,
            IServiceProvider personScope,
            FlagLiveReads live,
            IReadOnlyList<int> applicable,
            CancellationToken cancellationToken)
        {
            var now = _evaluator.Time.GetUtcNow();
            var judgements = new Dictionary<int, FlagJudgement>();
            var open = new List<int>();
            foreach (var i in applicable.Distinct())
            {
                if (Uncheckable(person, _conditions[i], _credentialLimitTo24Hours) is { } uncheckable)
                    judgements[i] = uncheckable;
                else
                    open.Add(i);
            }
            if (open.Count == 0)
                return new FlagPersonJudging(FlagPersonState.Judged, [.. applicable.Select(i => judgements[i])]);

            var cache = personScope.GetRequiredService<ITenantCache>();
            var served = new List<ServedRead>();
            var state = FlagPersonState.Judged;
            void Serve(IReadOnlyList<int> judged, ServedRead read)
            {
                foreach (var i in judged)
                    judgements[i] = JudgeOf(_conditions[i], read.Figures);
                if (!cache.TryGetLocal<bool>(read.Mark, out _))
                    served.Add(read);
            }

            foreach (var group in open.GroupBy(i => _conditions[i].ReadKey))
            {
                var rest = group.ToList();
                if (OnOwnReads(rest, live, now) is { } own)
                {
                    Serve(own.Judged, own.Read);
                    rest = [.. rest.Except(own.Judged)];
                    if (rest.Count == 0)
                        continue;
                }

                var (kept, keptState) = Kept(person, [.. rest.Select(i => _conditions[i])], personScope, cache, now);
                state = Worse(state, keptState);
                if (kept is not null)
                    Serve(rest, kept);
            }

            if (state == FlagPersonState.Pending)
            {
                Pending++;
                return FlagPersonJudging.Pending;
            }
            if (state == FlagPersonState.Failed)
                return FlagPersonJudging.Failed;

            if (served.Count > 0)
                await _evaluator.AuditAsync(person.Tenant, served, cache, _http, cancellationToken);
            return new FlagPersonJudging(FlagPersonState.Judged, [.. applicable.Select(i => judgements[i])]);
        }

        /// <summary>Starts the background reads this judging found missing or ageing. It does not wait for them.</summary>
        public void Finish()
        {
            _evaluator.Reads.Start([.. _warms]);
            _warms.Clear();
        }

        private static FlagPersonState Worse(FlagPersonState a, FlagPersonState b) =>
            a == FlagPersonState.Pending || b == FlagPersonState.Pending ? FlagPersonState.Pending
            : a == FlagPersonState.Failed || b == FlagPersonState.Failed ? FlagPersonState.Failed
            : FlagPersonState.Judged;

        /// <summary>
        /// The conditions of <paramref name="group"/>, one read's, that are judged on <paramref name="live"/>, with the
        /// figures they are judged on; null when none are.
        /// </summary>
        private (IReadOnlyList<int> Judged, ServedRead Read)? OnOwnReads(IReadOnlyList<int> group, FlagLiveReads live, DateTimeOffset now)
        {
            var first = _conditions[group[0]];
            var from = now - first.Dataset.DefaultRange;
            var readLog = new ReportReadLog();
            IReadOnlyList<int> judged;
            Dictionary<string, object?>? row;
            if (first.Dataset.Name == CurrentStatusParameters.Name)
            {
                judged = group;
                var trackers = group.Any(i => _conditions[i].Field.Scope == Scope.AlertsRead) ? live.ActiveTrackers : null;
                var onBoard = group.Any(i => _conditions[i].Field.Scope == Scope.TreatmentsRead) ? live.OnBoard : null;
                row = CurrentStatusDataset.RowOf(live.Latest, trackers, onBoard, now, readLog);
            }
            else if (first.Dataset.Name == DeviceStatusParameters.Name)
            {
                judged = [.. group.Where(i => DeviceStatusDataset.PumpFields.Contains(_conditions[i].Field.Name))];
                row = judged.Count > 0 ? DeviceStatusDataset.PumpRowOf(live.Pump, from, now, readLog) : null;
            }
            else
            {
                return null;
            }
            if (row is null)
                return null;

            var figures = new FlagFigures(row, new ReportWindow(from.ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds()), now, readLog);
            return (judged, new ServedRead(figures, [.. judged.Select(i => _conditions[i])], LiveMark(first.ReadKey, now)));
        }

        /// <summary>
        /// The read-audit mark of live figures judged under <paramref name="key"/>: one row per viewer, person and key
        /// per <see cref="PeriodKeep"/>, however often the figures are read.
        /// </summary>
        private string LiveMark(string key, DateTimeOffset now) => $"flag-fact-audited:{_viewer}:live:{key}:{now.UtcTicks / PeriodKeep.Ticks}";

        private (ServedRead? Read, FlagPersonState State) Kept(
            GlucoseReadTenant person,
            IReadOnlyList<FlagFactCondition> shared,
            IServiceProvider personScope,
            ITenantCache cache,
            DateTimeOffset now)
        {
            var first = shared[0];
            var (shape, granted) = ShapeOf(shared);
            var key = KeepKey(first, shape, granted);
            var life = KeepLife(person.Tenant.Id, first.ReadKey, first.Fact.Kind);
            if (!cache.TryGetLocal<FlagFactKeep>(key, out var keep) || keep is null || now - keep.ReadAt >= life)
            {
                _warms.Add(new FlagFactReads.Warm(person.Tenant.Id, TenantReader.Of(personScope), first, shape, granted, key, life));
                return (null, FlagPersonState.Pending);
            }

            if (keep.Figures is not { } figures)
                return (null, FlagPersonState.Failed);

            if (now - keep.ReadAt >= life * RefreshAfter)
                _warms.Add(new FlagFactReads.Warm(person.Tenant.Id, TenantReader.Of(personScope), first, shape, granted, key, life));
            var mark = first.Fact.Kind == ReportFlagFactKind.Live
                ? LiveMark(key, now)
                : $"flag-fact-audited:{_viewer}:{key}:{keep.ReadAt.UtcTicks}";
            return (new ServedRead(figures, shared, mark), FlagPersonState.Judged);
        }
    }

    /// <summary>The fields <paramref name="shared"/> ask of one read, and the scopes it is read under.</summary>
    private static (ReportReadShape Shape, IReadOnlySet<string> Granted) ShapeOf(IReadOnlyList<FlagFactCondition> shared) =>
        (new ReportReadShape([.. shared.Select(c => c.Field.Name).Distinct(StringComparer.Ordinal)]),
            shared.SelectMany(c => FlagFacts.Scopes(c.Dataset, c.Field)).ToHashSet(StringComparer.Ordinal));

    /// <summary>The local key a period read's <see cref="FlagFactKeep"/> is held under in the person's cache.</summary>
    private static string KeepKey(FlagFactCondition first, ReportReadShape shape, IReadOnlySet<string> granted) =>
        $"flag-fact:{first.ReadKey}|{first.Dataset.ReadVariant(shape)}|{string.Join(',', granted.Order(StringComparer.Ordinal))}";

    /// <summary>
    /// Figures a caller was judged on, the conditions they were judged for, and the local mark that says the caller's
    /// read-audit row for them is enqueued. A mark is held on this instance only, so another instance audits again.
    /// </summary>
    private sealed record ServedRead(FlagFigures Figures, IReadOnlyList<FlagFactCondition> Conditions, string Mark);

    /// <summary>One read of <paramref name="condition"/>'s dataset in <paramref name="services"/>, a scope for the person.</summary>
    internal static async Task<FlagFigures> ReadAsync(
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
        return new FlagFigures(output.Rows.Count > 0 ? output.Rows[0] : new Dictionary<string, object?>(), window, now, readLog);
    }

    private static FlagJudgement JudgeOf(FlagFactCondition condition, FlagFigures figures)
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
    /// The person's read-audit row: what the figures the caller was judged on, and has no row for, took from the tenant,
    /// and the facts they were for. None when they took nothing. Once the row is enqueued, the reads in it are marked in
    /// <paramref name="cache"/>, the person's.
    /// </summary>
    private async Task AuditAsync(
        TenantRecord tenant, IReadOnlyList<ServedRead> served, ITenantCache cache, HttpContext? http, CancellationToken cancellationToken)
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
        if (!enqueued)
            return;

        foreach (var read in served)
            cache.SetLocal(read.Mark, true, PeriodKeep);
    }
}

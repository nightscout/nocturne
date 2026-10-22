using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// Service for deduplicating records from multiple data sources.
/// Links records that represent the same underlying event and provides unified views.
/// </summary>
public class DeduplicationService : IDeduplicationService
{
    private const string JobAuditEndpoint = "service:deduplication";

    private readonly INocturneDbContextFactory _contexts;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICategoryReadContext? _categoryReadContext;
    private readonly ILogger<DeduplicationService> _logger;

    /// <summary>
    /// The ambient tenant of the scope this instance was resolved in. Null in hosts that do not
    /// register tenant resolution; only <see cref="StartDeduplicationJobAsync"/> and the job-status
    /// lookups need it, and they fail closed without it.
    /// </summary>
    private readonly ITenantAccessor? _tenantAccessor;

    private static readonly TimeSpan MatchingWindow = TimeSpan.FromSeconds(30);
    private static readonly long MatchingWindowMillis = (long)MatchingWindow.TotalMilliseconds;

    /// <summary>
    /// Second-chance window used only when <see cref="MatchingWindow"/> found no match. Two
    /// connectors reporting the same pump can disagree by minutes when one carries the raw pump
    /// clock and the other a corrected one, and that offset drifts without bound. The wide path
    /// buys the extra reach by dropping the tight path's value tolerances for exact equality and
    /// requiring a single candidate group that holds no record of the incoming record's own
    /// source, so it can only ever collapse a cross-source pair.
    /// </summary>
    private static readonly TimeSpan WideMatchingWindow = TimeSpan.FromMinutes(10);
    private static readonly long WideMatchingWindowMillis = (long)WideMatchingWindow.TotalMilliseconds;

    /// <summary>
    /// Matched offset above which a cross-source match is logged at Warning rather than Debug —
    /// 60% of <see cref="WideMatchingWindow"/>, so drift is visible while matching still works.
    /// </summary>
    private static readonly long CrossSourceOffsetWarningMillis =
        (long)(WideMatchingWindow.TotalMilliseconds * 0.6);

    /// <summary>
    /// Value equality epsilon for the wide path, which admits no tolerance: a tolerance-based
    /// match minutes away from the record risks hiding a distinct real dose.
    /// </summary>
    private const double ExactValueEpsilon = 1e-6;

    /// <summary>
    /// How far two sources' temp basal durations may disagree and still describe one delivery.
    /// Unlike a value, a duration is a measurement each source rounds for itself: mylife reports
    /// the pump's raw clock quantised to ten seconds while Glooko re-anchors and reports to the
    /// second, so the same eleven-minute temp basal arrives as 650s from one and 654s from the
    /// other. Demanding equality refused those outright — on one two-connector tenant that was
    /// ~2800 unmerged pairs in thirty days against 125 merged, nearly every temp basal doubled.
    /// Ten seconds covers the quantisation without reaching a neighbouring delivery, which on that
    /// tenant still leaves the pairs disagreeing by a minute or more refused.
    /// </summary>
    private static readonly TimeSpan ExactDurationTolerance = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Record types eligible for <see cref="WideMatchingWindow"/>. Continuous streams
    /// (<see cref="RecordType.SensorGlucose"/>), free-text records (<see cref="RecordType.Note"/>)
    /// and interval records (<see cref="RecordType.StateSpan"/>) are excluded: repeating the same
    /// value inside ten minutes is normal for them, so a wide match would merge distinct events.
    /// </summary>
    private static readonly HashSet<RecordType> WideMatchableTypes =
    [
        RecordType.Bolus,
        RecordType.CarbIntake,
        RecordType.DeviceEvent,
        RecordType.BGCheck,
        RecordType.TempBasal,
        RecordType.BolusCalculation
    ];

    /// <summary>
    /// Maximum number of records processed per <see cref="DeduplicateBatchAsync"/> matching-window
    /// query. A connector backfill can hand the dedup pass thousands of records spanning months;
    /// sorting by event time and slicing into chunks of this size keeps each window query's time
    /// span narrow so it stays an index range scan over a few hundred rows rather than loading
    /// millions of <c>linked_records</c> for a high-volume tenant.
    /// </summary>
    private const int DedupChunkSize = 500;

    /// <summary>
    /// How far apart the first and last run of one candidate-bounded neighbour load may start. A run
    /// longer than this still loads whole, so a load spans at most this plus one run.
    /// </summary>
    private static readonly long NeighbourSliceMillis = (long)TimeSpan.FromHours(1).TotalMilliseconds;

    /// <summary>
    /// How far behind the present <see cref="ReconcileNewLinksAsync"/> reads. A link's
    /// <c>sys_created_at</c> is the API clock when its insert's <c>SaveChanges</c> began
    /// (<see cref="NocturneDbContext"/> stamps it before opening the transaction), so an insert
    /// still committing when a pass reads past that instant would land a link the cursor has
    /// already passed. Links younger than this are left for a later pass, by which time any
    /// insert shorter than the lag has committed. The horizon is taken from the same clock.
    /// </summary>
    private static readonly TimeSpan CommitVisibilityLag = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How old a link must be before <see cref="PruneSingletonLinksAsync"/> may delete it as the only
    /// link of its group, and how old a record with no link must be before matching reads it as that
    /// pruned group (<see cref="UnlinkedAsync"/>). A link is written after its record, so every record
    /// whose link was pruned is older than this. A younger record with no link is one whose own
    /// insert-time pass has not run, which matching must not see: it would make the later records of
    /// an in-flight batch candidates for its earlier ones.
    /// </summary>
    internal static readonly TimeSpan SingletonLinkRetention = TimeSpan.FromDays(1);

    /// <summary>
    /// The records of <paramref name="recordType"/> with no link that are old enough to have been a
    /// pruned singleton, event time within the optional bounds, each as a primary link of a group of
    /// its own that is not yet stored. A caller that puts one into a group stores it.
    /// </summary>
    private static Task<List<LinkedRecordEntity>> UnlinkedAsync(
        NocturneDbContext db, RecordType recordType, long? fromMills, long? toMills, CancellationToken ct) =>
        TypePhases.SingleOrDefault(p => p.RecordType == recordType) is { } phase
            ? phase.Unlinked(db, fromMills, toMills, DateTime.UtcNow - SingletonLinkRetention, ct)
            : Task.FromResult(new List<LinkedRecordEntity>());

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction holding the tenant's dedup write lock, which
    /// <see cref="PruneSingletonLinksAsync"/> also takes. Without it a pass could read a singleton
    /// link as a match, the prune delete it, and the pass then add a record to a group that no longer
    /// has a primary, hiding it from every read.
    /// </summary>
    private static Task<T> UnderWriteLockAsync<T>(
        NocturneDbContext db, Func<CancellationToken, Task<T>> work, CancellationToken ct) =>
        db.ExecuteInTransactionAsync(async token =>
        {
            if (db.Database.IsNpgsql())
            {
                await db.Database.ExecuteSqlAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({WriteLockKey(db.TenantId)}, 0))", token);
            }

            return await work(token);
        }, ct: ct);

    /// <summary>The text the tenant's dedup write lock is hashed from.</summary>
    internal static string WriteLockKey(Guid tenantId) => $"dedup:{tenantId}";

    /// <summary>
    /// A record's match criteria paired with its soft-deleted status, and for a state span its
    /// <see cref="SpanEnd"/>, keyed by record id when returned from <see cref="LoadRecordInfoAsync"/>.
    /// </summary>
    internal sealed record RecordInfo(MatchCriteria Criteria, bool IsDeleted, SpanEnd Span = default);

    /// <summary>
    /// How a state span ended, as <see cref="PickSurvivor(IEnumerable{LinkedRecordEntity}, Func{Guid, bool}, Func{Guid, SpanEnd})"/>
    /// reads it. Every other record type reads as the default: open, superseded by nothing.
    /// </summary>
    internal readonly record struct SpanEnd(Guid? SupersededById, DateTime? EndTimestamp);

    /// <summary>
    /// One union-find root's span across every link of every canonical group beneath it, used by
    /// the reconcile wide pass in place of the root's primary timestamp, together with the criteria
    /// of every canonical beneath it. <see cref="PrimaryTimestamp"/> is the root's earliest primary
    /// and is used only for the logged offset.
    /// </summary>
    private sealed class WideGroupExtent(Guid root, long primaryTimestamp)
    {
        public Guid Root { get; } = root;
        public long PrimaryTimestamp { get; } = primaryTimestamp;
        public long Min { get; private set; } = long.MaxValue;
        public long Max { get; private set; } = long.MinValue;
        public List<MatchCriteria> Criteria { get; } = [];

        public void Absorb(long min, long max)
        {
            Min = Math.Min(Min, min);
            Max = Math.Max(Max, max);
        }

        public void AddCriteria(MatchCriteria criteria) => Criteria.Add(criteria);
    }

    /// <summary>
    /// True when any canonical beneath one root exactly matches any canonical beneath the other.
    /// A root that the tight pass built from several canonicals carries each of their values, and
    /// the insert path compares an incoming record against every link it can see — so comparing
    /// every pair is what agrees with it. Matching on one representative instead would both hide
    /// a root that should have made a neighbouring pair ambiguous and miss a merge that a matching
    /// value one canonical deeper justifies.
    /// </summary>
    private static bool AnyCriteriaMatch(RecordType recordType, WideGroupExtent a, WideGroupExtent b)
    {
        foreach (var criteriaA in a.Criteria)
        {
            foreach (var criteriaB in b.Criteria)
            {
                if (CriteriaMatch(recordType, criteriaA, criteriaB, exact: true))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Number of wide reconcile merges logged individually before the pass switches to a single
    /// summary line. A deploy-day full job over a year of drift heals tens of thousands of pairs.
    /// </summary>
    private const int WideMergeLogLimit = 20;

    private static readonly ConcurrentDictionary<Guid, DeduplicationJobStatus> _runningJobs = new();
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> _jobCancellations = new();

    /// <summary>
    /// Owning tenant of each job. The job dictionaries are static and therefore shared by every
    /// tenant in the process, so status and cancellation are matched against this before answering.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, Guid> _jobTenants = new();

    /// <inheritdoc cref="IDeduplicationService" />
    public DeduplicationService(
        INocturneDbContextFactory contexts,
        IServiceScopeFactory scopeFactory,
        ILogger<DeduplicationService> logger,
        ITenantAccessor? tenantAccessor = null,
        ICategoryReadContext? categoryReadContext = null)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _scopeFactory = scopeFactory;
        _logger = logger;
        _tenantAccessor = tenantAccessor;
        _categoryReadContext = categoryReadContext;
    }

    /// <summary>
    /// Returns the single canonical id present in <paramref name="canonicalIds"/>, or null when
    /// the sequence is empty or spans more than one group. Two candidate groups mean the record
    /// cannot be attributed to one event, so the wide path refuses rather than guessing.
    /// </summary>
    private static Guid? SingleCandidateCanonical(IEnumerable<Guid> canonicalIds)
    {
        Guid? only = null;
        foreach (var id in canonicalIds)
        {
            if (only is null)
                only = id;
            else if (only.Value != id)
                return null;
        }

        return only;
    }

    /// <summary>
    /// Loads the full set of data sources behind each of the given canonical groups. The whole
    /// group is read rather than just the links inside a matching window, so a same-source record
    /// sitting outside the window still blocks a wide match. A group in <paramref name="unlinked"/> is
    /// not stored and its one record is its source.
    /// </summary>
    private static async Task<Dictionary<Guid, HashSet<string>>> LoadGroupSourcesAsync(
        NocturneDbContext db,
        string recordTypeStr,
        List<Guid> canonicalIds,
        IReadOnlyDictionary<Guid, LinkedRecordEntity> unlinked,
        CancellationToken ct)
    {
        var sourcesByCanonical = new Dictionary<Guid, HashSet<string>>();
        void Add(Guid canonicalId, string dataSource)
        {
            if (!sourcesByCanonical.TryGetValue(canonicalId, out var sources))
            {
                sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                sourcesByCanonical[canonicalId] = sources;
            }

            sources.Add(dataSource);
        }

        var stored = new List<Guid>();
        foreach (var canonicalId in canonicalIds)
        {
            if (unlinked.TryGetValue(canonicalId, out var link))
                Add(canonicalId, link.DataSource);
            else
                stored.Add(canonicalId);
        }

        if (stored.Count == 0)
            return sourcesByCanonical;

        var rows = await db.LinkedRecords
            .AsNoTracking()
            .Where(lr => lr.RecordType == recordTypeStr && stored.Contains(lr.CanonicalId))
            .Select(lr => new { lr.CanonicalId, lr.DataSource })
            .ToListAsync(ct);

        foreach (var row in rows)
            Add(row.CanonicalId, row.DataSource);

        return sourcesByCanonical;
    }

    /// <summary>
    /// Loads the first and last link timestamp of each of the given canonical groups — the group's
    /// extent, which after a wide join can be far wider than the gap its primary suggests.
    /// <para>
    /// The canonical ids travel as a client-materialized array. A full-history job has no time
    /// bound to narrow it with, so the array is as large as the type's group count; the aggregate
    /// itself stays a grouped index scan.
    /// </para>
    /// </summary>
    private static async Task<Dictionary<Guid, (long Min, long Max)>> LoadGroupExtentsAsync(
        NocturneDbContext db,
        string recordTypeStr,
        List<Guid> canonicalIds,
        CancellationToken ct)
    {
        if (canonicalIds.Count == 0)
            return [];

        var rows = await db.LinkedRecords
            .AsNoTracking()
            .Where(lr => lr.RecordType == recordTypeStr && canonicalIds.Contains(lr.CanonicalId))
            .GroupBy(lr => lr.CanonicalId)
            .Select(g => new
            {
                CanonicalId = g.Key,
                Min = g.Min(lr => lr.SourceTimestamp),
                Max = g.Max(lr => lr.SourceTimestamp)
            })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.CanonicalId, r => (r.Min, r.Max));
    }

    /// <summary>
    /// Records a match between two different data sources. Same-source and unknown-source matches
    /// are not logged: the offset being tracked is the clock drift between connectors. Matches the
    /// tight window already handled log at Debug; a <paramref name="wide"/> match logs at
    /// Information, because a drift large enough to need the wide window is the condition being
    /// watched and would be invisible at Debug in production. An offset past
    /// <see cref="CrossSourceOffsetWarningMillis"/> logs at Warning because matching stops working
    /// entirely once the drift passes <see cref="WideMatchingWindow"/>.
    /// </summary>
    private void LogCrossSourceMatch(
        RecordType recordType,
        string? incomingSource,
        string? matchedSource,
        long offsetMillis,
        bool wide)
    {
        if (string.IsNullOrEmpty(incomingSource)
            || string.IsNullOrEmpty(matchedSource)
            || string.Equals(incomingSource, matchedSource, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var level = Math.Abs(offsetMillis) > CrossSourceOffsetWarningMillis
            ? LogLevel.Warning
            : wide ? LogLevel.Information : LogLevel.Debug;

        _logger.Log(
            level,
            "Cross-source {RecordType} match at {OffsetSeconds}s between {IncomingSource} and {MatchedSource}",
            recordType, Math.Abs(offsetMillis) / 1000.0, incomingSource, matchedSource);
    }

    /// <summary>
    /// True when a data source can establish cross-source provenance: it is present and is not
    /// <see cref="DeduplicationInput.UnknownDataSource"/>, which names no connector and so cannot
    /// distinguish a second connector's copy of a dose from a manually entered second dose.
    /// </summary>
    private static bool CanEstablishCrossSource([NotNullWhen(true)] string? dataSource) =>
        !string.IsNullOrEmpty(dataSource)
        && !string.Equals(dataSource, DeduplicationInput.UnknownDataSource, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a canonical group's data sources can establish cross-source provenance: the group
    /// has at least one source and none of them is the unknown sentinel.
    /// </summary>
    private static bool CanEstablishCrossSource(HashSet<string> sources) =>
        sources.Count > 0 && !sources.Contains(DeduplicationInput.UnknownDataSource);

    /// <summary>
    /// Whether the tight path refuses to put a record from <paramref name="source"/> into a group
    /// already holding <paramref name="groupSources"/>. Two same-amount records from one source
    /// seconds apart are two doses, and the tight window's tolerances cannot tell them from one, so
    /// a group takes at most one record per source. The exception is a source and record type that
    /// <see cref="EmitsDuplicateEvents"/>, whose twins must still merge.
    /// <see cref="DeduplicationInput.UnknownDataSource"/> counts as one source like any other.
    /// Only dose-like record types are guarded; see <see cref="TracksTightSources"/>.
    /// </summary>
    internal static bool RefusesTightJoin(RecordType recordType, string source, IReadOnlySet<string>? groupSources) =>
        TracksTightSources(recordType)
        && !EmitsDuplicateEvents(recordType, source)
        && groupSources is not null
        && groupSources.Contains(source);

    /// <summary>
    /// True for a source and record type known to report one event twice under two ids. Tidepool
    /// imports some carb events as two food records. It is not known to double any other type, so
    /// two Tidepool boluses of one size at one second are two doses and stay apart.
    /// </summary>
    private static bool EmitsDuplicateEvents(RecordType recordType, string source) =>
        recordType == RecordType.CarbIntake
        && string.Equals(source, DataSources.TidepoolConnector, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <see cref="RefusesTightJoin"/> applies to this record type. Sensor glucose and state
    /// spans are exempt: neither is a discrete dose, and one uploader routinely reports the same
    /// reading or the same span twice seconds apart (a repeated device status re-opens the same pump
    /// mode), which must still collapse to one.
    /// </summary>
    private static bool TracksTightSources(RecordType recordType) =>
        recordType is not (RecordType.SensorGlucose or RecordType.StateSpan);

    /// <inheritdoc />
    public async Task<DeduplicationBatchResult> DeduplicateBatchAsync(
        RecordType recordType,
        IReadOnlyList<DeduplicationInput> records,
        CancellationToken ct = default)
    {
        if (records.Count == 0)
            return new DeduplicationBatchResult(0, 0, 0, 0);

        // Small batches go straight through; only large backfill batches pay the sort.
        if (records.Count <= DedupChunkSize)
            return await DeduplicateChunkAsync(recordType, records, ct);

        // Sort by event time and process in chunks so each chunk's matching-window query
        // spans only a narrow range. Chunks run in time order and each is persisted before
        // the next, so a record at a chunk boundary still matches its neighbour in the
        // adjacent chunk through the freshly-written rows the next window query re-reads
        // (the +/-MatchingWindow expansion covers the seam); ReconcileNewLinksAsync collapses
        // any residual cross-chunk pair idempotently.
        var processed = 0;
        var groupsCreated = 0;
        var recordsLinked = 0;
        var duplicateGroups = 0;

        foreach (var chunk in TimeOrderedChunks(records, r => r.Mills))
        {
            var result = await DeduplicateChunkAsync(recordType, chunk, ct);
            processed += result.Processed;
            groupsCreated += result.GroupsCreated;
            recordsLinked += result.RecordsLinked;
            duplicateGroups += result.DuplicateGroups;
        }

        return new DeduplicationBatchResult(processed, groupsCreated, recordsLinked, duplicateGroups);
    }

    /// <summary>
    /// Orders <paramref name="items"/> by event time and slices them into chunks of at most
    /// <see cref="DedupChunkSize"/>, also starting a new chunk wherever two consecutive items sit
    /// more than <paramref name="maxGapMillis"/> apart, or an item sits more than
    /// <paramref name="maxSpanMillis"/> after its chunk's first.
    /// </summary>
    private static IEnumerable<List<T>> TimeOrderedChunks<T>(
        IEnumerable<T> items, Func<T, long> mills, long maxGapMillis = long.MaxValue,
        long maxSpanMillis = long.MaxValue)
    {
        var chunk = new List<T>();
        var first = 0L;
        var previous = 0L;
        foreach (var item in items.OrderBy(mills))
        {
            var at = mills(item);
            if (chunk.Count == DedupChunkSize
                || (chunk.Count > 0 && (at - previous > maxGapMillis || at - first > maxSpanMillis)))
            {
                yield return chunk;
                chunk = [];
            }

            if (chunk.Count == 0)
                first = at;
            chunk.Add(item);
            previous = at;
        }

        if (chunk.Count > 0)
            yield return chunk;
    }

    /// <summary>
    /// Links a single bounded chunk of records to canonical groups in one matching-window query.
    /// Callers must keep <paramref name="records"/> within <see cref="DedupChunkSize"/> and time
    /// contiguous; <see cref="DeduplicateBatchAsync"/> is the entry point that enforces both.
    /// </summary>
    private async Task<DeduplicationBatchResult> DeduplicateChunkAsync(
        RecordType recordType,
        IReadOnlyList<DeduplicationInput> records,
        CancellationToken ct)
    {
        if (records.Count == 0)
            return new DeduplicationBatchResult(0, 0, 0, 0);

        await using var db = await _contexts.CreateAsync(ct);
        return await UnderWriteLockAsync(db, token => LinkChunkAsync(db, recordType, records, token), ct);
    }

    /// <summary>The body of <see cref="DeduplicateChunkAsync"/>, run under its write lock.</summary>
    private async Task<DeduplicationBatchResult> LinkChunkAsync(
        NocturneDbContext db,
        RecordType recordType,
        IReadOnlyList<DeduplicationInput> records,
        CancellationToken ct)
    {
        var recordTypeStr = RecordTypeKeys.Key(recordType);
        var wideEligible = WideMatchableTypes.Contains(recordType);

        // 1. Compute union time window. Wide-eligible types load the wider window so the wide
        //    fallback below sees the same candidates a standalone query would; the tight scan is
        //    bounded by binary search on the tight window either way, so its results are unchanged.
        var loadWindowMillis = wideEligible ? WideMatchingWindowMillis : MatchingWindowMillis;
        var minMills = records.Min(r => r.Mills) - loadWindowMillis;
        var maxMills = records.Max(r => r.Mills) + loadWindowMillis;

        // 2. One query: all linked_records in the window for this type.
        //    Read-only: matched against, never mutated — the new links are constructed fresh
        //    below and AddRange'd. AsNoTracking avoids change-tracker snapshots, which for a
        //    wide-window historical backfill batch (thousands of records spanning months) would
        //    otherwise materialize tens of thousands of tracked entities and exhaust memory.
        var allPotentialMatches = await db.LinkedRecords
            .AsNoTracking()
            .Where(lr => lr.RecordType == recordTypeStr)
            .Where(lr => lr.SourceTimestamp >= minMills && lr.SourceTimestamp <= maxMills)
            .ToListAsync(ct);

        // 2b. The window's records whose singleton link was pruned, as the groups they were. One that
        //     a record joins is stored with it. The chunk's own records are never their own candidates.
        var inputIdSet = records.Select(r => r.RecordId).ToHashSet();
        var unlinked = (await UnlinkedAsync(db, recordType, minMills, maxMills, ct))
            .Where(u => !inputIdSet.Contains(u.RecordId))
            .ToDictionary(u => u.CanonicalId);
        allPotentialMatches.AddRange(unlinked.Values);

        // 3. One query: the criteria and soft-deleted status behind every candidate link, read by
        //    both the tight matcher below and the wide pass.
        var referencedIds = allPotentialMatches.Select(m => m.RecordId).ToHashSet();
        var info = await LoadRecordInfoAsync(db, recordType, referencedIds, ct);

        bool Matches(Guid recordId, MatchCriteria criteria) =>
            info.TryGetValue(recordId, out var candidate)
            && !candidate.IsDeleted
            && CriteriaMatch(recordType, candidate.Criteria, criteria);

        // 3b. The data sources behind each candidate group, read by the tight source guard and the
        //     wide pass. Mutated as this chunk assigns records, so a second record of the same
        //     source sees the group its predecessor just joined.
        var trackSources = wideEligible || TracksTightSources(recordType);
        Dictionary<Guid, HashSet<string>> groupSources = new();
        if (trackSources)
        {
            groupSources = await LoadGroupSourcesAsync(
                db,
                recordTypeStr, allPotentialMatches.Select(m => m.CanonicalId).Distinct().ToList(), unlinked, ct);
        }

        bool Refused(Guid canonical, string source) =>
            RefusesTightJoin(recordType, source, groupSources.GetValueOrDefault(canonical));

        // 4. One query: which input records are already linked?
        var inputIds = records.Select(r => r.RecordId).ToList();
        var alreadyLinked = (await db.LinkedRecords
            .Where(lr => lr.RecordType == recordTypeStr && inputIds.Contains(lr.RecordId))
            .Select(lr => lr.RecordId)
            .ToListAsync(ct))
            .ToHashSet();

        // 5. Pre-compute structures so the per-record loop is O(log M + window) instead of O(N*(M+N)):
        //    - sortedMatches: timestamp-sorted, sliced via binary search
        //    - existingCanonicals: O(1) "is this canonical already in DB?" check for IsPrimary
        //    - newCanonicalsSeen: O(1) "did this batch already create a primary for this canonical?"
        //    - newCanonicalReps: one (mills, canonicalId, criteria) per new canonical for intra-batch
        //      matching — record-vs-canonical instead of record-vs-every-prior-record
        allPotentialMatches.Sort((a, b) => a.SourceTimestamp.CompareTo(b.SourceTimestamp));
        var sortedTimestamps = new long[allPotentialMatches.Count];
        for (int i = 0; i < allPotentialMatches.Count; i++)
        {
            sortedTimestamps[i] = allPotentialMatches[i].SourceTimestamp;
        }

        var existingCanonicals = new HashSet<Guid>(allPotentialMatches.Count);
        foreach (var m in allPotentialMatches)
        {
            existingCanonicals.Add(m.CanonicalId);
        }

        var newCanonicalsSeen = new HashSet<Guid>();
        var newCanonicalReps = new List<(long mills, Guid canonicalId, MatchCriteria criteria, string dataSource)>();

        // Every record this chunk assigns, joined or newly minted. allPotentialMatches is read
        // before any link is written, so without this the wide scan would not see records assigned
        // earlier in the same chunk — and the ambiguity guard would count fewer candidates than the
        // persisted state holds, merging where two sequential batches would refuse. The tight path
        // deliberately keeps using newCanonicalReps: one representative per canonical is enough
        // there because its members match each other by transitivity.
        var chunkAssignments = new List<(long mills, Guid canonicalId, MatchCriteria criteria, string dataSource)>();

        // Canonical groups whose primary may need re-deriving once the new links are persisted.
        var repickCanonicals = new HashSet<Guid>();

        var newLinks = new List<LinkedRecordEntity>();
        var restoredLinks = 0;
        var groupsCreated = 0;
        var duplicateGroups = 0;

        foreach (var record in records)
        {
            if (alreadyLinked.Contains(record.RecordId))
                continue;

            var windowStart = record.Mills - MatchingWindowMillis;
            var windowEnd = record.Mills + MatchingWindowMillis;

            Guid? canonicalId = null;

            // Match against existing canonical groups: scan only the timestamp slice.
            int lo = LowerBoundTimestamp(sortedTimestamps, windowStart);
            int hi = UpperBoundTimestamp(sortedTimestamps, windowEnd);
            for (int i = lo; i < hi; i++)
            {
                var m = allPotentialMatches[i];
                if (Matches(m.RecordId, record.Criteria) && !Refused(m.CanonicalId, record.DataSource))
                {
                    canonicalId = m.CanonicalId;
                    duplicateGroups++;
                    LogCrossSourceMatch(
                        recordType, record.DataSource, m.DataSource, m.SourceTimestamp - record.Mills, wide: false);
                    break;
                }
            }

            // Intra-batch match: scan canonicals created earlier in this batch, not records.
            // Records sharing a canonical share matching criteria by transitivity, so checking
            // one representative per canonical is sufficient.
            if (canonicalId == null)
            {
                foreach (var (priorMills, priorCanonical, priorCriteria, priorSource) in newCanonicalReps)
                {
                    if (Math.Abs(priorMills - record.Mills) <= MatchingWindowMillis
                        && CriteriaMatch(recordType, priorCriteria, record.Criteria)
                        && !Refused(priorCanonical, record.DataSource))
                    {
                        canonicalId = priorCanonical;
                        duplicateGroups++;
                        LogCrossSourceMatch(
                            recordType, record.DataSource, priorSource, priorMills - record.Mills, wide: false);
                        break;
                    }
                }
            }

            // Wide fallback: exactly one exact-value candidate group in the wide window, holding
            // no record of this record's own source. Records assigned earlier in this chunk are
            // candidates too, so a wide pair split across a batch resolves the same way it would
            // across two batches.
            if (canonicalId == null && wideEligible && CanEstablishCrossSource(record.DataSource))
            {
                var wideLo = LowerBoundTimestamp(sortedTimestamps, record.Mills - WideMatchingWindowMillis);
                var wideHi = UpperBoundTimestamp(sortedTimestamps, record.Mills + WideMatchingWindowMillis);

                var wideCandidates = new List<(Guid CanonicalId, long Mills, string DataSource)>();
                for (int i = wideLo; i < wideHi; i++)
                {
                    var m = allPotentialMatches[i];
                    if (info.TryGetValue(m.RecordId, out var recordInfo)
                        && !recordInfo.IsDeleted
                        && CriteriaMatch(recordType, recordInfo.Criteria, record.Criteria, exact: true))
                    {
                        wideCandidates.Add((m.CanonicalId, m.SourceTimestamp, m.DataSource));
                    }
                }

                foreach (var (priorMills, priorCanonical, priorCriteria, priorSource) in chunkAssignments)
                {
                    if (Math.Abs(priorMills - record.Mills) <= WideMatchingWindowMillis
                        && CriteriaMatch(recordType, priorCriteria, record.Criteria, exact: true))
                    {
                        wideCandidates.Add((priorCanonical, priorMills, priorSource));
                    }
                }

                var wideCanonical = SingleCandidateCanonical(wideCandidates.Select(c => c.CanonicalId));

                // groupSources reflects only this context's view. A concurrent ingest of the same
                // source into the same group can still slip past between this read and the write;
                // that race is pre-existing and self-corrects on the next reconcile pass.
                if (wideCanonical is not null
                    && groupSources.TryGetValue(wideCanonical.Value, out var candidateSources)
                    && CanEstablishCrossSource(candidateSources)
                    && !candidateSources.Contains(record.DataSource))
                {
                    canonicalId = wideCanonical;
                    duplicateGroups++;

                    // Chunk-minted groups need re-deriving too: this chunk's records are not
                    // sorted on the fast path, so a later-timestamped record can mint the group
                    // that an earlier one then joins.
                    repickCanonicals.Add(wideCanonical.Value);

                    var closest = wideCandidates
                        .Where(c => c.CanonicalId == wideCanonical.Value)
                        .OrderBy(c => Math.Abs(c.Mills - record.Mills))
                        .First();
                    LogCrossSourceMatch(
                        recordType, record.DataSource, closest.DataSource, closest.Mills - record.Mills, wide: true);
                }
            }

            bool isNewCanonical = false;
            if (canonicalId == null)
            {
                canonicalId = Guid.CreateVersion7();
                groupsCreated++;
                isNewCanonical = true;
            }
            else if (recordType == RecordType.StateSpan)
            {
                repickCanonicals.Add(canonicalId.Value);
            }

            // IsPrimary iff this is the first link this batch creates for a not-already-existing canonical.
            var isPrimary = !existingCanonicals.Contains(canonicalId.Value)
                            && newCanonicalsSeen.Add(canonicalId.Value);

            if (unlinked.Remove(canonicalId.Value, out var restored))
            {
                newLinks.Add(restored);
                restoredLinks++;
            }

            newLinks.Add(new LinkedRecordEntity
            {
                CanonicalId = canonicalId.Value,
                RecordType = recordTypeStr,
                RecordId = record.RecordId,
                SourceTimestamp = record.Mills,
                DataSource = record.DataSource,
                IsPrimary = isPrimary
            });

            if (isNewCanonical)
            {
                newCanonicalReps.Add((record.Mills, canonicalId.Value, record.Criteria, record.DataSource));
            }

            chunkAssignments.Add((record.Mills, canonicalId.Value, record.Criteria, record.DataSource));

            // Keep the group's source set current within this chunk so a later same-source record
            // sees the group it just joined and refuses to join it.
            if (trackSources)
            {
                if (!groupSources.TryGetValue(canonicalId.Value, out var assignedSources))
                {
                    assignedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    groupSources[canonicalId.Value] = assignedSources;
                }

                assignedSources.Add(record.DataSource);
            }
        }

        // 6. Bulk insert
        if (newLinks.Count > 0)
        {
            db.LinkedRecords.AddRange(newLinks);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        // 7. A wide join reaches up to ten minutes back, so it can land earlier than the group's
        //    primary — and unlike reconcile-merged groups, ingest-formed groups are never revisited
        //    by MergeDuplicateGroupsAsync. Re-derive the primary here, or the event time the group
        //    displays would depend on which connector synced first. A state span joined on the tight
        //    path is re-derived too, since a copy can supersede the primary (see PickSurvivor). Other
        //    tight joins keep their sticky primary, which is long-standing behaviour.
        if (repickCanonicals.Count > 0)
            await RepickPrimariesAsync(db, recordType, repickCanonicals.ToArray(), ct);

        return new DeduplicationBatchResult(
            Processed: records.Count,
            GroupsCreated: groupsCreated,
            RecordsLinked: newLinks.Count - restoredLinks,
            DuplicateGroups: duplicateGroups);
    }

    /// <inheritdoc cref="DuplicateGroupPrimaries.RepickAsync"/>
    private static Task<IReadOnlyList<Guid>> RepickPrimariesAsync(
        NocturneDbContext db, RecordType recordType, Guid[] canonicalIds, CancellationToken ct)
        => DuplicateGroupPrimaries.RepickAsync(db, recordType, canonicalIds, ct);

    /// <summary>
    /// The link a canonical group's <see cref="LinkedRecordEntity.IsPrimary"/> belongs on: the
    /// earliest promotable record, falling back to the earliest overall when the group holds
    /// nothing promotable. Reads hide soft-deleted records and non-primary links alike, so
    /// promoting either a deleted record or an orphaned link renders the whole group as nothing —
    /// a record id missing from <paramref name="rowInfo"/> is an orphaned link and is no more
    /// promotable than a deleted one. <see cref="LinkedRecordEntity.RecordId"/> settles a
    /// timestamp tie, which a cross-source pair normally has.
    /// <para>
    /// Among state spans, a member superseded by another live member of its own group is passed
    /// over while a promotable member is not, and of the rest an open one wins over the earliest:
    /// a copy re-reported seconds later closes the earlier copy at its own start, and Loop's
    /// treatment and devicestatus records of one override never close each other but may end
    /// apart. Promoting a closed copy would show the group as ended while it is still in effect.
    /// Between closed copies the earliest still wins, so an observed end is not traded for a
    /// planned one.
    /// </para>
    /// </summary>
    internal static LinkedRecordEntity PickSurvivor(
        IEnumerable<LinkedRecordEntity> rows,
        IReadOnlyDictionary<Guid, RecordInfo> rowInfo)
        => PickSurvivor(
            rows,
            id => rowInfo.TryGetValue(id, out var ri) && !ri.IsDeleted,
            id => rowInfo.TryGetValue(id, out var ri) ? ri.Span : default);

    /// <inheritdoc cref="PickSurvivor(IEnumerable{LinkedRecordEntity}, IReadOnlyDictionary{Guid, RecordInfo})"/>
    /// <param name="rows">The group's links.</param>
    /// <param name="isPromotable">Whether a record id names a live record.</param>
    /// <param name="spanEnd">How a record id ended, when it is a state span.</param>
    internal static LinkedRecordEntity PickSurvivor(
        IEnumerable<LinkedRecordEntity> rows,
        Func<Guid, bool> isPromotable,
        Func<Guid, SpanEnd> spanEnd)
    {
        var ordered = rows.OrderBy(r => r.SourceTimestamp).ThenBy(r => r.RecordId).ToList();
        var live = ordered.Select(r => r.RecordId).Where(isPromotable).ToHashSet();

        var unsuperseded = ordered
            .Where(r => live.Contains(r.RecordId)
                && !(spanEnd(r.RecordId).SupersededById is { } by && live.Contains(by)))
            .ToList();

        return unsuperseded.FirstOrDefault(r => spanEnd(r.RecordId).EndTimestamp is null)
               ?? unsuperseded.FirstOrDefault()
               ?? ordered.FirstOrDefault(r => live.Contains(r.RecordId))
               ?? ordered[0];
    }

    /// <summary>
    /// How long after a member started a state span group with no open member is still checked by
    /// <see cref="RepairStateSpanPrimariesAsync"/>. Reads that matter ask what is in effect now, and
    /// bounding the check keeps it from rescanning the tenant's whole history under the write lock.
    /// </summary>
    internal static readonly TimeSpan StateSpanRepairHorizon = TimeSpan.FromDays(1);

    /// <inheritdoc />
    public async Task<int> RepairStateSpanPrimariesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        return await UnderWriteLockAsync(db, async token =>
        {
            var misplaced = await MisplacedStateSpanPrimaries(db, DateTime.UtcNow - StateSpanRepairHorizon)
                .ToArrayAsync(token);

            return misplaced.Length == 0
                ? 0
                : (await RepickPrimariesAsync(db, RecordType.StateSpan, misplaced, token)).Count;
        }, cancellationToken);
    }

    /// <summary>
    /// The state span groups with a member open or started after <paramref name="startedAfter"/>
    /// whose primary is closed while a live mate superseded it or is still open: the groups
    /// <see cref="PickSurvivor(IEnumerable{LinkedRecordEntity}, Func{Guid, bool}, Func{Guid, SpanEnd})"/>
    /// would move. The two halves of the bound are separate queries so each is an index range:
    /// the partial open-span index and the links' event time.
    /// </summary>
    internal static IQueryable<Guid> MisplacedStateSpanPrimaries(NocturneDbContext db, DateTime startedAfter)
    {
        var key = RecordTypeKeys.Key(RecordType.StateSpan);
        var startedAfterMills = new DateTimeOffset(startedAfter, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var withOpenMember = db.StateSpans
            .Where(s => s.EndTimestamp == null)
            .Join(db.LinkedRecords.Where(m => m.RecordType == key),
                s => s.Id, m => m.RecordId, (s, m) => m.CanonicalId);
        var startedRecently = db.LinkedRecords
            .Where(m => m.RecordType == key && m.SourceTimestamp > startedAfterMills)
            .Select(m => m.CanonicalId);
        var recent = withOpenMember.Concat(startedRecently);

        return db.LinkedRecords.AsNoTracking()
            .Where(p => p.RecordType == key && p.IsPrimary && recent.Contains(p.CanonicalId))
            .Join(db.StateSpans.Where(s => s.EndTimestamp != null),
                p => p.RecordId, s => s.Id,
                (p, s) => new { p.CanonicalId, s.Id, s.SupersededById })
            .Where(p => db.LinkedRecords.Any(m =>
                m.RecordType == key && m.CanonicalId == p.CanonicalId && m.RecordId != p.Id
                && db.StateSpans.Any(s => s.Id == m.RecordId
                    && (s.Id == p.SupersededById || s.EndTimestamp == null))))
            .Select(p => p.CanonicalId)
            .Distinct();
    }

    /// <summary>
    /// Collapses duplicate canonical groups for a record type within the current tenant.
    /// Two groups merge when their primary records fall within <see cref="MatchingWindowMillis"/>
    /// of each other and their <see cref="MatchCriteria"/> match; merging is transitive
    /// (union-find) and source-agnostic, mirroring insert-time <see cref="DeduplicateBatchAsync"/>
    /// semantics. A second pass then applies the same wide rules the insert path uses, so a
    /// cross-source pair missed at ingest (out-of-order connector syncs) heals here.
    /// For each merged super-group <see cref="PickSurvivor(IEnumerable{LinkedRecordEntity}, IReadOnlyDictionary{Guid, RecordInfo})"/> chooses the surviving primary; all
    /// linked rows are re-pointed to the survivor's canonical id and <c>IsPrimary</c> is set on
    /// exactly the survivor.
    /// </summary>
    /// <param name="recordType">The record type whose canonical groups are reconciled.</param>
    /// <param name="candidateCanonicalIds">
    /// When non-null, only canonical groups in this set plus their event-window neighbours are
    /// considered; when null, all primaries of the type are considered.
    /// </param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The number of duplicate groups collapsed (merges that reduced group count).</returns>
    internal async Task<int> MergeDuplicateGroupsAsync(
        RecordType recordType,
        IReadOnlySet<Guid>? candidateCanonicalIds,
        CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        return await MergeDuplicateGroupsAsync(db, recordType, candidateCanonicalIds, ct);
    }

    /// <inheritdoc cref="MergeDuplicateGroupsAsync(RecordType, IReadOnlySet{Guid}?, CancellationToken)"/>
    private async Task<int> MergeDuplicateGroupsAsync(
        NocturneDbContext db,
        RecordType recordType,
        IReadOnlySet<Guid>? candidateCanonicalIds,
        CancellationToken ct)
    {
        var recordTypeStr = RecordTypeKeys.Key(recordType);

        if (candidateCanonicalIds == null)
        {
            // Full reconcile: load every primary of this type, ordered by source timestamp.
            // Read-only (union-find input); the rows actually re-pointed are loaded separately
            // and tracked. AsNoTracking keeps this O(all primaries) read off the heap.
            var allPrimaries = await db.LinkedRecords
                .AsNoTracking()
                .Where(lr => lr.RecordType == recordTypeStr && lr.IsPrimary)
                .OrderBy(lr => lr.SourceTimestamp)
                .ToListAsync(ct);

            var all = WithUnlinked(allPrimaries, await UnlinkedAsync(db, recordType, null, null, ct));
            return await MergePrimariesAsync(db, recordType, all.Primaries, all.Unlinked, null, null, null, ct);
        }

        // Candidate-bounded reconcile: never load all primaries. Load only the candidate
        // canonicals' primary links to learn their event timestamps, then DB-bound each neighbour
        // query to a slice of runs of candidates plus the window either side. This keeps the
        // candidate path bounded by the primaries in the slices around the candidates rather than
        // by all primaries.
        var candidatePrimaries = await PrimariesOf(db, recordTypeStr, candidateCanonicalIds).ToListAsync(ct);

        if (candidatePrimaries.Count == 0)
            return 0;

        // Wide-eligible types must see their wide neighbours too, or a drifted pair could
        // never heal on the candidate-bounded path. The reach is twice the wide window rather
        // than one: deciding a pair needs to see any third same-value group that would make it
        // ambiguous, and such a group can sit a full window beyond the pair's own edge. At one
        // window the same three groups merge or refuse depending on which one is the candidate.
        var wideEligible = WideMatchableTypes.Contains(recordType);
        var neighbourWindowMillis = wideEligible
            ? 2 * WideMatchingWindowMillis
            : MatchingWindowMillis;

        // Links are paged in creation order, so one batch can hold candidates years apart in event
        // time. A single range over them would load every primary in between. Runs split where two
        // candidates' neighbour ranges cannot meet, so each run is decided over its own neighbour range.
        var runs = TimeOrderedChunks(candidatePrimaries, p => p.SourceTimestamp, 2 * neighbourWindowMillis);

        // A wide load selects whole groups by their links, so its primaries cannot be cut back to one
        // run's range in memory; a wide type loads per run.
        var slices = wideEligible
            ? runs.Select(run => new List<List<LinkedRecordEntity>> { run })
            : TimeOrderedChunks(runs, run => run[0].SourceTimestamp, maxSpanMillis: NeighbourSliceMillis);

        var merged = 0;
        foreach (var slice in slices)
        {
            NeighbourPrimaries? loaded = null;
            foreach (var run in slice)
            {
                var minTs = run[0].SourceTimestamp - neighbourWindowMillis;
                var maxTs = run[^1].SourceTimestamp + neighbourWindowMillis;
                loaded ??= await LoadNeighbourPrimariesAsync(
                    db, recordType, minTs, slice[^1][^1].SourceTimestamp + neighbourWindowMillis, ct);

                var primaries = wideEligible
                    ? loaded.Primaries
                    : loaded.Primaries.Where(p => p.SourceTimestamp >= minTs && p.SourceTimestamp <= maxTs).ToList();
                var runMerged = await MergePrimariesAsync(
                    db, recordType, primaries, loaded.Unlinked, candidateCanonicalIds, minTs, maxTs, ct);

                // A merge re-points links, possibly promoting one inside a later run's range.
                if (runMerged > 0)
                    loaded = null;
                merged += runMerged;
            }
        }

        return merged;
    }

    /// <summary>
    /// Primaries in source timestamp order, with the groups among them that are not stored: records
    /// whose singleton link was pruned (<see cref="UnlinkedAsync"/>), keyed by their canonical id.
    /// </summary>
    private sealed record NeighbourPrimaries(
        List<LinkedRecordEntity> Primaries,
        IReadOnlyDictionary<Guid, LinkedRecordEntity> Unlinked);

    /// <summary>
    /// <paramref name="primaries"/> with <paramref name="unlinked"/> merged into their order. The sort
    /// is stable, so with nothing unlinked the stored primaries keep the order they were read in.
    /// </summary>
    private static NeighbourPrimaries WithUnlinked(
        List<LinkedRecordEntity> primaries, List<LinkedRecordEntity> unlinked) =>
        new(
            unlinked.Count == 0 ? primaries : [.. primaries.Concat(unlinked).OrderBy(p => p.SourceTimestamp)],
            unlinked.ToDictionary(u => u.CanonicalId));

    /// <summary>
    /// The primaries <see cref="MergeDuplicateGroupsAsync"/> must see to decide every pair touching
    /// a candidate whose neighbour range is <paramref name="minTs"/> to <paramref name="maxTs"/>.
    /// </summary>
    private static async Task<NeighbourPrimaries> LoadNeighbourPrimariesAsync(
        NocturneDbContext db, RecordType recordType, long minTs, long maxTs, CancellationToken ct) =>
        WithUnlinked(
            await LoadStoredNeighbourPrimariesAsync(db, recordType, minTs, maxTs, ct),
            await UnlinkedAsync(db, recordType, minTs, maxTs, ct));

    /// <summary>The stored part of <see cref="LoadNeighbourPrimariesAsync"/>.</summary>
    private static async Task<List<LinkedRecordEntity>> LoadStoredNeighbourPrimariesAsync(
        NocturneDbContext db, RecordType recordType, long minTs, long maxTs, CancellationToken ct)
    {
        var recordTypeStr = RecordTypeKeys.Key(recordType);

        if (!WideMatchableTypes.Contains(recordType))
        {
            return await db.LinkedRecords
                .AsNoTracking()
                .Where(lr => lr.RecordType == recordTypeStr && lr.IsPrimary
                             && lr.SourceTimestamp >= minTs && lr.SourceTimestamp <= maxTs)
                .OrderBy(lr => lr.SourceTimestamp)
                .ToListAsync(ct);
        }

        // A wide-joined group spans up to a window, so its primary can sit outside the
        // neighbour range while its members sit inside it. Select the groups by their links
        // and then load those groups' primaries, so no group is ever half-visible to the
        // extent comparison. Second, unremarked consequence: the tight pass now sees
        // those same extra primaries, so its reach on this path widens too, which is convergent,
        // since it only lets the tight rules collapse pairs a later pass would have anyway.
        var neighbourCanonicals = await db.LinkedRecords
            .AsNoTracking()
            .Where(lr => lr.RecordType == recordTypeStr
                         && lr.SourceTimestamp >= minTs && lr.SourceTimestamp <= maxTs)
            .Select(lr => lr.CanonicalId)
            .Distinct()
            .ToListAsync(ct);

        // Selected by canonical id alone. A timestamp bound here would be unsound at any
        // width: everything in the merge (the union-find, the extents, the boundary deferral)
        // is built from this list, so a group dropped for having a distant primary is not
        // deferred, it is invisible, and the pairs it would have made ambiguous merge.
        return await db.LinkedRecords
            .AsNoTracking()
            .Where(lr => lr.RecordType == recordTypeStr && lr.IsPrimary
                         && neighbourCanonicals.Contains(lr.CanonicalId))
            .OrderBy(lr => lr.SourceTimestamp)
            .ToListAsync(ct);
    }

    /// <summary>
    /// The union-find, wide pass and re-pointing behind <see cref="MergeDuplicateGroupsAsync"/>, over
    /// <paramref name="primaries"/> ordered by source timestamp.
    /// </summary>
    /// <param name="unlinked">
    /// The groups among <paramref name="primaries"/> that are not stored. One merged into a group is
    /// stored with it.
    /// </param>
    /// <param name="neighbourMinTs">
    /// The start of the span the candidate-bounded path loaded. Groups whose extent reaches within a
    /// wide window of either end may have an ambiguator just outside it, so the wide pass defers
    /// them. Null on the full path, which loads everything and has no boundary: absence rather than
    /// a sentinel value, so the deferral cannot be reached with a bound that would overflow the
    /// subtraction.
    /// </param>
    /// <param name="neighbourMaxTs">The end of that span; null exactly when <paramref name="neighbourMinTs"/> is.</param>
    private async Task<int> MergePrimariesAsync(
        NocturneDbContext db,
        RecordType recordType,
        List<LinkedRecordEntity> primaries,
        IReadOnlyDictionary<Guid, LinkedRecordEntity> unlinked,
        IReadOnlySet<Guid>? candidateCanonicalIds,
        long? neighbourMinTs,
        long? neighbourMaxTs,
        CancellationToken ct)
    {
        var recordTypeStr = RecordTypeKeys.Key(recordType);
        var wideEligible = WideMatchableTypes.Contains(recordType);

        if (primaries.Count < 2)
            return 0;

        // 2. Load criteria + deleted status for the primary record ids.
        var primaryIds = primaries.Select(p => p.RecordId).ToHashSet();
        var info = await LoadRecordInfoAsync(db, recordType, primaryIds, ct);

        // 3. Sorted sliding-window union-find over primaries by canonical id.
        var parent = new Dictionary<Guid, Guid>();
        Guid Find(Guid x)
        {
            if (!parent.TryGetValue(x, out var p) || p.Equals(x))
            {
                parent[x] = x;
                return x;
            }
            var root = Find(p);
            parent[x] = root;
            return root;
        }
        void Union(Guid a, Guid b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (!ra.Equals(rb))
                parent[ra] = rb;
        }

        foreach (var p in primaries)
            parent[p.CanonicalId] = p.CanonicalId;

        // The tight pass applies RefusesTightJoin to whole roots, so a chain through a connector
        // copy cannot carry two records of one uploader into one group. Keyed by root and merged on
        // each union.
        Dictionary<Guid, HashSet<string>>? tightRootSources = null;
        if (TracksTightSources(recordType))
        {
            tightRootSources = await LoadGroupSourcesAsync(
                db,
                recordTypeStr, primaries.Select(p => p.CanonicalId).Distinct().ToList(), unlinked, ct);
        }

        bool TightUnionRefused(Guid a, Guid b)
        {
            if (tightRootSources is null)
                return false;
            var sourcesA = tightRootSources.GetValueOrDefault(Find(a));
            var sourcesB = tightRootSources.GetValueOrDefault(Find(b));
            if (sourcesA is null || sourcesB is null)
                return false;
            return sourcesA.Any(source => RefusesTightJoin(recordType, source, sourcesB));
        }

        void TightUnion(Guid a, Guid b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra.Equals(rb) || TightUnionRefused(ra, rb))
                return;
            Union(ra, rb);
            if (tightRootSources is not null && tightRootSources.Remove(ra, out var moved))
            {
                if (tightRootSources.TryGetValue(rb, out var kept))
                    kept.UnionWith(moved);
                else
                    tightRootSources[rb] = moved;
            }
        }

        for (int i = 0; i < primaries.Count; i++)
        {
            if (!info.TryGetValue(primaries[i].RecordId, out var infoI))
                continue;
            for (int j = i + 1; j < primaries.Count
                 && primaries[j].SourceTimestamp - primaries[i].SourceTimestamp <= MatchingWindowMillis; j++)
            {
                if (!info.TryGetValue(primaries[j].RecordId, out var infoJ))
                    continue;
                if (CriteriaMatch(recordType, infoI.Criteria, infoJ.Criteria))
                    TightUnion(primaries[i].CanonicalId, primaries[j].CanonicalId);
            }
        }

        // 3b. Wide pass, mirroring the insert path: groups the tight pass left apart may still be
        // one event once two connectors' clocks have drifted. A pair merges only on exact values,
        // only when each group's sole wide candidate is the other, and only when the two groups
        // share no data source. Anything ambiguous is left as separate groups.
        if (wideEligible)
        {
            // Root -> (partner root -> smallest observed offset between their primaries).
            var widePartners = new Dictionary<Guid, Dictionary<Guid, long>>();
            void AddWidePartner(Guid from, Guid to, long offsetMillis)
            {
                if (!widePartners.TryGetValue(from, out var partners))
                {
                    partners = new Dictionary<Guid, long>();
                    widePartners[from] = partners;
                }

                if (!partners.TryGetValue(to, out var existing) || Math.Abs(offsetMillis) < Math.Abs(existing))
                    partners[to] = offsetMillis;
            }

            // Pairing and ambiguity are judged on a group's whole extent, not on its primary. A
            // group that has already absorbed a wide join spans up to a window, so a primary-only
            // comparison would miss a group whose member — not its primary — sits beside a pair,
            // and would merge a pair the insert path (which scans every link) would refuse.
            var extents = await LoadGroupExtentsAsync(
                db,
                recordTypeStr, primaries.Select(p => p.CanonicalId).Distinct().ToList(), ct);

            var extentByRoot = new Dictionary<Guid, WideGroupExtent>();
            foreach (var p in primaries)
            {
                var root = Find(p.CanonicalId);
                if (!extentByRoot.TryGetValue(root, out var group))
                {
                    // primaries is ordered by SourceTimestamp, so the first primary seen for a root
                    // is its earliest — a deterministic representative for the logged offset.
                    group = new WideGroupExtent(root, p.SourceTimestamp);
                    extentByRoot[root] = group;
                }

                // Absorb the extent whether or not the record info is present. A root's span must
                // cover every canonical beneath it: an under-covered extent under-counts ambiguity,
                // which is the direction that produces false merges.
                if (extents.TryGetValue(p.CanonicalId, out var extent))
                    group.Absorb(extent.Min, extent.Max);
                else
                    group.Absorb(p.SourceTimestamp, p.SourceTimestamp);

                // Each canonical beneath a root contributes its own criteria. Comparing on a single
                // representative would count partners against one value only, and a root spanning
                // canonicals with slightly different values would then permit merges the ambiguity
                // guard should refuse.
                if (info.TryGetValue(p.RecordId, out var primaryInfo))
                    group.AddCriteria(primaryInfo.Criteria);
            }

            // Ordered by extent start, so for a fixed i the gap to each later j only grows and the
            // inner loop can stop at the first j out of range. Worst case is O(G^2) when long
            // absorbed chains overlap; the loop still terminates, and a group only grows that wide
            // by having already absorbed wide joins.
            var wideGroups = extentByRoot.Values.OrderBy(g => g.Min).ThenBy(g => g.Root).ToList();
            for (int i = 0; i < wideGroups.Count; i++)
            {
                for (int j = i + 1; j < wideGroups.Count
                     && wideGroups[j].Min - wideGroups[i].Max <= WideMatchingWindowMillis; j++)
                {
                    if (!AnyCriteriaMatch(recordType, wideGroups[i], wideGroups[j]))
                        continue;

                    var offset = wideGroups[j].PrimaryTimestamp - wideGroups[i].PrimaryTimestamp;
                    AddWidePartner(wideGroups[i].Root, wideGroups[j].Root, offset);
                    AddWidePartner(wideGroups[j].Root, wideGroups[i].Root, offset);
                }
            }

            // A group whose extent reaches within a window of either end of the load may have an
            // ambiguator just beyond it that was never read — the neighbour margin was proven
            // sufficient when groups were points, and an absorbed group's span breaks that proof.
            // Defer every pair touching one; the full job, or the pass that owns the boundary
            // group, decides it with the whole picture.
            var boundaryRoots = new HashSet<Guid>();
            if (neighbourMinTs is { } loadStart && neighbourMaxTs is { } loadEnd)
            {
                foreach (var g in wideGroups)
                {
                    if (g.Min - loadStart <= WideMatchingWindowMillis
                        || loadEnd - g.Max <= WideMatchingWindowMillis)
                    {
                        boundaryRoots.Add(g.Root);
                    }
                }
            }

            if (widePartners.Count > 0)
            {
                // Only roots that actually have a wide partner need their sources; the rest of the
                // loaded primaries were read as evidence for the ambiguity count, nothing more.
                // Roots holding a candidate canonical are tracked at the same time: a candidate-
                // bounded pass may only merge pairs it owns.
                var partneredPrimaries = primaries
                    .Where(p => widePartners.ContainsKey(Find(p.CanonicalId)))
                    .ToList();

                var sourcesByCanonical = await LoadGroupSourcesAsync(
                    db,
                    recordTypeStr, partneredPrimaries.Select(p => p.CanonicalId).Distinct().ToList(), unlinked, ct);
                var sourcesByRoot = new Dictionary<Guid, HashSet<string>>();
                var candidateRoots = new HashSet<Guid>();
                foreach (var p in partneredPrimaries)
                {
                    var root = Find(p.CanonicalId);
                    if (!sourcesByRoot.TryGetValue(root, out var rootSources))
                    {
                        rootSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        sourcesByRoot[root] = rootSources;
                    }

                    if (sourcesByCanonical.TryGetValue(p.CanonicalId, out var canonicalSources))
                        rootSources.UnionWith(canonicalSources);

                    if (candidateCanonicalIds?.Contains(p.CanonicalId) == true)
                        candidateRoots.Add(root);
                }

                var mergedOffsets = new List<long>();
                foreach (var (root, partners) in widePartners)
                {
                    if (partners.Count != 1)
                        continue;
                    var (partner, offset) = partners.First();

                    // AddWidePartner inserts both directions, so a mutual pair appears twice in
                    // this loop. Act on one ordering only, or the merge would be logged twice.
                    if (root.CompareTo(partner) >= 0)
                        continue;

                    // AddWidePartner's symmetric insertion means partner's entries always include
                    // root, so a single reverse entry is necessarily root itself.
                    if (!widePartners.TryGetValue(partner, out var reverse) || reverse.Count != 1)
                        continue;

                    // A candidate-bounded pass loaded neighbours around the candidates only, so a
                    // pair of two non-candidate groups may be sitting at the edge of that load with
                    // its own evidence horizon cut short. Leave it to the full job or to the pass
                    // whose candidates it belongs to.
                    if (candidateCanonicalIds != null
                        && !candidateRoots.Contains(root)
                        && !candidateRoots.Contains(partner))
                    {
                        continue;
                    }

                    // Same reasoning one step out: either group's own extent may run up against the
                    // end of what was loaded, hiding an ambiguator entirely.
                    if (boundaryRoots.Contains(root) || boundaryRoots.Contains(partner))
                        continue;

                    if (!sourcesByRoot.TryGetValue(root, out var rootSources)
                        || !sourcesByRoot.TryGetValue(partner, out var partnerSources)
                        || !CanEstablishCrossSource(rootSources)
                        || !CanEstablishCrossSource(partnerSources)
                        || rootSources.Overlaps(partnerSources))
                    {
                        continue;
                    }

                    Union(root, partner);
                    mergedOffsets.Add(offset);

                    // A full job healing a year of drift would otherwise emit one line per pair.
                    if (mergedOffsets.Count <= WideMergeLogLimit)
                    {
                        LogCrossSourceMatch(
                            recordType,
                            string.Join(",", rootSources),
                            string.Join(",", partnerSources),
                            offset,
                            wide: true);
                    }
                }

                if (mergedOffsets.Count > WideMergeLogLimit)
                {
                    var offsetSeconds = mergedOffsets.Select(o => Math.Abs(o) / 1000.0).Order().ToList();
                    var middle = offsetSeconds.Count / 2;
                    var median = offsetSeconds.Count % 2 == 1
                        ? offsetSeconds[middle]
                        : (offsetSeconds[middle - 1] + offsetSeconds[middle]) / 2.0;

                    // The cap keeps a deploy-day job from emitting a line per pair, but it must not
                    // swallow the signal the Warning level exists for, so the summary carries it.
                    var maxOffsetMillis = mergedOffsets.Max(Math.Abs);
                    var level = maxOffsetMillis > CrossSourceOffsetWarningMillis
                        ? LogLevel.Warning
                        : LogLevel.Information;

                    _logger.Log(
                        level,
                        "Wide reconcile merged {MergedPairs} {RecordType} group pairs; offset seconds min {MinOffsetSeconds}, median {MedianOffsetSeconds}, max {MaxOffsetSeconds}",
                        offsetSeconds.Count,
                        recordType,
                        offsetSeconds[0],
                        median,
                        offsetSeconds[^1]);
                }
            }
        }

        // 4. Group canonical ids by union root; only roots spanning >1 distinct canonical merge.
        var groupsByRoot = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var p in primaries)
        {
            var root = Find(p.CanonicalId);
            if (!groupsByRoot.TryGetValue(root, out var set))
            {
                set = new HashSet<Guid>();
                groupsByRoot[root] = set;
            }
            set.Add(p.CanonicalId);
        }

        var mergedGroups = groupsByRoot.Values.Where(canonicals => canonicals.Count > 1).ToList();
        if (mergedGroups.Count == 0)
            return 0;

        return await UnderWriteLockAsync(db, async token =>
        {
            var merged = 0;
            foreach (var canonicals in mergedGroups)
            {
                // Load every linked row in the merged canonicals (soft-deleted records included),
                // and store the unlinked groups among them.
                var rows = await db.LinkedRecords
                    .Where(lr => lr.RecordType == recordTypeStr && canonicals.Contains(lr.CanonicalId))
                    .ToListAsync(token);
                foreach (var canonicalId in canonicals)
                {
                    if (unlinked.TryGetValue(canonicalId, out var restored))
                    {
                        db.LinkedRecords.Add(restored);
                        rows.Add(restored);
                    }
                }

                if (rows.Count == 0)
                    continue;

                // Load deleted status for all record ids in the super-group.
                var rowIds = rows.Select(r => r.RecordId).ToHashSet();
                var rowInfo = await LoadRecordInfoAsync(db, recordType, rowIds, token);

                var survivor = PickSurvivor(rows, rowInfo);

                // Re-point every row to the survivor's canonical id and fix the IsPrimary invariant.
                foreach (var r in rows)
                {
                    r.CanonicalId = survivor.CanonicalId;
                    r.IsPrimary = ReferenceEquals(r, survivor);
                }

                // Collapsing N canonicals into 1 removes (N-1) groups.
                merged += canonicals.Count - 1;
            }

            if (merged > 0)
            {
                await db.SaveChangesAsync(token);
                db.ChangeTracker.Clear();
            }

            return merged;
        }, ct);
    }

    private const int MergeLockClass = 0x4E44_5550;

    /// <summary>
    /// Takes the tenant's merge lock (<see cref="AdvisoryLockExtensions"/>), waiting for any holder.
    /// A merge reads a group's rows and re-points them in separate statements, so of two merges of
    /// one tenant at once the later can strand rows the earlier re-pointed in a group with no primary.
    /// </summary>
    private static Task LockTenantMergesAsync(NocturneDbContext db, CancellationToken ct) =>
        db.LockForTransactionAsync(MergeLockClass, db.TenantId, ct);

    /// <summary>
    /// <see cref="LockTenantMergesAsync"/> without the wait.
    /// </summary>
    /// <returns>Whether this transaction now holds the lock.</returns>
    private static Task<bool> TryLockTenantMergesAsync(NocturneDbContext db, CancellationToken ct) =>
        db.TryLockForTransactionAsync(MergeLockClass, db.TenantId, ct);

    /// <inheritdoc />
    /// <remarks>
    /// The pass runs on one context. Each batch is one transaction that holds the tenant's merge
    /// lock (<see cref="LockTenantMergesAsync"/>) while it reads the cursor, merges and advances it.
    /// A batch that finds the lock held ends the pass.
    /// </remarks>
    public async Task<ReconcileResult> ReconcileNewLinksAsync(
        int batchSize,
        int maxBatches,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var horizon = DateTime.UtcNow - CommitVisibilityLag;
        var merged = 0;

        for (var batchNo = 0; batchNo < maxBatches; batchNo++)
        {
            var batch = await db.ExecuteInTransactionAsync(
                ct => ReconcileNextBatchAsync(db, batchSize, horizon, ct), ct: cancellationToken);

            if (batch is not { } step)
                break;

            merged += step.Merged;
            if (step.Drained)
                return new ReconcileResult(merged, true);
        }

        return new ReconcileResult(merged, false);
    }

    /// <summary>
    /// Reconciles the batch after the tenant's cursor and advances it.
    /// </summary>
    /// <returns>
    /// The groups merged and whether the batch drained everything up to <paramref name="horizon"/>,
    /// or null when another transaction holds the tenant's merge lock.
    /// </returns>
    private async Task<(int Merged, bool Drained)?> ReconcileNextBatchAsync(
        NocturneDbContext db,
        int batchSize,
        DateTime horizon,
        CancellationToken ct)
    {
        if (!await TryLockTenantMergesAsync(db, ct))
            return null;

        var cursor = await GetCursorAsync(db, ct);
        var batch = await LinksAfter(db, cursor, horizon).Take(batchSize).ToListAsync(ct);
        if (batch.Count == 0)
            return (0, true);

        var merged = 0;
        foreach (var group in batch.GroupBy(l => l.RecordType))
        {
            if (ParseRecordType(group.Key) is not { } type)
            {
                continue;
            }

            var candidateCanonicalIds = group.Select(l => l.CanonicalId).ToHashSet();
            try
            {
                // GroupsMerged is the reduction in group count (k canonicals -> 1 == k-1), matching MergeDuplicateGroupsAsync.
                merged += await MergeDuplicateGroupsAsync(db, type, candidateCanonicalIds, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex,
                    "Dedup reconcile for tenant {TenantId} failed merging {RecordType}; the cursor stays at {CursorCreatedAt} {CursorLinkId} and every pass retries this batch until it succeeds",
                    db.TenantId, type, cursor?.CreatedAt, cursor?.Id);
                throw;
            }
        }

        var last = batch[^1];
        await SetCursorAsync(db, new ReconcileCursor(last.SysCreatedAt, last.Id), ct);

        return (merged, batch.Count < batchSize);
    }

    /// <inheritdoc />
    public async Task<int> PruneSingletonLinksAsync(
        int batchSize,
        int maxBatches,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);

        // A link the reconcile cursor has not passed may still be a candidate for a merge.
        if (await GetCursorAsync(db, cancellationToken) is not { } cursor)
            return 0;

        var retained = DateTime.UtcNow - SingletonLinkRetention;
        var createdBefore = cursor.CreatedAt < retained ? cursor.CreatedAt : retained;

        var pruned = 0;
        for (var batchNo = 0; batchNo < maxBatches; batchNo++)
        {
            var deleted = await UnderWriteLockAsync(
                db,
                token => PrunableSingletons(db, createdBefore).Take(batchSize).ExecuteDeleteAsync(token),
                cancellationToken);
            pruned += deleted;

            if (deleted < batchSize)
                break;
        }

        return pruned;
    }

    /// <summary>
    /// The tenant's primary links created before <paramref name="createdBefore"/> that no other link
    /// shares a group with. A lone non-primary link is kept: it hides its record from every read, and
    /// with the link gone the record would show.
    /// </summary>
    internal static IQueryable<LinkedRecordEntity> PrunableSingletons(NocturneDbContext db, DateTime createdBefore) =>
        db.LinkedRecords
            .Where(lr => lr.IsPrimary && lr.SysCreatedAt < createdBefore
                && !db.LinkedRecords.Any(other => other.CanonicalId == lr.CanonicalId && other.Id != lr.Id))
            .OrderBy(lr => lr.SysCreatedAt);

    /// <summary>
    /// The tenant's links in (SysCreatedAt, Id) order, strictly after <paramref name="after"/> and
    /// created before <paramref name="horizon"/>. Ordering on the id as well lets a page end inside
    /// a run of links sharing one <c>sys_created_at</c> (a bulk insert stamps every link in the
    /// transaction with the same instant) and the next page continue from there; a cursor on the
    /// timestamp alone would re-read or skip the rest of the run.
    /// </summary>
    internal static IQueryable<LinkedRecordEntity> LinksAfter(
        NocturneDbContext db, ReconcileCursor? after, DateTime horizon)
    {
        // Tenant-scoped automatically via the global query filter.
        var query = db.LinkedRecords
            .AsNoTracking()
            .Where(lr => lr.SysCreatedAt < horizon);

        if (after is { } c)
        {
            // The redundant lower bound is what the planner ranges the (tenant_id, sys_created_at)
            // index on; the disjunction alone is not recognised as a range.
            query = query.Where(lr => lr.SysCreatedAt >= c.CreatedAt
                && (lr.SysCreatedAt > c.CreatedAt || lr.Id.CompareTo(c.Id) > 0));
        }

        return query
            .OrderBy(lr => lr.SysCreatedAt)
            .ThenBy(lr => lr.Id);
    }

    /// <summary>
    /// The primary links of <paramref name="canonicalIds"/>. The set is copied to an array because
    /// EF expands an <see cref="IReadOnlySet{T}"/> operand into one parameter per element, so every
    /// set size would get its own statement and plan; an array binds as one parameter.
    /// </summary>
    internal static IQueryable<LinkedRecordEntity> PrimariesOf(
        NocturneDbContext db, string recordTypeStr, IReadOnlySet<Guid> canonicalIds)
    {
        var ids = canonicalIds.ToArray();
        return db.LinkedRecords
            .AsNoTracking()
            .Where(lr => lr.RecordType == recordTypeStr && lr.IsPrimary && ids.Contains(lr.CanonicalId));
    }

    /// <summary>
    /// Loads, for each requested record id, its <see cref="MatchCriteria"/> and whether the
    /// underlying record is soft-deleted. Soft-deleted rows are included via
    /// <c>IgnoreQueryFilters</c>; record types without a <c>DeletedAt</c> column report
    /// <see cref="RecordInfo.IsDeleted"/> as <see langword="false"/>.
    /// <para>
    /// The global query filter combines tenant scoping (<c>TenantId == this.TenantId</c>) with
    /// the soft-delete predicate (<c>DeletedAt == null</c>) and <c>IgnoreQueryFilters</c> drops
    /// both. To bypass only the soft-delete filter, each query re-applies the tenant predicate
    /// (<c>e.TenantId == db.TenantId</c>) explicitly so cross-tenant rows can never leak in.
    /// </para>
    /// </summary>
    private static async Task<Dictionary<Guid, RecordInfo>> LoadRecordInfoAsync(
        NocturneDbContext db,
        RecordType recordType, HashSet<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return new Dictionary<Guid, RecordInfo>();

        return recordType switch
        {
            RecordType.SensorGlucose => await LoadAsync<SensorGlucoseEntity>(db, MatchCriteriaMapper.From, ids, ct),
            RecordType.Bolus => await LoadAsync<BolusEntity>(db, MatchCriteriaMapper.From, ids, ct),
            RecordType.CarbIntake => await LoadAsync<CarbIntakeEntity>(db, MatchCriteriaMapper.From, ids, ct),
            RecordType.BGCheck => await LoadAsync<BGCheckEntity>(db, MatchCriteriaMapper.From, ids, ct),
            RecordType.DeviceEvent => await LoadAsync<DeviceEventEntity>(db, MatchCriteriaMapper.From, ids, ct),
            RecordType.Note => await LoadAsync<NoteEntity>(db, MatchCriteriaMapper.From, ids, ct),
            RecordType.BolusCalculation => await LoadAsync<BolusCalculationEntity>(db, MatchCriteriaMapper.From, ids, ct),
            RecordType.TempBasal => await LoadAsync<TempBasalEntity>(db, MatchCriteriaMapper.From, ids, ct),
            RecordType.StateSpan => await LoadStateSpanInfoAsync(db, ids, ct),
            _ => new Dictionary<Guid, RecordInfo>()
        };
    }

    /// <summary>
    /// Loads the criteria and soft-deleted status of every requested row of one entity type.
    /// </summary>
    private static async Task<Dictionary<Guid, RecordInfo>> LoadAsync<TEntity>(
        NocturneDbContext db,
        Func<TEntity, MatchCriteria> toCriteria, HashSet<Guid> ids, CancellationToken ct)
        where TEntity : class, IV4Entity
    {
        var records = await db.Set<TEntity>().AsNoTracking().IgnoreQueryFilters()
            .Where(e => e.TenantId == db.TenantId && ids.Contains(e.Id)).ToListAsync(ct);
        return records.ToDictionary(e => e.Id, e => new RecordInfo(toCriteria(e), e.DeletedAt != null));
    }

    /// <summary>
    /// State spans are loaded apart from <see cref="LoadAsync{TEntity}"/> because
    /// <see cref="StateSpanEntity"/> is keyed by <c>OriginalId</c> rather than the
    /// <c>LegacyId</c> that <see cref="IV4Entity"/> requires.
    /// </summary>
    private static async Task<Dictionary<Guid, RecordInfo>> LoadStateSpanInfoAsync(
        NocturneDbContext db,
        HashSet<Guid> ids, CancellationToken ct)
    {
        var records = await db.StateSpans.AsNoTracking().IgnoreQueryFilters()
            .Where(s => s.TenantId == db.TenantId && ids.Contains(s.Id)).ToListAsync(ct);
        return records.ToDictionary(s => s.Id,
            s => new RecordInfo(
                MatchCriteriaMapper.From(s), s.DeletedAt != null, new SpanEnd(s.SupersededById, s.EndTimestamp)));
    }

    /// <summary>
    /// Internal test seam over <see cref="LoadRecordInfoAsync"/>. Exposed to the
    /// test assembly via <c>InternalsVisibleTo</c>; not part of the public API.
    /// </summary>
    internal static Task<Dictionary<Guid, RecordInfo>> LoadRecordInfoForTestAsync(
        NocturneDbContext db, RecordType recordType, HashSet<Guid> ids, CancellationToken ct = default)
        => LoadRecordInfoAsync(db, recordType, ids, ct);

    private static int LowerBoundTimestamp(long[] sortedTimestamps, long value)
    {
        int lo = 0, hi = sortedTimestamps.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (sortedTimestamps[mid] < value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static int UpperBoundTimestamp(long[] sortedTimestamps, long value)
    {
        int lo = 0, hi = sortedTimestamps.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (sortedTimestamps[mid] <= value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// True when two temp basal durations describe one delivery. Both must be known: an open-ended
    /// temp basal carries no duration, and admitting a null would reduce the wide comparison to
    /// rate alone, which for a stream that repeats the same rate all day is no evidence at all.
    /// </summary>
    private static bool DurationsAgree(TimeSpan? a, TimeSpan? b) =>
        a.HasValue && b.HasValue && (a.Value - b.Value).Duration() <= ExactDurationTolerance;

    /// <summary>
    /// Per-type value comparison shared by the tight and wide paths. With <paramref name="exact"/>
    /// the criteria tolerances are replaced by <see cref="ExactValueEpsilon"/> and each type adds
    /// whatever the wide window needs to keep the comparison meaningful over ten minutes.
    /// <para>
    /// Internal so the exact-mode type guard can be pinned directly: every caller reaches it
    /// through a <see cref="WideMatchableTypes"/> check already, so the guard is unreachable from
    /// the public surface and would otherwise be an untestable defence.
    /// </para>
    /// </summary>
    internal static bool CriteriaMatch(RecordType recordType, MatchCriteria a, MatchCriteria b, bool exact = false)
    {
        if (exact && !WideMatchableTypes.Contains(recordType))
            return false;

        double Tolerance(double aTolerance, double bTolerance) =>
            exact ? ExactValueEpsilon : Math.Max(aTolerance, bTolerance);

        return recordType switch
        {
            // An open-ended temp basal carries no duration, and null == null would quietly reduce
            // the exact comparison to rate alone; both intervals must be known. The tight path
            // still never pairs an open-ended temp with a timed one: a legacy cancel maps to an
            // open-ended 0 U/h, which a real timed 0 U/h temp beside it would otherwise absorb.
            RecordType.TempBasal => a.Rate.HasValue && b.Rate.HasValue
                && Math.Abs(a.Rate.Value - b.Rate.Value) <= Tolerance(a.RateTolerance, b.RateTolerance)
                && (exact ? DurationsAgree(a.Duration, b.Duration) : a.Duration.HasValue == b.Duration.HasValue),
            RecordType.SensorGlucose or RecordType.BGCheck => a.GlucoseValue.HasValue && b.GlucoseValue.HasValue
                && Math.Abs(a.GlucoseValue.Value - b.GlucoseValue.Value) <= Tolerance(a.GlucoseTolerance, b.GlucoseTolerance),
            RecordType.Bolus => a.Insulin.HasValue && b.Insulin.HasValue
                && Math.Abs(a.Insulin.Value - b.Insulin.Value) <= Tolerance(a.InsulinTolerance, b.InsulinTolerance),
            RecordType.CarbIntake => a.Carbs.HasValue && b.Carbs.HasValue
                && Math.Abs(a.Carbs.Value - b.Carbs.Value) <= Tolerance(a.CarbsTolerance, b.CarbsTolerance),
            // A correction-only calculation has no carb input, which the mapper reports as 0, so
            // every such calculation in a ten-minute span would compare exactly equal to every
            // other. Only a calculation that actually carries carbs can wide-match.
            RecordType.BolusCalculation => a.Carbs.HasValue && b.Carbs.HasValue
                && Math.Abs(a.Carbs.Value - b.Carbs.Value) <= Tolerance(a.CarbsTolerance, b.CarbsTolerance)
                && (!exact || (a.Carbs.Value > 0 && b.Carbs.Value > 0)),
            // The event type is the whole value here, so an absent one leaves nothing to compare and
            // every such event in the window would read as equal to every other.
            // Distinguishing two same-type events relies on the single-candidate and disjoint-source
            // guards instead: when a type genuinely repeats within ten minutes both connectors
            // report both occurrences, which puts two candidates in range and refuses the match.
            RecordType.DeviceEvent => !string.IsNullOrEmpty(a.EventType)
                && string.Equals(a.EventType, b.EventType, StringComparison.OrdinalIgnoreCase),
            RecordType.Note => a.Text is not null && string.Equals(a.Text, b.Text, StringComparison.Ordinal),
            // An unparseable category maps to null, so null == null would collapse every state span
            // whose category the mapper could not read into one group. The states must agree
            // outright: treating an absent one as a wildcard makes the result depend on which span
            // is the argument, and the merge pass compares stored spans in both orders.
            RecordType.StateSpan => a.Category.HasValue && a.Category == b.Category
                && string.Equals(a.State, b.State, StringComparison.OrdinalIgnoreCase)
                && a.Metadata.IsSamePresetAs(b.Metadata),
            _ => false
        };
    }

    /// <inheritdoc />
    public async Task<IEnumerable<LinkedRecord>> GetLinkedRecordsAsync(
        Guid canonicalId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var entities = await db.LinkedRecords
            .Where(lr => lr.CanonicalId == canonicalId)
            .OrderBy(static lr => lr.SourceTimestamp)
            .ToListAsync(cancellationToken);

        return entities
            .Select(e => (Entity: e, Type: ParseRecordType(e.RecordType)))
            .Where(static x => x.Type.HasValue)
            .Select(x => new LinkedRecord
            {
                Id = x.Entity.Id.ToString(),
                CanonicalId = x.Entity.CanonicalId,
                RecordType = x.Type!.Value,
                RecordId = x.Entity.RecordId,
                SourceTimestamp = x.Entity.SourceTimestamp,
                DataSource = x.Entity.DataSource,
                IsPrimary = x.Entity.IsPrimary,
                CreatedAt = x.Entity.SysCreatedAt
            });
    }

    /// <summary>
    /// The <see cref="RecordType"/> a <c>linked_records.record_type</c> string names, or null when
    /// the column holds a value outside the enum. The column's surface is broader than the enum —
    /// it carries whatever earlier versions wrote — and one such row must neither fail a read nor
    /// block a tenant's remaining reconcile batches, so every caller skips a null.
    /// </summary>
    private RecordType? ParseRecordType(string recordType)
    {
        if (Enum.TryParse<RecordType>(recordType, ignoreCase: true, out var type))
        {
            return type;
        }

        _logger.LogWarning("Skipping linked_records row with unknown RecordType '{RecordType}'", recordType);
        return null;
    }

    /// <inheritdoc />
    public async Task<LinkedRecord?> GetLinkedRecordAsync(
        RecordType recordType,
        Guid recordId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var recordTypeStr = RecordTypeKeys.Key(recordType);

        var entity = await db.LinkedRecords
            .FirstOrDefaultAsync(lr =>
                lr.RecordType == recordTypeStr && lr.RecordId == recordId,
                cancellationToken);

        if (entity == null)
            return null;

        return new LinkedRecord
        {
            Id = entity.Id.ToString(),
            CanonicalId = entity.CanonicalId,
            RecordType = recordType,
            RecordId = entity.RecordId,
            SourceTimestamp = entity.SourceTimestamp,
            DataSource = entity.DataSource,
            IsPrimary = entity.IsPrimary,
            CreatedAt = entity.SysCreatedAt
        };
    }

    /// <inheritdoc />
    public async Task<StateSpan?> GetUnifiedStateSpanAsync(
        Guid canonicalId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var key = RecordTypeKeys.Key(RecordType.StateSpan);
        var linkedRecords = await db.LinkedRecords
            .Where(lr => lr.CanonicalId == canonicalId && lr.RecordType == key)
            .OrderBy(static lr => lr.SourceTimestamp)
            .ToListAsync(cancellationToken);

        if (linkedRecords.Count == 0)
            return null;

        var recordIds = linkedRecords.Select(lr => lr.RecordId).ToList();
        var stateSpans = await db.StateSpans
            .Where(s => recordIds.Contains(s.Id))
            .ToListAsync(cancellationToken);

        if (stateSpans.Count == 0)
            return null;

        // Sort by timestamp to get primary first
        var sortedStateSpans = stateSpans
            .OrderBy(static s => s.StartTimestamp)
            .Select(StateSpanMapper.ToDomainModel)
            .ToList();

        return MergeStateSpans(sortedStateSpans, canonicalId);
    }

    private delegate Task<(int processed, int groups, int linked, int duplicates)> PhaseRunner(
        DeduplicationService service,
        NocturneDbContext db,
        int totalRecords,
        int startOffset,
        IProgress<DeduplicationProgress>? progress,
        CancellationToken ct);

    /// <summary>
    /// One record type's <see cref="DeduplicateAllAsync"/> phase. <see cref="Name"/> is reported as
    /// <see cref="DeduplicationProgress.CurrentPhase"/>, so callers observe it.
    /// <see cref="OrphanedLinks"/> is this type's links whose record id is absent from the type's rows
    /// with <see cref="NocturneDbContext.SoftDeleteFilterKey"/> lifted, because a soft-deleted record
    /// is still linked. It is a correlated <c>NOT EXISTS</c>: a negated <c>Contains</c> translates to
    /// <c>NOT IN</c>, which PostgreSQL rescans per link once the record table outgrows work_mem.
    /// Tenant isolation stays on, which the <see cref="ITenantScoped"/> constraint on
    /// <see cref="Phase{TEntity}"/> is what guarantees.
    /// </summary>
    /// <param name="Unlinked">
    /// <see cref="UnlinkedAsync"/> for this type: its records created before the given instant with no
    /// link, event time within the optional bounds, each as the singleton group it would have been.
    /// </param>
    private sealed record TypePhase(
        RecordType RecordType,
        string Name,
        Func<NocturneDbContext, CancellationToken, Task<int>> CountAsync,
        Func<NocturneDbContext, IQueryable<LinkedRecordEntity>> OrphanedLinks,
        Func<NocturneDbContext, long?, long?, DateTime, CancellationToken, Task<List<LinkedRecordEntity>>> Unlinked,
        PhaseRunner RunAsync);

    /// <summary>
    /// Builds one <see cref="TypePhase"/>. <paramref name="timestamp"/> both orders the query and
    /// supplies the event time, so a type whose event time is not <c>Timestamp</c> states it once;
    /// <paramref name="id"/> serves the same two purposes, and <paramref name="createdAt"/> is the
    /// insert stamp <see cref="SingletonLinkRetention"/> is measured on.
    /// </summary>
    private static TypePhase Phase<TEntity>(
        RecordType recordType,
        string name,
        Func<NocturneDbContext, IQueryable<TEntity>> set,
        Expression<Func<TEntity, DateTime>> timestamp,
        Expression<Func<TEntity, Guid>> id,
        Expression<Func<TEntity, DateTime>> createdAt,
        Func<TEntity, string?> dataSource,
        Func<TEntity, MatchCriteria> criteria) where TEntity : class, ITenantScoped
    {
        var eventTime = timestamp.Compile();
        var recordId = id.Compile();
        var key = RecordTypeKeys.Key(recordType);
        var timestampName = MemberName(timestamp);
        var idName = MemberName(id);
        var createdAtName = MemberName(createdAt);

        return new TypePhase(
            recordType,
            name,
            (context, ct) => set(context).CountAsync(ct),
            context =>
            {
                var recordIds = set(context)
                    .IgnoreQueryFilters([NocturneDbContext.SoftDeleteFilterKey])
                    .Select(id);
                return context.LinkedRecords
                    .Where(lr => lr.RecordType == key && !recordIds.Any(recordId => recordId == lr.RecordId));
            },
            async (context, fromMills, toMills, createdBefore, ct) =>
            {
                // Soft-deleted rows included: a pruned link was a link whatever its record's state,
                // and the merge pass reads deleted primaries as evidence.
                var query = set(context).AsNoTracking()
                    .IgnoreQueryFilters([NocturneDbContext.SoftDeleteFilterKey])
                    .Where(e => EF.Property<DateTime>(e, createdAtName) < createdBefore
                        && !context.LinkedRecords.Any(lr =>
                            lr.RecordType == key && lr.RecordId == EF.Property<Guid>(e, idName)));
                if (fromMills is { } from)
                {
                    var fromTime = DateTimeOffset.FromUnixTimeMilliseconds(from).UtcDateTime;
                    query = query.Where(e => EF.Property<DateTime>(e, timestampName) >= fromTime);
                }
                if (toMills is { } to)
                {
                    var toTime = DateTimeOffset.FromUnixTimeMilliseconds(to).UtcDateTime;
                    query = query.Where(e => EF.Property<DateTime>(e, timestampName) <= toTime);
                }

                return (await query.ToListAsync(ct)).Select(e => new LinkedRecordEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = context.TenantId,
                    CanonicalId = Guid.CreateVersion7(),
                    RecordType = key,
                    RecordId = recordId(e),
                    SourceTimestamp = new DateTimeOffset(eventTime(e), TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    DataSource = dataSource(e) ?? DeduplicationInput.UnknownDataSource,
                    IsPrimary = true,
                }).ToList();
            },
            (service, db, totalRecords, startOffset, progress, ct) => service.DeduplicateTypeAsync(
                recordType,
                set(db),
                timestamp,
                e => new DeduplicationInput(
                    recordId(e),
                    new DateTimeOffset(eventTime(e), TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    dataSource(e) ?? DeduplicationInput.UnknownDataSource,
                    criteria(e)),
                name, totalRecords, startOffset, progress, ct));
    }

    /// <summary>
    /// Every record type <see cref="DeduplicateAllAsync"/> passes over, in processing order.
    /// MeterGlucose was previously processed via DeduplicateEntriesAsync alongside SensorGlucose,
    /// but there is no <see cref="RecordType"/> value for it, and the old code also double-processed
    /// SensorGlucose (once in Entries, once standalone). MeterGlucose dedup is intentionally
    /// dropped; add a <see cref="RecordType"/> if it is needed in the future.
    /// </summary>
    private static readonly TypePhase[] TypePhases =
    [
        Phase(RecordType.SensorGlucose, "SensorGlucose",
            static c => c.SensorGlucose, static e => e.Timestamp,
            static e => e.Id, static e => e.SysCreatedAt, static e => e.DataSource, MatchCriteriaMapper.From),
        Phase(RecordType.Bolus, "Boluses",
            static c => c.Boluses, static b => b.Timestamp,
            static b => b.Id, static b => b.SysCreatedAt, static b => b.DataSource, MatchCriteriaMapper.From),
        Phase(RecordType.CarbIntake, "CarbIntakes",
            static c => c.CarbIntakes, static c => c.Timestamp,
            static c => c.Id, static c => c.SysCreatedAt, static c => c.DataSource, MatchCriteriaMapper.From),
        Phase(RecordType.BGCheck, "BGChecks",
            static c => c.BGChecks, static bg => bg.Timestamp,
            static bg => bg.Id, static bg => bg.SysCreatedAt, static bg => bg.DataSource, MatchCriteriaMapper.From),
        Phase(RecordType.DeviceEvent, "DeviceEvents",
            static c => c.DeviceEvents, static d => d.Timestamp,
            static d => d.Id, static d => d.SysCreatedAt, static d => d.DataSource, MatchCriteriaMapper.From),
        Phase(RecordType.Note, "Notes",
            static c => c.Notes, static n => n.Timestamp,
            static n => n.Id, static n => n.SysCreatedAt, static n => n.DataSource, MatchCriteriaMapper.From),
        Phase(RecordType.BolusCalculation, "BolusCalculations",
            static c => c.BolusCalculations, static bc => bc.Timestamp,
            static bc => bc.Id, static bc => bc.SysCreatedAt, static bc => bc.DataSource, MatchCriteriaMapper.From),
        Phase(RecordType.TempBasal, "TempBasals",
            static c => c.TempBasals, static t => t.Timestamp,
            static t => t.Id, static t => t.SysCreatedAt, static t => t.DataSource, MatchCriteriaMapper.From),
        Phase(RecordType.StateSpan, "StateSpans",
            static c => c.StateSpans, static s => s.StartTimestamp,
            static s => s.Id, static s => s.CreatedAt, static s => s.Source, MatchCriteriaMapper.From)
    ];

    /// <summary>
    /// The record types <see cref="DeduplicateAllAsync"/> covers, in processing order. Internal so
    /// a test can drive itself off the registry instead of restating it.
    /// </summary>
    internal static IReadOnlyList<RecordType> DeduplicatedRecordTypes { get; } =
        [.. TypePhases.Select(static p => p.RecordType)];

    /// <summary>
    /// Deletes the links of <paramref name="context"/>'s tenant that have nothing left to point at:
    /// a record id no longer in its type's table, and a record type no <see cref="RecordType"/>
    /// member names, which no caller can read back — <see cref="GetLinkedRecordsAsync"/> drops a row
    /// whose key does not parse. A soft-deleted record is not an orphan.
    /// </summary>
    /// <returns>The number of links deleted.</returns>
    public static async Task<int> DeleteOrphanedLinksAsync(
        NocturneDbContext context, CancellationToken ct = default)
    {
        var namedTypes = Enum.GetValues<RecordType>().Select(RecordTypeKeys.Key).ToList();

        var deleted = await context.LinkedRecords
            .Where(lr => !namedTypes.Contains(lr.RecordType))
            .ExecuteDeleteAsync(ct);

        foreach (var phase in TypePhases)
        {
            deleted += await phase.OrphanedLinks(context).ExecuteDeleteAsync(ct);
        }

        return deleted;
    }

    /// <summary>
    /// The property <paramref name="member"/> reads. One query shape serves every entity type, so a
    /// column arrives as a property name rather than an expression to compose into it.
    /// </summary>
    private static string MemberName<TEntity, TValue>(Expression<Func<TEntity, TValue>> member) =>
        member.Body is MemberExpression access
            ? access.Member.Name
            : throw new ArgumentException(
                "The expression must be a simple member access (e => e.Timestamp): the query reads "
                + "that property by name.",
                nameof(member));

    internal static IQueryable<LinkedRecordEntity> OrphanedLinksOf(
        NocturneDbContext context, RecordType recordType) =>
        TypePhases.Single(p => p.RecordType == recordType).OrphanedLinks(context);

    /// <inheritdoc />
    public async Task<DeduplicationResult> DeduplicateAllAsync(
        IProgress<DeduplicationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var totalRecords = 0;
            foreach (var phase in TypePhases)
            {
                totalRecords += await phase.CountAsync(db, cancellationToken);
            }

            var processed = 0;
            var groupsCreated = 0;
            var recordsLinked = 0;
            var duplicateGroups = 0;
            var processedByType = new Dictionary<RecordType, int>();

            foreach (var phase in TypePhases)
            {
                var result = await phase.RunAsync(this, db, totalRecords, processed, progress, cancellationToken);
                processedByType[phase.RecordType] = result.processed;
                processed += result.processed;
                groupsCreated += result.groups;
                recordsLinked += result.linked;
                duplicateGroups += result.duplicates;
            }

            stopwatch.Stop();

            _logger.LogInformation(
                "Deduplication completed: {TotalRecords} records processed, {Groups} groups created, {Linked} records linked, {Duplicates} duplicate groups in {Duration}",
                processed, groupsCreated, recordsLinked, duplicateGroups, stopwatch.Elapsed);

            return new DeduplicationResult
            {
                TotalRecordsProcessed = processed,
                CanonicalGroupsCreated = groupsCreated,
                RecordsLinked = recordsLinked,
                DuplicateGroupsFound = duplicateGroups,
                Duration = stopwatch.Elapsed,
                StateSpansProcessed = processedByType[RecordType.StateSpan],
                SensorGlucoseProcessed = processedByType[RecordType.SensorGlucose],
                BolusesProcessed = processedByType[RecordType.Bolus],
                CarbIntakesProcessed = processedByType[RecordType.CarbIntake],
                BGChecksProcessed = processedByType[RecordType.BGCheck],
                DeviceEventsProcessed = processedByType[RecordType.DeviceEvent],
                NotesProcessed = processedByType[RecordType.Note],
                BolusCalculationsProcessed = processedByType[RecordType.BolusCalculation],
                TempBasalsProcessed = processedByType[RecordType.TempBasal],
                Success = true
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Deduplication cancelled after {Duration}", stopwatch.Elapsed);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deduplication failed after {Duration}", stopwatch.Elapsed);
            return new DeduplicationResult
            {
                Duration = stopwatch.Elapsed,
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <inheritdoc />
    public async Task<Guid> StartDeduplicationJobAsync(CancellationToken cancellationToken = default)
    {
        // Capture the caller's tenant before leaving the request scope. Without it the background
        // scope's DbContext is unpinned, and under FORCE row-level security every tenant-scoped
        // table reads as empty — the job would report success having processed nothing. Fail here
        // instead: a job that cannot see the tenant's data must not look like one that ran.
        var tenantContext = _tenantAccessor?.Context;
        if (tenantContext is null)
        {
            throw new InvalidOperationException(
                "Cannot start a deduplication job without a resolved tenant: the background scope "
                + "would read no rows and report a vacuous success.");
        }

        var jobId = Guid.CreateVersion7();
        var cts = new CancellationTokenSource();

        var status = new DeduplicationJobStatus
        {
            JobId = jobId,
            State = DeduplicationJobState.Pending,
            StartedAt = DateTime.UtcNow
        };

        var reader = TenantReader.Of(
            tenantContext,
            _categoryReadContext ?? throw new InvalidOperationException(
                "Cannot start a deduplication job without the caller's read context: the job would read "
                + "past the caller's history clamp."));

        _runningJobs[jobId] = status;
        _jobTenants[jobId] = tenantContext.TenantId;
        _jobCancellations[jobId] = cts;

        // Start the job in the background with its own scope
        _ = Task.Run(async () =>
        {
            await using var scope = TenantJobScope.OpenFor(_scopeFactory, reader, JobAuditEndpoint);

            var scopedService = scope.ServiceProvider.GetRequiredService<IDeduplicationService>();

            try
            {
                _runningJobs[jobId] = status with { State = DeduplicationJobState.Running };

                var progressReporter = new Progress<DeduplicationProgress>(p =>
                {
                    if (_runningJobs.TryGetValue(jobId, out var currentStatus))
                    {
                        _runningJobs[jobId] = currentStatus with { Progress = p };
                    }
                });

                var result = await scopedService.DeduplicateAllAsync(progressReporter, cts.Token);

                _runningJobs[jobId] = _runningJobs[jobId] with
                {
                    State = result.Success ? DeduplicationJobState.Completed : DeduplicationJobState.Failed,
                    CompletedAt = DateTime.UtcNow,
                    Result = result
                };
            }
            catch (OperationCanceledException)
            {
                _runningJobs[jobId] = _runningJobs[jobId] with
                {
                    State = DeduplicationJobState.Cancelled,
                    CompletedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Deduplication job {JobId} failed", jobId);
                _runningJobs[jobId] = _runningJobs[jobId] with
                {
                    State = DeduplicationJobState.Failed,
                    CompletedAt = DateTime.UtcNow,
                    Result = new DeduplicationResult
                    {
                        Success = false,
                        ErrorMessage = ex.Message
                    }
                };
            }
            finally
            {
                _jobCancellations.TryRemove(jobId, out _);
            }
        });

        return jobId;
    }

    /// <inheritdoc />
    public Task<DeduplicationJobStatus?> GetJobStatusAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        if (!OwnsJob(jobId))
            return Task.FromResult<DeduplicationJobStatus?>(null);

        _runningJobs.TryGetValue(jobId, out var status);
        return Task.FromResult(status);
    }

    /// <inheritdoc />
    public Task<bool> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (!OwnsJob(jobId))
            return Task.FromResult(false);

        if (_jobCancellations.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    /// <summary>
    /// Whether the calling scope's tenant started <paramref name="jobId"/>. Answers false for an
    /// unknown job and for a caller with no resolved tenant, so both look exactly like a job that
    /// does not exist.
    /// </summary>
    private bool OwnsJob(Guid jobId)
    {
        var tenantId = _tenantAccessor?.Context?.TenantId;
        return tenantId is not null
               && _jobTenants.TryGetValue(jobId, out var owner)
               && owner == tenantId;
    }

    /// <summary>
    /// Pages <paramref name="set"/> in <paramref name="timestamp"/> order into
    /// <see cref="DeduplicateBatchAsync"/>. A page ends on a whole timestamp — the run of rows
    /// sharing the last one is drained with it — so the cursor can advance strictly past it and no
    /// row is read twice or skipped, without needing an order over record ids that both the
    /// database and the CLR agree on. A canonical group split across pages still collapses: each
    /// page's links are persisted before the next page's matching-window query re-reads them,
    /// the same seam <see cref="DeduplicateBatchAsync"/> already documents for its own chunking.
    /// </summary>
    private async Task<(int processed, int groups, int linked, int duplicates)> DeduplicateTypeAsync<TEntity>(
        RecordType recordType,
        IQueryable<TEntity> set,
        Expression<Func<TEntity, DateTime>> timestamp,
        Func<TEntity, DeduplicationInput> toInput,
        string phaseName,
        int totalRecords,
        int startOffset,
        IProgress<DeduplicationProgress>? progress,
        CancellationToken ct) where TEntity : class
    {
        const int batchSize = 500;

        var timestampProperty = MemberName(timestamp);
        var eventTime = timestamp.Compile();

        var ordered = set
            .AsNoTracking()
            .OrderBy(e => EF.Property<DateTime>(e, timestampProperty));

        var totalProcessed = 0;
        var totalGroups = 0;
        var totalLinked = 0;
        var totalDuplicates = 0;
        DateTime? cursor = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var page = await (cursor is { } after
                    ? ordered.Where(e => EF.Property<DateTime>(e, timestampProperty) > after)
                    : ordered)
                .Take(batchSize)
                .ToListAsync(ct);

            if (page.Count == 0)
                break;

            var pageEnd = eventTime(page[^1]);
            if (page.Count == batchSize)
            {
                var tail = await ordered
                    .Where(e => EF.Property<DateTime>(e, timestampProperty) == pageEnd)
                    .ToListAsync(ct);

                page = [.. page.Where(e => eventTime(e) < pageEnd), .. tail];
            }

            cursor = pageEnd;

            var result = await DeduplicateBatchAsync(recordType, page.Select(toInput).ToList(), ct);
            totalProcessed += result.Processed;
            totalGroups += result.GroupsCreated;
            totalLinked += result.RecordsLinked;
            totalDuplicates += result.DuplicateGroups;

            progress?.Report(new DeduplicationProgress
            {
                TotalRecords = totalRecords,
                ProcessedRecords = startOffset + totalProcessed,
                GroupsFound = totalGroups,
                RecordsLinked = totalLinked,
                CurrentPhase = phaseName
            });
        }

        // Records linked by an earlier run are skipped above, so re-running over existing history
        // creates no links and on its own would heal nothing. Collapsing the type's canonical
        // groups repairs history ingested while the connectors' clocks were drifting apart.
        // Only wide-eligible types can hold such a split — the tight window never stopped working
        // — and this pass loads every primary of the type, which for a dense stream like
        // SensorGlucose would be hundreds of thousands of rows for nothing.
        // Unlike a reconcile batch it waits for the tenant's merge lock: a skipped pass would leave
        // the splits unhealed until the job is run again.
        if (WideMatchableTypes.Contains(recordType))
        {
            await using var db = await _contexts.CreateAsync(ct);
            totalDuplicates += await db.ExecuteInTransactionAsync(async token =>
            {
                await LockTenantMergesAsync(db, token);
                return await MergeDuplicateGroupsAsync(db, recordType, null, token);
            }, ct: ct);
        }

        return (totalProcessed, totalGroups, totalLinked, totalDuplicates);
    }

    private static StateSpan MergeStateSpans(List<StateSpan> stateSpans, Guid canonicalId)
    {
        if (stateSpans.Count == 0)
            throw new ArgumentException("Cannot merge empty list of state spans");

        var primary = stateSpans[0];
        var merged = new StateSpan
        {
            Id = primary.Id,
            Category = primary.Category,
            State = primary.State,
            StartTimestamp = primary.StartTimestamp,
            EndTimestamp = primary.EndTimestamp,
            Source = primary.Source,
            OriginalId = primary.OriginalId,
            Metadata = primary.Metadata != null
                ? new Dictionary<string, object>(primary.Metadata)
                : new(),
            CanonicalId = canonicalId,
            Sources = stateSpans.Select(s => s.Source).Where(s => s != null).Distinct().ToArray()!
        };

        // Enrich with data from other sources
        foreach (var span in stateSpans.Skip(1))
        {
            // If one source has end time and merged doesn't, take the end time
            if (!merged.EndTimestamp.HasValue && span.EndTimestamp.HasValue)
            {
                merged.EndTimestamp = span.EndTimestamp;
            }

            // Merge metadata
            if (span.Metadata != null)
            {
                foreach (var kvp in span.Metadata)
                {
                    merged.Metadata.TryAdd(kvp.Key, kvp.Value);
                }
            }
        }

        return merged;
    }

    /// <summary>
    /// Returns the current tenant's reconciliation cursor — the last reconciled link — or null if
    /// reconciliation has never run. A row written before the id column existed resumes at the
    /// first link of its instant, so the rest of that instant is reconciled again; the merge is
    /// idempotent.
    /// </summary>
    internal static async Task<ReconcileCursor?> GetCursorAsync(NocturneDbContext db, CancellationToken ct)
    {
        var state = await db.DedupReconcileState
            .Where(s => s.TenantId == db.TenantId)
            .FirstOrDefaultAsync(ct);

        return state is null
            ? null
            : new ReconcileCursor(state.LastReconciledLinkCreatedAt, state.LastReconciledLinkId ?? Guid.Empty);
    }

    /// <summary>
    /// Upserts the current tenant's reconciliation cursor, inserting a row if none exists
    /// or updating the existing one otherwise.
    /// </summary>
    internal static async Task SetCursorAsync(NocturneDbContext db, ReconcileCursor cursor, CancellationToken ct)
    {
        // Npgsql requires Kind=Utc to persist a timestamptz; SQLite tests won't catch a bad caller.
        var createdAt = cursor.CreatedAt.Kind switch
        {
            DateTimeKind.Utc => cursor.CreatedAt,
            DateTimeKind.Local => cursor.CreatedAt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(cursor.CreatedAt, DateTimeKind.Utc)
        };

        var state = await db.DedupReconcileState
            .Where(s => s.TenantId == db.TenantId)
            .FirstOrDefaultAsync(ct);

        if (state is null)
        {
            state = new DedupReconcileStateEntity { TenantId = db.TenantId };
            db.DedupReconcileState.Add(state);
        }

        state.LastReconciledLinkCreatedAt = createdAt;
        state.LastReconciledLinkId = cursor.Id;

        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Keyset position of <see cref="DeduplicationService.ReconcileNewLinksAsync"/> in the tenant's
/// links: the <c>sys_created_at</c> and id of the last link reconciled.
/// </summary>
internal readonly record struct ReconcileCursor(DateTime CreatedAt, Guid Id);

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// Service for deduplicating records from multiple data sources.
/// Links records that represent the same underlying event and provides unified views.
/// </summary>
public class DeduplicationService : IDeduplicationService
{
    private readonly NocturneDbContext _context;
    private readonly IServiceScopeFactory _scopeFactory;
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
    /// How far before the watermark each reconcile chunk re-reads. Covers links whose
    /// SysCreatedAt straddles a previous batch boundary; re-processing them is idempotent
    /// because <see cref="MergeDuplicateGroupsAsync"/> is a no-op once a region is merged.
    /// </summary>
    private static readonly TimeSpan ReconcileOverlap = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A record's match criteria paired with its soft-deleted status, keyed by record id
    /// when returned from <see cref="LoadRecordInfoAsync"/>.
    /// </summary>
    internal sealed record RecordInfo(MatchCriteria Criteria, bool IsDeleted);

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

    /// <summary>
    /// Event types that should be grouped together for deduplication.
    /// When a Basal and Temp Basal occur at the same time, they represent
    /// the same underlying event and should be deduplicated together.
    /// </summary>
    private static readonly HashSet<string> BasalRelatedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Basal",
        "Temp Basal"
    };

    /// <summary>
    /// Priority order for basal-related types. Higher priority types
    /// are preferred when merging duplicates.
    /// </summary>
    private static readonly Dictionary<string, int> BasalTypePriority = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Temp Basal", 1 },  // Highest priority - most specific
        { "Basal", 0 }       // Lower priority - generic
    };

    /// <inheritdoc cref="IDeduplicationService" />
    public DeduplicationService(
        NocturneDbContext context,
        IServiceScopeFactory scopeFactory,
        ILogger<DeduplicationService> logger,
        ITenantAccessor? tenantAccessor = null)
    {
        _context = context;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _tenantAccessor = tenantAccessor;
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
    /// sitting outside the window still blocks a wide match.
    /// </summary>
    private async Task<Dictionary<Guid, HashSet<string>>> LoadGroupSourcesAsync(
        string recordTypeStr,
        List<Guid> canonicalIds,
        CancellationToken ct)
    {
        var sourcesByCanonical = new Dictionary<Guid, HashSet<string>>();
        if (canonicalIds.Count == 0)
            return sourcesByCanonical;

        var rows = await _context.LinkedRecords
            .AsNoTracking()
            .Where(lr => lr.RecordType == recordTypeStr && canonicalIds.Contains(lr.CanonicalId))
            .Select(lr => new { lr.CanonicalId, lr.DataSource })
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            if (!sourcesByCanonical.TryGetValue(row.CanonicalId, out var sources))
            {
                sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                sourcesByCanonical[row.CanonicalId] = sources;
            }

            sources.Add(row.DataSource);
        }

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
    private async Task<Dictionary<Guid, (long Min, long Max)>> LoadGroupExtentsAsync(
        string recordTypeStr,
        List<Guid> canonicalIds,
        CancellationToken ct)
    {
        if (canonicalIds.Count == 0)
            return [];

        var rows = await _context.LinkedRecords
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
        var ordered = records.OrderBy(r => r.Mills).ToList();
        var processed = 0;
        var groupsCreated = 0;
        var recordsLinked = 0;
        var duplicateGroups = 0;

        foreach (var chunk in ordered.Chunk(DedupChunkSize))
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

        var recordTypeStr = recordType.ToString().ToLowerInvariant();
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
        var allPotentialMatches = await _context.LinkedRecords
            .AsNoTracking()
            .Where(lr => lr.RecordType == recordTypeStr)
            .Where(lr => lr.SourceTimestamp >= minMills && lr.SourceTimestamp <= maxMills)
            .ToListAsync(ct);

        // 3. One query: the criteria and soft-deleted status behind every candidate link, read by
        //    both the tight matcher below and the wide pass.
        var referencedIds = allPotentialMatches.Select(m => m.RecordId).ToHashSet();
        var info = await LoadRecordInfoAsync(recordType, referencedIds, ct);

        bool Matches(Guid recordId, MatchCriteria criteria) =>
            info.TryGetValue(recordId, out var candidate)
            && !candidate.IsDeleted
            && CriteriaMatch(recordType, candidate.Criteria, criteria);

        // 3b. The data sources behind each candidate group, needed only by the wide pass.
        //     Mutated as this chunk assigns records, so a second record of the same source sees
        //     the group its predecessor just joined.
        Dictionary<Guid, HashSet<string>> groupSources = new();
        if (wideEligible)
        {
            groupSources = await LoadGroupSourcesAsync(
                recordTypeStr, allPotentialMatches.Select(m => m.CanonicalId).Distinct().ToList(), ct);
        }

        // 4. One query: which input records are already linked?
        var inputIds = records.Select(r => r.RecordId).ToList();
        var alreadyLinked = (await _context.LinkedRecords
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

        // Canonical groups that already existed and were joined through the wide path, whose
        // primary may need re-deriving once the new links are persisted.
        var wideJoinedCanonicals = new HashSet<Guid>();

        var newLinks = new List<LinkedRecordEntity>();
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
                if (Matches(m.RecordId, record.Criteria))
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
                        && CriteriaMatch(recordType, priorCriteria, record.Criteria))
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
                    wideJoinedCanonicals.Add(wideCanonical.Value);

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

            // IsPrimary iff this is the first link this batch creates for a not-already-existing canonical.
            var isPrimary = !existingCanonicals.Contains(canonicalId.Value)
                            && newCanonicalsSeen.Add(canonicalId.Value);

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
            // sees the group it just joined and refuses to wide-match it.
            if (wideEligible)
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
            _context.LinkedRecords.AddRange(newLinks);
            await _context.SaveChangesAsync(ct);
            _context.ChangeTracker.Clear();
        }

        // 7. A wide join reaches up to ten minutes back, so it can land earlier than the group's
        //    primary — and unlike reconcile-merged groups, ingest-formed groups are never revisited
        //    by MergeDuplicateGroupsAsync. Re-derive the primary here with the same survivor rule
        //    MergeDuplicateGroupsAsync uses, or the event time the group displays would depend on
        //    which connector synced first. Scoped to wide joins: the tight path's sticky primary is
        //    long-standing behaviour.
        if (wideJoinedCanonicals.Count > 0)
        {
            var joined = wideJoinedCanonicals.ToList();
            var rows = await _context.LinkedRecords
                .Where(lr => lr.RecordType == recordTypeStr && joined.Contains(lr.CanonicalId))
                .ToListAsync(ct);

            var rowInfo = await LoadRecordInfoAsync(recordType, rows.Select(r => r.RecordId).ToHashSet(), ct);

            // Reads hide soft-deleted records and non-primary links alike, so promoting either a
            // deleted record or an orphaned link would render the whole group as nothing. A record
            // id missing from rowInfo is an orphaned link and is treated like a deleted one.
            bool IsPromotable(LinkedRecordEntity r) =>
                rowInfo.TryGetValue(r.RecordId, out var ri) && !ri.IsDeleted;

            var repointed = false;
            foreach (var group in rows.GroupBy(r => r.CanonicalId))
            {
                // Earliest promotable record, falling back to earliest overall when the group holds
                // nothing promotable — the same survivor rule as the reconcile merge.
                var survivor = group
                    .Where(IsPromotable)
                    .OrderBy(r => r.SourceTimestamp)
                    .ThenBy(r => r.RecordId)
                    .FirstOrDefault()
                    ?? group
                        .OrderBy(r => r.SourceTimestamp)
                        .ThenBy(r => r.RecordId)
                        .First();

                // A group with no primary at all renders as nothing, so repair it while the
                // survivor is already in hand.
                var currentPrimary = group.FirstOrDefault(r => r.IsPrimary);
                if (ReferenceEquals(survivor, currentPrimary))
                    continue;

                if (currentPrimary is not null)
                    currentPrimary.IsPrimary = false;
                survivor.IsPrimary = true;
                repointed = true;
            }

            if (repointed)
            {
                await _context.SaveChangesAsync(ct);
                _context.ChangeTracker.Clear();
            }
        }

        return new DeduplicationBatchResult(
            Processed: records.Count,
            GroupsCreated: groupsCreated,
            RecordsLinked: newLinks.Count,
            DuplicateGroups: duplicateGroups);
    }

    /// <summary>
    /// Collapses duplicate canonical groups for a record type within the current tenant.
    /// Two groups merge when their primary records fall within <see cref="MatchingWindowMillis"/>
    /// of each other and their <see cref="MatchCriteria"/> match; merging is transitive
    /// (union-find) and source-agnostic, mirroring insert-time <see cref="DeduplicateBatchAsync"/>
    /// semantics. A second pass then applies the same wide rules the insert path uses, so a
    /// cross-source pair missed at ingest (out-of-order connector syncs) heals here.
    /// For each merged super-group the surviving primary is the earliest-timestamp
    /// non-deleted record (falling back to earliest-overall when every record is soft-deleted);
    /// all linked rows are re-pointed to the survivor's canonical id and <c>IsPrimary</c> is set
    /// on exactly the survivor.
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
        var recordTypeStr = recordType.ToString().ToLowerInvariant();
        var wideEligible = WideMatchableTypes.Contains(recordType);

        // The span the candidate-bounded path actually loaded. Groups whose extent reaches within a
        // wide window of either end may have an ambiguator just outside it, so the wide pass defers
        // them. Null on the full path, which loads everything and has no boundary — absence rather
        // than a sentinel value, so the deferral below cannot be reached with a bound that would
        // overflow the subtraction.
        long? neighbourMinTs = null;
        long? neighbourMaxTs = null;

        List<LinkedRecordEntity> primaries;
        if (candidateCanonicalIds == null)
        {
            // Full reconcile: load every primary of this type, ordered by source timestamp.
            // Read-only (union-find input); the rows actually re-pointed below are loaded
            // separately and tracked. AsNoTracking keeps this O(all primaries) read off the heap.
            primaries = await _context.LinkedRecords
                .AsNoTracking()
                .Where(lr => lr.RecordType == recordTypeStr && lr.IsPrimary)
                .OrderBy(lr => lr.SourceTimestamp)
                .ToListAsync(ct);
        }
        else
        {
            // Candidate-bounded reconcile: never load all primaries. Load only the candidate
            // canonicals' primary links to learn their event timestamps, then DB-bound the
            // neighbour query to [minCandidateTs - window, maxCandidateTs + window]. This keeps
            // the candidate path O(candidates + window-slice) rather than O(all primaries).
            var candidatePrimaries = await _context.LinkedRecords
                .AsNoTracking()
                .Where(lr => lr.RecordType == recordTypeStr && lr.IsPrimary
                             && candidateCanonicalIds.Contains(lr.CanonicalId))
                .ToListAsync(ct);

            if (candidatePrimaries.Count == 0)
                return 0;

            // Wide-eligible types must see their wide neighbours too, or a drifted pair could
            // never heal on the candidate-bounded path. The reach is twice the wide window rather
            // than one: deciding a pair needs to see any third same-value group that would make it
            // ambiguous, and such a group can sit a full window beyond the pair's own edge. At one
            // window the same three groups merge or refuse depending on which one is the candidate.
            var neighbourWindowMillis = wideEligible ? 2 * WideMatchingWindowMillis : MatchingWindowMillis;
            var minTs = candidatePrimaries.Min(p => p.SourceTimestamp) - neighbourWindowMillis;
            var maxTs = candidatePrimaries.Max(p => p.SourceTimestamp) + neighbourWindowMillis;
            neighbourMinTs = minTs;
            neighbourMaxTs = maxTs;

            if (wideEligible)
            {
                // A wide-joined group spans up to a window, so its primary can sit outside the
                // neighbour range while its members sit inside it. Select the groups by their links
                // and then load those groups' primaries, so no group is ever half-visible to the
                // extent comparison below. Second, unremarked consequence: the tight pass now sees
                // those same extra primaries, so its reach on this path widens too — convergent,
                // since it only lets the tight rules collapse pairs a later pass would have anyway.
                var neighbourCanonicals = await _context.LinkedRecords
                    .AsNoTracking()
                    .Where(lr => lr.RecordType == recordTypeStr
                                 && lr.SourceTimestamp >= minTs && lr.SourceTimestamp <= maxTs)
                    .Select(lr => lr.CanonicalId)
                    .Distinct()
                    .ToListAsync(ct);

                // Selected by canonical id alone. A timestamp bound here would be unsound at any
                // width: everything below — the union-find, the extents, the boundary deferral —
                // is built from this list, so a group dropped for having a distant primary is not
                // deferred, it is invisible, and the pairs it would have made ambiguous merge.
                primaries = await _context.LinkedRecords
                    .AsNoTracking()
                    .Where(lr => lr.RecordType == recordTypeStr && lr.IsPrimary
                                 && neighbourCanonicals.Contains(lr.CanonicalId))
                    .OrderBy(lr => lr.SourceTimestamp)
                    .ToListAsync(ct);
            }
            else
            {
                primaries = await _context.LinkedRecords
                    .AsNoTracking()
                    .Where(lr => lr.RecordType == recordTypeStr && lr.IsPrimary
                                 && lr.SourceTimestamp >= minTs && lr.SourceTimestamp <= maxTs)
                    .OrderBy(lr => lr.SourceTimestamp)
                    .ToListAsync(ct);
            }
        }

        if (primaries.Count < 2)
            return 0;

        // 2. Load criteria + deleted status for the primary record ids.
        var primaryIds = primaries.Select(p => p.RecordId).ToHashSet();
        var info = await LoadRecordInfoAsync(recordType, primaryIds, ct);

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
                    Union(primaries[i].CanonicalId, primaries[j].CanonicalId);
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
                    recordTypeStr, partneredPrimaries.Select(p => p.CanonicalId).Distinct().ToList(), ct);
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

        var merged = 0;
        foreach (var canonicals in groupsByRoot.Values)
        {
            if (canonicals.Count < 2)
                continue;

            // Load every linked row in the merged canonicals (soft-deleted records included).
            var rows = await _context.LinkedRecords
                .Where(lr => lr.RecordType == recordTypeStr && canonicals.Contains(lr.CanonicalId))
                .ToListAsync(ct);
            if (rows.Count == 0)
                continue;

            // Load deleted status for all record ids in the super-group.
            var rowIds = rows.Select(r => r.RecordId).ToHashSet();
            var rowInfo = await LoadRecordInfoAsync(recordType, rowIds, ct);

            // Survivor = earliest-timestamp non-deleted record; fall back to earliest-overall
            // only if every record in the group is soft-deleted. A record id missing from rowInfo
            // is an orphaned link: reads hide it exactly as they hide a deleted record, so it is
            // never promotable. Same rule as the ingest path's primary re-derivation.
            bool IsDeleted(LinkedRecordEntity r) =>
                !rowInfo.TryGetValue(r.RecordId, out var ri) || ri.IsDeleted;

            var survivor = rows
                .Where(r => !IsDeleted(r))
                .OrderBy(r => r.SourceTimestamp)
                .FirstOrDefault()
                ?? rows.OrderBy(r => r.SourceTimestamp).First();

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
            await _context.SaveChangesAsync(ct);
            _context.ChangeTracker.Clear();
        }

        return merged;
    }

    /// <inheritdoc />
    public async Task<ReconcileResult> ReconcileNewLinksAsync(
        int batchSize,
        int maxBatches,
        CancellationToken cancellationToken = default)
    {
        var watermark = await GetWatermarkAsync(cancellationToken);

        // Re-read from a little before the watermark so links whose SysCreatedAt straddled the
        // previous batch's boundary aren't missed. Re-processing is idempotent: once a region is
        // merged, MergeDuplicateGroupsAsync finds nothing more to collapse there.
        // A fresh tenant (deploy-day backfill) has no watermark yet, so it defaults to
        // DateTime.MinValue — subtracting the overlap would underflow, so skip it and start at MinValue.
        var cutoff = watermark == DateTime.MinValue ? DateTime.MinValue : watermark - ReconcileOverlap;

        var merged = 0;
        var caughtUp = false;
        var previousMaxCreated = DateTime.MinValue;

        for (var batchNo = 0; batchNo < maxBatches; batchNo++)
        {
            // Tenant-scoped automatically via the global query filter.
            var batch = await _context.LinkedRecords
                .Where(lr => lr.SysCreatedAt >= cutoff)
                .OrderBy(lr => lr.SysCreatedAt)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
            {
                caughtUp = true;
                break;
            }

            // Reconcile per record type: parse the lowercased RecordType string back to the enum
            // and merge each type's candidate canonical groups.
            foreach (var group in batch.GroupBy(l => l.RecordType))
            {
                // RecordType is a free-form string column whose surface is broader than the enum.
                // A legacy/typo'd value must not throw and block all further batches for this tenant.
                if (!Enum.TryParse<RecordType>(group.Key, ignoreCase: true, out var type))
                {
                    _logger.LogWarning(
                        "Skipping linked_records group with unknown RecordType '{RecordType}' during reconcile",
                        group.Key);
                    continue;
                }

                var candidateCanonicalIds = group.Select(l => l.CanonicalId).ToHashSet();
                // GroupsMerged is the reduction in group count (k canonicals -> 1 == k-1), matching MergeDuplicateGroupsAsync.
                merged += await MergeDuplicateGroupsAsync(type, candidateCanonicalIds, cancellationToken);
            }

            var maxCreated = batch.Max(l => l.SysCreatedAt);
            await SetWatermarkAsync(maxCreated, cancellationToken);

            // Forward-progress guard: if the batch's max SysCreatedAt did not advance past the
            // previous batch (e.g. many links share the same instant and a full batch sits on one
            // boundary), re-reading from cutoff would loop forever — so stop here.
            if (batchNo > 0 && maxCreated <= previousMaxCreated)
            {
                caughtUp = true;
                break;
            }
            previousMaxCreated = maxCreated;
            cutoff = maxCreated - ReconcileOverlap;

            // A partial batch means we've drained everything at/after the cutoff.
            if (batch.Count < batchSize)
            {
                caughtUp = true;
                break;
            }
        }

        return new ReconcileResult(merged, caughtUp);
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
    /// (<c>e.TenantId == _context.TenantId</c>) explicitly so cross-tenant rows can never leak in.
    /// </para>
    /// </summary>
    private async Task<Dictionary<Guid, RecordInfo>> LoadRecordInfoAsync(
        RecordType recordType, HashSet<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return new Dictionary<Guid, RecordInfo>();

        return recordType switch
        {
            RecordType.SensorGlucose => await LoadAsync<SensorGlucoseEntity>(MatchCriteriaMapper.From, ids, ct),
            RecordType.Bolus => await LoadAsync<BolusEntity>(MatchCriteriaMapper.From, ids, ct),
            RecordType.CarbIntake => await LoadAsync<CarbIntakeEntity>(MatchCriteriaMapper.From, ids, ct),
            RecordType.BGCheck => await LoadAsync<BGCheckEntity>(MatchCriteriaMapper.From, ids, ct),
            RecordType.DeviceEvent => await LoadAsync<DeviceEventEntity>(MatchCriteriaMapper.From, ids, ct),
            RecordType.Note => await LoadAsync<NoteEntity>(_ => MatchCriteriaMapper.ForNote(), ids, ct),
            RecordType.BolusCalculation => await LoadAsync<BolusCalculationEntity>(MatchCriteriaMapper.From, ids, ct),
            RecordType.TempBasal => await LoadAsync<TempBasalEntity>(MatchCriteriaMapper.From, ids, ct),
            RecordType.StateSpan => await LoadStateSpanInfoAsync(ids, ct),
            _ => new Dictionary<Guid, RecordInfo>()
        };
    }

    /// <summary>
    /// Loads the criteria and soft-deleted status of every requested row of one entity type.
    /// </summary>
    private async Task<Dictionary<Guid, RecordInfo>> LoadAsync<TEntity>(
        Func<TEntity, MatchCriteria> toCriteria, HashSet<Guid> ids, CancellationToken ct)
        where TEntity : class, IV4Entity
    {
        var records = await _context.Set<TEntity>().AsNoTracking().IgnoreQueryFilters()
            .Where(e => e.TenantId == _context.TenantId && ids.Contains(e.Id)).ToListAsync(ct);
        return records.ToDictionary(e => e.Id, e => new RecordInfo(toCriteria(e), e.DeletedAt != null));
    }

    /// <summary>
    /// State spans are loaded apart from <see cref="LoadAsync{TEntity}"/> because
    /// <see cref="StateSpanEntity"/> is keyed by <c>OriginalId</c> rather than the
    /// <c>LegacyId</c> that <see cref="IV4Entity"/> requires.
    /// </summary>
    private async Task<Dictionary<Guid, RecordInfo>> LoadStateSpanInfoAsync(
        HashSet<Guid> ids, CancellationToken ct)
    {
        var records = await _context.StateSpans.AsNoTracking().IgnoreQueryFilters()
            .Where(s => s.TenantId == _context.TenantId && ids.Contains(s.Id)).ToListAsync(ct);
        return records.ToDictionary(s => s.Id,
            s => new RecordInfo(MatchCriteriaMapper.From(s), s.DeletedAt != null));
    }

    /// <summary>
    /// Internal test seam over <see cref="LoadRecordInfoAsync"/>. Exposed to the
    /// test assembly via <c>InternalsVisibleTo</c>; not part of the public API.
    /// </summary>
    internal Task<Dictionary<Guid, RecordInfo>> LoadRecordInfoForTestAsync(
        RecordType recordType, HashSet<Guid> ids, CancellationToken ct = default)
        => LoadRecordInfoAsync(recordType, ids, ct);

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
            // the exact comparison to rate alone; both intervals must be known and equal.
            RecordType.TempBasal => a.Rate.HasValue && b.Rate.HasValue
                && Math.Abs(a.Rate.Value - b.Rate.Value) <= Tolerance(a.RateTolerance, b.RateTolerance)
                && (!exact || (a.Duration.HasValue && a.Duration == b.Duration)),
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
            RecordType.Note => true,
            // An unparseable category maps to null, so null == null would collapse every state span
            // whose category the mapper could not read into one group. The states must agree
            // outright: treating an absent one as a wildcard makes the result depend on which span
            // is the argument, and the merge pass compares stored spans in both orders.
            RecordType.StateSpan => a.Category.HasValue && a.Category == b.Category
                && string.Equals(a.State, b.State, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    /// <inheritdoc />
    public async Task<IEnumerable<LinkedRecord>> GetLinkedRecordsAsync(
        Guid canonicalId,
        CancellationToken cancellationToken = default)
    {
        var entities = await _context.LinkedRecords
            .Where(lr => lr.CanonicalId == canonicalId)
            .OrderBy(static lr => lr.SourceTimestamp)
            .ToListAsync(cancellationToken);

        return entities.Select(e => new LinkedRecord
        {
            Id = e.Id.ToString(),
            CanonicalId = e.CanonicalId,
            RecordType = Enum.Parse<RecordType>(e.RecordType, ignoreCase: true),
            RecordId = e.RecordId,
            SourceTimestamp = e.SourceTimestamp,
            DataSource = e.DataSource,
            IsPrimary = e.IsPrimary,
            CreatedAt = e.SysCreatedAt
        });
    }

    /// <inheritdoc />
    public async Task<LinkedRecord?> GetLinkedRecordAsync(
        RecordType recordType,
        Guid recordId,
        CancellationToken cancellationToken = default)
    {
        var recordTypeStr = recordType.ToString().ToLowerInvariant();

        var entity = await _context.LinkedRecords
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
        var linkedRecords = await _context.LinkedRecords
            .Where(lr => lr.CanonicalId == canonicalId && lr.RecordType == "statespan")
            .OrderBy(static lr => lr.SourceTimestamp)
            .ToListAsync(cancellationToken);

        if (linkedRecords.Count == 0)
            return null;

        var recordIds = linkedRecords.Select(lr => lr.RecordId).ToList();
        var stateSpans = await _context.StateSpans
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

    /// <inheritdoc />
    public async Task<DeduplicationResult> DeduplicateAllAsync(
        IProgress<DeduplicationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var stateSpanCount = await _context.StateSpans.CountAsync(cancellationToken);
            var sensorGlucoseCount = await _context.SensorGlucose.CountAsync(cancellationToken);
            var bolusCount = await _context.Boluses.CountAsync(cancellationToken);
            var carbIntakeCount = await _context.CarbIntakes.CountAsync(cancellationToken);
            var bgCheckCount = await _context.BGChecks.CountAsync(cancellationToken);
            var deviceEventCount = await _context.DeviceEvents.CountAsync(cancellationToken);
            var noteCount = await _context.Notes.CountAsync(cancellationToken);
            var bolusCalcCount = await _context.BolusCalculations.CountAsync(cancellationToken);
            var tempBasalCount = await _context.TempBasals.CountAsync(cancellationToken);
            // NOTE: MeterGlucose was previously processed via DeduplicateEntriesAsync alongside
            // SensorGlucose, but there is no RecordType.MeterGlucose enum value. The old code also
            // double-processed SensorGlucose (once in Entries, once standalone). MeterGlucose dedup
            // is intentionally dropped; add a RecordType if it's needed in the future.
            var totalRecords = stateSpanCount + sensorGlucoseCount + bolusCount + carbIntakeCount
                + bgCheckCount + deviceEventCount + noteCount + bolusCalcCount + tempBasalCount;

            var processed = 0;
            var groupsCreated = 0;
            var recordsLinked = 0;
            var duplicateGroups = 0;

            // --- SensorGlucose ---
            var sensorGlucoseResult = await DeduplicateTypeAsync(
                RecordType.SensorGlucose,
                _context.SensorGlucose.OrderBy(e => e.Timestamp),
                e => new DeduplicationInput(
                    e.Id,
                    new DateTimeOffset(e.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    e.DataSource ?? DeduplicationInput.UnknownDataSource,
                    MatchCriteriaMapper.From(e)),
                "SensorGlucose", totalRecords, processed, progress, cancellationToken);
            processed += sensorGlucoseResult.processed;
            groupsCreated += sensorGlucoseResult.groups;
            recordsLinked += sensorGlucoseResult.linked;
            duplicateGroups += sensorGlucoseResult.duplicates;

            // --- Boluses ---
            var bolusResult = await DeduplicateTypeAsync(
                RecordType.Bolus,
                _context.Boluses.OrderBy(b => b.Timestamp),
                b => new DeduplicationInput(
                    b.Id,
                    new DateTimeOffset(b.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    b.DataSource ?? DeduplicationInput.UnknownDataSource,
                    MatchCriteriaMapper.From(b)),
                "Boluses", totalRecords, processed, progress, cancellationToken);
            processed += bolusResult.processed;
            groupsCreated += bolusResult.groups;
            recordsLinked += bolusResult.linked;
            duplicateGroups += bolusResult.duplicates;

            // --- CarbIntakes ---
            var carbIntakeResult = await DeduplicateTypeAsync(
                RecordType.CarbIntake,
                _context.CarbIntakes.OrderBy(c => c.Timestamp),
                c => new DeduplicationInput(
                    c.Id,
                    new DateTimeOffset(c.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    c.DataSource ?? DeduplicationInput.UnknownDataSource,
                    MatchCriteriaMapper.From(c)),
                "CarbIntakes", totalRecords, processed, progress, cancellationToken);
            processed += carbIntakeResult.processed;
            groupsCreated += carbIntakeResult.groups;
            recordsLinked += carbIntakeResult.linked;
            duplicateGroups += carbIntakeResult.duplicates;

            // --- BGChecks ---
            var bgCheckResult = await DeduplicateTypeAsync(
                RecordType.BGCheck,
                _context.BGChecks.OrderBy(bg => bg.Timestamp),
                bg => new DeduplicationInput(
                    bg.Id,
                    new DateTimeOffset(bg.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    bg.DataSource ?? DeduplicationInput.UnknownDataSource,
                    MatchCriteriaMapper.From(bg)),
                "BGChecks", totalRecords, processed, progress, cancellationToken);
            processed += bgCheckResult.processed;
            groupsCreated += bgCheckResult.groups;
            recordsLinked += bgCheckResult.linked;
            duplicateGroups += bgCheckResult.duplicates;

            // --- DeviceEvents ---
            var deviceEventResult = await DeduplicateTypeAsync(
                RecordType.DeviceEvent,
                _context.DeviceEvents.OrderBy(d => d.Timestamp),
                d => new DeduplicationInput(
                    d.Id,
                    new DateTimeOffset(d.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    d.DataSource ?? DeduplicationInput.UnknownDataSource,
                    MatchCriteriaMapper.From(d)),
                "DeviceEvents", totalRecords, processed, progress, cancellationToken);
            processed += deviceEventResult.processed;
            groupsCreated += deviceEventResult.groups;
            recordsLinked += deviceEventResult.linked;
            duplicateGroups += deviceEventResult.duplicates;

            // --- Notes ---
            var noteResult = await DeduplicateTypeAsync(
                RecordType.Note,
                _context.Notes.OrderBy(n => n.Timestamp),
                n => new DeduplicationInput(
                    n.Id,
                    new DateTimeOffset(n.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    n.DataSource ?? DeduplicationInput.UnknownDataSource,
                    MatchCriteriaMapper.ForNote()),
                "Notes", totalRecords, processed, progress, cancellationToken);
            processed += noteResult.processed;
            groupsCreated += noteResult.groups;
            recordsLinked += noteResult.linked;
            duplicateGroups += noteResult.duplicates;

            // --- BolusCalculations ---
            var bolusCalcResult = await DeduplicateTypeAsync(
                RecordType.BolusCalculation,
                _context.BolusCalculations.OrderBy(bc => bc.Timestamp),
                bc => new DeduplicationInput(
                    bc.Id,
                    new DateTimeOffset(bc.Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    bc.DataSource ?? DeduplicationInput.UnknownDataSource,
                    MatchCriteriaMapper.From(bc)),
                "BolusCalculations", totalRecords, processed, progress, cancellationToken);
            processed += bolusCalcResult.processed;
            groupsCreated += bolusCalcResult.groups;
            recordsLinked += bolusCalcResult.linked;
            duplicateGroups += bolusCalcResult.duplicates;

            // --- TempBasals ---
            var tempBasalResult = await DeduplicateTypeAsync(
                RecordType.TempBasal,
                _context.TempBasals.OrderBy(t => t.StartTimestamp),
                t => new DeduplicationInput(
                    t.Id,
                    new DateTimeOffset(t.StartTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    t.DataSource ?? DeduplicationInput.UnknownDataSource,
                    MatchCriteriaMapper.From(t)),
                "TempBasals", totalRecords, processed, progress, cancellationToken);
            processed += tempBasalResult.processed;
            groupsCreated += tempBasalResult.groups;
            recordsLinked += tempBasalResult.linked;
            duplicateGroups += tempBasalResult.duplicates;

            // --- StateSpans ---
            var stateSpanResult = await DeduplicateTypeAsync(
                RecordType.StateSpan,
                _context.StateSpans.OrderBy(s => s.StartTimestamp),
                s => new DeduplicationInput(
                    s.Id,
                    new DateTimeOffset(s.StartTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    s.Source ?? DeduplicationInput.UnknownDataSource,
                    MatchCriteriaMapper.From(s)),
                "StateSpans", totalRecords, processed, progress, cancellationToken);
            processed += stateSpanResult.processed;
            groupsCreated += stateSpanResult.groups;
            recordsLinked += stateSpanResult.linked;
            duplicateGroups += stateSpanResult.duplicates;

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
                EntriesProcessed = sensorGlucoseResult.processed,
                TreatmentsProcessed = 0,
                StateSpansProcessed = stateSpanResult.processed,
                SensorGlucoseProcessed = sensorGlucoseResult.processed,
                BolusesProcessed = bolusResult.processed,
                CarbIntakesProcessed = carbIntakeResult.processed,
                BGChecksProcessed = bgCheckResult.processed,
                DeviceEventsProcessed = deviceEventResult.processed,
                NotesProcessed = noteResult.processed,
                BolusCalculationsProcessed = bolusCalcResult.processed,
                TempBasalsProcessed = tempBasalResult.processed,
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

        _runningJobs[jobId] = status;
        _jobTenants[jobId] = tenantContext.TenantId;
        _jobCancellations[jobId] = cts;

        // Start the job in the background with its own scope
        _ = Task.Run(async () =>
        {
            // Create a new scope for the background work to get a fresh DbContext
            await using var scope = _scopeFactory.CreateAsyncScope();

            // Pin the tenant before anything is resolved from the scope: the scoped DbContext reads
            // the accessor in its factory, so resolving the service first would bake in an empty
            // tenant. Same ordering as DeduplicationReconciliationBackgroundService.
            scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(tenantContext);

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

    private async Task<(int processed, int groups, int linked, int duplicates)> DeduplicateTypeAsync<TEntity>(
        RecordType recordType,
        IQueryable<TEntity> query,
        Func<TEntity, DeduplicationInput> toInput,
        string phaseName,
        int totalRecords,
        int startOffset,
        IProgress<DeduplicationProgress>? progress,
        CancellationToken ct) where TEntity : class
    {
        const int batchSize = 500;
        var allEntities = await query.ToListAsync(ct);
        var inputs = allEntities.Select(toInput).ToList();

        var totalProcessed = 0;
        var totalGroups = 0;
        var totalLinked = 0;
        var totalDuplicates = 0;

        foreach (var chunk in inputs.Chunk(batchSize))
        {
            ct.ThrowIfCancellationRequested();

            var result = await DeduplicateBatchAsync(recordType, chunk, ct);
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
        if (WideMatchableTypes.Contains(recordType))
        {
            totalDuplicates += await MergeDuplicateGroupsAsync(recordType, null, ct);
        }

        return (totalProcessed, totalGroups, totalLinked, totalDuplicates);
    }

    private static Treatment MergeTreatments(List<Treatment> treatments, Guid canonicalId)
    {
        if (treatments.Count == 0)
            throw new ArgumentException("Cannot merge empty list of treatments");

        // For basal-related treatments, prefer the highest priority type (e.g., Temp Basal over Basal)
        var primary = treatments[0];
        var preferredEventType = GetPreferredEventType(treatments);

        // When the preferred event type differs from the primary (e.g., Temp Basal preferred but
        // Basal is first by timestamp), use basal-related fields from the preferred-type treatment
        // so Duration/Percent/Rate come from the correct source.
        var basalSource = primary;
        if (preferredEventType != null && preferredEventType != primary.EventType)
        {
            basalSource = treatments.FirstOrDefault(t => t.EventType == preferredEventType) ?? primary;
        }

        var merged = new Treatment
        {
            Id = primary.Id,
            Mills = primary.Mills,
            Created_at = primary.Created_at,
            EventType = preferredEventType,
            Insulin = primary.Insulin,
            Carbs = primary.Carbs,
            Protein = primary.Protein,
            Fat = primary.Fat,
            Duration = basalSource.Duration,
            EnteredBy = primary.EnteredBy,
            Notes = primary.Notes,
            Reason = primary.Reason,
            Glucose = primary.Glucose,
            GlucoseType = primary.GlucoseType,
            Profile = primary.Profile,
            Percent = basalSource.Percent,
            Rate = basalSource.Rate,
            DataSource = primary.DataSource,
            AdditionalProperties = primary.AdditionalProperties != null
                ? new Dictionary<string, object>(primary.AdditionalProperties)
                : new(),
            CanonicalId = canonicalId,
            Sources = treatments.Select(t => t.DataSource).Where(s => s != null).Distinct().ToArray()!
        };

        // Enrich with data from other sources
        foreach (var treatment in treatments.Skip(1))
        {
            merged.Notes ??= treatment.Notes;
            merged.Reason ??= treatment.Reason;
            merged.Glucose ??= treatment.Glucose;
            merged.GlucoseType ??= treatment.GlucoseType;
            merged.Profile ??= treatment.Profile;
            merged.Protein ??= treatment.Protein;
            merged.Fat ??= treatment.Fat;

            // Enrich basal-related fields
            merged.Duration ??= treatment.Duration;
            merged.Percent ??= treatment.Percent;
            merged.Rate ??= treatment.Rate;
            merged.Carbs ??= treatment.Carbs;
            merged.Insulin ??= treatment.Insulin;

            // Merge additional properties
            if (treatment.AdditionalProperties != null)
            {
                foreach (var kvp in treatment.AdditionalProperties)
                {
                    merged.AdditionalProperties.TryAdd(kvp.Key, kvp.Value);
                }
            }
        }

        return merged;
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
    /// Gets the priority for a basal-related type.
    /// Higher values indicate higher priority (preferred when deduplicating).
    /// </summary>
    private static int GetBasalTypePriority(string? eventType)
    {
        if (string.IsNullOrEmpty(eventType))
            return -1;

        return BasalTypePriority.TryGetValue(eventType, out var priority) ? priority : -1;
    }

    /// <summary>
    /// Gets the preferred event type when merging treatments.
    /// For basal-related types, returns the highest priority type among all treatments.
    /// For other types, returns the primary treatment's event type.
    /// </summary>
    private static string? GetPreferredEventType(List<Treatment> treatments)
    {
        if (treatments.Count == 0)
            return null;

        var primary = treatments[0];

        // Check if any treatment is a basal-related type
        var basalTypes = treatments
            .Where(t => !string.IsNullOrEmpty(t.EventType) && BasalRelatedTypes.Contains(t.EventType))
            .Select(t => t.EventType!)
            .Distinct()
            .ToList();

        if (basalTypes.Count == 0)
        {
            // No basal-related types, use primary's event type
            return primary.EventType;
        }

        // Return the highest priority basal type
        return basalTypes
            .OrderByDescending(GetBasalTypePriority)
            .First();
    }

    /// <summary>
    /// Returns the current tenant's reconciliation watermark — the ingestion time of the last
    /// reconciled link — or <see cref="DateTime.MinValue"/> if reconciliation has never run.
    /// </summary>
    internal async Task<DateTime> GetWatermarkAsync(CancellationToken ct)
    {
        var state = await _context.DedupReconcileState
            .Where(s => s.TenantId == _context.TenantId)
            .FirstOrDefaultAsync(ct);

        return state?.LastReconciledLinkCreatedAt ?? DateTime.MinValue;
    }

    /// <summary>
    /// Upserts the current tenant's reconciliation watermark, inserting a row if none exists
    /// or updating the existing one otherwise.
    /// </summary>
    internal async Task SetWatermarkAsync(DateTime value, CancellationToken ct)
    {
        // Npgsql requires Kind=Utc to persist a timestamptz; SQLite tests won't catch a bad caller.
        value = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        var state = await _context.DedupReconcileState
            .Where(s => s.TenantId == _context.TenantId)
            .FirstOrDefaultAsync(ct);

        if (state is null)
        {
            // single-threaded per tenant; PK guards accidental concurrent insert
            _context.DedupReconcileState.Add(new DedupReconcileStateEntity
            {
                TenantId = _context.TenantId,
                LastReconciledLinkCreatedAt = value
            });
        }
        else
        {
            state.LastReconciledLinkCreatedAt = value;
        }

        await _context.SaveChangesAsync(ct);
    }
}

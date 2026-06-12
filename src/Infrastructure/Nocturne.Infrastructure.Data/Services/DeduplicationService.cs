using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Infrastructure;
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

    private static readonly TimeSpan MatchingWindow = TimeSpan.FromSeconds(30);
    private static readonly long MatchingWindowMillis = (long)MatchingWindow.TotalMilliseconds;

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

    private static readonly ConcurrentDictionary<Guid, DeduplicationJobStatus> _runningJobs = new();
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> _jobCancellations = new();

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
        ILogger<DeduplicationService> logger)
    {
        _context = context;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Guid> GetOrCreateCanonicalIdAsync(
        RecordType recordType,
        long mills,
        MatchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        var recordTypeStr = recordType.ToString().ToLowerInvariant();
        var windowStart = mills - MatchingWindowMillis;
        var windowEnd = mills + MatchingWindowMillis;

        // Look for existing linked records in the time window
        var potentialMatches = await _context.LinkedRecords
            .Where(lr => lr.RecordType == recordTypeStr)
            .Where(lr => lr.SourceTimestamp >= windowStart && lr.SourceTimestamp <= windowEnd)
            .ToListAsync(cancellationToken);

        if (potentialMatches.Count == 0)
        {
            // No matches found, create a new canonical ID
            return Guid.CreateVersion7();
        }

        // For state spans, check category and state
        if (recordType == RecordType.StateSpan && criteria.Category.HasValue)
        {
            var canonicalIds = potentialMatches.Select(m => m.CanonicalId).Distinct().ToList();
            var categoryStr = criteria.Category.Value.ToString();

            foreach (var canonicalId in canonicalIds)
            {
                var recordIds = potentialMatches
                    .Where(m => m.CanonicalId == canonicalId)
                    .Select(m => m.RecordId)
                    .ToList();

                var stateSpans = await _context.StateSpans
                    .Where(s => recordIds.Contains(s.Id))
                    .ToListAsync(cancellationToken);

                foreach (var stateSpan in stateSpans)
                {
                    if (!string.Equals(stateSpan.Category, categoryStr, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!string.IsNullOrEmpty(criteria.State) &&
                        !string.Equals(stateSpan.State, criteria.State, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Match found
                    return canonicalId;
                }
            }
        }
        // For sensor glucose, check glucose value matching
        else if (recordType == RecordType.SensorGlucose && criteria.GlucoseValue.HasValue)
        {
            var canonicalIds = potentialMatches.Select(m => m.CanonicalId).Distinct().ToList();

            foreach (var canonicalId in canonicalIds)
            {
                var recordIds = potentialMatches
                    .Where(m => m.CanonicalId == canonicalId)
                    .Select(m => m.RecordId)
                    .ToList();

                var readings = await _context.SensorGlucose
                    .Where(r => recordIds.Contains(r.Id))
                    .ToListAsync(cancellationToken);

                foreach (var reading in readings)
                {
                    if (Math.Abs(reading.Mgdl - criteria.GlucoseValue.Value) <= criteria.GlucoseTolerance)
                    {
                        return canonicalId;
                    }
                }
            }
        }
        // For boluses, check insulin value matching
        else if (recordType == RecordType.Bolus && criteria.Insulin.HasValue)
        {
            var canonicalIds = potentialMatches.Select(m => m.CanonicalId).Distinct().ToList();

            foreach (var canonicalId in canonicalIds)
            {
                var recordIds = potentialMatches
                    .Where(m => m.CanonicalId == canonicalId)
                    .Select(m => m.RecordId)
                    .ToList();

                var boluses = await _context.Boluses
                    .Where(b => recordIds.Contains(b.Id))
                    .ToListAsync(cancellationToken);

                foreach (var bolus in boluses)
                {
                    if (Math.Abs(bolus.Insulin - criteria.Insulin.Value) <= criteria.InsulinTolerance)
                    {
                        return canonicalId;
                    }
                }
            }
        }
        // For carb intakes, check carbs value matching
        else if (recordType == RecordType.CarbIntake && criteria.Carbs.HasValue)
        {
            var canonicalIds = potentialMatches.Select(m => m.CanonicalId).Distinct().ToList();

            foreach (var canonicalId in canonicalIds)
            {
                var recordIds = potentialMatches
                    .Where(m => m.CanonicalId == canonicalId)
                    .Select(m => m.RecordId)
                    .ToList();

                var carbs = await _context.CarbIntakes
                    .Where(c => recordIds.Contains(c.Id))
                    .ToListAsync(cancellationToken);

                foreach (var carb in carbs)
                {
                    if (Math.Abs(carb.Carbs - criteria.Carbs.Value) <= criteria.CarbsTolerance)
                    {
                        return canonicalId;
                    }
                }
            }
        }
        // For BG checks, check glucose value matching
        else if (recordType == RecordType.BGCheck && criteria.GlucoseValue.HasValue)
        {
            var canonicalIds = potentialMatches.Select(m => m.CanonicalId).Distinct().ToList();

            foreach (var canonicalId in canonicalIds)
            {
                var recordIds = potentialMatches
                    .Where(m => m.CanonicalId == canonicalId)
                    .Select(m => m.RecordId)
                    .ToList();

                var bgChecks = await _context.BGChecks
                    .Where(bg => recordIds.Contains(bg.Id))
                    .ToListAsync(cancellationToken);

                foreach (var bg in bgChecks)
                {
                    if (Math.Abs(bg.Glucose - criteria.GlucoseValue.Value) <= criteria.GlucoseTolerance)
                    {
                        return canonicalId;
                    }
                }
            }
        }
        // For device events, check event type matching
        else if (recordType == RecordType.DeviceEvent && !string.IsNullOrEmpty(criteria.EventType))
        {
            var canonicalIds = potentialMatches.Select(m => m.CanonicalId).Distinct().ToList();

            foreach (var canonicalId in canonicalIds)
            {
                var recordIds = potentialMatches
                    .Where(m => m.CanonicalId == canonicalId)
                    .Select(m => m.RecordId)
                    .ToList();

                var events = await _context.DeviceEvents
                    .Where(e => recordIds.Contains(e.Id))
                    .ToListAsync(cancellationToken);

                foreach (var e in events)
                {
                    if (string.Equals(e.EventType, criteria.EventType, StringComparison.OrdinalIgnoreCase))
                    {
                        return canonicalId;
                    }
                }
            }
        }
        // For notes, time-window only matching
        else if (recordType == RecordType.Note)
        {
            if (potentialMatches.Count > 0)
            {
                return potentialMatches.First().CanonicalId;
            }
        }
        // For bolus calculations, check carb input matching
        else if (recordType == RecordType.BolusCalculation && criteria.Carbs.HasValue)
        {
            var canonicalIds = potentialMatches.Select(m => m.CanonicalId).Distinct().ToList();

            foreach (var canonicalId in canonicalIds)
            {
                var recordIds = potentialMatches
                    .Where(m => m.CanonicalId == canonicalId)
                    .Select(m => m.RecordId)
                    .ToList();

                var calcs = await _context.BolusCalculations
                    .Where(bc => recordIds.Contains(bc.Id))
                    .ToListAsync(cancellationToken);

                foreach (var bc in calcs)
                {
                    if (Math.Abs((bc.CarbInput ?? 0) - criteria.Carbs.Value) <= criteria.CarbsTolerance)
                    {
                        return canonicalId;
                    }
                }
            }
        }
        // For temp basals, check rate and origin matching
        else if (recordType == RecordType.TempBasal && criteria.Rate.HasValue)
        {
            var canonicalIds = potentialMatches.Select(m => m.CanonicalId).Distinct().ToList();

            foreach (var canonicalId in canonicalIds)
            {
                var recordIds = potentialMatches
                    .Where(m => m.CanonicalId == canonicalId)
                    .Select(m => m.RecordId)
                    .ToList();

                var tempBasals = await _context.TempBasals
                    .Where(tb => recordIds.Contains(tb.Id))
                    .ToListAsync(cancellationToken);

                foreach (var tb in tempBasals)
                {
                    if (Math.Abs(tb.Rate - criteria.Rate.Value) <= criteria.RateTolerance)
                    {
                        return canonicalId;
                    }
                }
            }
        }

        // No matching records found, create a new canonical ID
        return Guid.CreateVersion7();
    }

    /// <inheritdoc />
    public async Task LinkRecordAsync(
        Guid canonicalId,
        RecordType recordType,
        Guid recordId,
        long mills,
        string dataSource,
        CancellationToken cancellationToken = default)
    {
        var recordTypeStr = recordType.ToString().ToLowerInvariant();

        // Check if this record is already linked
        var existing = await _context.LinkedRecords
            .FirstOrDefaultAsync(lr =>
                lr.RecordType == recordTypeStr && lr.RecordId == recordId,
                cancellationToken);

        if (existing != null)
        {
            _logger.LogDebug(
                "Record {RecordType} {RecordId} already linked to canonical {CanonicalId}",
                recordType, recordId, existing.CanonicalId);
            return;
        }

        // Check if this should be the primary record (earliest timestamp)
        var existingInGroup = await _context.LinkedRecords
            .Where(lr => lr.CanonicalId == canonicalId)
            .OrderBy(static lr => lr.SourceTimestamp)
            .FirstOrDefaultAsync(cancellationToken);

        var isPrimary = existingInGroup == null || mills < existingInGroup.SourceTimestamp;

        // If the existing primary references a record that no longer exists,
        // clean up the orphaned entries and promote this record to primary.
        if (!isPrimary && existingInGroup is { IsPrimary: true })
        {
            var primaryExists = await RecordExistsAsync(recordTypeStr, existingInGroup.RecordId, cancellationToken);
            if (!primaryExists)
            {
                // Remove all orphaned linked records in this group
                var orphaned = await _context.LinkedRecords
                    .Where(lr => lr.CanonicalId == canonicalId)
                    .ToListAsync(cancellationToken);
                var orphanedIds = orphaned.Select(lr => lr.RecordId).ToHashSet();

                foreach (var o in orphaned)
                {
                    var exists = orphanedIds.Contains(recordId) && o.RecordId == recordId
                        ? true // The record we're about to link obviously exists
                        : await RecordExistsAsync(recordTypeStr, o.RecordId, cancellationToken);
                    if (!exists)
                    {
                        _context.LinkedRecords.Remove(o);
                    }
                }

                isPrimary = true;
                _logger.LogDebug(
                    "Promoted {RecordType} {RecordId} to primary after orphaned primary cleanup in canonical {CanonicalId}",
                    recordType, recordId, canonicalId);
            }
        }

        // If this is the new primary, demote the old primary
        if (isPrimary && existingInGroup != null && _context.Entry(existingInGroup).State != Microsoft.EntityFrameworkCore.EntityState.Deleted)
        {
            existingInGroup.IsPrimary = false;
        }

        var linkedRecord = new LinkedRecordEntity
        {
            CanonicalId = canonicalId,
            RecordType = recordTypeStr,
            RecordId = recordId,
            SourceTimestamp = mills,
            DataSource = dataSource,
            IsPrimary = isPrimary
        };

        _context.LinkedRecords.Add(linkedRecord);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogDebug(
            "Linked {RecordType} {RecordId} to canonical {CanonicalId} (primary: {IsPrimary})",
            recordType, recordId, canonicalId, isPrimary);
    }

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

        // 1. Compute union time window
        var minMills = records.Min(r => r.Mills) - MatchingWindowMillis;
        var maxMills = records.Max(r => r.Mills) + MatchingWindowMillis;

        // 2. One query: all linked_records in the window for this type
        var allPotentialMatches = await _context.LinkedRecords
            .Where(lr => lr.RecordType == recordTypeStr)
            .Where(lr => lr.SourceTimestamp >= minMills && lr.SourceTimestamp <= maxMills)
            .ToListAsync(ct);

        // 3. One query: load type-specific matcher
        var referencedIds = allPotentialMatches.Select(m => m.RecordId).ToHashSet();
        var matcher = await LoadMatcherAsync(recordType, referencedIds, ct);

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
        var newCanonicalReps = new List<(long mills, Guid canonicalId, MatchCriteria criteria)>();
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
                if (matcher(m.RecordId, record.Criteria))
                {
                    canonicalId = m.CanonicalId;
                    duplicateGroups++;
                    break;
                }
            }

            // Intra-batch match: scan canonicals created earlier in this batch, not records.
            // Records sharing a canonical share matching criteria by transitivity, so checking
            // one representative per canonical is sufficient.
            if (canonicalId == null)
            {
                foreach (var (priorMills, priorCanonical, priorCriteria) in newCanonicalReps)
                {
                    if (Math.Abs(priorMills - record.Mills) <= MatchingWindowMillis
                        && CriteriaMatch(recordType, priorCriteria, record.Criteria))
                    {
                        canonicalId = priorCanonical;
                        duplicateGroups++;
                        break;
                    }
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
                newCanonicalReps.Add((record.Mills, canonicalId.Value, record.Criteria));
            }
        }

        // 6. Bulk insert
        if (newLinks.Count > 0)
        {
            _context.LinkedRecords.AddRange(newLinks);
            await _context.SaveChangesAsync(ct);
            _context.ChangeTracker.Clear();
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
    /// semantics. For each merged super-group the surviving primary is the earliest-timestamp
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

        List<LinkedRecordEntity> primaries;
        if (candidateCanonicalIds == null)
        {
            // Full reconcile: load every primary of this type, ordered by source timestamp.
            primaries = await _context.LinkedRecords
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
                .Where(lr => lr.RecordType == recordTypeStr && lr.IsPrimary
                             && candidateCanonicalIds.Contains(lr.CanonicalId))
                .ToListAsync(ct);

            if (candidatePrimaries.Count == 0)
                return 0;

            var minTs = candidatePrimaries.Min(p => p.SourceTimestamp) - MatchingWindowMillis;
            var maxTs = candidatePrimaries.Max(p => p.SourceTimestamp) + MatchingWindowMillis;

            primaries = await _context.LinkedRecords
                .Where(lr => lr.RecordType == recordTypeStr && lr.IsPrimary
                             && lr.SourceTimestamp >= minTs && lr.SourceTimestamp <= maxTs)
                .OrderBy(lr => lr.SourceTimestamp)
                .ToListAsync(ct);
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
            // only if every record in the group is soft-deleted.
            bool IsDeleted(LinkedRecordEntity r) =>
                rowInfo.TryGetValue(r.RecordId, out var ri) && ri.IsDeleted;

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

    private async Task<bool> RecordExistsAsync(string recordType, Guid recordId, CancellationToken ct)
    {
        return recordType switch
        {
            "bolus" => await _context.Boluses.AnyAsync(b => b.Id == recordId, ct),
            "carbintake" => await _context.CarbIntakes.AnyAsync(c => c.Id == recordId, ct),
            "sensorglucose" => await _context.SensorGlucose.AnyAsync(s => s.Id == recordId, ct),
            "tempbasal" => await _context.TempBasals.AnyAsync(t => t.Id == recordId, ct),
            "bgcheck" => await _context.BGChecks.AnyAsync(b => b.Id == recordId, ct),
            "deviceevent" => await _context.DeviceEvents.AnyAsync(d => d.Id == recordId, ct),
            "note" => await _context.Notes.AnyAsync(n => n.Id == recordId, ct),
            "boluscalculation" => await _context.BolusCalculations.AnyAsync(b => b.Id == recordId, ct),
            _ => true // Assume exists for unknown types to avoid accidental promotion
        };
    }

    // --- Per-type MatchCriteria builders ---
    // Single source of truth for each record type's MatchCriteria. Both the inline
    // dedup pass (DeduplicateAllAsync) and the reconciliation loader (LoadRecordInfoAsync)
    // call these so the criteria definitions cannot drift between the two code paths.

    private static MatchCriteria BuildCriteria(SensorGlucoseEntity e) =>
        new() { GlucoseValue = e.Mgdl, GlucoseTolerance = 5.0 };

    private static MatchCriteria BuildCriteria(BolusEntity b) =>
        new() { Insulin = b.Insulin, InsulinTolerance = 0.05 };

    private static MatchCriteria BuildCriteria(CarbIntakeEntity c) =>
        new() { Carbs = c.Carbs, CarbsTolerance = 1.0 };

    private static MatchCriteria BuildCriteria(BGCheckEntity bg) =>
        new() { GlucoseValue = bg.Glucose, GlucoseTolerance = 5.0 };

    private static MatchCriteria BuildCriteria(DeviceEventEntity d) =>
        new() { EventType = d.EventType };

    private static MatchCriteria BuildNoteCriteria() =>
        new();

    private static MatchCriteria BuildCriteria(BolusCalculationEntity bc) =>
        new() { Carbs = bc.CarbInput ?? 0, CarbsTolerance = 1.0 };

    private static MatchCriteria BuildCriteria(TempBasalEntity t) =>
        new() { Rate = t.Rate, RateTolerance = 0.05 };

    private static MatchCriteria BuildCriteria(StateSpanEntity s) =>
        new()
        {
            Category = Enum.TryParse<StateSpanCategory>(s.Category, ignoreCase: true, out var cat)
                ? cat : null,
            State = s.State
        };

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

        switch (recordType)
        {
            case RecordType.SensorGlucose:
            {
                var records = await _context.SensorGlucose.IgnoreQueryFilters()
                    .Where(e => e.TenantId == _context.TenantId && ids.Contains(e.Id)).ToListAsync(ct);
                return records.ToDictionary(e => e.Id,
                    e => new RecordInfo(BuildCriteria(e), e.DeletedAt != null));
            }
            case RecordType.Bolus:
            {
                var records = await _context.Boluses.IgnoreQueryFilters()
                    .Where(b => b.TenantId == _context.TenantId && ids.Contains(b.Id)).ToListAsync(ct);
                return records.ToDictionary(b => b.Id,
                    b => new RecordInfo(BuildCriteria(b), b.DeletedAt != null));
            }
            case RecordType.CarbIntake:
            {
                var records = await _context.CarbIntakes.IgnoreQueryFilters()
                    .Where(c => c.TenantId == _context.TenantId && ids.Contains(c.Id)).ToListAsync(ct);
                return records.ToDictionary(c => c.Id,
                    c => new RecordInfo(BuildCriteria(c), c.DeletedAt != null));
            }
            case RecordType.BGCheck:
            {
                var records = await _context.BGChecks.IgnoreQueryFilters()
                    .Where(bg => bg.TenantId == _context.TenantId && ids.Contains(bg.Id)).ToListAsync(ct);
                return records.ToDictionary(bg => bg.Id,
                    bg => new RecordInfo(BuildCriteria(bg), bg.DeletedAt != null));
            }
            case RecordType.DeviceEvent:
            {
                var records = await _context.DeviceEvents.IgnoreQueryFilters()
                    .Where(d => d.TenantId == _context.TenantId && ids.Contains(d.Id)).ToListAsync(ct);
                return records.ToDictionary(d => d.Id,
                    d => new RecordInfo(BuildCriteria(d), d.DeletedAt != null));
            }
            case RecordType.Note:
            {
                var records = await _context.Notes.IgnoreQueryFilters()
                    .Where(n => n.TenantId == _context.TenantId && ids.Contains(n.Id)).ToListAsync(ct);
                return records.ToDictionary(n => n.Id,
                    n => new RecordInfo(BuildNoteCriteria(), n.DeletedAt != null));
            }
            case RecordType.BolusCalculation:
            {
                var records = await _context.BolusCalculations.IgnoreQueryFilters()
                    .Where(bc => bc.TenantId == _context.TenantId && ids.Contains(bc.Id)).ToListAsync(ct);
                return records.ToDictionary(bc => bc.Id,
                    bc => new RecordInfo(BuildCriteria(bc), bc.DeletedAt != null));
            }
            case RecordType.TempBasal:
            {
                var records = await _context.TempBasals.IgnoreQueryFilters()
                    .Where(t => t.TenantId == _context.TenantId && ids.Contains(t.Id)).ToListAsync(ct);
                return records.ToDictionary(t => t.Id,
                    t => new RecordInfo(BuildCriteria(t), t.DeletedAt != null));
            }
            case RecordType.StateSpan:
            {
                // StateSpanEntity has no DeletedAt column, so IsDeleted is always false.
                var records = await _context.StateSpans.IgnoreQueryFilters()
                    .Where(s => s.TenantId == _context.TenantId && ids.Contains(s.Id)).ToListAsync(ct);
                return records.ToDictionary(s => s.Id,
                    s => new RecordInfo(BuildCriteria(s), false));
            }
            default:
                return new Dictionary<Guid, RecordInfo>();
        }
    }

    /// <summary>
    /// Internal test seam over <see cref="LoadRecordInfoAsync"/>. Exposed to the
    /// test assembly via <c>InternalsVisibleTo</c>; not part of the public API.
    /// </summary>
    internal Task<Dictionary<Guid, RecordInfo>> LoadRecordInfoForTestAsync(
        RecordType recordType, HashSet<Guid> ids, CancellationToken ct = default)
        => LoadRecordInfoAsync(recordType, ids, ct);

    private async Task<Func<Guid, MatchCriteria, bool>> LoadMatcherAsync(
        RecordType recordType, HashSet<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return (_, _) => false;

        switch (recordType)
        {
            case RecordType.TempBasal:
            {
                var records = (await _context.TempBasals
                    .Where(t => ids.Contains(t.Id))
                    .ToListAsync(ct))
                    .ToDictionary(t => t.Id);
                return (id, criteria) =>
                    records.TryGetValue(id, out var tb) && criteria.Rate.HasValue
                    && Math.Abs(tb.Rate - criteria.Rate.Value) <= criteria.RateTolerance;
            }
            case RecordType.SensorGlucose:
            {
                var records = (await _context.SensorGlucose
                    .Where(s => ids.Contains(s.Id))
                    .ToListAsync(ct))
                    .ToDictionary(s => s.Id);
                return (id, criteria) =>
                    records.TryGetValue(id, out var sg) && criteria.GlucoseValue.HasValue
                    && Math.Abs(sg.Mgdl - criteria.GlucoseValue.Value) <= criteria.GlucoseTolerance;
            }
            case RecordType.Bolus:
            {
                var records = (await _context.Boluses
                    .Where(b => ids.Contains(b.Id))
                    .ToListAsync(ct))
                    .ToDictionary(b => b.Id);
                return (id, criteria) =>
                    records.TryGetValue(id, out var b) && criteria.Insulin.HasValue
                    && Math.Abs(b.Insulin - criteria.Insulin.Value) <= criteria.InsulinTolerance;
            }
            case RecordType.CarbIntake:
            {
                var records = (await _context.CarbIntakes
                    .Where(c => ids.Contains(c.Id))
                    .ToListAsync(ct))
                    .ToDictionary(c => c.Id);
                return (id, criteria) =>
                    records.TryGetValue(id, out var c) && criteria.Carbs.HasValue
                    && Math.Abs(c.Carbs - criteria.Carbs.Value) <= criteria.CarbsTolerance;
            }
            case RecordType.BGCheck:
            {
                var records = (await _context.BGChecks
                    .Where(bg => ids.Contains(bg.Id))
                    .ToListAsync(ct))
                    .ToDictionary(bg => bg.Id);
                return (id, criteria) =>
                    records.TryGetValue(id, out var bg) && criteria.GlucoseValue.HasValue
                    && Math.Abs(bg.Glucose - criteria.GlucoseValue.Value) <= criteria.GlucoseTolerance;
            }
            case RecordType.DeviceEvent:
            {
                var records = (await _context.DeviceEvents
                    .Where(d => ids.Contains(d.Id))
                    .ToListAsync(ct))
                    .ToDictionary(d => d.Id);
                return (id, criteria) =>
                    records.TryGetValue(id, out var d) && !string.IsNullOrEmpty(criteria.EventType)
                    && string.Equals(d.EventType, criteria.EventType, StringComparison.OrdinalIgnoreCase);
            }
            case RecordType.Note:
                return (_, _) => true; // time-window only matching
            case RecordType.BolusCalculation:
            {
                var records = (await _context.BolusCalculations
                    .Where(bc => ids.Contains(bc.Id))
                    .ToListAsync(ct))
                    .ToDictionary(bc => bc.Id);
                return (id, criteria) =>
                    records.TryGetValue(id, out var bc) && criteria.Carbs.HasValue
                    && Math.Abs((bc.CarbInput ?? 0) - criteria.Carbs.Value) <= criteria.CarbsTolerance;
            }
            case RecordType.StateSpan:
            {
                var records = (await _context.StateSpans
                    .Where(s => ids.Contains(s.Id))
                    .ToListAsync(ct))
                    .ToDictionary(s => s.Id);
                return (id, criteria) =>
                {
                    if (!records.TryGetValue(id, out var ss) || !criteria.Category.HasValue)
                        return false;
                    var categoryStr = criteria.Category.Value.ToString();
                    if (!string.Equals(ss.Category, categoryStr, StringComparison.OrdinalIgnoreCase))
                        return false;
                    if (!string.IsNullOrEmpty(criteria.State)
                        && !string.Equals(ss.State, criteria.State, StringComparison.OrdinalIgnoreCase))
                        return false;
                    return true;
                };
            }
            default:
                return (_, _) => false;
        }
    }

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

    private static bool CriteriaMatch(RecordType recordType, MatchCriteria a, MatchCriteria b)
    {
        return recordType switch
        {
            RecordType.TempBasal => a.Rate.HasValue && b.Rate.HasValue
                && Math.Abs(a.Rate.Value - b.Rate.Value) <= Math.Max(a.RateTolerance, b.RateTolerance),
            RecordType.SensorGlucose or RecordType.BGCheck => a.GlucoseValue.HasValue && b.GlucoseValue.HasValue
                && Math.Abs(a.GlucoseValue.Value - b.GlucoseValue.Value) <= Math.Max(a.GlucoseTolerance, b.GlucoseTolerance),
            RecordType.Bolus => a.Insulin.HasValue && b.Insulin.HasValue
                && Math.Abs(a.Insulin.Value - b.Insulin.Value) <= Math.Max(a.InsulinTolerance, b.InsulinTolerance),
            RecordType.CarbIntake or RecordType.BolusCalculation => a.Carbs.HasValue && b.Carbs.HasValue
                && Math.Abs(a.Carbs.Value - b.Carbs.Value) <= Math.Max(a.CarbsTolerance, b.CarbsTolerance),
            RecordType.DeviceEvent => string.Equals(a.EventType, b.EventType, StringComparison.OrdinalIgnoreCase),
            RecordType.Note => true,
            RecordType.StateSpan => a.Category == b.Category
                && (string.IsNullOrEmpty(a.State) || string.IsNullOrEmpty(b.State)
                    || string.Equals(a.State, b.State, StringComparison.OrdinalIgnoreCase)),
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
                    e.DataSource ?? "unknown",
                    BuildCriteria(e)),
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
                    b.DataSource ?? "unknown",
                    BuildCriteria(b)),
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
                    c.DataSource ?? "unknown",
                    BuildCriteria(c)),
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
                    bg.DataSource ?? "unknown",
                    BuildCriteria(bg)),
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
                    d.DataSource ?? "unknown",
                    BuildCriteria(d)),
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
                    n.DataSource ?? "unknown",
                    BuildNoteCriteria()),
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
                    bc.DataSource ?? "unknown",
                    BuildCriteria(bc)),
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
                    t.DataSource ?? "unknown",
                    BuildCriteria(t)),
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
                    s.Source ?? "unknown",
                    BuildCriteria(s)),
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
        var jobId = Guid.CreateVersion7();
        var cts = new CancellationTokenSource();

        var status = new DeduplicationJobStatus
        {
            JobId = jobId,
            State = DeduplicationJobState.Pending,
            StartedAt = DateTime.UtcNow
        };

        _runningJobs[jobId] = status;
        _jobCancellations[jobId] = cts;

        // Start the job in the background with its own scope
        _ = Task.Run(async () =>
        {
            // Create a new scope for the background work to get a fresh DbContext
            await using var scope = _scopeFactory.CreateAsyncScope();
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
        _runningJobs.TryGetValue(jobId, out var status);
        return Task.FromResult(status);
    }

    /// <inheritdoc />
    public Task<bool> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (_jobCancellations.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
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

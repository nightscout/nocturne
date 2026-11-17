using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using static Nocturne.Infrastructure.Data.Services.Deduplication.DuplicateMatching;

namespace Nocturne.Infrastructure.Data.Services.Deduplication;

/// <summary>
/// Links each newly written record into the duplicate group of a matching record from the same event,
/// or into a group of its own, in the transaction that flags the new duplicates
/// (<see cref="DuplicateGroupPrimaries.FlagInsertedLinksAsync"/>).
/// </summary>
public sealed class DuplicateLinker(INocturneDbContextFactory contexts, ILogger<DuplicateLinker> logger) : IDuplicateLinker
{
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
        // (the +/-MatchingWindow expansion covers the seam); DuplicateReconciler.ReconcileNewLinksAsync collapses
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
    /// Links a single bounded chunk of records to canonical groups in one matching-window query.
    /// Callers must keep <paramref name="records"/> within <see cref="DuplicateMatching.DedupChunkSize"/> and time
    /// contiguous; <see cref="DeduplicateBatchAsync"/> is the entry point that enforces both.
    /// </summary>
    private async Task<DeduplicationBatchResult> DeduplicateChunkAsync(
        RecordType recordType,
        IReadOnlyList<DeduplicationInput> records,
        CancellationToken ct)
    {
        if (records.Count == 0)
            return new DeduplicationBatchResult(0, 0, 0, 0);

        await using var db = await contexts.CreateAsync(ct);
        return await LinkedRecordTypes.UnderWriteLockAsync(db, async token =>
        {
            // The stream lock before the first glucose link is written: the order GlucoseStreamFlags states.
            if (recordType == RecordType.SensorGlucose)
                await GlucoseStreamFlags.LockAsync(db, token);
            return await LinkChunkAsync(db, recordType, records, token);
        }, ct);
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
        var unlinked = (await LinkedRecordTypes.UnlinkedAsync(db, recordType, minMills, maxMills, ct))
            .Where(u => !inputIdSet.Contains(u.RecordId))
            .ToDictionary(u => u.CanonicalId);
        allPotentialMatches.AddRange(unlinked.Values);

        // 3. One query: the criteria and soft-deleted status behind every candidate link, read by
        //    both the tight matcher below and the wide pass.
        var referencedIds = allPotentialMatches.Select(m => m.RecordId).ToHashSet();
        var info = await LinkedRecordTypes.LoadRecordInfoAsync(db, recordType, referencedIds, ct);

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
            groupSources = await LinkedRecordTypes.LoadGroupSourcesAsync(
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
                        logger, recordType, record.DataSource, m.DataSource, m.SourceTimestamp - record.Mills, wide: false);
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
                            logger, recordType, record.DataSource, priorSource, priorMills - record.Mills, wide: false);
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
                        logger, recordType, record.DataSource, closest.DataSource, closest.Mills - record.Mills, wide: true);
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
            await DuplicateGroupPrimaries.FlagInsertedLinksAsync(
                db, recordType, [.. newLinks.Where(l => !l.IsPrimary).Select(l => l.RecordId)], ct);
        }

        // 7. A wide join reaches up to ten minutes back, so it can land earlier than the group's
        //    primary — and unlike reconcile-merged groups, ingest-formed groups are never revisited
        //    by MergeDuplicateGroupsAsync. Re-derive the primary here, or the event time the group
        //    displays would depend on which connector synced first. A state span joined on the tight
        //    path is re-derived too, since a copy can supersede the primary (see PickSurvivor). Other
        //    tight joins keep their sticky primary, which is long-standing behaviour.
        if (repickCanonicals.Count > 0)
            await DuplicateGroupPrimaries.RepickAsync(db, recordType, repickCanonicals.ToArray(), ct);

        return new DeduplicationBatchResult(
            Processed: records.Count,
            GroupsCreated: groupsCreated,
            RecordsLinked: newLinks.Count - restoredLinks,
            DuplicateGroups: duplicateGroups);
    }
}

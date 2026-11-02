using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using static Nocturne.Infrastructure.Data.Services.Deduplication.DuplicateMatching;
using static Nocturne.Infrastructure.Data.Services.Deduplication.LinkedRecordTypes;

namespace Nocturne.Infrastructure.Data.Services.Deduplication;

/// <summary>
/// Keeps the current tenant's duplicate groups right after insert-time linking: merges the groups it
/// left apart, moves misplaced state span primaries, prunes groups that never gained a second record,
/// and audits the stored duplicate flags against the links.
/// </summary>
public sealed class DuplicateReconciler(INocturneDbContextFactory contexts, ILogger<DuplicateReconciler> logger)
    : IDuplicateReconciler
{
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
    /// How long after a member started a state span group with no open member is still checked by
    /// <see cref="RepairStateSpanPrimariesAsync"/>. Reads that matter ask what is in effect now, and
    /// bounding the check keeps it from rescanning the tenant's whole history under the write lock.
    /// </summary>
    internal static readonly TimeSpan StateSpanRepairHorizon = TimeSpan.FromDays(1);

    /// <inheritdoc />
    public async Task<int> RepairStateSpanPrimariesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateAsync(cancellationToken);
        return await UnderWriteLockAsync(db, async token =>
        {
            var misplaced = await MisplacedStateSpanPrimaries(db, DateTime.UtcNow - StateSpanRepairHorizon)
                .ToArrayAsync(token);

            return misplaced.Length == 0
                ? 0
                : (await DuplicateGroupPrimaries.RepickAsync(db, RecordType.StateSpan, misplaced, token)).Count;
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
        var startedAfterMills = startedAfter.ToMills();
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

    /// <inheritdoc />
    public Task<IReadOnlyList<DuplicateFlagDrift>> AuditDuplicateFlagsAsync(CancellationToken cancellationToken = default) =>
        AuditDuplicateFlagsAsync(DuplicateGroupPrimaries.AuditPageSize, cancellationToken);

    /// <inheritdoc cref="AuditDuplicateFlagsAsync(CancellationToken)"/>
    /// <remarks>
    /// Each page found disagreeing is repaired in a transaction of its own under the write lock
    /// (<see cref="DuplicateGroupPrimaries.RepairFlagsAsync"/> says why), so the lock is held for one
    /// page's repair at a time and a tenant with no drift takes it never.
    /// </remarks>
    internal async Task<IReadOnlyList<DuplicateFlagDrift>> AuditDuplicateFlagsAsync(int pageSize, CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        var drift = new List<DuplicateFlagDrift>();
        foreach (var recordType in DeduplicatedRecordTypes)
        {
            int flagged = 0, unflagged = 0, repaired = 0;
            await foreach (var page in DuplicateGroupPrimaries.DisagreeingPagesAsync(db, recordType, pageSize, ct))
            {
                flagged += page.Count(r => r.IsDuplicate);
                unflagged += page.Count(r => !r.IsDuplicate);
                var ids = page.Select(r => r.Id).ToArray();
                repaired += await UnderWriteLockAsync(
                    db, token => DuplicateGroupPrimaries.RepairFlagsAsync(db, recordType, ids, token), ct);
            }

            if (flagged + unflagged > 0)
                drift.Add(new DuplicateFlagDrift(recordType, flagged, unflagged, repaired));
        }

        return drift;
    }

    /// <summary>
    /// Collapses duplicate canonical groups for a record type within the current tenant.
    /// Two groups merge when their primary records fall within <see cref="MatchingWindowMillis"/>
    /// of each other and their <see cref="MatchCriteria"/> match; merging is transitive
    /// (union-find) and source-agnostic, mirroring insert-time <see cref="DuplicateLinker.DeduplicateBatchAsync"/>
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
        await using var db = await contexts.CreateAsync(ct);
        return await MergeDuplicateGroupsAsync(db, recordType, candidateCanonicalIds, ct);
    }

    /// <summary>
    /// Merges every group of <paramref name="recordType"/> the rules join, for a full run. Unlike a
    /// reconcile batch it waits for the tenant's merge lock: a skipped pass would leave the splits
    /// unhealed until the run is made again.
    /// </summary>
    internal async Task<int> MergeAllAsync(RecordType recordType, CancellationToken ct)
    {
        await using var db = await contexts.CreateAsync(ct);
        return await db.ExecuteInTransactionAsync(async token =>
        {
            await LockTenantMergesAsync(db, token);
            return await MergeDuplicateGroupsAsync(db, recordType, null, token);
        }, ct: ct);
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
                            logger,
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

                    logger.Log(
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
            await DuplicateGroupPrimaries.LockGroupsAsync(db, recordType, [.. mergedGroups.SelectMany(c => c)], token);

            var merged = 0;
            var changes = new List<PrimaryChange>();
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

                foreach (var r in rows)
                {
                    r.CanonicalId = survivor.CanonicalId;
                    var primary = ReferenceEquals(r, survivor);
                    if (r.IsPrimary != primary)
                        changes.Add(new PrimaryChange(r.RecordId, primary));
                }

                // Collapsing N canonicals into 1 removes (N-1) groups.
                merged += canonicals.Count - 1;
            }

            if (merged > 0)
            {
                await db.SaveChangesAsync(token);
                db.ChangeTracker.Clear();
                await DuplicateGroupPrimaries.SetPrimariesAsync(db, recordType, changes, token);
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
        await using var db = await contexts.CreateAsync(cancellationToken);
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
            if (ParseRecordType(group.Key, logger) is not { } type)
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
                logger.LogError(ex,
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
    /// <remarks>
    /// The prune walks the tenant's primary links on a keyset cursor of its own, one page per
    /// transaction, so a tick reads only the links aged in since the last one. A link that becomes a
    /// singleton after the cursor passed it, when the rest of its group is deleted, is not read again
    /// until the cursor restarts from the first link, which it does once per
    /// <see cref="SingletonLinkRetention"/>. Such a link keeps its row for up to a day longer, and
    /// rereading every link each tick to catch it sooner would cost far more than the row.
    /// </remarks>
    public async Task<int> PruneSingletonLinksAsync(
        int batchSize,
        int maxBatches,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateAsync(cancellationToken);

        // A link the reconcile cursor has not passed may still be a candidate for a merge.
        if (await GetCursorAsync(db, cancellationToken) is not { } cursor)
            return 0;

        var retained = DateTime.UtcNow - SingletonLinkRetention;
        var createdBefore = cursor.CreatedAt < retained ? cursor.CreatedAt : retained;

        var pruned = 0;
        for (var batchNo = 0; batchNo < maxBatches; batchNo++)
        {
            var (deleted, examined) = await UnderWriteLockAsync(
                db, token => PruneNextPageAsync(db, batchSize, createdBefore, token), cancellationToken);
            pruned += deleted;

            if (examined < batchSize)
                break;
        }

        return pruned;
    }

    /// <summary>
    /// Deletes the singletons among the page of primary links after the tenant's prune cursor and
    /// advances the cursor to the page's last link.
    /// </summary>
    /// <returns>The links deleted and the links on the page.</returns>
    private static async Task<(int Deleted, int Examined)> PruneNextPageAsync(
        NocturneDbContext db, int batchSize, DateTime createdBefore, CancellationToken ct)
    {
        var state = await db.DedupReconcileState.AsNoTracking().SingleAsync(s => s.TenantId == db.TenantId, ct);

        var now = DateTime.UtcNow;
        var restart = state.PruneCursorResetAt is not { } lastReset || lastReset <= now - SingletonLinkRetention;
        ReconcileCursor? after = !restart && state.PruneCursorCreatedAt is { } at
            ? new ReconcileCursor(at, state.PruneCursorId ?? Guid.Empty)
            : null;

        var page = await LinksAfter(db, after, createdBefore)
            .Where(lr => lr.IsPrimary)
            .Take(batchSize)
            .Select(lr => new ReconcileCursor(lr.SysCreatedAt, lr.Id))
            .ToListAsync(ct);

        var deleted = page.Count > 0
            ? await SingletonsAmong(db, page.Select(p => p.Id)).ExecuteDeleteAsync(ct)
            : 0;

        if (page.Count > 0 || restart)
        {
            DateTime? cursorAt = page.Count > 0 ? page[^1].CreatedAt : null;
            Guid? cursorId = page.Count > 0 ? page[^1].Id : null;
            var resetAt = restart ? now : state.PruneCursorResetAt;
            await db.DedupReconcileState
                .Where(s => s.TenantId == db.TenantId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.PruneCursorCreatedAt, cursorAt)
                    .SetProperty(e => e.PruneCursorId, cursorId)
                    .SetProperty(e => e.PruneCursorResetAt, resetAt), ct);
        }

        return (deleted, page.Count);
    }

    /// <summary>
    /// The primary links among <paramref name="ids"/> that no other link shares a group with. A lone
    /// non-primary link is kept: it hides its record from every read, and with the link gone the
    /// record would show. The ids bind as one array, as <see cref="PrimariesOf"/> explains.
    /// </summary>
    internal static IQueryable<LinkedRecordEntity> SingletonsAmong(NocturneDbContext db, IEnumerable<Guid> ids)
    {
        var page = ids.ToArray();
        return db.LinkedRecords
            .Where(lr => page.Contains(lr.Id) && lr.IsPrimary
                && !db.LinkedRecords.Any(other => other.CanonicalId == lr.CanonicalId && other.Id != lr.Id));
    }

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
    /// The current tenant's <c>dedup_reconcile_state</c> row, tracked, inserted first when it has none,
    /// its reconcile cursor before every link, which reconciles from the first link as no row does. Two
    /// writers insert the row, this cursor and a full run's start (<see cref="DeduplicationAdmin"/>): the
    /// insert does nothing on conflict, waiting for a concurrent insert to commit, so neither fails on the
    /// other's. The table has no recency column, so no history clamp reads it. Other providers serialise
    /// their writes and add the row through the context.
    /// </summary>
    internal static async Task<DedupReconcileStateEntity> TrackedStateAsync(NocturneDbContext db, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO dedup_reconcile_state (tenant_id, last_reconciled_link_created_at)
                VALUES ({db.TenantId}, {DateTime.UnixEpoch})
                ON CONFLICT (tenant_id) DO NOTHING
                """, ct);
        }

        var state = await db.DedupReconcileState
            .Where(s => s.TenantId == db.TenantId)
            .FirstOrDefaultAsync(ct);
        if (state is null)
        {
            state = new DedupReconcileStateEntity { TenantId = db.TenantId, LastReconciledLinkCreatedAt = DateTime.UnixEpoch };
            db.DedupReconcileState.Add(state);
        }

        return state;
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

        var state = await TrackedStateAsync(db, ct);
        state.LastReconciledLinkCreatedAt = createdAt;
        state.LastReconciledLinkId = cursor.Id;

        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Keyset position in the tenant's links: the <c>sys_created_at</c> and id of the last link
/// <see cref="DuplicateReconciler.ReconcileNewLinksAsync"/> reconciled or
/// <see cref="DuplicateReconciler.PruneSingletonLinksAsync"/> examined.
/// </summary>
internal readonly record struct ReconcileCursor(DateTime CreatedAt, Guid Id);

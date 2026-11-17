using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Services.Deduplication;

/// <summary>
/// When two records describe one event: the matching windows, the per-type value comparison, the
/// source guards and the rule that picks a group's primary. Shared by insert-time linking
/// (<see cref="DuplicateLinker"/>) and the merge (<see cref="DuplicateReconciler"/>), which must agree.
/// </summary>
internal static class DuplicateMatching
{

    internal static readonly TimeSpan MatchingWindow = TimeSpan.FromSeconds(30);
    internal static readonly long MatchingWindowMillis = (long)MatchingWindow.TotalMilliseconds;

    /// <summary>
    /// Second-chance window used only when <see cref="MatchingWindow"/> found no match. Two
    /// connectors reporting the same pump can disagree by minutes when one carries the raw pump
    /// clock and the other a corrected one, and that offset drifts without bound. The wide path
    /// buys the extra reach by dropping the tight path's value tolerances for exact equality and
    /// requiring a single candidate group that holds no record of the incoming record's own
    /// source, so it can only ever collapse a cross-source pair.
    /// </summary>
    internal static readonly TimeSpan WideMatchingWindow = TimeSpan.FromMinutes(10);
    internal static readonly long WideMatchingWindowMillis = (long)WideMatchingWindow.TotalMilliseconds;

    /// <summary>
    /// Matched offset above which a cross-source match is logged at Warning rather than Debug —
    /// 60% of <see cref="WideMatchingWindow"/>, so drift is visible while matching still works.
    /// </summary>
    internal static readonly long CrossSourceOffsetWarningMillis =
        (long)(WideMatchingWindow.TotalMilliseconds * 0.6);

    /// <summary>
    /// Value equality epsilon for the wide path, which admits no tolerance: a tolerance-based
    /// match minutes away from the record risks hiding a distinct real dose.
    /// </summary>
    internal const double ExactValueEpsilon = 1e-6;

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
    internal static readonly TimeSpan ExactDurationTolerance = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Record types eligible for <see cref="WideMatchingWindow"/>. Continuous streams
    /// (<see cref="RecordType.SensorGlucose"/>), free-text records (<see cref="RecordType.Note"/>)
    /// and interval records (<see cref="RecordType.StateSpan"/>) are excluded: repeating the same
    /// value inside ten minutes is normal for them, so a wide match would merge distinct events.
    /// </summary>
    internal static readonly HashSet<RecordType> WideMatchableTypes =
    [
        RecordType.Bolus,
        RecordType.CarbIntake,
        RecordType.DeviceEvent,
        RecordType.BGCheck,
        RecordType.TempBasal,
        RecordType.BolusCalculation
    ];

    /// <summary>
    /// Maximum number of records processed per <see cref="DuplicateLinker.DeduplicateBatchAsync"/> matching-window
    /// query. A connector backfill can hand the dedup pass thousands of records spanning months;
    /// sorting by event time and slicing into chunks of this size keeps each window query's time
    /// span narrow so it stays an index range scan over a few hundred rows rather than loading
    /// millions of <c>linked_records</c> for a high-volume tenant.
    /// </summary>
    internal const int DedupChunkSize = 500;

    /// <summary>
    /// A record's match criteria paired with its soft-deleted status, and for a state span its
    /// <see cref="SpanEnd"/>, keyed by record id when returned from <see cref="LinkedRecordTypes.LoadRecordInfoAsync"/>.
    /// </summary>
    internal sealed record RecordInfo(MatchCriteria Criteria, bool IsDeleted, SpanEnd Span = default);

    /// <summary>
    /// How a state span ended, as <see cref="PickSurvivor(IEnumerable{LinkedRecordEntity}, Func{Guid, bool}, Func{Guid, SpanEnd})"/>
    /// reads it. Every other record type reads as the default: open, superseded by nothing.
    /// </summary>
    internal readonly record struct SpanEnd(Guid? SupersededById, DateTime? EndTimestamp);

    /// <summary>
    /// Returns the single canonical id present in <paramref name="canonicalIds"/>, or null when
    /// the sequence is empty or spans more than one group. Two candidate groups mean the record
    /// cannot be attributed to one event, so the wide path refuses rather than guessing.
    /// </summary>
    internal static Guid? SingleCandidateCanonical(IEnumerable<Guid> canonicalIds)
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
    /// Records a match between two different data sources. Same-source and unknown-source matches
    /// are not logged: the offset being tracked is the clock drift between connectors. Matches the
    /// tight window already handled log at Debug; a <paramref name="wide"/> match logs at
    /// Information, because a drift large enough to need the wide window is the condition being
    /// watched and would be invisible at Debug in production. An offset past
    /// <see cref="CrossSourceOffsetWarningMillis"/> logs at Warning because matching stops working
    /// entirely once the drift passes <see cref="WideMatchingWindow"/>.
    /// </summary>
    internal static void LogCrossSourceMatch(
        ILogger logger,
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

        logger.Log(
            level,
            "Cross-source {RecordType} match at {OffsetSeconds}s between {IncomingSource} and {MatchedSource}",
            recordType, Math.Abs(offsetMillis) / 1000.0, incomingSource, matchedSource);
    }

    /// <summary>
    /// True when a data source can establish cross-source provenance: it is present and is not
    /// <see cref="DeduplicationInput.UnknownDataSource"/>, which names no connector and so cannot
    /// distinguish a second connector's copy of a dose from a manually entered second dose.
    /// </summary>
    internal static bool CanEstablishCrossSource([NotNullWhen(true)] string? dataSource) =>
        !string.IsNullOrEmpty(dataSource)
        && !string.Equals(dataSource, DeduplicationInput.UnknownDataSource, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a canonical group's data sources can establish cross-source provenance: the group
    /// has at least one source and none of them is the unknown sentinel.
    /// </summary>
    internal static bool CanEstablishCrossSource(HashSet<string> sources) =>
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
    internal static bool EmitsDuplicateEvents(RecordType recordType, string source) =>
        recordType == RecordType.CarbIntake
        && string.Equals(source, DataSources.TidepoolConnector, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <see cref="RefusesTightJoin"/> applies to this record type. Sensor glucose and state
    /// spans are exempt: neither is a discrete dose, and one uploader routinely reports the same
    /// reading or the same span twice seconds apart (a repeated device status re-opens the same pump
    /// mode), which must still collapse to one.
    /// </summary>
    internal static bool TracksTightSources(RecordType recordType) =>
        recordType is not (RecordType.SensorGlucose or RecordType.StateSpan);

    /// <summary>
    /// Orders <paramref name="items"/> by event time and slices them into chunks of at most
    /// <see cref="DedupChunkSize"/>, also starting a new chunk wherever two consecutive items sit
    /// more than <paramref name="maxGapMillis"/> apart, or an item sits more than
    /// <paramref name="maxSpanMillis"/> after its chunk's first.
    /// </summary>
    internal static IEnumerable<List<T>> TimeOrderedChunks<T>(
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

    internal static int LowerBoundTimestamp(long[] sortedTimestamps, long value)
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

    internal static int UpperBoundTimestamp(long[] sortedTimestamps, long value)
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
    internal static bool DurationsAgree(TimeSpan? a, TimeSpan? b) =>
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
}

using System.Collections.Concurrent;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Per rule, the context the engine last evaluated it with, so a pass still running on an older
/// reading does not drive a rule a newer context has already decided. A stale evaluation is
/// acceptable and a skipped one is not, so only a rule that a newer context has decided is skipped:
/// a rule a newer pass never reached, never selected, or failed on is still evaluated.
/// </summary>
/// <remarks>
/// <see cref="AlertOrchestrator"/> reads and writes it inside the rule's
/// <see cref="AlertRuleEvaluationGate"/> section, which every pass over the rule takes, so a check and
/// the evaluation it allows are never split by another pass's decision.
/// <para>
/// In memory and per process: another process's decisions are not seen, as before. One small entry
/// per rule evaluated since the process started, so its size is bounded by the rules evaluated; the
/// entry of a deleted rule stays until the process restarts, which costs a few dozen bytes per rule.
/// </para>
/// </remarks>
internal sealed class AlertRuleDecisions(TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<Guid, ContextStamp> _decided = new();
    private readonly Lock _reads = new();

    /// <summary>Orders reads of the canonical stream against the decisions that follow them.</summary>
    private long _sequence;

    private DateTime _latestNow = DateTime.MinValue;

    /// <summary>
    /// Begins a read of the canonical stream: its place in the read order, which its contexts carry,
    /// and the instant it reads as of. Handed out together, and the instant never earlier than one
    /// handed out before, so a later read never bounds its selection earlier than an earlier read,
    /// even across a wall-clock step back: a later read sees every reading an earlier one could.
    /// </summary>
    public ReadStart BeginRead()
    {
        lock (_reads)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            if (now < _latestNow)
                now = _latestNow;
            _latestNow = now;
            return new ReadStart(++_sequence, now);
        }
    }

    /// <summary>The stamp of <paramref name="context"/> read at <paramref name="readAt"/>, or null for a context with no reading time.</summary>
    public static ContextStamp? StampOf(SensorContext context, long readAt) =>
        context.LatestTimestamp is { } at
            ? new ContextStamp(at, context.LatestValue, context.TrendRate, readAt)
            : null;

    /// <summary>
    /// Whether a context newer than <paramref name="stamp"/> has decided <paramref name="ruleId"/>: a
    /// different reading from a read of the canonical stream that began after this one. A later read
    /// sees what this one saw and what was written since, so its reading is the canonical latest
    /// even when it is older: the newer one was deleted or edited back. A correction at the same
    /// timestamp is ordered by its read too, and the same reading never supersedes itself.
    /// </summary>
    public bool DecidedByNewer(Guid ruleId, ContextStamp stamp) =>
        _decided.TryGetValue(ruleId, out var decided)
        && !decided.SameReading(stamp)
        && decided.ReadAt > stamp.ReadAt;

    /// <summary>
    /// Records that the engine evaluated <paramref name="ruleId"/> with <paramref name="stamp"/>,
    /// unless a later read already decided it: a pass on the same reading from an earlier read may
    /// still evaluate, and must not move the record back.
    /// </summary>
    public void Decided(Guid ruleId, ContextStamp stamp) =>
        _decided.AddOrUpdate(ruleId, stamp, (_, current) => stamp.ReadAt >= current.ReadAt ? stamp : current);
}

/// <summary>A read's place in the read order and the instant it reads the canonical stream as of.</summary>
internal readonly record struct ReadStart(long Sequence, DateTime Now);

/// <summary>The reading a context measured and when the pass that built it began reading.</summary>
internal readonly record struct ContextStamp(DateTime Timestamp, decimal? Value, decimal? TrendRate, long ReadAt)
{
    public bool SameReading(ContextStamp other) =>
        Timestamp == other.Timestamp && Value == other.Value && TrendRate == other.TrendRate;
}

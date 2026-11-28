using Nocturne.API.Services.Alerts;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.V4;

/// <summary>
/// The rule every pump-mode fold applies between a transition and an open
/// <see cref="StateSpanCategory.PumpMode"/> span. A record's lead is how far its time is ahead of when the
/// server stored it. A span and a transition whose leads agree within the clock tolerance read one clock,
/// however fast. Otherwise the one whose lead is larger by more than the tolerance is ahead of the other,
/// when it was also dated beyond the clock bound as it arrived (<see cref="AlertReadings.AheadOfArrival"/>),
/// which is what a pump with a wrong clock reports. <see cref="EndAt"/> holds the rule.
/// </summary>
/// <remarks>
/// Both sides are judged at arrival, so a fold run again after the clock has passed a transition gives the
/// same result. A span ended at its own start stays in the history, no read of what was active sees it,
/// it blocks no span the transition opens, and the store lets it end nothing
/// (<c>StateSpanSuccession.Follows</c>).
/// </remarks>
internal static class PumpModeSpanClock
{
    /// <summary>A transition at <see cref="At"/> of a record the server stored at <see cref="ArrivedAt"/>.</summary>
    /// <remarks>
    /// Both are kept as the store keeps a record time (<see cref="RecordTime.AtMillisecond"/>): either can
    /// become a span's start or end, and a fold run again compares it with the end the store kept.
    /// </remarks>
    public readonly record struct Transition(DateTime At, DateTime? ArrivedAt)
    {
        public DateTime At { get; } = RecordTime.AtMillisecond(At);

        public DateTime? ArrivedAt { get; } = ArrivedAt is { } arrived ? RecordTime.AtMillisecond(arrived) : null;

        /// <summary>Whether <paramref name="span"/> ends where this transition ends a span that starts no later than it.</summary>
        public bool Ended(StateSpan span) =>
            span.EndTimestamp == At || (IsAhead(At, ArrivedAt) && span.EndTimestamp == ArrivedAt);
    }

    /// <summary>
    /// The end <paramref name="transition"/> gives <paramref name="open"/> when the span starts after it:
    /// its own start when the span is ahead of the transition, else null, since the span belongs to a later
    /// transition.
    /// </summary>
    public static DateTime? OvertakenEnd(StateSpan open, Transition transition) =>
        open.StartTimestamp > transition.At && Judge(open, transition).SpanAhead ? open.StartTimestamp : null;

    /// <summary>
    /// The end a transition out of <paramref name="open"/>'s mode gives it. A span that starts after the
    /// transition takes its <see cref="OvertakenEnd"/>. Otherwise:
    /// <list type="bullet">
    /// <item><description>One clock, or the span ahead of the transition: the transition.</description></item>
    /// <item><description>
    /// The transition ahead of the span: when the transition arrived, and no earlier than the span's start.
    /// The transition's own time is not when the span ended, and ending there would carry the span past the
    /// present. A transition that arrived before the span did came before it, so it ends nothing.
    /// </description></item>
    /// </list>
    /// </summary>
    public static DateTime? EndAt(StateSpan open, Transition transition)
    {
        if (open.StartTimestamp > transition.At)
            return OvertakenEnd(open, transition);
        if (!Judge(open, transition).TransitionAhead)
            return transition.At;

        var arrived = transition.ArrivedAt!.Value;
        if (Stamped(open.CreatedAt) is { } spanArrived && arrived < RecordTime.AtMillisecond(spanArrived))
            return null;
        return arrived > open.StartTimestamp ? arrived : open.StartTimestamp;
    }

    /// <summary>Which of a span and a transition is ahead of the other; neither when they read one clock.</summary>
    private readonly record struct Pair(bool SpanAhead, bool TransitionAhead);

    /// <remarks>
    /// A transition that arrived late and is dated before the span arrived is an older one, as an import or a
    /// catching-up uploader sends. It puts no span ahead of it, so a span that starts after it is left to its
    /// own, however fast that span's clock. A source that lags behind its events, as a polling connector does,
    /// sends each transition late but dated after the spans before it arrived, so it still overtakes them.
    /// </remarks>
    private static Pair Judge(StateSpan span, Transition transition)
    {
        var spanLead = Lead(span.StartTimestamp, span.CreatedAt);
        var transitionLead = Lead(transition.At, transition.ArrivedAt);
        return new(
            !IsOlder(transition, span.CreatedAt) && IsAheadOf(span.StartTimestamp, span.CreatedAt, transitionLead),
            IsAheadOf(transition.At, transition.ArrivedAt, spanLead));
    }

    /// <summary>
    /// Whether a record dated <paramref name="at"/> and stored at <paramref name="storedAt"/> was ahead as it
    /// arrived and, against a record whose lead is <paramref name="otherLead"/>, still is once that lead is
    /// taken off its time. Against a record with no known lead, it is ahead as it arrived. One the store has not
    /// stamped is not ahead.
    /// </summary>
    private static bool IsAheadOf(DateTime at, DateTime? storedAt, TimeSpan? otherLead) =>
        IsAhead(at, storedAt) && (otherLead is not { } other || AlertReadings.AheadOfArrival(at - other, storedAt!.Value));

    private static bool IsAhead(DateTime at, DateTime? storedAt) =>
        Stamped(storedAt) is { } stored && AlertReadings.AheadOfArrival(at, stored);

    private static bool IsOlder(Transition transition, DateTime? spanStoredAt) =>
        Stamped(transition.ArrivedAt) is { } arrived && AlertReadings.AheadOfArrival(arrived, transition.At)
        && Stamped(spanStoredAt) is { } spanArrived && AlertReadings.AheadOfArrival(spanArrived, transition.At);

    private static TimeSpan? Lead(DateTime at, DateTime? storedAt) => Stamped(storedAt) is { } stored ? at - stored : null;

    /// <summary>
    /// The open spans of <paramref name="state"/> among <paramref name="spans"/>, with the spans released
    /// by each one <paramref name="ends"/> ends at its own start (<see cref="Release"/>).
    /// </summary>
    /// <param name="spans">The pump-mode spans the fold holds.</param>
    /// <param name="state">The state read.</param>
    /// <param name="ends">The end the transition gives a span.</param>
    /// <param name="endOf">A span's end as the fold sees it, when that is not its <see cref="StateSpan.EndTimestamp"/>.</param>
    public static List<StateSpan> OpenAfterRelease(
        List<StateSpan> spans, string state, Func<StateSpan, DateTime?> ends, Func<StateSpan, DateTime?>? endOf = null)
    {
        endOf ??= s => s.EndTimestamp;
        var open = spans.Where(s => s.State == state && endOf(s) == null).ToList();
        foreach (var start in open.Where(s => ends(s) == s.StartTimestamp).Select(s => s.StartTimestamp).Distinct().ToList())
            open.AddRange(Release(spans, state, start));

        return open;
    }

    /// <summary>
    /// Reopens in place, and returns, each span of <paramref name="state"/> the store ended at
    /// <paramref name="start"/>, the start of a span about to end there: that one then ends nothing, and the
    /// store releases what it ended (<c>StateSpanSuccession.EndsNothing</c>).
    /// </summary>
    /// <remarks>
    /// The reopened spans are not marked changed. The store's write of the span ended at its own start is
    /// what reopens them, and a write of one left open would run the store's succession on it again. A read
    /// span names its superseder by row id, which a span with an original id does not show, so a span the
    /// store ended is told by its pointer and its end.
    /// </remarks>
    public static List<StateSpan> Release(List<StateSpan> spans, string state, DateTime start)
    {
        var released = spans
            .Where(s => s.State == state && s.SupersededById is not null && s.EndTimestamp == start)
            .ToList();
        foreach (var span in released)
        {
            span.EndTimestamp = null;
            span.SupersededById = null;
        }

        return released;
    }

    private static DateTime? Stamped(DateTime? storedAt) => storedAt is { } stored && stored != default ? stored : null;
}

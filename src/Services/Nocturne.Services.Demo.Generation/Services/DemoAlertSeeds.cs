using Nocturne.Core.Models.Alerts;

namespace Nocturne.Services.Demo.Services;

/// <summary>An alert rule to seed. ConditionParams is the stored JSONB payload.</summary>
public sealed record AlertRuleSeed(
    string Name,
    AlertConditionType ConditionType,
    string ConditionParamsJson,
    AlertRuleSeverity Severity,
    string Description,
    double? EpisodeBelow = null,
    double? EpisodeAbove = null);

/// <summary>
/// Default alert rules for seeded tenants, plus an episode tracker that derives
/// realistic historical alarm firings from the generated glucose stream itself
/// — so the alert history lines up with the excursions visible on the chart.
/// </summary>
public static class DemoAlertSeeds
{
    /// <summary>
    /// A standard T1D alert set. Rules with <see cref="AlertRuleSeed.EpisodeBelow"/>
    /// or <see cref="AlertRuleSeed.EpisodeAbove"/> also get historical
    /// excursions from the glucose stream.
    /// </summary>
    public static IReadOnlyList<AlertRuleSeed> Defaults { get; } =
    [
        new("Urgent Low", AlertConditionType.Threshold,
            """{"direction":"below","value":55}""",
            AlertRuleSeverity.Critical, "Glucose below 55 mg/dL", EpisodeBelow: 55),
        new("Low", AlertConditionType.Threshold,
            """{"direction":"below","value":70}""",
            AlertRuleSeverity.Warning, "Glucose below 70 mg/dL", EpisodeBelow: 70),
        new("High", AlertConditionType.Threshold,
            """{"direction":"above","value":250}""",
            AlertRuleSeverity.Warning, "Glucose above 250 mg/dL", EpisodeAbove: 250),
        new("Signal Loss", AlertConditionType.SignalLoss,
            """{"timeout_minutes":30}""",
            AlertRuleSeverity.Warning, "No CGM readings for 30 minutes"),
    ];

    /// <summary>A contiguous out-of-range episode for one seeded rule.</summary>
    public sealed record GlucoseEpisode(string RuleName, DateTime StartUtc, DateTime EndUtc);

    /// <summary>
    /// Streaming threshold-crossing detector. Feed glucose readings in
    /// chronological order; completed episodes accumulate in
    /// <see cref="Episodes"/> (call <see cref="Flush"/> after the last reading
    /// to close any still-open episode at the stream end). Episodes shorter
    /// than 15 minutes are dropped — a single stray reading is not an alarm.
    /// </summary>
    public sealed class GlucoseEpisodeTracker
    {
        private static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(15);

        private sealed class RuleState
        {
            public required AlertRuleSeed Rule;
            public DateTime? OpenedAt;
        }

        private readonly List<RuleState> _states;
        private readonly List<GlucoseEpisode> _episodes = [];
        private DateTime _lastSeen;

        public GlucoseEpisodeTracker(IEnumerable<AlertRuleSeed> rules)
        {
            _states = rules
                .Where(r => r.EpisodeBelow.HasValue || r.EpisodeAbove.HasValue)
                .Select(r => new RuleState { Rule = r })
                .ToList();
        }

        public IReadOnlyList<GlucoseEpisode> Episodes => _episodes;

        public void Observe(DateTime utc, double? sgv)
        {
            if (sgv is not { } value)
                return;
            _lastSeen = utc;

            foreach (var state in _states)
            {
                var outOfRange =
                    (state.Rule.EpisodeBelow is { } below && value < below)
                    || (state.Rule.EpisodeAbove is { } above && value > above);

                if (outOfRange)
                {
                    state.OpenedAt ??= utc;
                }
                else if (state.OpenedAt is { } openedAt)
                {
                    Close(state, openedAt, utc);
                }
            }
        }

        /// <summary>Close episodes still open at the end of the stream.</summary>
        public void Flush()
        {
            foreach (var state in _states)
            {
                if (state.OpenedAt is { } openedAt)
                    Close(state, openedAt, _lastSeen);
            }
        }

        private void Close(RuleState state, DateTime openedAt, DateTime endedAt)
        {
            state.OpenedAt = null;
            if (endedAt - openedAt >= MinDuration)
                _episodes.Add(new GlucoseEpisode(state.Rule.Name, openedAt, endedAt));
        }
    }
}

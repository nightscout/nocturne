using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Reports;

/// <inheritdoc cref="IViewerFlagRules"/>
/// <remarks>
/// Team and personal rules, and the fact filters of the viewer's query, are judged by one
/// <see cref="FlagFactEvaluator.FlagJudging"/>, so a rule and a filter on one read share it, its keep and its audit mark,
/// and a person still being checked for either is counted once. A filter matches where a rule would give a reason
/// (<see cref="FlagJudgement.Matches"/>): never on no data, and never for a person it cannot be checked for. A team's rule is judged only for the people the team
/// follows, so nothing is read for it of anyone else. A rule whose fact no longer checks (a dataset or field gone since
/// it was saved) is left out. A person is judged by <see cref="FlagFactEvaluator"/>, which reads nothing on the caller's
/// flow: a read that fails does so in the background (<see cref="FlagFactReads"/>), and leaves only that person with the
/// fixed set.
/// </remarks>
internal sealed class ViewerFlagRules(
    IFlagRuleRepository flagRules,
    ITeamRepository teams,
    FlagFactEvaluator evaluator,
    TimeProvider time) : IViewerFlagRules
{
    public async Task<IViewerFlagRuleRun?> BeginAsync(
        Guid subjectId,
        OverviewRuleSet rules,
        IReadOnlyList<PeopleFilter> facts,
        IReadOnlyList<GlucoseReadTenant> people,
        bool credentialLimitTo24Hours,
        CancellationToken ct)
    {
        var held = new List<HeldRule>();
        if (rules != OverviewRuleSet.Fixed)
        {
            var staffed = (await teams.GetTeamsAndTheirPeopleAsync(subjectId, time.GetUtcNow().UtcDateTime, ct))
                .Where(team => team.CallerIsStaff && people.Any(person => team.People.Contains(person.Tenant.Id)))
                .ToList();
            if (staffed.Count > 0)
            {
                var ofTeams = await flagRules.GetOfTeamsAsync(subjectId, [.. staffed.Select(t => t.Id)], ct);
                foreach (var team in staffed)
                    foreach (var rule in ofTeams.GetValueOrDefault(team.Id) ?? [])
                        Hold(held, rule, new TenantOverviewRuleTeam(team.Id, team.Name), team.People);
            }
            if (rules == OverviewRuleSet.All)
                foreach (var rule in await flagRules.GetOfSubjectAsync(subjectId, ct))
                    Hold(held, rule, team: null, people: null);
        }

        if (held.Count == 0 && facts.Count == 0)
            return null;

        var filters = facts.Select(ConditionOf).ToList();
        var applied = held
            .Where(h => h.Team is not null)
            .Select(h => h.Team!)
            .DistinctBy(t => t.Id)
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Id)
            .ToList();
        var conditions = held.Select(h => h.Condition).Concat(filters.OfType<FlagFactCondition>()).ToList();
        return new Run(held, filters, applied, evaluator.Begin(conditions, credentialLimitTo24Hours));
    }

    /// <summary>A fact filter's condition; null when its fact no longer checks, and then it matches no one.</summary>
    private FlagFactCondition? ConditionOf(PeopleFilter filter) =>
        filter is { Dataset: { } dataset, Field: { } field, Comparison: { } comparison, Value: { } value }
            ? evaluator.CheckRule(dataset, field, filter.Days, comparison, value).Condition
            : null;

    private void Hold(List<HeldRule> held, FlagRule rule, TenantOverviewRuleTeam? team, IReadOnlySet<Guid>? people)
    {
        if (evaluator.CheckRule(rule.Dataset, rule.Field, rule.Days, rule.Comparison, rule.Value).Condition is { } condition)
            held.Add(new HeldRule(rule, condition, team, people));
    }

    /// <summary>The reason a rule gives for a judgement: a match, or too little data that matched; else none.</summary>
    /// <param name="team">The team that holds the rule; null for the viewer's own.</param>
    internal static TenantOverviewAttention? ReasonOf(
        FlagRule rule, TenantOverviewRuleTeam? team, FlagFactCondition condition, FlagJudgement judgement)
    {
        var tooLittle = judgement.Outcome == FlagOutcome.TooLittleData;
        if (judgement.Value is not { } value || !(judgement.Outcome == FlagOutcome.Matched || (tooLittle && judgement.Matches)))
            return null;

        var range = condition.Fact.Range;
        return new TenantOverviewAttention(TenantOverviewAttentionKind.FlagRule, rule.Level)
        {
            Rule = new TenantOverviewRuleReason(
                rule.Id,
                team is null ? FlagRuleSource.Yours : FlagRuleSource.Team,
                team,
                rule.Dataset,
                rule.Field,
                condition.Field.Label,
                condition.Field.Unit,
                range == ReportRangeFamily.None ? null : ReportWire.Name(range),
                value,
                rule.Comparison,
                rule.Value,
                rule.Days,
                judgement.Window is { } window ? DateTimeOffset.FromUnixTimeMilliseconds(window.From).UtcDateTime : null,
                judgement.Window is { } until ? DateTimeOffset.FromUnixTimeMilliseconds(until.To).UtcDateTime : null,
                judgement.Bound is { } bound ? new TenantOverviewRuleBound(bound.Low, bound.High, bound.VariesOverDay) : null,
                tooLittle ? judgement.Coverage : null),
        };
    }

    /// <summary>
    /// The reasons of <paramref name="reasons"/> to show, in order: every team's, and a personal one only where no
    /// team's has the same fact, days, comparison and value, so a rule a viewer shares with a team shows once, as the
    /// team's. Two teams' reasons are both kept.
    /// </summary>
    internal static IReadOnlyList<TenantOverviewAttention> ShownOnce(IReadOnlyList<TenantOverviewAttention> reasons)
    {
        var ofTeams = reasons
            .Select(r => r.Rule!)
            .Where(r => r.Source == FlagRuleSource.Team)
            .Select(Bound)
            .ToHashSet();
        return [.. reasons.Where(r => r.Rule!.Source == FlagRuleSource.Team || !ofTeams.Contains(Bound(r.Rule)))];
    }

    private static (string Dataset, string Field, int? Days, FlagRuleComparison Comparison, double Value) Bound(
        TenantOverviewRuleReason reason) =>
        (reason.Dataset, reason.Field, reason.Days, reason.Comparison, reason.RuleValue);

    /// <param name="People">The sites the rule applies to; null for the viewer's own rule, which applies to everyone.</param>
    private sealed record HeldRule(FlagRule Rule, FlagFactCondition Condition, TenantOverviewRuleTeam? Team, IReadOnlySet<Guid>? People);

    /// <param name="filters">The condition of each fact filter, in order; null for one whose fact no longer checks.</param>
    private sealed class Run(
        IReadOnlyList<HeldRule> held,
        IReadOnlyList<FlagFactCondition?> filters,
        IReadOnlyList<TenantOverviewRuleTeam> teams,
        FlagFactEvaluator.FlagJudging judging) : IViewerFlagRuleRun
    {
        public int PeopleWithRulesPending => judging.Pending;

        public IReadOnlyList<TenantOverviewRuleTeam> Teams => teams;

        public async Task<ViewerJudgement> JudgeAsync(
            GlucoseReadTenant person,
            IServiceProvider personScope,
            SensorGlucose? latest,
            IReadOnlyList<TrackerInstance>? activeTrackers,
            PumpSnapshot? pump,
            CancellationToken ct)
        {
            var ruled = Enumerable.Range(0, held.Count)
                .Where(i => held[i].People is not { } people || people.Contains(person.Tenant.Id))
                .ToList();
            var filtered = Enumerable.Range(held.Count, filters.Count(f => f is not null)).ToList();
            var checkable = filters.All(f => f is not null);
            if (ruled.Count == 0 && filtered.Count == 0)
                return new ViewerJudgement([], checkable);

            var judged = await judging.JudgeAsync(
                person, personScope, new FlagLiveReads(latest, activeTrackers, pump), [.. ruled, .. filtered], ct);
            if (judged.State != FlagPersonState.Judged)
                return new ViewerJudgement([], filters.Count == 0);

            var reasons = new List<TenantOverviewAttention>();
            for (var k = 0; k < ruled.Count; k++)
            {
                var rule = held[ruled[k]];
                if (ReasonOf(rule.Rule, rule.Team, rule.Condition, judged.Judgements[k]) is { } reason)
                    reasons.Add(reason);
            }
            var matches = checkable && judged.Judgements.Skip(ruled.Count).All(j => j.Matches);
            return new ViewerJudgement(ShownOnce(reasons), matches);
        }

        public void Finish() => judging.Finish();
    }
}

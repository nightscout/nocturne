using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Reports;

/// <inheritdoc cref="IPersonalFlagRules"/>
/// <remarks>
/// A rule whose fact no longer checks (a dataset or field gone since it was saved) is left out. A person is judged by
/// <see cref="FlagFactEvaluator"/>, which reads nothing on the caller's flow: a read that fails does so in the
/// background (<see cref="FlagFactReads"/>), and leaves only that person with the fixed set.
/// </remarks>
internal sealed class PersonalFlagRules(IFlagRuleRepository rules, FlagFactEvaluator evaluator) : IPersonalFlagRules
{
    public async Task<IPersonalFlagRuleRun?> BeginAsync(Guid subjectId, bool credentialLimitTo24Hours, CancellationToken ct)
    {
        var held = new List<(FlagRule Rule, FlagFactCondition Condition)>();
        foreach (var rule in await rules.GetOfSubjectAsync(subjectId, ct))
            if (evaluator.CheckRule(rule.Dataset, rule.Field, rule.Days, rule.Comparison, rule.Value).Condition is { } condition)
                held.Add((rule, condition));

        return held.Count == 0
            ? null
            : new Run(held, evaluator.Begin([.. held.Select(h => h.Condition)], credentialLimitTo24Hours));
    }

    /// <summary>The reason a rule gives for a judgement: a match, or too little data that matched; else none.</summary>
    internal static TenantOverviewAttention? ReasonOf(FlagRule rule, FlagFactCondition condition, FlagJudgement judgement)
    {
        var tooLittle = judgement.Outcome == FlagOutcome.TooLittleData;
        if (judgement.Value is not { } value || !(judgement.Outcome == FlagOutcome.Matched || (tooLittle && judgement.Matches)))
            return null;

        var range = condition.Fact.Range;
        return new TenantOverviewAttention(TenantOverviewAttentionKind.FlagRule, rule.Level)
        {
            Rule = new TenantOverviewRuleReason(
                rule.Id,
                FlagRuleSource.Yours,
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

    private sealed class Run(
        IReadOnlyList<(FlagRule Rule, FlagFactCondition Condition)> held,
        FlagFactEvaluator.FlagJudging judging) : IPersonalFlagRuleRun
    {
        public int PeopleWithRulesPending => judging.Pending;

        public async Task<IReadOnlyList<TenantOverviewAttention>> ReasonsAsync(
            GlucoseReadTenant person,
            IServiceProvider personScope,
            SensorGlucose? latest,
            IReadOnlyList<TrackerInstance>? activeTrackers,
            PumpSnapshot? pump,
            CancellationToken ct)
        {
            var judged = await judging.JudgeAsync(person, personScope, new FlagLiveReads(latest, activeTrackers, pump), ct);
            if (judged.State != FlagPersonState.Judged)
                return [];

            var reasons = new List<TenantOverviewAttention>();
            for (var i = 0; i < held.Count; i++)
                if (ReasonOf(held[i].Rule, held[i].Condition, judged.Judgements[i]) is { } reason)
                    reasons.Add(reason);
            return reasons;
        }

        public void Finish() => judging.Finish();
    }
}

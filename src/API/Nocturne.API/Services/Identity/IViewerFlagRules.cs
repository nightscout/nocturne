using Nocturne.API.Reports;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Identity;

/// <summary>The flag rules that apply to a viewer's people, and the fact filters of their query, judged for the people of an overview read.</summary>
public interface IViewerFlagRules
{
    /// <summary>
    /// The rules <paramref name="rules"/> names that apply to <paramref name="people"/>, and <paramref name="facts"/>,
    /// ready to judge for them as one: the rules of every team <paramref name="subjectId"/> is staff of, each for the
    /// people the team follows, for <see cref="OverviewRuleSet.All"/> the subject's own rules too, for everyone, and each
    /// fact filter (<see cref="PeopleFilterKind.Fact"/>) for everyone. Null when there is neither a rule nor a filter.
    /// <paramref name="credentialLimitTo24Hours"/> is the caller's credential limit, as the overview takes it.
    /// </summary>
    Task<IViewerFlagRuleRun?> BeginAsync(
        Guid subjectId,
        OverviewRuleSet rules,
        IReadOnlyList<PeopleFilter> facts,
        IReadOnlyList<GlucoseReadTenant> people,
        bool credentialLimitTo24Hours,
        CancellationToken ct);
}

/// <summary>What one read's judging found for a person.</summary>
/// <param name="Reasons">The reasons the rules give: none while the person is still being checked or a read failed.</param>
/// <param name="MatchesFilters">
/// Whether every fact filter matched. Not while the person is still being checked, a read failed, a filter cannot be
/// checked for them, or a filter's fact no longer exists; always when there is no filter.
/// </param>
public sealed record ViewerJudgement(IReadOnlyList<TenantOverviewAttention> Reasons, bool MatchesFilters);

/// <summary>One overview read's judging of the rules that apply to the viewer's people, and of the fact filters of their query.</summary>
public interface IViewerFlagRuleRun
{
    /// <summary>
    /// How many people the rules or the filters are still being checked for
    /// (<see cref="TenantOverviewResponse.PeopleWithRulesPending"/>).
    /// </summary>
    int PeopleWithRulesPending { get; }

    /// <summary>The teams whose rules apply to at least one of the people the run was begun for (<see cref="TenantOverviewResponse.RuleTeams"/>).</summary>
    IReadOnlyList<TenantOverviewRuleTeam> Teams { get; }

    /// <summary>
    /// What the rules and the filters give for <paramref name="person"/>, judged by one judging, so a rule and a filter
    /// on one fact share its read. A personal rule's reason is left out where a team's rule with the same fact, days,
    /// comparison and value gave one. It never waits on a read. <paramref name="personScope"/> is the overview's scope
    /// for the person, and <paramref name="latest"/>, <paramref name="activeTrackers"/>, <paramref name="pump"/> and
    /// <paramref name="onBoard"/> what it already read there (trackers, pump and on board figures null where the caller
    /// may not read them).
    /// </summary>
    Task<ViewerJudgement> JudgeAsync(
        GlucoseReadTenant person,
        IServiceProvider personScope,
        SensorGlucose? latest,
        IReadOnlyList<TrackerInstance>? activeTrackers,
        PumpSnapshot? pump,
        OnBoardFigures? onBoard,
        CancellationToken ct);

    /// <summary>Starts the reads left to the background. Called once, after the last person.</summary>
    void Finish();
}

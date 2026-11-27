using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Identity;

/// <summary>The flag rules that apply to a viewer's people, judged for the people of an overview read.</summary>
public interface IViewerFlagRules
{
    /// <summary>
    /// The rules <paramref name="rules"/> names that apply to <paramref name="people"/>, ready to judge for them: the
    /// rules of every team <paramref name="subjectId"/> is staff of, each for the people the team follows, and for
    /// <see cref="OverviewRuleSet.All"/> the subject's own rules too, for everyone. Null when none applies.
    /// <paramref name="credentialLimitTo24Hours"/> is the caller's credential limit, as the overview takes it.
    /// </summary>
    Task<IViewerFlagRuleRun?> BeginAsync(
        Guid subjectId,
        OverviewRuleSet rules,
        IReadOnlyList<GlucoseReadTenant> people,
        bool credentialLimitTo24Hours,
        CancellationToken ct);
}

/// <summary>One overview read's judging of the rules that apply to the viewer's people.</summary>
public interface IViewerFlagRuleRun
{
    /// <summary>How many people the rules are still being checked for (<see cref="TenantOverviewResponse.PeopleWithRulesPending"/>).</summary>
    int PeopleWithRulesPending { get; }

    /// <summary>The teams whose rules apply to at least one of the people the run was begun for (<see cref="TenantOverviewResponse.RuleTeams"/>).</summary>
    IReadOnlyList<TenantOverviewRuleTeam> Teams { get; }

    /// <summary>
    /// The reasons the rules give for <paramref name="person"/>: none while the person is pending or its read failed.
    /// A personal rule's reason is left out where a team's rule with the same fact, days, comparison and value gave
    /// one. It never waits on a read. <paramref name="personScope"/> is the overview's scope for the person, and
    /// <paramref name="latest"/>, <paramref name="activeTrackers"/> and <paramref name="pump"/> what it already read
    /// there (trackers and pump null where the caller may not read them).
    /// </summary>
    Task<IReadOnlyList<TenantOverviewAttention>> ReasonsAsync(
        GlucoseReadTenant person,
        IServiceProvider personScope,
        SensorGlucose? latest,
        IReadOnlyList<TrackerInstance>? activeTrackers,
        PumpSnapshot? pump,
        CancellationToken ct);

    /// <summary>Starts the reads left to the background. Called once, after the last person.</summary>
    void Finish();
}

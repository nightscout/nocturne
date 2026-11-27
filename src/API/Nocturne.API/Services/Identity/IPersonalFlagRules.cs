using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// Whether a read of people judges the caller's own flag rules: what the controller makes of the
/// <see cref="OverviewRuleSet"/> a request names. It never reaches the wire.
/// </summary>
public enum OverviewFlagRules
{
    Judge,

    /// <summary>For a view that is not the caller's alone, such as the wall, or a caller that asked for no rules.</summary>
    LeaveOut,
}

/// <summary>The caller's own flag rules, judged for the people of an overview read.</summary>
public interface IPersonalFlagRules
{
    /// <summary>
    /// The rules of <paramref name="subjectId"/>, ready to judge for the people of one overview read; null when it holds
    /// none. <paramref name="credentialLimitTo24Hours"/> is the caller's credential limit, as the overview takes it.
    /// </summary>
    Task<IPersonalFlagRuleRun?> BeginAsync(Guid subjectId, bool credentialLimitTo24Hours, CancellationToken ct);
}

/// <summary>One overview read's judging of the caller's rules.</summary>
public interface IPersonalFlagRuleRun
{
    /// <summary>How many people the rules are still being checked for (<see cref="TenantOverviewResponse.PeopleWithRulesPending"/>).</summary>
    int PeopleWithRulesPending { get; }

    /// <summary>
    /// The reasons the rules give for <paramref name="person"/>: none while the person is pending or its read failed.
    /// It never waits on a read. <paramref name="personScope"/> is the overview's scope for the person, and
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

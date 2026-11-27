using Nocturne.Core.Models.Identity;

namespace Nocturne.Core.Contracts.Identity;

/// <summary>
/// A viewer's own flag rules and their teams' rules. A rule belongs to a subject or a team, not to any tenant, and
/// every call reads on a context pinned to the caller's subject. The <c>subject_pin</c> policy on <c>flag_rules</c>
/// admits only the subject's own rows, and <c>team_pin</c> the rows of a team the subject is a member of, which only a
/// manager of it may write. No other context reads any.
/// </summary>
public interface IFlagRuleRepository
{
    /// <summary>The subject's rules, oldest first.</summary>
    Task<IReadOnlyList<FlagRule>> GetOfSubjectAsync(Guid subjectId, CancellationToken ct);

    /// <summary>
    /// Adds a rule, or returns null when the subject already holds <see cref="FlagRuleLimits.PerViewer"/>.
    /// Creates for one subject run one at a time, so two at once cannot pass the limit together.
    /// </summary>
    Task<FlagRule?> CreateAsync(Guid subjectId, FlagRuleDraft draft, CancellationToken ct);

    /// <summary>The changed rule, or null when the subject holds no rule <paramref name="ruleId"/>.</summary>
    Task<FlagRule?> UpdateAsync(Guid subjectId, Guid ruleId, FlagRuleDraft draft, CancellationToken ct);

    /// <summary>Whether the subject held rule <paramref name="ruleId"/>, now deleted.</summary>
    Task<bool> DeleteAsync(Guid subjectId, Guid ruleId, CancellationToken ct);

    /// <summary>
    /// The team's rules, oldest first; null when <paramref name="callerSubjectId"/> is not a member of it whose end has
    /// not passed at <paramref name="now"/>.
    /// </summary>
    Task<IReadOnlyList<FlagRule>?> GetOfTeamAsync(Guid teamId, Guid callerSubjectId, DateTime now, CancellationToken ct);

    /// <summary>
    /// The rules of each of <paramref name="teamIds"/> that <paramref name="subjectId"/> is a member of, oldest first,
    /// keyed by team. Whether the subject's place in a team has ended is the caller's to decide.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<FlagRule>>> GetOfTeamsAsync(
        Guid subjectId, IReadOnlyCollection<Guid> teamIds, CancellationToken ct);

    /// <summary>
    /// Adds a rule to the team, with its row in the team's history. Refused when the caller does not manage the team,
    /// or it already holds <see cref="FlagRuleLimits.PerTeam"/>; creates for one team run one at a time, so two at once
    /// cannot pass the limit together.
    /// </summary>
    Task<TeamFlagRuleWrite> CreateForTeamAsync(Guid teamId, Guid callerSubjectId, FlagRuleDraft draft, CancellationToken ct);

    /// <summary>Changes the team's rule, with its row in the team's history holding the rule before and after.</summary>
    Task<TeamFlagRuleWrite> UpdateForTeamAsync(
        Guid teamId, Guid callerSubjectId, Guid ruleId, FlagRuleDraft draft, CancellationToken ct);

    /// <summary>Deletes the team's rule, with its row in the team's history.</summary>
    Task<TeamFlagRuleChange> DeleteForTeamAsync(Guid teamId, Guid callerSubjectId, Guid ruleId, CancellationToken ct);
}

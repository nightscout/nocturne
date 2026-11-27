using Nocturne.Core.Models.Identity;

namespace Nocturne.Core.Contracts.Identity;

/// <summary>
/// A viewer's own flag rules. They belong to the subject, not to any tenant, and every call reads on a
/// context pinned to that subject, whose <c>subject_pin</c> policy on <c>flag_rules</c> admits only its own
/// rows. No other context reads any.
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
}

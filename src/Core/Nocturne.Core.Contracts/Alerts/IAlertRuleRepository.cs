using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.Core.Contracts.Alerts;

/// <summary>
/// Writes to alert rules and their channels, the reads that find rules by their tag (the feature
/// that manages them or the starter kind they were made as), and the reads of a tenant's channels.
/// The reads every caller shares are on <see cref="IAlertRepository"/>.
/// </summary>
/// <remarks>
/// Every method runs as the scope's reader on its tenant, except <see cref="SetScopeClassAsync"/>,
/// which pins the tenant it is given. A write that returns <c>false</c> or null found no such rule
/// of the tenant and changed nothing.
/// </remarks>
public interface IAlertRuleRepository
{
    /// <summary>Stores a rule with its channels and returns its id.</summary>
    Task<Guid> CreateAsync(NewAlertRule rule, CancellationToken ct);

    /// <summary>
    /// Sets every field of <paramref name="rule"/> on the stored rule and stamps it updated. The
    /// rule's tags and channels are left as they are.
    /// </summary>
    Task<bool> UpdateAsync(Guid ruleId, AlertRuleWrite rule, CancellationToken ct);

    /// <summary>
    /// Replaces the rule's channels with <paramref name="channels"/>, stored in the order given,
    /// after which the stored channels <paramref name="kept"/> selects follow in their stored order.
    /// A channel that <see cref="AlertRuleChannelWrite.KeepsStoredSecret"/> takes the secret of the
    /// stored channel with its type and destination, replaced or kept.
    /// </summary>
    /// <param name="kept">The stored channels to keep; null keeps none.</param>
    Task<bool> ReplaceChannelsAsync(
        Guid ruleId,
        IReadOnlyList<AlertRuleChannelWrite> channels,
        Func<AlertRuleChannelSnapshot, bool>? kept,
        CancellationToken ct);

    /// <summary>Adds a channel after the rule's stored ones.</summary>
    Task<bool> AddChannelAsync(Guid ruleId, AlertRuleChannelWrite channel, CancellationToken ct);

    /// <summary>Removes one channel of the rule.</summary>
    Task<bool> RemoveChannelAsync(Guid ruleId, Guid channelId, CancellationToken ct);

    /// <summary>
    /// Flips the rule's enablement and stamps it updated. Returns the enablement before the flip.
    /// </summary>
    Task<bool?> ToggleEnabledAsync(Guid ruleId, CancellationToken ct);

    /// <summary>Deletes the rule; its channels go with it.</summary>
    Task<bool> DeleteAsync(Guid ruleId, CancellationToken ct);

    /// <summary>The rules whose <c>ManagedBy</c> tag is <paramref name="managedBy"/>, by id.</summary>
    Task<IReadOnlyDictionary<Guid, AlertRuleRecord>> GetManagedRulesAsync(string managedBy, CancellationToken ct);

    /// <summary>
    /// The setup hub's starter rules by kind. A kind stored more than once is its earliest-created
    /// rule.
    /// </summary>
    Task<IReadOnlyDictionary<StarterAlertKind, AlertRuleRecord>> GetStarterRulesAsync(CancellationToken ct);

    /// <summary>
    /// The channels of each of <paramref name="ruleIds"/>, in <c>SortOrder</c>. A rule with none, or
    /// not of the tenant, has no entry.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<AlertRuleChannelSnapshot>>> GetChannelsAsync(
        IReadOnlyCollection<Guid> ruleIds, CancellationToken ct);

    /// <summary>Whether the tenant has a channel with <paramref name="channelId"/>.</summary>
    Task<bool> ChannelExistsAsync(Guid channelId, CancellationToken ct);

    /// <summary>
    /// The tenant's channels that carry a secret, by rule name and then channel order.
    /// </summary>
    Task<IReadOnlyList<AlertChannelSecret>> GetChannelSecretsAsync(CancellationToken ct);

    /// <summary>The distinct destinations of the tenant's stored <c>in_app</c> channels.</summary>
    Task<IReadOnlySet<string>> GetInAppDestinationsAsync(CancellationToken ct);

    /// <summary>
    /// Sets <paramref name="scopeClass"/> on each of <paramref name="ruleIds"/> of
    /// <paramref name="tenantId"/> in one statement, and returns how many rows it reached. Stamps
    /// nothing else: a reclassification is not an edit.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The repository runs on a caller's unit of work, whose tenant it cannot replace.
    /// </exception>
    Task<int> SetScopeClassAsync(
        Guid tenantId, RuleScopeClass scopeClass, IReadOnlyCollection<Guid> ruleIds, CancellationToken ct);

    /// <summary>
    /// Stores each of <paramref name="rules"/> whose name the tenant has no rule with yet, and
    /// returns every one's id by name.
    /// </summary>
    Task<SeededAlertRules> CreateMissingByNameAsync(IReadOnlyList<NewAlertRule> rules, CancellationToken ct);
}

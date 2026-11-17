using Nocturne.Core.Models.Alerts;

namespace Nocturne.Core.Models;

/// <summary>
/// The fields of a stored <see cref="AlertRule"/> a writer sets. A writer that changes some of them
/// starts from <see cref="Of"/>, so the rest keep their stored values.
/// </summary>
/// <param name="ConditionParams">
/// The body as stored. A writer whose tree is JSON-equal to the stored one passes the stored text,
/// so an edit that leaves the tree alone is not a new condition version.
/// </param>
/// <param name="ReviewReason">
/// One of <see cref="AlertRuleReviewReasons"/>, or null. The rule editor's save passes null, which
/// clears it; a sync that is not the owner looking passes the stored value.
/// </param>
public sealed record AlertRuleWrite(
    string Name,
    string? Description,
    AlertConditionType ConditionType,
    string ConditionParams,
    RuleScopeClass ScopeClass,
    AlertRuleSeverity Severity,
    bool IsEnabled,
    int SortOrder,
    bool AllowThroughDnd,
    bool AutoResolveEnabled,
    string? AutoResolveParams,
    string ClientConfiguration,
    string? ReviewReason = null)
{
    /// <summary>The stored rule's values.</summary>
    public static AlertRuleWrite Of(AlertRuleRecord record) => new(
        record.Rule.Name,
        record.Description,
        record.Rule.ConditionType,
        record.Rule.ConditionParams,
        record.Rule.ScopeClass,
        record.Rule.Severity,
        record.IsEnabled,
        record.Rule.SortOrder,
        record.Rule.AllowThroughDnd,
        record.Rule.AutoResolveEnabled,
        record.Rule.AutoResolveParams,
        record.Rule.ClientConfiguration,
        record.ReviewReason);
}

/// <summary>
/// A rule to store, with the channels it starts with and the tags that say whose it is. The tags
/// are set once: <see cref="ManagedBy"/> hands the rule's condition to another feature's sync, and
/// <see cref="StarterKind"/> lets the setup hub find it again.
/// </summary>
public sealed record NewAlertRule(
    AlertRuleWrite Rule,
    IReadOnlyList<AlertRuleChannelWrite> Channels,
    string? ManagedBy = null,
    StarterAlertKind? StarterKind = null);

/// <summary>
/// A delivery channel as a writer stores it. Channels are stored in the order given.
/// </summary>
/// <param name="Secret">
/// The webhook signing secret as ciphertext, or null for a channel that stores none.
/// </param>
/// <param name="KeepsStoredSecret">
/// True when the caller did not say: the channel keeps the secret stored against its type and
/// destination, if there is exactly one, because the editor cannot re-send what it was never shown.
/// False stores <paramref name="Secret"/>, so a null clears it.
/// </param>
public sealed record AlertRuleChannelWrite(
    ChannelType ChannelType,
    string Destination,
    string? DestinationLabel = null,
    string? Metadata = null,
    string? Secret = null,
    bool KeepsStoredSecret = false);

/// <summary>
/// The outcome of seeding rules by name: every seeded rule's id by its name, whether it was already
/// stored or made by this call, and how many were made.
/// </summary>
public sealed record SeededAlertRules(IReadOnlyDictionary<string, Guid> RuleIdsByName, int Created);

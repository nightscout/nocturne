using Nocturne.Core.Models;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Shape of the smart-snooze evaluation scope. A rule's configured snooze conditions
/// (<c>client_configuration.snooze.conditions</c>) are a flat array; the engine sees them
/// as a single tree, so they are wrapped in <c>composite{and, conditions}</c> and evaluated
/// under <see cref="AlertConditionTypeNames.SnoozePathRoot"/>.
/// </summary>
/// <remarks>
/// The wrap lives here rather than inline in <c>AlertSweepService</c> because the parity
/// corpus has to build the byte-identical tree — a corpus that pinned a differently shaped
/// wrapper would pin condition paths the sweep never produces.
/// </remarks>
internal static class SnoozeConditionTree
{
    /// <summary>
    /// The composite payload alone, for callers that need the rule's
    /// <c>condition_params</c> shape rather than a full node — the sweep builds a synthetic
    /// rule in this shape so <c>RuleDataNeeds.Walk</c> enriches exactly the facts the
    /// conditions will read.
    /// </summary>
    public static CompositeCondition Payload(List<ConditionNode> conditions) =>
        new("and", conditions);

    /// <summary>
    /// Wraps <paramref name="conditions"/> as the root node of the snooze scope. Callers
    /// gate on a non-empty list (an empty <c>composite</c> evaluates false, which as a
    /// snooze predicate means "clear the snooze and re-fire").
    /// </summary>
    public static ConditionNode Wrap(List<ConditionNode> conditions) =>
        new("composite", Composite: Payload(conditions));
}

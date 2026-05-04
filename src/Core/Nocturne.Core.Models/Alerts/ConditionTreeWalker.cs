namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// Pure-model recursive walks over a <see cref="ConditionNode"/> tree. Mirrors the wrapper
/// recursion used by <c>ConditionPath.Walk</c> and <c>RuleDataNeeds.VisitNode</c> in the API
/// layer, but lives in Core.Models so validation that runs before the request reaches the
/// evaluator pipeline (controllers, mappers) can share the same traversal.
/// </summary>
public static class ConditionTreeWalker
{
    /// <summary>
    /// Returns true when any leaf in <paramref name="root"/>'s tree is a
    /// <c>state_span_active</c> node carrying <see cref="StateSpanCategory.PumpMode"/>.
    /// Pump-mode rules must use <see cref="PumpStateCondition"/> instead so the enricher
    /// loads the dedicated pump-mode snapshot; the runtime <c>StateSpanActiveEvaluator</c>
    /// fails closed for this combination, so without rejecting upfront the user gets a rule
    /// that silently never fires.
    /// </summary>
    public static bool ContainsPumpModeStateSpan(ConditionNode root)
    {
        if (root is null) return false;

        // Leaf check: state_span_active with PumpMode.
        if (root.StateSpanActive is { Category: StateSpanCategory.PumpMode }
            && string.Equals(root.Type, "state_span_active", System.StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Recurse into wrappers — same shape as ConditionPath.Walk.
        switch (root.Type?.ToLowerInvariant())
        {
            case "composite" when root.Composite is not null:
                foreach (var child in root.Composite.Conditions)
                {
                    if (ContainsPumpModeStateSpan(child)) return true;
                }
                return false;
            case "not" when root.Not is not null:
                return ContainsPumpModeStateSpan(root.Not.Child);
            case "sustained" when root.Sustained is not null:
                return ContainsPumpModeStateSpan(root.Sustained.Child);
            default:
                return false;
        }
    }
}

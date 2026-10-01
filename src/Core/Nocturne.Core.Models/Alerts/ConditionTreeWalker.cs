namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// Pure-model recursive walks over a <see cref="ConditionNode"/> tree, in Core.Models so code
/// outside the evaluator pipeline (controllers, replay) can share them.
/// </summary>
public static class ConditionTreeWalker
{
    /// <summary>
    /// Every <c>alert_state</c> target id in <paramref name="root"/>'s tree, in pre-order,
    /// skipping missing children.
    /// </summary>
    public static IEnumerable<Guid> AlertStateReferences(ConditionNode? root)
    {
        if (root is null) yield break;
        if (root.AlertState is { } alertState) yield return alertState.AlertId;
        foreach (var child in root.Composite?.Conditions ?? [])
            foreach (var id in AlertStateReferences(child)) yield return id;
        foreach (var id in AlertStateReferences(root.Not?.Child)) yield return id;
        foreach (var id in AlertStateReferences(root.Sustained?.Child)) yield return id;
    }
}

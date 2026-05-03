namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// Assigns stable, sequential leaf IDs to a <see cref="ConditionNode"/> tree using a
/// pure pre-order DFS traversal. Composite, NOT and Sustained nodes are containers —
/// they do not receive IDs themselves; the walker descends into their child(ren).
/// Every other node type is treated as a leaf and gets the next sequential ID.
/// </summary>
/// <remarks>
/// The ID space is per-rule (each call starts at 0). The output ordering matches the
/// order leaves appear in a depth-first, left-to-right walk of the tree, which is the
/// same order <see cref="Nocturne.Core.Models.Alerts"/> evaluators see them via the
/// recursive registry dispatch.
/// </remarks>
public static class LeafIdentity
{
    /// <summary>
    /// Returns every leaf node in <paramref name="root"/> paired with its sequential
    /// pre-order id. Container nodes (composite/not/sustained) are unwrapped and never
    /// appear in the output themselves — only the leaf inside them does.
    /// </summary>
    /// <param name="root">Root of the condition tree to walk.</param>
    public static IReadOnlyList<(int LeafId, ConditionNode Node)> AssignLeafIds(ConditionNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var leaves = new List<(int, ConditionNode)>();
        var nextId = 0;
        Walk(root, ref nextId, leaves);
        return leaves;
    }

    private static void Walk(ConditionNode node, ref int nextId, List<(int, ConditionNode)> leaves)
    {
        switch (node.Type.ToLowerInvariant())
        {
            case "composite" when node.Composite is { Conditions: { } children }:
                foreach (var child in children)
                    Walk(child, ref nextId, leaves);
                return;
            case "not" when node.Not is { Child: { } notChild }:
                Walk(notChild, ref nextId, leaves);
                return;
            case "sustained" when node.Sustained is { Child: { } sustainedChild }:
                Walk(sustainedChild, ref nextId, leaves);
                return;
            default:
                leaves.Add((nextId++, node));
                return;
        }
    }
}

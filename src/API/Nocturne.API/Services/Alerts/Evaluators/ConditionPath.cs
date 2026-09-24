using System.Text;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Stable path strings into an <see cref="AlertRule"/>'s <see cref="ConditionNode"/> tree.
/// Used as the persistent identity for state attached to an interior node — currently
/// the per-sustained timer rows in <c>alert_condition_timers</c>, keyed by
/// (rule id, condition path).
/// </summary>
/// <remarks>
/// Format: the root node renders as just its kind (e.g. <c>composite</c>). Each
/// descendant prepends its position in the parent's children list, then its kind:
/// <c>composite[0].sustained</c>, <c>composite[0].sustained[0].threshold</c>.
/// <see cref="Build"/> and <see cref="Walk"/> are mutually consistent — a path emitted
/// by the walker for a given node round-trips through serialisation provided the tree
/// structure is unchanged.
/// </remarks>
public static class ConditionPath
{
    /// <summary>Sentinel for the root segment, which has no parent and therefore no child index.</summary>
    public const int RootIndex = -1;

    /// <summary>
    /// Joins an ordered sequence of <c>(nodeKind, childIndex)</c> segments into a path string.
    /// The first segment is treated as the root: its <c>childIndex</c> is ignored (pass
    /// <see cref="RootIndex"/> for clarity). Every subsequent segment contributes
    /// <c>[childIndex].nodeKind</c>.
    /// </summary>
    public static string Build(IEnumerable<(string NodeKind, int ChildIndex)> segments)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var (kind, index) in segments)
        {
            if (first)
            {
                sb.Append(kind);
                first = false;
            }
            else
            {
                sb.Append('[').Append(index).Append("].").Append(kind);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Walks the condition tree in document order, invoking <paramref name="visit"/>
    /// for every node with the canonical path string for that node. Returns the first
    /// non-null visitor result, or <see langword="null"/> if no node matches.
    /// </summary>
    /// <remarks>
    /// Recurses into wrapper kinds: <c>composite</c> children, <c>not.child</c>,
    /// <c>sustained.child</c>. All other condition kinds are leaves. A missing child, a missing
    /// child list and an untyped node are not visited: stored trees predating save-time
    /// validation can hold them, and <see cref="ConditionTreeFaults"/> is what reports them.
    /// </remarks>
    public static T? Walk<T>(ConditionNode? root, Func<ConditionNode, string, T?> visit)
        where T : class
    {
        return root?.Type is null ? null : WalkInternal(root, root.Type, visit);
    }

    private static T? WalkInternal<T>(ConditionNode node, string path, Func<ConditionNode, string, T?> visit)
        where T : class
    {
        var hit = visit(node, path);
        if (hit is not null)
            return hit;

        switch (node.Type.ToLowerInvariant())
        {
            case "composite" when node.Composite?.Conditions is { } children:
                for (var i = 0; i < children.Count; i++)
                {
                    if (WalkChild(children[i], path, i, visit) is { } result)
                        return result;
                }
                break;
            case "not":
                return WalkChild(node.Not?.Child, path, 0, visit);
            case "sustained":
                return WalkChild(node.Sustained?.Child, path, 0, visit);
        }

        return null;
    }

    private static T? WalkChild<T>(ConditionNode? child, string path, int index, Func<ConditionNode, string, T?> visit)
        where T : class =>
        child?.Type is null ? null : WalkInternal(child, $"{path}[{index}].{child.Type}", visit);
}

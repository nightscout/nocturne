namespace Nocturne.Core.Models;

/// <summary>
///     What one pass of folding stored state spans did. Folding at write time keeps new device
///     readings from piling up as many short same-state spans; this pass applies the same rule to
///     spans stored before the rule existed, or written by a path that bypassed it.
/// </summary>
public sealed class StateSpanFoldResult
{
    /// <summary>Spans the pass looked at: closed, connector-keyed spans of the foldable categories.</summary>
    public int Examined { get; set; }

    /// <summary>Spans that survived and were widened to cover one or more neighbours.</summary>
    public int Widened { get; set; }

    /// <summary>Spans absorbed into a neighbour and soft-deleted.</summary>
    public int Removed { get; set; }
}

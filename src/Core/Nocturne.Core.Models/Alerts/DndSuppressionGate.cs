namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// The single source of truth for scoped Do Not Disturb suppression (ADR 0004 D5):
/// given a rule and the set of DND scopes active at the evaluation instant, decides
/// whether the rule's delivery is silenced. Mirrors the device's
/// <c>DndSuppression.isSuppressed</c> / <c>RuleScopeClass.coveredBy</c> exactly so the
/// two halves agree byte-for-byte; both <see cref="AlertOrchestrator"/> (live) and
/// <see cref="AlertReplayService"/> (replay) call this instead of duplicating the gate.
/// </summary>
/// <remarks>
/// The alert still fires and is recorded — only delivery is suppressed. Critical severity
/// and <c>AllowThroughDnd</c> both pierce every mute. A <c>lows</c>/<c>highs</c> window
/// silences a rule only when its <see cref="RuleScopeClass"/> matches; an <c>all</c> window
/// silences every class (composite/undirected rules are therefore all-only).
/// </remarks>
public static class DndSuppressionGate
{
    /// <summary>
    /// Whether <paramref name="scope"/> covers a rule of class <paramref name="scopeClass"/>:
    /// <c>all</c> covers everything; <c>lows</c> covers only <see cref="RuleScopeClass.Low"/>;
    /// <c>highs</c> covers only <see cref="RuleScopeClass.High"/>. Composite/undirected are
    /// covered by <c>all</c> alone. Mirrors the device's <c>RuleScopeClass.coveredBy</c>.
    /// </summary>
    public static bool Covers(DndScope scope, RuleScopeClass scopeClass) => scope switch
    {
        DndScope.All => true,
        DndScope.Lows => scopeClass == RuleScopeClass.Low,
        DndScope.Highs => scopeClass == RuleScopeClass.High,
        _ => false,
    };

    /// <summary>
    /// The active scope that silences a rule with the given severity / allow-through flag /
    /// scope class, or <c>null</c> when the rule is not suppressed (so the caller can stamp
    /// <c>SuppressionReason = "dnd:&lt;scope&gt;"</c>). Critical and <c>allowThroughDnd</c>
    /// pierce first. When more than one active scope covers the rule, the class-specific scope
    /// (<c>lows</c>/<c>highs</c>) is preferred over <c>all</c> so the recorded reason names the
    /// most precise cause; for composite/undirected only <c>all</c> can apply.
    /// </summary>
    public static DndScope? SuppressingScope(
        AlertRuleSeverity severity,
        bool allowThroughDnd,
        RuleScopeClass scopeClass,
        IReadOnlySet<DndScope> activeScopes)
    {
        if (severity == AlertRuleSeverity.Critical) return null;
        if (allowThroughDnd) return null;

        var specific = scopeClass switch
        {
            RuleScopeClass.Low => (DndScope?)DndScope.Lows,
            RuleScopeClass.High => DndScope.Highs,
            _ => null,
        };
        if (specific is { } s && activeScopes.Contains(s)) return s;
        if (activeScopes.Contains(DndScope.All)) return DndScope.All;
        return null;
    }

    /// <summary>Convenience overload over an <see cref="AlertRuleSnapshot"/>.</summary>
    public static DndScope? SuppressingScope(AlertRuleSnapshot rule, IReadOnlySet<DndScope> activeScopes) =>
        SuppressingScope(rule.Severity, rule.AllowThroughDnd, rule.ScopeClass, activeScopes);

    /// <summary>True when the rule's delivery is silenced by one of the active scopes.</summary>
    public static bool IsSuppressed(AlertRuleSnapshot rule, IReadOnlySet<DndScope> activeScopes) =>
        SuppressingScope(rule, activeScopes) is not null;

    /// <summary>
    /// The value stamped on <c>alert_instances.suppression_reason</c> when
    /// <paramref name="scope"/> silenced a rule: <c>dnd:lows</c> / <c>dnd:highs</c> /
    /// <c>dnd:all</c> (the contract is per-instance, scope-tagged — the excursion still fires).
    /// </summary>
    public static string SuppressionReason(DndScope scope) => scope switch
    {
        DndScope.Lows => "dnd:lows",
        DndScope.Highs => "dnd:highs",
        _ => "dnd:all",
    };
}

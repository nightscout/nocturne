using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Evaluators;

/// <summary>
/// Maps <see cref="RuleScopeClass"/> to/from the wire strings the shared alert
/// engine emits (<c>low | high | composite | undirected</c>) — the byte-identical
/// device↔server contract for scoped Do Not Disturb (ADR 0004). The set is small
/// and frozen, so this is an explicit switch rather than the reflection
/// <see cref="AlertConditionTypeNames"/> uses.
/// </summary>
internal static class RuleScopeClassNames
{
    /// <summary>Wire string for a scope class (matches the crate's <c>ScopeClass::wire</c>).</summary>
    public static string ToWireString(RuleScopeClass scopeClass) => scopeClass switch
    {
        RuleScopeClass.Low => "low",
        RuleScopeClass.High => "high",
        RuleScopeClass.Composite => "composite",
        RuleScopeClass.Undirected => "undirected",
        _ => "undirected",
    };

    /// <summary>
    /// Resolves a wire string to its <see cref="RuleScopeClass"/>, or null if the
    /// value is unrecognised (the caller decides how to fail).
    /// </summary>
    public static RuleScopeClass? FromWireString(string wire) => wire switch
    {
        "low" => RuleScopeClass.Low,
        "high" => RuleScopeClass.High,
        "composite" => RuleScopeClass.Composite,
        "undirected" => RuleScopeClass.Undirected,
        _ => null,
    };
}

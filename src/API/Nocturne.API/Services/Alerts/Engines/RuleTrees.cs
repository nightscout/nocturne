using System.Collections.Concurrent;
using System.Text.Json;
using Nocturne.API.Services.Alerts.Conditions;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Engines;

/// <summary>One leaf of a rule's condition tree as the engine reads it.</summary>
/// <param name="Kind">The leaf's kind, or null for one this host does not know.</param>
/// <param name="Params">The operands the engine decoded, or JSON null when there are none.</param>
internal readonly record struct ConditionLeaf(AlertConditionType? Kind, JsonElement Params)
{
    /// <summary>The operand <paramref name="name"/> as a GUID, or null when absent or not one.</summary>
    public Guid? GuidParam(string name) =>
        StringParam(name) is { } text && Guid.TryParse(text, out var id) ? id : null;

    /// <summary>The member of <typeparamref name="TEnum"/> whose name is exactly the operand <paramref name="name"/>.</summary>
    public TEnum? EnumParam<TEnum>(string name) where TEnum : struct, Enum
    {
        var text = StringParam(name);
        foreach (var value in Enum.GetValues<TEnum>())
        {
            if (value.ToString() == text)
                return value;
        }
        return null;
    }

    /// <summary>The operand <paramref name="name"/> when it is a string.</summary>
    public string? StringParam(string name) =>
        Params.ValueKind == JsonValueKind.Object
        && Params.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// A rule's condition trees read through the engine (<c>describe</c> and
/// <c>references_wall_clock</c>), so the host never parses a condition itself: what it finds in a
/// tree is what evaluation reads.
/// </summary>
/// <remarks>
/// A body that is not JSON goes to the engine as a JSON string, as replay sends it. It has no
/// leaves, like any body the engine cannot read, and live evaluation fails such a rule outright.
/// An auto-resolve tree that is not JSON, or that the engine cannot evaluate, has no leaves.
/// <para>
/// Every evaluation pass reads every rule's leaves, twice (<see cref="RuleReferenceResolver"/> and
/// <see cref="RuleDataNeeds"/>), so a description is kept per condition, keyed by the columns it
/// was read from. The cache is cleared whole when it outgrows <see cref="CacheLimit"/>.
/// </para>
/// </remarks>
internal static class RuleTrees
{
    /// <summary>Descriptions kept before the cache is cleared.</summary>
    internal const int CacheLimit = 4096;

    private static readonly ConcurrentDictionary<DescriptionKey, Description> Descriptions = new();

    /// <summary>
    /// The leaves of the rule's body in pre-order, then those of its auto-resolve tree when
    /// <paramref name="includeAutoResolve"/> and auto-resolve is enabled.
    /// </summary>
    /// <exception cref="RustAlertEngineException">The engine rejected the request.</exception>
    public static IReadOnlyList<ConditionLeaf> Leaves(AlertRuleSnapshot rule, bool includeAutoResolve)
    {
        var description = Describe(
            rule.ConditionType, rule.ConditionParams, rule.AutoResolveEnabled ? rule.AutoResolveParams : null);
        return includeAutoResolve ? description.All : description.Body;
    }

    /// <summary>The leaves of a body stored as <paramref name="conditionParams"/>.</summary>
    /// <exception cref="RustAlertEngineException">The engine rejected the request.</exception>
    public static IReadOnlyList<ConditionLeaf> Leaves(AlertConditionType type, string conditionParams) =>
        Describe(type, conditionParams, null).Body;

    /// <summary>The rule ids the <c>alert_state</c> leaves among <paramref name="leaves"/> name.</summary>
    public static IEnumerable<Guid> AlertStateTargets(IEnumerable<ConditionLeaf> leaves) =>
        leaves.Where(leaf => leaf.Kind == AlertConditionType.AlertState)
            .Select(leaf => leaf.GuidParam("alert_id"))
            .OfType<Guid>();

    /// <summary>
    /// Whether the rule must also be evaluated on the wall clock (docs/alerts/engine-semantics.md §5.1).
    /// </summary>
    /// <exception cref="RustAlertEngineException">The engine rejected the request.</exception>
    public static bool ReferencesWallClock(AlertRuleSnapshot rule) =>
        RustAlertEngine.ReferencesWallClock(new RustRuleConditionsRequest
        {
            ConditionType = AlertConditionTypeNames.ToWireString(rule.ConditionType),
            ConditionParams = Body(rule.ConditionType, rule.ConditionParams),
            AutoResolveEnabled = rule.AutoResolveEnabled,
            AutoResolveParams = rule.AutoResolveEnabled ? AutoResolve(rule.AutoResolveParams) : null,
        });

    /// <summary>
    /// The body as replay sends it: a body that is not JSON goes as a JSON string, which the engine
    /// cannot read as a payload.
    /// </summary>
    public static JsonElement Body(AlertConditionType type, string conditionParams)
    {
        try
        {
            return RustEnvelopeMapper.ParseJson(conditionParams);
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(conditionParams);
        }
    }

    /// <summary>The auto-resolve tree as evaluation sends it, or null when it is blank or not JSON.</summary>
    public static JsonElement? AutoResolve(string? autoResolveParams)
    {
        if (string.IsNullOrWhiteSpace(autoResolveParams))
            return null;
        try
        {
            return RustEnvelopeMapper.ParseJson(autoResolveParams);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Drops every kept description.</summary>
    internal static void ClearCache() => Descriptions.Clear();

    /// <param name="autoResolveParams">The auto-resolve tree to describe too, or null for none.</param>
    private static Description Describe(AlertConditionType type, string conditionParams, string? autoResolveParams)
    {
        var key = new DescriptionKey(type, conditionParams, autoResolveParams);
        if (Descriptions.TryGetValue(key, out var kept))
            return kept;

        var response = RustAlertEngine.Describe(new RustDescribeRequest
        {
            ConditionType = AlertConditionTypeNames.ToWireString(type),
            ConditionParams = Body(type, conditionParams),
            AutoResolveParams = AutoResolve(autoResolveParams),
        });
        var body = new List<ConditionLeaf>();
        if (response.Tree is { } tree)
            Collect(tree, body);
        var all = new List<ConditionLeaf>(body);
        if (response.AutoResolveTree is { ValueKind: JsonValueKind.Object } autoResolveTree)
            Collect(autoResolveTree, all);

        if (Descriptions.Count >= CacheLimit)
            Descriptions.Clear();
        var description = new Description(body, all);
        Descriptions[key] = description;
        return description;
    }

    private static void Collect(JsonElement node, List<ConditionLeaf> leaves)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return;

        if (node.TryGetProperty("leaf_id", out _))
        {
            var kind = node.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
                ? AlertConditionTypeNames.FromWireString(k.GetString())
                : null;
            var operands = node.TryGetProperty("params", out var p) ? p.Clone() : default;
            leaves.Add(new ConditionLeaf(kind, operands));
            return;
        }

        if (node.TryGetProperty("conditions", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
                Collect(child, leaves);
        }
        if (node.TryGetProperty("child", out var only))
            Collect(only, leaves);
    }

    private readonly record struct DescriptionKey(
        AlertConditionType Type, string ConditionParams, string? AutoResolveParams);

    /// <param name="Body">The body's leaves.</param>
    /// <param name="All">The body's leaves, then the auto-resolve tree's.</param>
    private sealed record Description(IReadOnlyList<ConditionLeaf> Body, IReadOnlyList<ConditionLeaf> All);
}

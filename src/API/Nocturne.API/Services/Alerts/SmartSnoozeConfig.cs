using System.Text.Json;
using System.Text.Json.Nodes;
using Nocturne.API.Services.Alerts.Conditions;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// The <c>client_configuration.snooze</c> section of an alert rule: the single source of truth for
/// both manual snoozing (<c>AlertsController.SnoozeInstance</c>) and the sweep's smart-snooze
/// extension.
/// </summary>
/// <remarks>
/// <see cref="MaxCount"/> caps one shared <c>SnoozeCount</c>: a manual snooze and an automatic
/// extension each consume one, so the total time an instance can stay silenced is bounded no
/// matter how it was silenced.
/// <para>
/// Parsing is per field and never throws: a field of the wrong JSON kind falls back to its
/// default alone. The sweep parses every expired instance of every tenant in one pass, so one
/// malformed rule must not abort the others.
/// </para>
/// </remarks>
/// <param name="SmartSnooze">Whether an expired snooze may be extended instead of cleared.</param>
/// <param name="ExtendMinutes">Length of one automatic extension.</param>
/// <param name="MaxCount">Cap on the instance's shared snooze count.</param>
/// <param name="Conditions">
/// Optional predicate: the stored <c>conditions</c> list, as written, evaluated as
/// <c>composite{and, conditions}</c> (<see cref="AndBody"/>). Null when absent, <c>null</c> or
/// empty, and the sweep then falls back to <see cref="SmartSnoozeTrendGate"/>.
/// </param>
/// <param name="Malformed">True when some part of the section could not be read.</param>
/// <param name="ConditionsUnreadable">
/// <c>conditions</c> is neither a list nor <c>null</c>. The sweep clears the snooze rather than
/// falling back to <see cref="SmartSnoozeTrendGate"/>, which would extend on a basis the user never
/// configured.
/// </param>
internal sealed record SmartSnoozeConfig(
    bool SmartSnooze,
    int ExtendMinutes,
    int MaxCount,
    JsonElement? Conditions,
    bool Malformed = false,
    bool ConditionsUnreadable = false)
{
    /// <summary>Mirrored by <c>defaultClientConfig()</c> in the web app's <c>alerts/types.ts</c>.</summary>
    public const int DefaultMaxCount = 3;

    /// <inheritdoc cref="DefaultMaxCount"/>
    public const int DefaultExtendMinutes = 10;

    /// <summary>
    /// <paramref name="conditions"/> as the one node the sweep evaluates: <c>composite{and}</c>,
    /// named as <see cref="AndBody"/> names its body.
    /// </summary>
    public static JsonElement ConditionTree(JsonElement conditions)
    {
        var (current, children) = Composed(conditions);
        var node = new JsonObject
        {
            ["type"] = "composite",
            ["composite"] = new JsonObject { ["operator"] = "and", ["conditions"] = children },
        };
        return JsonSerializer.SerializeToElement(current ? ConditionSchema.Stamp(node) : node);
    }

    public static SmartSnoozeConfig Default { get; } =
        new(false, DefaultExtendMinutes, DefaultMaxCount, null);

    private const string SectionProperty = "snooze";
    private const string SmartSnoozeProperty = "smartSnooze";
    private const string ConditionsProperty = "conditions";

    /// <summary>
    /// The raw <see cref="Conditions"/> list, whether or not smart snooze is on. Null when there is
    /// none or the JSON is unreadable.
    /// </summary>
    public static JsonElement? StoredConditions(string? clientConfiguration)
    {
        if (string.IsNullOrWhiteSpace(clientConfiguration))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(clientConfiguration);
            return Section(doc.RootElement) is { } snooze && ConditionList(snooze) is { } conditions
                ? conditions.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether two <see cref="StoredConditions"/> lists are the same tree
    /// (<see cref="ConditionTreeEquality"/>). A rule edit that changes them prunes the rule's
    /// timers (<see cref="AlertRuleRearm.SaveAndPruneAsync"/>), and a snooze evaluation of the old
    /// list is then dropped.
    /// </summary>
    public static bool SameConditions(JsonElement? stored, JsonElement? other) =>
        ConditionTreeEquality.Same(stored?.GetRawText(), other?.GetRawText());

    /// <summary>
    /// The payload-only body of the <c>composite{and}</c> <paramref name="conditions"/> evaluate as.
    /// Each stored condition names its <see cref="ConditionSchema"/> at its own root; the composite
    /// names it when every one does, and its children drop the name, which no node below a root
    /// carries. One that does not name it leaves the composite naming none, so the engine reads it
    /// as <c>unsupported_schema</c>. The engine's own <c>validate::snooze_tree</c> composes them
    /// the same way.
    /// </summary>
    public static string AndBody(JsonElement conditions)
    {
        var (current, children) = Composed(conditions);
        var body = new JsonObject { ["operator"] = "and", ["conditions"] = children };
        return (current ? ConditionSchema.Stamp(body) : body).ToJsonString();
    }

    private static (bool Current, JsonArray Children) Composed(JsonElement conditions)
    {
        var current = true;
        var children = new JsonArray();
        foreach (var condition in conditions.EnumerateArray())
        {
            current &= ConditionSchema.Names(condition);
            var child = JsonNode.Parse(condition.GetRawText());
            children.Add(child is JsonObject node ? ConditionSchema.Unstamp(node) : child);
        }
        return (current, children);
    }

    private static JsonElement? Section(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(SectionProperty, out var snooze)
        && snooze.ValueKind == JsonValueKind.Object
            ? snooze
            : null;

    private static JsonElement? ConditionList(JsonElement snooze) =>
        snooze.TryGetProperty(ConditionsProperty, out var conditions)
        && conditions.ValueKind == JsonValueKind.Array
            ? conditions
            : null;

    public static SmartSnoozeConfig Parse(string? clientConfiguration)
    {
        if (string.IsNullOrWhiteSpace(clientConfiguration))
            return Default;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(clientConfiguration);
        }
        catch (JsonException)
        {
            return Default with { Malformed = true };
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(SectionProperty, out _))
                return Default;
            if (Section(doc.RootElement) is not { } snooze)
                return Default with { Malformed = true };

            var malformed = false;

            var smartSnooze = false;
            if (snooze.TryGetProperty(SmartSnoozeProperty, out var smartEl))
            {
                if (smartEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    smartSnooze = smartEl.GetBoolean();
                else
                    malformed = true;
            }

            var extendMinutes = ReadPositiveInt(snooze, "smartSnoozeExtendMinutes", DefaultExtendMinutes, ref malformed);
            var maxCount = ReadNonNegativeInt(snooze, "maxCount", DefaultMaxCount, ref malformed);

            JsonElement? conditions = null;
            var conditionsUnreadable = false;
            if (snooze.TryGetProperty(ConditionsProperty, out var conditionsEl)
                && conditionsEl.ValueKind != JsonValueKind.Null)
            {
                if (conditionsEl.ValueKind != JsonValueKind.Array)
                {
                    conditionsUnreadable = true;
                    malformed = true;
                }
                else if (conditionsEl.GetArrayLength() > 0)
                {
                    conditions = conditionsEl.Clone();
                }
            }

            return new SmartSnoozeConfig(
                smartSnooze, extendMinutes, maxCount, conditions, malformed, conditionsUnreadable);
        }
    }

    private static int ReadPositiveInt(JsonElement section, string name, int fallback, ref bool malformed)
    {
        if (!section.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var value) && value > 0) return value;
        malformed = true;
        return fallback;
    }

    private static int ReadNonNegativeInt(JsonElement section, string name, int fallback, ref bool malformed)
    {
        if (!section.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var value) && value >= 0) return value;
        malformed = true;
        return fallback;
    }
}

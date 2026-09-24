using System.Text.Json;
using System.Text.Json.Nodes;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;

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
/// Optional predicate, evaluated as <c>composite{and, conditions}</c>. When absent or empty the
/// sweep falls back to <see cref="SmartSnoozeTrendGate"/>.
/// </param>
/// <param name="Malformed">True when some part of the section could not be read.</param>
internal sealed record SmartSnoozeConfig(
    bool SmartSnooze,
    int ExtendMinutes,
    int MaxCount,
    IReadOnlyList<ConditionNode>? Conditions,
    bool Malformed = false)
{
    /// <summary>Mirrored by <c>defaultClientConfig()</c> in the web app's <c>alerts/types.ts</c>.</summary>
    public const int DefaultMaxCount = 3;

    /// <inheritdoc cref="DefaultMaxCount"/>
    public const int DefaultExtendMinutes = 10;

    public static SmartSnoozeConfig Default { get; } =
        new(false, DefaultExtendMinutes, DefaultMaxCount, null);

    private const string SectionProperty = "snooze";
    private const string SmartSnoozeProperty = "smartSnooze";
    private const string ConditionsProperty = "conditions";

    /// <summary>
    /// The raw <see cref="Conditions"/> the sweep evaluates: present only when
    /// <see cref="SmartSnooze"/> is on and <c>conditions</c> is a list. Null for unreadable JSON.
    /// </summary>
    public static JsonElement? EvaluatedConditions(string? clientConfiguration)
    {
        if (string.IsNullOrWhiteSpace(clientConfiguration))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(clientConfiguration);
            return Section(doc.RootElement) is { } snooze
                   && snooze.TryGetProperty(SmartSnoozeProperty, out var smart)
                   && smart.ValueKind == JsonValueKind.True
                   && ConditionList(snooze) is { } conditions
                ? conditions.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The <c>conditions</c> list in a mutable <c>client_configuration</c>, whether or not smart snooze is on.</summary>
    public static JsonArray? ConditionsNode(JsonNode? clientConfiguration) =>
        clientConfiguration is JsonObject root
        && root[SectionProperty] is JsonObject snooze
            ? snooze[ConditionsProperty] as JsonArray
            : null;

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

            IReadOnlyList<ConditionNode>? conditions = null;
            if (ConditionList(snooze) is { } conditionsEl)
            {
                try
                {
                    conditions = JsonSerializer.Deserialize<List<ConditionNode>>(
                        conditionsEl.GetRawText(), EvaluatorJson.Options);
                }
                catch (JsonException)
                {
                    // An unreadable predicate must not fall through to the trend fallback, which
                    // would extend on a basis the user never configured. A lone unknown-kind node
                    // evaluates false, so the snooze clears.
                    conditions = [new ConditionNode("unparseable")];
                    malformed = true;
                }
            }

            return new SmartSnoozeConfig(smartSnooze, extendMinutes, maxCount, conditions, malformed);
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

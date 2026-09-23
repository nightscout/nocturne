using System.Text.Json;
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
                || !doc.RootElement.TryGetProperty("snooze", out var snooze))
                return Default;
            if (snooze.ValueKind != JsonValueKind.Object)
                return Default with { Malformed = true };

            var malformed = false;

            var smartSnooze = false;
            if (snooze.TryGetProperty("smartSnooze", out var smartEl))
            {
                if (smartEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    smartSnooze = smartEl.GetBoolean();
                else
                    malformed = true;
            }

            var extendMinutes = ReadPositiveInt(snooze, "smartSnoozeExtendMinutes", DefaultExtendMinutes, ref malformed);
            var maxCount = ReadNonNegativeInt(snooze, "maxCount", DefaultMaxCount, ref malformed);

            IReadOnlyList<ConditionNode>? conditions = null;
            if (snooze.TryGetProperty("conditions", out var conditionsEl)
                && conditionsEl.ValueKind == JsonValueKind.Array)
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

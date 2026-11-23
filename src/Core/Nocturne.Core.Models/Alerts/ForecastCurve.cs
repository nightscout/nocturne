using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// A forecast series a <c>predicted</c> condition selects (docs/alerts/engine-semantics.md §3).
/// <see cref="Default"/> is the series the host has always sent, not a curve of its own; for an oref
/// uploader it is usually the <see cref="Iob"/> curve. The others are the oref curves.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ForecastCurve>))]
public enum ForecastCurve
{
    [JsonStringEnumMemberName("default")] Default,

    /// <summary>The forecast from insulin on board alone.</summary>
    [JsonStringEnumMemberName("iob")] Iob,

    /// <summary>The forecast with the carbs on board absorbed as entered.</summary>
    [JsonStringEnumMemberName("cob")] Cob,

    /// <summary>The forecast that reads a current rise as unannounced carbs.</summary>
    [JsonStringEnumMemberName("uam")] Uam,

    /// <summary>The forecast if all basal stopped now (zero temp).</summary>
    [JsonStringEnumMemberName("zt")] Zt,
}

/// <summary>
/// The loop's newest determination at or before the evaluation instant
/// (docs/alerts/engine-semantics.md §4).
/// </summary>
/// <param name="At">When the loop made it; the engine judges its freshness from here.</param>
/// <param name="EventualMgdl">The glucose the loop expected at the end of its forecast.</param>
/// <param name="CarbsRequiredGrams">
/// The carbohydrate the loop said it needed: 0 for an oref command that reports none, null for a loop
/// that never reports it or a command the host cannot read.
/// </param>
public sealed record ApsDetermination(DateTime At, decimal? EventualMgdl, decimal? CarbsRequiredGrams);

/// <summary>
/// What a true <c>predicted</c>, <c>eventual_glucose</c> or <c>carbs_required</c> condition, or a true
/// <c>threshold</c> whose limit names a fact, met its comparison with (docs/alerts/engine-semantics.md
/// §3, met by).
/// </summary>
/// <param name="LeafId">The condition's pre-order leaf id in the rule's tree.</param>
/// <param name="Kind">The condition's type.</param>
/// <param name="Curve">For <c>predicted</c>, the series that supplied the point.</param>
/// <param name="OffsetMinutes">For <c>predicted</c>, how far ahead the point is.</param>
/// <param name="Value">The value compared: mg/dL, or grams for <c>carbs_required</c>.</param>
/// <param name="Limit">For a limit from a fact, the limit compared against: the fact's value plus its offset.</param>
/// <param name="LimitFact">For a limit from a fact, the fact.</param>
/// <param name="LimitOffset">For a limit from a fact, the rule's offset to it in mg/dL; null when 0.</param>
public sealed record AlertMetPoint(
    int LeafId,
    AlertConditionType Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ForecastCurve? Curve,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? OffsetMinutes,
    decimal Value,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Limit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AlertLimitFact? LimitFact = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? LimitOffset = null);

using System.Text.Json;
using System.Text.Json.Serialization;
using Nocturne.API.Services.Alerts.Conditions;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.Alerts.Corpus;

/// <summary>
/// Conversions from the corpus wire records (<see cref="ScenarioRule"/> /
/// <see cref="ScenarioContext"/>) to the domain types the engine consumes, shared by the
/// engine-seam test harnesses so the wire-to-domain mapping exists exactly once.
/// </summary>
public static class ScenarioConversions
{
    private static readonly JsonSerializerOptions EnumWireOptions = BuildEnumWireOptions();

    private static JsonSerializerOptions BuildEnumWireOptions()
    {
        // TrendBucket carries JsonStringEnumMemberName attributes but no type-level
        // converter (the live engine never deserialises it from JSON — it arrives on the
        // SensorContext). The scenario wire format spells it in snake_case, so register
        // the converter here. StateSpanCategory/PumpModeState/GlucoseBucket have
        // type-level converters already.
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter<TrendBucket>());
        return options;
    }

    public static AlertRule ToAlertRule(ScenarioRule rule)
    {
        var conditionType = AlertConditionTypeNames.FromWireString(rule.ConditionType)
            ?? throw new InvalidOperationException(
                $"Scenario rule '{rule.Name}' has unknown condition_type '{rule.ConditionType}'");

        return new AlertRule
        {
            Id = rule.Id,
            Name = rule.Name,
            ConditionType = conditionType,
            ConditionParams = rule.ConditionParams.GetRawText(),
            HysteresisMinutes = rule.HysteresisMinutes,
            AutoResolveEnabled = rule.AutoResolveEnabled,
            AutoResolveParams = rule.AutoResolveParams?.GetRawText(),
            OnUnknown = rule.OnUnknown ?? AlertRuleOnUnknown.Hold,
            GlucoseFreshnessMinutes = rule.GlucoseFreshnessMinutes,
            EnabledAt = rule.EnabledAt,
        };
    }

    public static SensorContext ToSensorContext(ScenarioContext ctx)
    {
        return new SensorContext
        {
            LatestValue = ctx.LatestValue,
            LatestTimestamp = Utc(ctx.LatestTimestamp),
            LatestReceivedAt = Utc(ctx.LatestReceivedAt),
            TrendRate = ctx.TrendRate,
            LastReadingAt = Utc(ctx.LastReadingAt),
            TrendBucket = ParseWireEnum<TrendBucket>(ctx.TrendBucket),
            IobUnits = ctx.IobUnits,
            CobGrams = ctx.CobGrams,
            ReservoirUnits = ctx.ReservoirUnits,
            ReservoirIsLowerBound = ctx.ReservoirIsLowerBound ?? false,
            LastSiteChangeAt = Utc(ctx.LastSiteChangeAt),
            LastSensorStartAt = Utc(ctx.LastSensorStartAt),
            Predictions = ctx.Predictions?.Select(p => new PredictedGlucosePoint(p.OffsetMinutes, p.Mgdl)).ToList()
                ?? (IReadOnlyList<PredictedGlucosePoint>)Array.Empty<PredictedGlucosePoint>(),
            PredictionCurves = (ctx.PredictionCurves ?? [])
                .Select(kv => (Curve: TryParseWireEnum<ForecastCurve>(kv.Key), Points: kv.Value))
                .Where(c => c.Curve is { } curve && curve != ForecastCurve.Default)
                .ToDictionary(
                    c => c.Curve!.Value,
                    c => (IReadOnlyList<PredictedGlucosePoint>)c.Points
                        .Select(p => new PredictedGlucosePoint(p.OffsetMinutes, p.Mgdl)).ToList()),
            ApsDetermination = ctx.ApsDetermination is { } determination
                ? new ApsDetermination(
                    Utc(determination.At)!.Value, determination.EventualMgdl, determination.CarbsRequiredGrams)
                : null,
            ActiveAlerts = ctx.ActiveAlerts?.ToDictionary(
                    a => a.AlertId,
                    a => new ActiveAlertSnapshot(a.State, Utc(a.TriggeredAt)!.Value, Utc(a.AcknowledgedAt)))
                ?? new Dictionary<Guid, ActiveAlertSnapshot>(),
            LastApsCycleAt = Utc(ctx.LastApsCycleAt),
            LastApsEnactedAt = Utc(ctx.LastApsEnactedAt),
            PumpBatteryPercent = ctx.PumpBatteryPercent,
            ActiveTempBasal = ctx.ActiveTempBasal is { } tb
                ? new TempBasalSnapshot(tb.Rate, tb.ScheduledRate, Utc(tb.StartedAt)!.Value)
                : null,
            UploaderBatteryPercent = ctx.UploaderBatteryPercent,
            TransmitterVoltageA = ctx.TransmitterVoltageA,
            TransmitterVoltageB = ctx.TransmitterVoltageB,
            TransmitterResistance = ctx.TransmitterResistance,
            TransmitterDays = ctx.TransmitterDays,
            ActiveOverride = ctx.ActiveOverride is { } ov
                ? new OverrideSnapshot(Utc(ov.StartedAt)!.Value)
                : null,
            ActivePumpSuspension = ctx.ActivePumpSuspension is { } ps
                ? new PumpSuspensionSnapshot(Utc(ps.StartedAt)!.Value)
                : null,
            SensitivityRatio = ctx.SensitivityRatio,
            ActiveDoNotDisturb = ctx.ActiveDoNotDisturb is { } dnd
                ? new DoNotDisturbSnapshot(Utc(dnd.StartedAt)!.Value, dnd.Source)
                : null,
            HasEverApsCycled = ctx.HasEverApsCycled,
            HasEverPumpSnapshot = ctx.HasEverPumpSnapshot,
            HasEverUploaderSnapshot = ctx.HasEverUploaderSnapshot,
            HasEverApsSensitivity = ctx.HasEverApsSensitivity,
            GlucoseBucket = ParseWireEnum<GlucoseBucket>(ctx.GlucoseBucket),
            TargetLow = ctx.TargetLow,
            TargetHigh = ctx.TargetHigh,
            LastCarbAt = Utc(ctx.LastCarbAt),
            LastBolusAt = Utc(ctx.LastBolusAt),
            TenantTimeZoneId = ctx.TenantTimeZoneId,
            ActivePumpModes = ctx.ActivePumpModes?
                    .Select(m => new PumpModeSnapshot(m.Mode, Utc(m.StartedAt)!.Value))
                    .ToList()
                ?? [],
            ActiveStateSpans = ctx.ActiveStateSpans?.ToDictionary(
                    s => (ParseWireEnum<StateSpanCategory>(s.Category)!.Value, s.State),
                    s => new StateSpanSnapshot(
                        ParseWireEnum<StateSpanCategory>(s.Category)!.Value,
                        s.State,
                        Utc(s.StartedAt)!.Value))
                ?? new Dictionary<(StateSpanCategory, string?), StateSpanSnapshot>(),
            ActiveTrackers = ctx.ActiveTrackers?.ToDictionary(
                    t => t.TrackerDefinitionId,
                    t => new ActiveTrackerReference(Utc(t.ReferenceAt)!.Value, Utc(t.RecordedAt), t.Scheduled))
                ?? new Dictionary<Guid, ActiveTrackerReference>(),
            SleepSessionActive = ctx.SleepSessionActive,
            SensorState = ctx.SensorState is { } sensor
                ? new SensorStateSnapshot(ParseWireEnum<SensorState>(sensor.State)!.Value, Utc(sensor.Since), Utc(sensor.Until))
                : null,
        };
    }

    private static DateTime? Utc(DateTime? value) =>
        value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    private static TEnum? ParseWireEnum<TEnum>(string? wire) where TEnum : struct, Enum =>
        wire is null ? null : JsonSerializer.Deserialize<TEnum>($"\"{wire}\"", EnumWireOptions);

    /// <summary>The member <paramref name="wire"/> names, or null for a name the host has none for.</summary>
    private static TEnum? TryParseWireEnum<TEnum>(string wire) where TEnum : struct, Enum
    {
        try
        {
            return ParseWireEnum<TEnum>(wire);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

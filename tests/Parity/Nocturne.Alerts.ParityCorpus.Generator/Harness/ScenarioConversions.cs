using System.Text.Json;
using System.Text.Json.Serialization;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.Alerts.ParityCorpus.Generator.Harness;

/// <summary>
/// Conversions from the corpus wire records (<see cref="ScenarioRule"/> /
/// <see cref="ScenarioContext"/>) to the domain types the engines consume. Shared by the
/// generator's <see cref="ScenarioRunner"/> and the engine-seam test harnesses so the
/// wire-to-domain mapping exists exactly once.
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
            ConfirmationReadings = rule.ConfirmationReadings,
            HysteresisMinutes = rule.HysteresisMinutes,
            AutoResolveEnabled = rule.AutoResolveEnabled,
            AutoResolveParams = rule.AutoResolveParams?.GetRawText(),
        };
    }

    public static SensorContext ToSensorContext(ScenarioContext ctx)
    {
        return new SensorContext
        {
            LatestValue = ctx.LatestValue,
            LatestTimestamp = Utc(ctx.LatestTimestamp),
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
            ActiveOverride = ctx.ActiveOverride is { } ov
                ? new OverrideSnapshot(Utc(ov.StartedAt)!.Value, null, null, null)
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
            LastCarbAt = Utc(ctx.LastCarbAt),
            LastBolusAt = Utc(ctx.LastBolusAt),
            TenantTimeZoneId = ctx.TenantTimeZoneId,
            ActivePumpState = ctx.ActivePumpState is { } pumpState
                ? new PumpStateSnapshot(
                    ParseWireEnum<PumpModeState>(pumpState.Mode)!.Value,
                    Utc(pumpState.StartedAt)!.Value)
                : null,
            ActiveStateSpans = ctx.ActiveStateSpans?.ToDictionary(
                    s => (ParseWireEnum<StateSpanCategory>(s.Category)!.Value, s.State),
                    s => new StateSpanSnapshot(
                        ParseWireEnum<StateSpanCategory>(s.Category)!.Value,
                        s.State,
                        Utc(s.StartedAt)!.Value))
                ?? new Dictionary<(StateSpanCategory, string?), StateSpanSnapshot>(),
            ActiveTrackers = ctx.ActiveTrackers?.ToDictionary(
                    t => t.TrackerDefinitionId,
                    t => Utc(t.ReferenceAt)!.Value)
                ?? new Dictionary<Guid, DateTime>(),
            SleepSessionActive = ctx.SleepSessionActive,
        };
    }

    private static DateTime? Utc(DateTime? value) =>
        value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    private static TEnum? ParseWireEnum<TEnum>(string? wire) where TEnum : struct, Enum =>
        wire is null ? null : JsonSerializer.Deserialize<TEnum>($"\"{wire}\"", EnumWireOptions);
}

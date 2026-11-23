using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// An alert opened by a forecast or loop condition names what met it: the curve, the point and its
/// value (docs/alerts/engine-semantics.md §3, met by).
/// </summary>
public class AlertPayloadsMetByTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

    private static readonly AlertRuleSnapshot Rule = new(Guid.NewGuid(), Tenant, "low soon", AlertConditionType.Predicted,
        """{"operator":"<","value":70,"within_minutes":30,"min_of":["iob","cob"]}""", AlertRuleSeverity.Warning, "{}", 0,
        AutoResolveEnabled: false, AutoResolveParams: null);

    private static readonly SensorContext Sensor = new()
    {
        LatestValue = 95m, LatestTimestamp = Now, TrendRate = null, LastReadingAt = Now,
    };

    private static AlertPayload Build(IReadOnlyList<AlertMetPoint>? metBy) =>
        AlertPayloads.Build(Rule, Sensor, Tenant, Guid.NewGuid(), Guid.NewGuid(), null, 1, Now, metBy);

    [Fact]
    public void The_payload_names_the_curve_and_point_that_met_the_rule()
    {
        var met = new AlertMetPoint(0, AlertConditionType.Predicted, ForecastCurve.Cob, 20, 65m);

        var payload = Build([met]);

        payload.MetBy.Should().Equal(met);
        var chat = JsonSerializer.SerializeToElement(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var point = chat.GetProperty("metBy")[0];
        point.GetProperty("kind").GetString().Should().Be("predicted");
        point.GetProperty("curve").GetString().Should().Be("cob");
        point.GetProperty("offsetMinutes").GetInt32().Should().Be(20);
        point.GetProperty("value").GetDecimal().Should().Be(65m);
    }

    /// <summary>A loop value has no curve or offset, and the payload leaves them out.</summary>
    [Fact]
    public void A_loop_value_carries_its_value_alone()
    {
        var payload = Build([new AlertMetPoint(1, AlertConditionType.CarbsRequired, null, null, 12m)]);

        var point = JsonSerializer.SerializeToElement(payload).GetProperty("MetBy")[0];
        point.TryGetProperty("Curve", out _).Should().BeFalse();
        point.TryGetProperty("OffsetMinutes", out _).Should().BeFalse();
        point.GetProperty("Kind").GetString().Should().Be("carbs_required");
    }

    /// <summary>A rule met by no such condition, and a dispatch with no evaluation, carry no member at all.</summary>
    [Fact]
    public void Nothing_met_leaves_the_member_out()
    {
        JsonSerializer.SerializeToElement(Build([])).TryGetProperty("MetBy", out _).Should().BeFalse();
        JsonSerializer.SerializeToElement(Build(null)).TryGetProperty("MetBy", out _).Should().BeFalse();
    }

    /// <summary>
    /// The engine's wire names map to the host's members; an entry of a kind, curve or limit fact this
    /// host has no member for comes from a newer engine and is dropped, not guessed at.
    /// </summary>
    [Fact]
    public void The_engines_met_by_maps_to_typed_points()
    {
        var points = RustEnvelopeMapper.MetByFromWire(
        [
            new RustMetPoint(0, "predicted", "zt", 25, 62.5m),
            new RustMetPoint(1, "eventual_glucose", null, null, 75m),
            new RustMetPoint(2, "predicted", "ar2", 10, 50m),
            new RustMetPoint(3, "future_leaf", null, null, 1m),
            new RustMetPoint(4, "threshold", null, null, 66m, 70m, "target_low", -10m),
            new RustMetPoint(5, "threshold", null, null, 76m, 80m, "max_iob"),
        ]);

        points.Should().Equal(
            new AlertMetPoint(0, AlertConditionType.Predicted, ForecastCurve.Zt, 25, 62.5m),
            new AlertMetPoint(1, AlertConditionType.EventualGlucose, null, null, 75m),
            new AlertMetPoint(4, AlertConditionType.Threshold, null, null, 66m, 70m, AlertLimitFact.TargetLow, -10m));
        RustEnvelopeMapper.MetByFromWire(null).Should().BeEmpty();
    }
}

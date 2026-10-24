using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Nocturne.API.Controllers;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Hubs;

/// <summary>
/// Pins the JSON each Home Assistant hub message carries, serialized as the hub serializes it, so a
/// change to a payload shape is a deliberate one that its clients (the Home Assistant integration)
/// are told about.
/// </summary>
[Trait("Category", "Unit")]
public class HomeAssistantHubMessageShapeTests
{
    private static readonly JsonSerializerOptions HubJson = new JsonHubProtocolOptions().PayloadSerializerOptions;

    private static readonly Guid TenantId = Guid.Parse("6f1c2d3e-4a5b-4c6d-8e7f-9a0b1c2d3e4f");
    private static readonly Guid ExcursionId = Guid.Parse("0b9f4e52-1c7a-4d3e-a6f8-2e5d7c9b1a34");
    private static readonly Guid OtherExcursionId = Guid.Parse("c3a1d8f0-7b2e-4f59-9d46-5a8e0b6c2d17");
    private static readonly Guid RuleId = Guid.Parse("a41e7c90-3d5b-4b28-8f6a-9c2d1e0b7f53");
    private static readonly DateTime At = new(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc);

    private static JsonElement Wire(object payload) =>
        JsonSerializer.SerializeToElement(payload, payload.GetType(), HubJson);

    private static IEnumerable<string> PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name);

    [Fact]
    public void GlucoseReading_CarriesSensorGlucoseReadings()
    {
        var readingId = Guid.NewGuid();
        var json = Wire(new GlucoseReadingEvent
        {
            Readings =
            [
                new SensorGlucose
                {
                    Id = readingId,
                    Timestamp = At,
                    Mgdl = 120,
                    Direction = GlucoseDirection.Flat,
                    TrendRate = 0.1,
                    DataSource = "dexcom-connector",
                },
            ],
        });

        PropertyNames(json).Should().Equal("readings");
        var reading = json.GetProperty("readings").EnumerateArray().Should().ContainSingle().Which;
        reading.GetProperty("id").GetGuid().Should().Be(readingId);
        reading.GetProperty("mgdl").GetDouble().Should().Be(120);
        reading.GetProperty("direction").GetString().Should().Be("Flat");
        reading.GetProperty("trendRate").GetDouble().Should().Be(0.1);
        reading.GetProperty("dataSource").GetString().Should().Be("dexcom-connector");
        reading.TryGetProperty("timestamp", out _).Should().BeTrue();
        reading.TryGetProperty("sgv", out _).Should().BeFalse("the reading is a v4 SensorGlucose, not a legacy entry");
    }

    [Fact]
    public void AlertResolved_CarriesTheExcursionRuleAndReason()
    {
        var json = Wire(new AlertResolvedEvent
        {
            TenantId = TenantId,
            ExcursionId = ExcursionId,
            AlertRuleId = RuleId,
            RuleName = "Urgent Low",
            ResolvedAt = At,
            Reason = ExcursionCloseReason.RuleDisabled,
        });

        PropertyNames(json).Should().BeEquivalentTo(
            "tenantId", "excursionId", "alertRuleId", "ruleName", "resolvedAt", "reason");
        json.GetProperty("excursionId").GetGuid().Should().Be(ExcursionId);
        json.GetProperty("alertRuleId").GetGuid().Should().Be(RuleId);
        json.GetProperty("ruleName").GetString().Should().Be("Urgent Low");
        json.GetProperty("reason").GetString().Should().Be("rule-disabled");
    }

    [Fact]
    public void AlertAcknowledged_CarriesEveryAcknowledgedExcursion()
    {
        var json = Wire(new AlertAcknowledgedEvent
        {
            TenantId = TenantId,
            ExcursionIds = [ExcursionId, OtherExcursionId],
            AcknowledgedBy = "homeassistant:test-hub",
            AcknowledgedAt = At,
        });

        PropertyNames(json).Should().BeEquivalentTo(
            "tenantId", "excursionIds", "acknowledgedBy", "acknowledgedAt");
        json.GetProperty("excursionIds").EnumerateArray().Select(e => e.GetGuid())
            .Should().Equal(ExcursionId, OtherExcursionId);
        json.GetProperty("acknowledgedBy").GetString().Should().Be("homeassistant:test-hub");
    }

    [Fact]
    public void AlertDispatch_CarriesTheAlertPayload()
    {
        var json = Wire(new AlertPayload
        {
            AlertType = AlertConditionType.Threshold,
            RuleName = "Urgent Low",
            GlucoseValue = 55,
            Trend = null,
            TrendRate = -2.0m,
            ReadingTimestamp = At,
            ExcursionId = ExcursionId,
            InstanceId = RuleId,
            TenantId = TenantId,
            SubjectName = "Test User",
            ActiveExcursionCount = 1,
            Severity = AlertRuleSeverity.Critical,
        });

        PropertyNames(json).Should().BeEquivalentTo(
            "alertType", "ruleName", "glucoseValue", "trend", "trendRate", "readingTimestamp",
            "excursionId", "instanceId", "tenantId", "subjectName", "activeExcursionCount", "severity");
        json.GetProperty("alertType").GetString().Should().Be("threshold");
        json.GetProperty("severity").GetString().Should().Be("critical");
    }

    [Fact]
    public void Metadata_NamesEveryMessageTheHubSends()
    {
        typeof(HomeAssistantHubMessagesMetadata).GetProperties()
            .ToDictionary(p => p.Name, p => Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType)
            .Should().BeEquivalentTo(new Dictionary<string, Type>
            {
                ["GlucoseReading"] = typeof(GlucoseReadingEvent),
                ["AlertDispatch"] = typeof(AlertPayload),
                ["AlertResolved"] = typeof(AlertResolvedEvent),
                ["AlertAcknowledged"] = typeof(AlertAcknowledgedEvent),
                ["AcknowledgeOutcomes"] = typeof(AlertAcknowledgementOutcome[]),
                ["Description"] = typeof(string),
            });
    }

    [Fact]
    public async Task GlucoseBroadcaster_SendsTheReadingsOldestFirst()
    {
        var broadcast = new Mock<ISignalRBroadcastService>();
        GlucoseReadingEvent? sent = null;
        broadcast.Setup(b => b.BroadcastHomeAssistantGlucoseAsync(It.IsAny<GlucoseReadingEvent>()))
            .Callback<GlucoseReadingEvent>(e => sent = e)
            .Returns(Task.CompletedTask);
        var newer = new SensorGlucose { Timestamp = At.AddMinutes(5), Mgdl = 130 };
        var older = new SensorGlucose { Timestamp = At, Mgdl = 120 };

        await new HomeAssistantGlucoseBroadcaster(broadcast.Object).BroadcastAsync([newer, older]);

        sent!.Readings.Should().Equal(older, newer);
    }

    [Fact]
    public async Task GlucoseBroadcaster_SendsNothingForNoReadings()
    {
        var broadcast = new Mock<ISignalRBroadcastService>();

        await new HomeAssistantGlucoseBroadcaster(broadcast.Object).BroadcastAsync([]);

        broadcast.VerifyNoOtherCalls();
    }
}

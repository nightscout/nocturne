using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

public class RustEnvelopeMapperTests
{
    private static SensorContext BuildContext() => new()
    {
        LatestValue = 100,
        LatestTimestamp = new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc),
        TrendRate = -1.5m,
        LastReadingAt = new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc),
    };

    private static AlertRule Rule(bool autoResolveEnabled, string? autoResolveParams) => new()
    {
        Id = Guid.Parse("00000000-0000-0000-0000-0000000000aa"),
        ConditionType = AlertConditionType.Threshold,
        ConditionParams = """{"direction":"below","value":70}""",
        AutoResolveEnabled = autoResolveEnabled,
        AutoResolveParams = autoResolveParams,
    };

    private const string AutoResolveNode = """{"type":"threshold","threshold":{"direction":"above","value":90}}""";

    [Fact]
    public void BuildRule_sends_auto_resolve_params_when_enabled()
    {
        var wire = RustEnvelopeMapper.BuildRule(Rule(autoResolveEnabled: true, AutoResolveNode));

        wire.AutoResolveParams!.Value.GetProperty("type").GetString().Should().Be("threshold");
    }

    [Fact]
    public void BuildRule_omits_auto_resolve_params_when_disabled()
    {
        RustEnvelopeMapper.BuildRule(Rule(autoResolveEnabled: false, AutoResolveNode))
            .AutoResolveParams.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildRule_sends_null_for_unparseable_auto_resolve_params(bool enabled)
    {
        RustEnvelopeMapper.BuildRule(Rule(enabled, "{ not json"))
            .AutoResolveParams.Should().BeNull();
    }

    [Fact]
    public void BuildContext_sends_a_windows_tenant_zone_as_iana()
    {
        var context = BuildContext() with { TenantTimeZoneId = "AUS Eastern Standard Time" };

        RustEnvelopeMapper.BuildContext(context).GetProperty("tenant_time_zone_id").GetString()
            .Should().Be("Australia/Sydney");
    }

    [Fact]
    public void BuildRule_sends_windows_rule_zones_as_iana()
    {
        var rule = new AlertRule
        {
            ConditionType = AlertConditionType.TimeOfDay,
            ConditionParams = """{"from":"22:00","to":"06:00","timezone":"AUS Eastern Standard Time"}""",
        };

        RustEnvelopeMapper.BuildRule(rule).ConditionParams.GetProperty("timezone").GetString()
            .Should().Be("Australia/Sydney");
    }

    [Fact]
    public void BuildContext_memoises_the_element_per_context_instance()
    {
        var context = BuildContext();

        var first = RustEnvelopeMapper.BuildContextBoxed(context);
        var second = RustEnvelopeMapper.BuildContextBoxed(context);

        second.Should().BeSameAs(first,
            "the orchestrator evaluates N rules against one context instance; the wire element must be built once");
    }

    [Fact]
    public void BuildContext_rebuilds_for_a_different_context_instance()
    {
        var context = BuildContext();
        var other = context with { };

        var first = RustEnvelopeMapper.BuildContextBoxed(context);
        var second = RustEnvelopeMapper.BuildContextBoxed(other);

        second.Should().NotBeSameAs(first, "the memo is keyed by reference identity, not value equality");
        JsonSerializer.Serialize((JsonElement)second).Should().Be(
            JsonSerializer.Serialize((JsonElement)first),
            "equal contexts still produce identical wire JSON");
    }
}

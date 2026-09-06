using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.Core.Models;
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

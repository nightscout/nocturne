using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Nocturne.Core.Models.Tests;

[Trait("Category", "Unit")]
public class StatusResponseServerTimeTests
{
    private const long Instant = 1_790_000_000_000;

    private static readonly DateTime UtcValue = DateTimeOffset.FromUnixTimeMilliseconds(Instant).UtcDateTime;

    [Fact]
    public void AnUnsetServerTime_SerialisesWithoutThrowing()
    {
        var act = () => JsonSerializer.Serialize(new StatusResponse());

        act.Should().NotThrow();
    }

    [Fact]
    public void AUtcServerTime_SerialisesItsEpoch()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new StatusResponse { ServerTime = UtcValue }));

        json.RootElement.GetProperty("serverTimeEpoch").GetInt64().Should().Be(Instant);
    }

    [Fact]
    public void AnUnspecifiedServerTime_IsReadAsUtc()
    {
        var response = new StatusResponse { ServerTime = DateTime.SpecifyKind(UtcValue, DateTimeKind.Unspecified) };

        response.ServerTimeEpoch.Should().Be(Instant);
    }
}

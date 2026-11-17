using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Controllers.V4.Analytics;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

public class CreateLabHbA1cResultRequestBindingTests
{
    private static CreateLabHbA1cResultRequest Bind(string measuredAt) =>
        JsonSerializer.Deserialize<CreateLabHbA1cResultRequest>(
            $$"""{"measuredAt":{{measuredAt}},"value":7.0}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        )!;

    [Theory]
    [InlineData("\"2025-01-04\"")]
    [InlineData("\"2025-01-04T00:00:00.000Z\"")]
    [InlineData("\"2025-01-04T23:59:59.999Z\"")]
    [InlineData("\"2025-01-05T08:00:00+13:00\"")]
    [InlineData("\"2025-01-03T19:00:00-05:00\"")]
    public void A_day_and_an_instant_on_that_utc_day_bind_to_the_same_day(string measuredAt)
    {
        Bind(measuredAt).MeasuredAt.Should().Be(new DateOnly(2025, 1, 4));
    }

    [Theory]
    [InlineData("\"tomorrow\"")]
    [InlineData("\"2025-13-45\"")]
    [InlineData("\"\"")]
    [InlineData("20250104")]
    [InlineData("null")]
    public void A_value_that_is_neither_a_day_nor_an_instant_is_refused(string measuredAt)
    {
        var bind = () => Bind(measuredAt);

        bind.Should().Throw<JsonException>();
    }
}

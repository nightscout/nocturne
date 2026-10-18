using System.Text.Json;
using FluentAssertions;
using Nocturne.Connectors.Glooko.Xt;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Xt;

public class GlookoXtRecordDeserializationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Numbers_AreReadWhetherQuotedOrNot()
    {
        const string json = """
            {"id":"13548461316","recorded_at":"2026-06-17T20:15:30.000Z","glycemia":"6.7","carbs":45,"fast_insulin":"","duration":30}
            """;

        var record = JsonSerializer.Deserialize<GlookoXtRecord>(json, Json)!;

        record.Id.Should().Be(13548461316);
        record.Glycemia.Should().Be(6.7);
        record.Carbs.Should().Be(45);
        record.FastInsulin.Should().BeNull("an empty string is an absent value, not a failed record");
        record.Duration.Should().Be(30);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("\"true\"", true)]
    [InlineData("\"1\"", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("null", null)]
    [InlineData("\"auto\"", null)]
    public void Flags_AreReadInEveryShapeTheServerWrites(string literal, bool? expected)
    {
        var record = JsonSerializer.Deserialize<GlookoXtRecord>($$"""{"recorded_at":"2026-06-17T20:15:30.000Z","pump_stop":{{literal}}}""", Json)!;

        record.PumpStop.Should().Be(expected);
    }

    [Fact]
    public void UnknownFields_LandInExtra()
    {
        var record = JsonSerializer.Deserialize<GlookoXtRecord>("""{"recorded_at":"2026-06-17T20:15:30.000Z","str_pictures":"[]","day":"2026-06-17"}""", Json)!;

        record.Extra.Should().ContainKeys("str_pictures", "day");
    }

    [Fact]
    public void ParseCollectedData_AcceptsObjectArrayAndStringShapes()
    {
        const string body = """{"collected_data":[{"id":1,"recorded_at":"2026-06-17T20:15:30.000Z","carbs":10}]}""";

        var asObject = GlookoXtSocketDataClient.ParseCollectedData(JsonDocument.Parse(body).RootElement);
        var asArray = GlookoXtSocketDataClient.ParseCollectedData(JsonDocument.Parse("""[{"id":2,"recorded_at":"2026-06-17T20:15:30.000Z"}]""").RootElement);
        var asString = GlookoXtSocketDataClient.ParseCollectedData(JsonDocument.Parse(JsonSerializer.Serialize(body)).RootElement);

        asObject.Should().ContainSingle().Which.Id.Should().Be(1);
        asArray.Should().ContainSingle().Which.Id.Should().Be(2);
        asString.Should().ContainSingle().Which.Carbs.Should().Be(10);
    }

    [Fact]
    public void ParseCollectedData_ThrowsOnServerError()
    {
        var element = JsonDocument.Parse("""{"error":"WRONG_INPUT"}""").RootElement;

        var act = () => GlookoXtSocketDataClient.ParseCollectedData(element);

        act.Should().Throw<InvalidOperationException>().WithMessage("*WRONG_INPUT*");
    }

    [Fact]
    public void FormatUtc_MatchesTheAppsRecordedAtShape()
    {
        var when = new DateTime(2026, 6, 17, 20, 15, 30, 123, DateTimeKind.Utc);

        GlookoXtSocketDataClient.FormatUtc(when).Should().Be("2026-06-17T20:15:30.123Z");
    }
}

using System.Text.Json;
using FluentAssertions;
using Nocturne.Connectors.Glooko.Xt;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Xt;

public class GlookoXtAccountTests
{
    [Fact]
    public void TryParse_ReadsUnitAndZoneOnly_FromTheStringWrappedAnswer()
    {
        const string body = """{"profile":{"id":1,"email":"x@example.com","blood_glucose_unit":"mg/dl","timezone":"Europe/Paris","target_from":70,"target_to":139},"success":"OK"}""";
        var element = JsonDocument.Parse(JsonSerializer.Serialize(body)).RootElement;

        var account = GlookoXtAccount.TryParse(element)!;

        account.Unit.Should().Be(GlookoXtGlucoseUnitSetting.MgDl);
        account.TimeZoneId.Should().Be("Europe/Paris");
        account.TargetFrom.Should().Be(70);
        account.TargetTo.Should().Be(139);
    }

    [Theory]
    [InlineData("mmol/l", GlookoXtGlucoseUnitSetting.Mmol)]
    [InlineData("MG/DL", GlookoXtGlucoseUnitSetting.MgDl)]
    [InlineData("cups", null)]
    public void Unit_ReadsTheProfileSpelling(string spelling, GlookoXtGlucoseUnitSetting? expected)
    {
        new GlookoXtAccount { BloodGlucoseUnit = spelling }.Unit.Should().Be(expected);
    }

    [Fact]
    public void TryParse_WithoutAProfile_IsNull()
    {
        GlookoXtAccount.TryParse(JsonDocument.Parse("""{"error":"WRONG_INPUT"}""").RootElement).Should().BeNull();
    }
}

using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Services.Migration;
using Xunit;

namespace Nocturne.API.Tests.Services.Migration;

[Trait("Category", "Unit")]
public class NightscoutDisplaySettingsTests
{
    private const string MmolStatus = """{"status":"ok","settings":{"units":"mmol"}}""";

    [Fact]
    public void ReadsTheDisplayUnitsAndTheCurrentProfilesUnitsAndTimezone()
    {
        const string profiles = """
            [{"defaultProfile":"Day","startDate":"2026-01-01T00:00:00.000Z","units":"mg/dl",
              "store":{"Day":{"units":"mmol/L","timezone":"Europe/London"}}}]
            """;

        NightscoutDisplaySettings.Parse(MmolStatus, profiles)
            .Should().Be(new NightscoutDisplaySettings("mmol", "mmol", "Europe/London"));
    }

    // Nightscout shows glucose in DISPLAY_UNITS whatever unit the profile's numbers are written in.
    [Fact]
    public void KeepsAProfileInOtherUnitsDistinctFromTheDisplayUnits()
    {
        const string profiles = """
            [{"defaultProfile":"Default","startDate":"2026-01-01T00:00:00.000Z",
              "store":{"Default":{"units":"mg/dL","timezone":"America/Denver"}}}]
            """;

        NightscoutDisplaySettings.Parse(MmolStatus, profiles)
            .Should().Be(new NightscoutDisplaySettings("mmol", "mg/dl", "America/Denver"));
    }

    [Fact]
    public void ReadsTheNewestProfileDocument()
    {
        const string profiles = """
            [{"defaultProfile":"Old","startDate":"2024-05-01T00:00:00.000Z","store":{"Old":{"units":"mg/dl","timezone":"Asia/Tokyo"}}},
             {"defaultProfile":"New","startDate":"2026-05-01T00:00:00.000Z","store":{"New":{"units":"mmol","timezone":"Europe/Oslo"}}}]
            """;

        NightscoutDisplaySettings.Parse(MmolStatus, profiles)
            .Should().Be(new NightscoutDisplaySettings("mmol", "mmol", "Europe/Oslo"));
    }

    [Fact]
    public void FallsBackToTheDocumentsUnitsWhenTheStoreStatesNone()
    {
        const string profiles = """
            [{"defaultProfile":"Default","units":"mmol","store":{"Default":{"timezone":" "}}}]
            """;

        NightscoutDisplaySettings.Parse(MmolStatus, profiles)
            .Should().Be(new NightscoutDisplaySettings("mmol", "mmol", null));
    }

    [Fact]
    public void LeavesOutWhatTheSourceDoesNotStateInARecognisedForm()
    {
        NightscoutDisplaySettings.Parse("""{"settings":{"units":"furlongs"}}""", "[]")
            .Should().Be(new NightscoutDisplaySettings(null, null, null));
    }

    // The PUT that saves the step accepts only a zone it can resolve, so a pre-fill must be one.
    [Theory]
    [InlineData("ETC/GMT-2", "Etc/GMT-2")]
    [InlineData(" Europe/London ", "Europe/London")]
    [InlineData("AUS Eastern Standard Time", "Australia/Sydney")]
    [InlineData("Middle/Earth", null)]
    public void CanonicalisesTheProfileTimezoneOrDropsIt(string stored, string? expected)
    {
        var profiles = JsonSerializer.Serialize(new[]
        {
            new { defaultProfile = "Default", store = new { Default = new { units = "mmol", timezone = stored } } },
        });

        NightscoutDisplaySettings.Parse(MmolStatus, profiles).ProfileTimezone.Should().Be(expected);
    }

    [Fact]
    public void ReadsAProfileWhoseMillsIsNotANumber()
    {
        const string profiles = """
            [{"defaultProfile":"Default","mills":"soon","store":{"Default":{"units":"mmol","timezone":"Europe/Oslo"}}}]
            """;

        NightscoutDisplaySettings.Parse(MmolStatus, profiles)
            .Should().Be(new NightscoutDisplaySettings("mmol", "mmol", "Europe/Oslo"));
    }

    [Fact]
    public void RefusesABodyThatIsNotJson()
    {
        var parse = () => NightscoutDisplaySettings.Parse("<h1>STATUS OK</h1>", "[]");

        parse.Should().Throw<JsonException>();
    }
}

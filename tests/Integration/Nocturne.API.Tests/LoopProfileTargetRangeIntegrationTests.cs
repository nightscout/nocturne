using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// A Loop profile's target ranges are served as the <c>[min, max]</c> arrays Loop uploaded on every
/// legacy read, whichever form the upload used, while the v4 settings keep their object form.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class LoopProfileTargetRangeIntegrationTests : ApiIntegrationTestBase
{
    public LoopProfileTargetRangeIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private static readonly DateTimeOffset Start =
        DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds());

    private static object[] Schedule(double value) => [new { time = "00:00", value, timeAsSeconds = 0 }];

    private static object LoopProfile(string id, object presetRange, object preMealRange) => new
    {
        _id = id,
        defaultProfile = "Default",
        startDate = Start.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        units = "mg/dL",
        enteredBy = "Loop",
        store = new Dictionary<string, object>
        {
            ["Default"] = new
            {
                dia = 6,
                timezone = "UTC",
                units = "mg/dL",
                basal = Schedule(0.8),
                carbratio = Schedule(10),
                sens = Schedule(45),
                target_low = Schedule(100),
                target_high = Schedule(110),
            },
        },
        loopSettings = new
        {
            dosingEnabled = true,
            preMealTargetRange = preMealRange,
            overridePresets = new object[]
            {
                new { name = "Running", symbol = "R", duration = 3600, targetRange = presetRange },
                new { name = "Plain", symbol = "P", duration = 600 },
            },
        },
    };

    private async Task<JsonObject> LoopSettingsOfAsync(string path)
    {
        var response = await CreateAuthenticatedClient().GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var profile = body is JsonObject { } obj && obj["result"] is { } result ? result : body;
        var document = profile is JsonArray array ? array[0]! : profile;
        return document["loopSettings"]!.AsObject();
    }

    private static void AssertArrays(JsonObject settings)
    {
        var presets = settings["overridePresets"]!.AsArray();
        presets[0]!["targetRange"]!.ToJsonString().Should().Be("[100,110]");
        presets[1]!.AsObject().ContainsKey("targetRange").Should().BeFalse();
        settings["preMealTargetRange"]!.ToJsonString().Should().Be("[80,90]");
    }

    [Theory]
    [InlineData("array")]
    [InlineData("object")]
    public async Task EveryLegacyReadServesTheArrays_WhicheverFormWasUploaded(string form)
    {
        object Range(int min, int max) => form == "array" ? new[] { min, max } : new { minValue = min, maxValue = max };
        var id = "5f8d0c1e8a7b4c3d9e5f03" + (form == "array" ? "01" : "02");
        var client = CreateAuthenticatedClient();

        (await client.PostAsJsonAsync("/api/v1/profile", LoopProfile(id, Range(100, 110), Range(80, 90))))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        AssertArrays(await LoopSettingsOfAsync("/api/v1/profile"));
        AssertArrays(await LoopSettingsOfAsync("/api/v1/profiles.json?count=1"));
        AssertArrays(await LoopSettingsOfAsync("/api/v1/profile/current"));
        AssertArrays(await LoopSettingsOfAsync("/api/v3/profile"));
    }

    [Fact]
    public async Task TheV4SettingsKeepTheObjectForm()
    {
        var id = "5f8d0c1e8a7b4c3d9e5f0303";
        var client = CreateAuthenticatedClient();
        (await client.PostAsJsonAsync("/api/v1/profile", LoopProfile(id, new[] { 100, 110 }, new[] { 80, 90 })))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var versions = await client.GetFromJsonAsync<JsonArray>("/api/v4/profile/settings/by-name/Default");

        var loop = versions!.OfType<JsonObject>().First()["loopSettings"]!.AsObject();
        loop["preMealTargetRange"]!["minValue"]!.GetValue<double>().Should().Be(80);
        loop["overridePresets"]![0]!["targetRange"]!["maxValue"]!.GetValue<double>().Should().Be(110);
    }
}

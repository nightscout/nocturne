using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// AAPS reads its profile switches and effective profile switches back through the v1 and v3
/// treatment reads and drops any that lack a field it requires (<c>TreatmentMapper.kt</c> in AAPS
/// core/nssdk). These read the wire JSON those endpoints serve.
/// </summary>
[Collection(ApiIntegrationShards.One)]
[Trait("Category", "Integration")]
public class AapsProfileSwitchReadbackIntegrationTests : ApiIntegrationTestBase
{
    public AapsProfileSwitchReadbackIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private const string ProfileJson =
        """{"units":"mmol","dia":5,"timezone":"UTC","sens":[{"time":"00:00","timeAsSeconds":0,"value":2.0}],"carbratio":[{"time":"00:00","timeAsSeconds":0,"value":9.1}],"basal":[{"time":"00:00","timeAsSeconds":0,"value":0.8}],"target_low":[{"time":"00:00","timeAsSeconds":0,"value":5.5}],"target_high":[{"time":"00:00","timeAsSeconds":0,"value":5.5}]}""";

    private static readonly string[] EffectiveSwitchFields =
    [
        "profileJson", "originalProfileName", "originalCustomizedName", "originalTimeshift",
        "originalPercentage", "originalDuration", "originalEnd",
    ];

    private static long MinutesAgo(int minutes) => DateTimeOffset.UtcNow.AddMinutes(-minutes).ToUnixTimeMilliseconds();

    private static object EffectiveSwitchV1(long mills) => new
    {
        eventType = "Note",
        created_at = DateTimeOffset.FromUnixTimeMilliseconds(mills).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        enteredBy = "openaps://AndroidAPS",
        isValid = true,
        notes = "Rhys (110%,1h)",
        profileJson = ProfileJson,
        originalProfileName = "Rhys",
        originalCustomizedName = "Rhys (110%,1h)",
        originalTimeshift = 3_600_000,
        originalPercentage = 110,
        originalDuration = 0,
        originalEnd = mills,
        pumpId = 4102,
        pumpType = "OMNIPOD_DASH",
        pumpSerial = "33013206",
    };

    private static object ProfileSwitchV3(long mills) => new
    {
        eventType = "Profile Switch",
        date = mills,
        utcOffset = 0,
        app = "AAPS",
        device = "AAPS",
        isValid = true,
        profile = "Rhys (110%,1h)",
        originalProfileName = "Rhys",
        originalDuration = 3_600_000,
        duration = 60,
        durationInMilliseconds = 3_600_000,
        timeshift = 3_600_000,
        percentage = 110,
        profileJson = ProfileJson,
        pumpId = 4103,
        pumpType = "OMNIPOD_DASH",
        pumpSerial = "33013206",
    };

    private static async Task<JsonArray> GetArrayAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
    }

    private static async Task<JsonArray> GetV3ResultAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["result"]!.AsArray();
    }

    private static async Task PostV3Async(HttpClient client, object treatment)
    {
        var post = await client.PostAsJsonAsync("/api/v3/treatments", treatment);
        post.IsSuccessStatusCode.Should().BeTrue(await post.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_effective_profile_switch_is_served_every_field_AAPS_requires_on_v1_and_v3()
    {
        var client = CreateAuthenticatedClient();
        var mills = MinutesAgo(30);
        var post = await client.PostAsJsonAsync("/api/v1/treatments", new[] { EffectiveSwitchV1(mills) });
        post.StatusCode.Should().Be(HttpStatusCode.OK, await post.Content.ReadAsStringAsync());

        var v1 = (await GetArrayAsync(client, "/api/v1/treatments")).Should().ContainSingle().Subject!.AsObject();
        var v3 = (await GetV3ResultAsync(client, "/api/v3/treatments")).Should().ContainSingle().Subject!.AsObject();

        foreach (var served in new[] { v1, v3 })
        {
            served["eventType"]!.GetValue<string>().Should().Be("Note");
            foreach (var field in EffectiveSwitchFields)
                served[field].Should().NotBeNull($"AAPS drops an effective profile switch without {field}");
            served["profileJson"]!.GetValue<string>().Should().Be(ProfileJson);
            served["originalTimeshift"]!.GetValue<long>().Should().Be(3_600_000);
            served["originalDuration"]!.GetValue<long>().Should().Be(0);
            served["originalEnd"]!.GetValue<long>().Should().Be(mills);
            served["pumpId"]!.GetValue<long>().Should().Be(4102);
            served["pumpType"]!.GetValue<string>().Should().Be("OMNIPOD_DASH");
            served["pumpSerial"]!.GetValue<string>().Should().Be("33013206");
        }
    }

    [Fact]
    public async Task An_AAPS_v3_profile_switch_is_served_its_length_shift_and_pump_record_as_uploaded()
    {
        var client = CreateAuthenticatedClient();
        await PostV3Async(client, ProfileSwitchV3(MinutesAgo(30)));

        var v3 = (await GetV3ResultAsync(client, "/api/v3/treatments")).Should().ContainSingle().Subject!.AsObject();
        v3["eventType"]!.GetValue<string>().Should().Be("Profile Switch");
        v3["duration"]!.GetValue<double>().Should().Be(60);
        v3["durationInMilliseconds"]!.GetValue<long>().Should().Be(3_600_000);
        v3["originalDuration"]!.GetValue<long>().Should().Be(3_600_000);
        v3["timeshift"]!.GetValue<double>().Should().Be(3_600_000, "AAPS reads the shift back in milliseconds");
        v3["percentage"]!.GetValue<double>().Should().Be(110);
        v3["profileJson"]!.GetValue<string>().Should().Be(ProfileJson);
        v3["app"]!.GetValue<string>().Should().Be("AAPS");
        v3["pumpId"]!.GetValue<long>().Should().Be(4103);
        v3["pumpType"]!.GetValue<string>().Should().Be("OMNIPOD_DASH");
        v3["pumpSerial"]!.GetValue<string>().Should().Be("33013206");

        var v1 = (await GetArrayAsync(client, "/api/v1/treatments")).Should().ContainSingle().Subject!.AsObject();
        v1["timeshift"]!.GetValue<double>().Should().Be(3_600_000);
        v1["profileJson"]!.GetValue<string>().Should().Be(ProfileJson);
    }

    [Fact]
    public async Task A_profile_switch_snapshot_is_not_listed_as_a_profile()
    {
        var client = CreateAuthenticatedClient();
        var mills = MinutesAgo(30);
        await PostV3Async(client, ProfileSwitchV3(mills));
        var snapshot = Uri.EscapeDataString($"Rhys (110%,1h)@@@@@{mills}");
        (await GetArrayAsync(client, $"/api/v4/profile/settings/by-name/{snapshot}"))
            .Should().BeEmpty("the switch's inline profile is its activation's snapshot, not a stored profile");

        foreach (var url in new[]
                 {
                     "/api/v4/profile/summary", "/api/v4/profile/settings", "/api/v4/profile/records",
                     "/api/v1/profile", "/api/v3/profile", "/api/v3/profile/history/0",
                 })
        {
            var response = await client.GetAsync(url);
            response.StatusCode.Should().Be(HttpStatusCode.OK, url);
            (await response.Content.ReadAsStringAsync()).Should().NotContain("@@@@@", url);
        }
    }

    [Fact]
    public async Task A_note_rewritten_as_an_effective_profile_switch_is_served_once_as_the_switch()
    {
        var client = CreateAuthenticatedClient();
        var mills = MinutesAgo(30);
        var uploadedId = "6500000000000000000000e1";
        var note = new JsonObject
        {
            ["_id"] = uploadedId,
            ["eventType"] = "Note",
            ["created_at"] = DateTimeOffset.FromUnixTimeMilliseconds(mills).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["enteredBy"] = "openaps://AndroidAPS",
            ["notes"] = "Rhys (110%,1h)",
        };
        var post = await client.PostAsJsonAsync("/api/v1/treatments", new[] { note });
        post.StatusCode.Should().Be(HttpStatusCode.OK, await post.Content.ReadAsStringAsync());

        var effective = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(EffectiveSwitchV1(mills)))!.AsObject();
        effective["_id"] = uploadedId;
        var put = await client.PutAsJsonAsync("/api/v1/treatments", effective);
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());

        var served = (await GetArrayAsync(client, "/api/v1/treatments")).Should().ContainSingle().Subject!;
        served["originalProfileName"]!.GetValue<string>().Should().Be("Rhys");
        (await GetV3ResultAsync(client, "/api/v3/treatments")).Should().ContainSingle();
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// A mmol profile's ISF and targets are stored in mg/dL and served back on every legacy read in the
/// units it was uploaded in, so a client that reads it and uploads it again stores the same values,
/// on real PostgreSQL.
/// </summary>
[Collection(ApiIntegrationShards.One)]
[Trait("Category", "Integration")]
public class MmolProfileUnitsIntegrationTests : ApiIntegrationTestBase
{
    private const string DocumentId = "5f8d0c1e8a7b4c3d9e5f1836";

    public MmolProfileUnitsIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private static object[] Values(params (string Time, double Value)[] entries) =>
        entries.Select(e => (object)new { time = e.Time, value = e.Value }).ToArray();

    /// <summary>
    /// "Default" in <c>mmol</c> as AAPS uploads it, "Night" in <c>mmol/L</c> as Loop does, "Bare" naming no
    /// units of its own under a mmol document, and "Mgdl" in mg/dL.
    /// </summary>
    private static object Document(DateTimeOffset startDate) => new
    {
        _id = DocumentId,
        defaultProfile = "Default",
        startDate = startDate.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        mills = startDate.ToUnixTimeMilliseconds(),
        units = "mmol",
        store = new Dictionary<string, object>
        {
            ["Default"] = new
            {
                dia = 5, timezone = "UTC", units = "mmol",
                basal = Values(("00:00", 0.8), ("06:00", 1.1)),
                carbratio = Values(("00:00", 9.1), ("06:00", 10)),
                sens = Values(("00:00", 3.5), ("06:00", 3.2)),
                target_low = Values(("00:00", 5.0), ("06:00", 6.0)),
                target_high = Values(("00:00", 5.5), ("06:00", 7.0)),
            },
            ["Night"] = new
            {
                dia = 5, timezone = "UTC", units = "mmol/L",
                basal = Values(("00:00", 0.7)),
                carbratio = Values(("00:00", 12)),
                sens = Values(("00:00", 2.8)),
                target_low = Values(("00:00", 4.5)),
                target_high = Values(("00:00", 5.0)),
            },
            ["Bare"] = new
            {
                dia = 5, timezone = "UTC",
                basal = Values(("00:00", 0.5)),
                carbratio = Values(("00:00", 15)),
                sens = Values(("00:00", 4.0)),
                target_low = Values(("00:00", 5.2)),
                target_high = Values(("00:00", 6.3)),
            },
            ["Mgdl"] = new
            {
                dia = 5, timezone = "UTC", units = "mg/dl",
                basal = Values(("00:00", 0.9)),
                carbratio = Values(("00:00", 8)),
                sens = Values(("00:00", 45)),
                target_low = Values(("00:00", 100)),
                target_high = Values(("00:00", 120)),
            },
        },
    };

    /// <summary>Each store's ISF, low and high targets in the units it was uploaded in.</summary>
    private static readonly Dictionary<string, (double[] Sens, double[] Low, double[] High, double[] Basal, double[] CarbRatio)>
        Uploaded = new()
        {
            ["Default"] = ([3.5, 3.2], [5.0, 6.0], [5.5, 7.0], [0.8, 1.1], [9.1, 10]),
            ["Night"] = ([2.8], [4.5], [5.0], [0.7], [12]),
            ["Bare"] = ([4.0], [5.2], [6.3], [0.5], [15]),
            ["Mgdl"] = ([45], [100], [120], [0.9], [8]),
        };

    /// <summary>The same, in the mg/dL the V4 schedules hold.</summary>
    private static readonly Dictionary<string, (double[] Sens, double[] Low, double[] High)> StoredMgdl = new()
    {
        ["Default"] = ([63, 58], [90, 108], [99, 126]),
        ["Night"] = ([50], [81], [90]),
        ["Bare"] = ([72], [94], [114]),
        ["Mgdl"] = ([45], [100], [120]),
    };

    private static async Task<JsonNode> GetAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return body is JsonObject o && o.ContainsKey("result") ? o["result"]! : body;
    }

    private async Task<HttpClient> SeedAsync()
    {
        var client = CreateAuthenticatedClient();
        (await client.PostAsJsonAsync("/api/v1/profile", Document(DateTimeOffset.UtcNow.AddDays(-3))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        return client;
    }

    private static double[] ValuesOf(JsonNode? entries) =>
        entries!.AsArray().Select(e => e!["value"]!.GetValue<double>()).ToArray();

    private static void ShouldBeAsUploaded(JsonNode document, string surface)
    {
        var store = document["store"]!.AsObject();
        store.Select(s => s.Key).Should().BeEquivalentTo(Uploaded.Keys, surface);

        foreach (var (name, expected) in Uploaded)
        {
            var entry = store[name]!;
            var because = $"{surface} store {name}";
            ValuesOf(entry["sens"]).Should().Equal(expected.Sens, because);
            ValuesOf(entry["target_low"]).Should().Equal(expected.Low, because);
            ValuesOf(entry["target_high"]).Should().Equal(expected.High, because);
            ValuesOf(entry["basal"]).Should().Equal(expected.Basal, because);
            ValuesOf(entry["carbratio"]).Should().Equal(expected.CarbRatio, because);
        }

        store["Default"]!["units"]!.GetValue<string>().Should().Be("mmol", surface);
        store["Night"]!["units"]!.GetValue<string>().Should().Be("mmol/L", surface);
        store["Mgdl"]!["units"]!.GetValue<string>().Should().Be("mg/dl", surface);
    }

    private static async Task ShouldBeStoredInMgdlAsync(HttpClient client)
    {
        foreach (var (name, expected) in StoredMgdl)
        {
            var sensitivity = (await GetAsync(client, $"/api/v4/profile/sensitivity/{name}")).AsArray()
                .Should().ContainSingle().Subject!;
            ValuesOf(sensitivity["entries"]).Should().Equal(expected.Sens, $"V4 ISF of {name}");

            var targets = (await GetAsync(client, $"/api/v4/profile/target-range/{name}")).AsArray()
                .Should().ContainSingle().Subject!["entries"]!.AsArray();
            targets.Select(e => e!["low"]!.GetValue<double>()).Should().Equal(expected.Low, $"V4 low target of {name}");
            targets.Select(e => e!["high"]!.GetValue<double>()).Should().Equal(expected.High, $"V4 high target of {name}");
        }
    }

    [Fact]
    public async Task EveryLegacyProfileRead_ServesIsfAndTargetsInTheUploadedUnits()
    {
        var client = await SeedAsync();

        ShouldBeAsUploaded((await GetAsync(client, "/api/v1/profile")).AsArray().Single()!, "v1 /profile");
        ShouldBeAsUploaded((await GetAsync(client, "/api/v1/profiles")).AsArray().Single()!, "v1 /profiles");
        ShouldBeAsUploaded(await GetAsync(client, "/api/v1/profile/current"), "v1 /profile/current");
        ShouldBeAsUploaded((await GetAsync(client, $"/api/v1/profile/{DocumentId}")).AsArray().Single()!, "v1 /profile/{id}");

        var v3 = (await GetAsync(client, "/api/v3/profile?limit=10")).AsArray().Single()!;
        ShouldBeAsUploaded(v3, "v3 search");
        ShouldBeAsUploaded(await GetAsync(client, $"/api/v3/profile/{v3["identifier"]!.GetValue<string>()}"), "v3 by id");
        ShouldBeAsUploaded((await GetAsync(client, "/api/v3/profile/history/0?limit=10")).AsArray().Single()!, "v3 history");

        ShouldBeAsUploaded((await GetAsync(client, "/api/v2/ddata"))["profiles"]!.AsArray().Single()!, "v2 ddata");
        ShouldBeAsUploaded((await GetAsync(client, "/api/v4/profile/records"))["data"]!.AsArray().Single()!, "v4 records");
    }

    [Fact]
    public async Task V2Summary_ServesTheActiveStoreInItsUploadedUnits()
    {
        var client = await SeedAsync();

        var profile = (await GetAsync(client, "/api/v2/summary"))["profile"]!;

        ValuesOf(profile["sens"]).Should().Equal(3.5, 3.2);
        ValuesOf(profile["target_low"]).Should().Equal(5.0, 6.0);
        ValuesOf(profile["target_high"]).Should().Equal(5.5, 7.0);
        profile["units"]!.GetValue<string>().Should().Be("mmol");
    }

    [Fact]
    public async Task V4Schedules_HoldIsfAndTargetsInMgdl()
    {
        var client = await SeedAsync();

        await ShouldBeStoredInMgdlAsync(client);
    }

    [Fact]
    public async Task V4Resolvers_AnswerTheDefaultStoreInMgdl()
    {
        await SeedAsync();
        var yesterday = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero);
        long At(int hour) => yesterday.AddHours(hour).ToUnixTimeMilliseconds();

        var answers = await WithTenantScopeAsync(async services =>
        {
            var sensitivity = services.GetRequiredService<ISensitivityResolver>();
            var targets = services.GetRequiredService<ITargetRangeResolver>();
            return (
                Early: (await sensitivity.GetSensitivityAsync(At(3)), await targets.GetBGTargetRangeAsync(At(3))),
                Late: (await sensitivity.GetSensitivityAsync(At(7)), await targets.GetBGTargetRangeAsync(At(7))));
        });

        answers.Early.Should().Be((63, (90, 99)));
        answers.Late.Should().Be((58, (108, 126)));
    }

    [Fact]
    public async Task ReadingAndReuploadingOverV1_LeavesEveryValueWhereItWas()
    {
        var client = await SeedAsync();

        for (var cycle = 0; cycle < 3; cycle++)
        {
            var current = await GetAsync(client, "/api/v1/profile/current");
            (await client.PutAsync("/api/v1/profile", JsonContent.Create(current)))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }

        ShouldBeAsUploaded(await GetAsync(client, "/api/v1/profile/current"), "v1 after three re-uploads");
        await ShouldBeStoredInMgdlAsync(client);
    }

    [Fact]
    public async Task ReadingAndReuploadingOverV3_LeavesEveryValueWhereItWas()
    {
        var client = await SeedAsync();
        var identifier = (await GetAsync(client, "/api/v3/profile?limit=10"))
            .AsArray().Single()!["identifier"]!.GetValue<string>();

        for (var cycle = 0; cycle < 3; cycle++)
        {
            var read = (await GetAsync(client, $"/api/v3/profile/{identifier}")).AsObject();
            read.Remove("identifier");
            read.Remove("srvModified");
            read.Remove("srvCreated");
            var put = await client.PutAsync($"/api/v3/profile/{identifier}", JsonContent.Create(read));
            put.IsSuccessStatusCode.Should().BeTrue(await put.Content.ReadAsStringAsync());
        }

        ShouldBeAsUploaded(await GetAsync(client, $"/api/v3/profile/{identifier}"), "v3 after three re-uploads");
        await ShouldBeStoredInMgdlAsync(client);
    }

    [Fact]
    public async Task AProfileSwitchCarryingAMmolProfile_IsServedAsUploaded_AndActivatesItInMgdl()
    {
        var client = CreateAuthenticatedClient();
        const string profileJson =
            """{"units":"mmol","dia":5,"timezone":"UTC","sens":[{"time":"00:00","timeAsSeconds":0,"value":3.5}],"carbratio":[{"time":"00:00","timeAsSeconds":0,"value":9.1}],"basal":[{"time":"00:00","timeAsSeconds":0,"value":0.8}],"target_low":[{"time":"00:00","timeAsSeconds":0,"value":5.0}],"target_high":[{"time":"00:00","timeAsSeconds":0,"value":5.5}]}""";
        var at = DateTimeOffset.UtcNow.AddMinutes(-5);
        (await client.PostAsJsonAsync("/api/v1/treatments", new[]
        {
            new
            {
                eventType = "Profile Switch",
                profile = "Exercise",
                profileJson,
                duration = 0,
                created_at = at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                mills = at.ToUnixTimeMilliseconds(),
                enteredBy = "AndroidAPS",
            },
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var served = (await GetAsync(client, "/api/v1/treatments?find[eventType]=Profile%20Switch")).AsArray()
            .Should().ContainSingle().Subject!;
        served["profileJson"]!.GetValue<string>().Should().Be(profileJson);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (sensitivity, targets) = await WithTenantScopeAsync(async services => (
            await services.GetRequiredService<ISensitivityResolver>().GetSensitivityAsync(now),
            await services.GetRequiredService<ITargetRangeResolver>().GetBGTargetRangeAsync(now)));
        sensitivity.Should().Be(63);
        targets.Should().Be((90, 99));
    }
}

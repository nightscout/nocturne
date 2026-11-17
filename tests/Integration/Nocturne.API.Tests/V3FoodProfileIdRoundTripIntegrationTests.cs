using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// A v3 food or profile is read, replaced and deleted by the <c>identifier</c> v3 lists it under, and
/// by the raw <c>_id</c> v3 served before it was coerced, and each reaches the one stored record.
/// </summary>
[Collection(ApiIntegrationShards.One)]
[Trait("Category", "Integration")]
public class V3FoodProfileIdRoundTripIntegrationTests : ApiIntegrationTestBase
{
    private const string LegacyId = "5f8d0c1e8a7b4c3d9e5f0001";
    private const string UppercaseLegacyId = "5F8D0C1E8A7B4C3D9E5F0002";

    public V3FoodProfileIdRoundTripIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public enum Seed { LegacyId, UppercaseLegacyId, UuidKeyed, V3Created }

    public enum Address { Identifier, StoredId }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static async Task<JsonElement> ResultAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.ValueKind == JsonValueKind.Object && body.TryGetProperty("result", out var result)
            ? result
            : body;
    }

    private static async Task<JsonElement[]> ListAsync(HttpClient client, string collection)
    {
        var response = await client.GetAsync($"/api/v3/{collection}?limit=100");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await ResultAsync(response)).EnumerateArray().ToArray();
    }

    private static async Task AssertDeletedAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Be("{\"status\":200}");
    }

    private static string AddressOf(JsonElement listed, string storedId, Address address) =>
        address == Address.Identifier ? listed.GetProperty("identifier").GetString()! : storedId;

    private async Task<string> SeedFoodAsync(HttpClient client, Seed seed)
    {
        const string name = "Apple";
        switch (seed)
        {
            case Seed.LegacyId:
            case Seed.UppercaseLegacyId:
            case Seed.UuidKeyed:
                var id = seed switch
                {
                    Seed.LegacyId => LegacyId,
                    Seed.UppercaseLegacyId => UppercaseLegacyId,
                    _ => Guid.CreateVersion7().ToString(),
                };
                (await client.PostAsJsonAsync("/api/v1/food", new { _id = id, type = "food", name, carbs = 10 }))
                    .IsSuccessStatusCode.Should().BeTrue();
                break;
            case Seed.V3Created:
                (await client.PostAsJsonAsync("/api/v3/food", new { date = Now, type = "food", name, carbs = 10 }))
                    .IsSuccessStatusCode.Should().BeTrue();
                break;
        }

        var stored = (await client.GetFromJsonAsync<JsonElement[]>("/api/v1/food"))!;
        stored.Should().ContainSingle();
        return stored[0].GetProperty("_id").GetString()!;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Food_IsReadReplacedAndDeletedByTheIdItWasServed(Seed seed, Address address)
    {
        var client = CreateAuthenticatedClient();
        var storedId = await SeedFoodAsync(client, seed);
        var listed = (await ListAsync(client, "food")).Should().ContainSingle().Subject;
        var identifier = listed.GetProperty("identifier").GetString();
        var id = AddressOf(listed, storedId, address);

        var read = await client.GetAsync($"/api/v3/food/{id}");
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ResultAsync(read)).GetProperty("identifier").GetString().Should().Be(identifier);

        var put = await client.PutAsJsonAsync(
            $"/api/v3/food/{id}", new { date = Now, type = "food", name = "Apple", carbs = 25 });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var replaced = (await ListAsync(client, "food")).Should().ContainSingle().Subject;
        replaced.GetProperty("identifier").GetString().Should().Be(identifier);
        replaced.GetProperty("carbs").GetDouble().Should().Be(25);

        await AssertDeletedAsync(await client.DeleteAsync($"/api/v3/food/{id}"));
        (await ListAsync(client, "food")).Should().BeEmpty();
        (await client.GetAsync($"/api/v3/food/{identifier}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(Seed.LegacyId)]
    [InlineData(Seed.UppercaseLegacyId)]
    [InlineData(Seed.UuidKeyed)]
    [InlineData(Seed.V3Created)]
    public async Task Food_IsFilteredByTheIdItWasServed(Seed seed)
    {
        var client = CreateAuthenticatedClient();
        await SeedFoodAsync(client, seed);
        var identifier = (await ListAsync(client, "food")).Single().GetProperty("identifier").GetString();

        var response = await client.GetAsync($"/api/v3/food?_id$eq={identifier}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ResultAsync(response)).EnumerateArray().Should().ContainSingle();
    }

    private static object StoreData(double dia) => new
    {
        dia,
        carbratio = new[] { new { time = "00:00", value = 10, timeAsSeconds = 0 } },
        sens = new[] { new { time = "00:00", value = 50, timeAsSeconds = 0 } },
        basal = new[] { new { time = "00:00", value = 1.0, timeAsSeconds = 0 } },
        target_low = new[] { new { time = "00:00", value = 80, timeAsSeconds = 0 } },
        target_high = new[] { new { time = "00:00", value = 120, timeAsSeconds = 0 } },
        units = "mg/dl",
        timezone = "UTC",
    };

    private static object ProfileDocument(string storeName, double dia, string? id = null)
    {
        var startDate = DateTimeOffset.UtcNow.AddHours(-1).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var store = new Dictionary<string, object> { [storeName] = StoreData(dia) };
        return id is null
            ? new { date = Now, defaultProfile = storeName, startDate, store }
            : new { _id = id, date = Now, defaultProfile = storeName, startDate, store };
    }

    private const string StoreName = "Default";

    private async Task<string> SeedProfileAsync(HttpClient client, Seed seed)
    {
        switch (seed)
        {
            case Seed.LegacyId:
                (await client.PostAsJsonAsync("/api/v1/profile", ProfileDocument(StoreName, 3, LegacyId)))
                    .StatusCode.Should().Be(HttpStatusCode.OK);
                break;
            case Seed.UuidKeyed:
                (await client.PostAsJsonAsync(
                        "/api/v4/profile/settings",
                        new
                        {
                            timestamp = DateTimeOffset.UtcNow.AddHours(-1).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                            profileName = StoreName,
                            dia = 3,
                        }))
                    .StatusCode.Should().Be(HttpStatusCode.Created);
                break;
            case Seed.V3Created:
                (await client.PostAsJsonAsync("/api/v3/profile", ProfileDocument(StoreName, 3)))
                    .StatusCode.Should().Be(HttpStatusCode.Created);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(seed));
        }

        var stored = (await client.GetFromJsonAsync<JsonElement[]>("/api/v1/profile?count=100"))!;
        stored.Should().ContainSingle();
        return stored[0].GetProperty("_id").GetString()!;
    }

    [Theory]
    [MemberData(nameof(ProfileCases))]
    public async Task Profile_IsReadReplacedAndDeletedByTheIdItWasServed(Seed seed, Address address)
    {
        var client = CreateAuthenticatedClient();
        var storedId = await SeedProfileAsync(client, seed);
        var listed = (await ListAsync(client, "profile")).Should().ContainSingle().Subject;
        var identifier = listed.GetProperty("identifier").GetString();
        identifier.Should().Be(MongoObjectId.Coerce(storedId));
        var id = AddressOf(listed, storedId, address);

        var read = await client.GetAsync($"/api/v3/profile/{id}");
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ResultAsync(read)).GetProperty("identifier").GetString().Should().Be(identifier);

        var put = await client.PutAsJsonAsync($"/api/v3/profile/{id}", ProfileDocument(StoreName, 6));
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var replaced = (await ListAsync(client, "profile")).Should().ContainSingle().Subject;
        replaced.GetProperty("identifier").GetString().Should().Be(identifier);
        replaced.GetProperty("store").GetProperty(StoreName).GetProperty("dia").GetDouble().Should().Be(6);

        await AssertDeletedAsync(await client.DeleteAsync($"/api/v3/profile/{id}"));
        (await ListAsync(client, "profile")).Should().BeEmpty();
        (await client.GetAsync($"/api/v3/profile/{identifier}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    public static TheoryData<Seed, Address> Cases()
    {
        var data = new TheoryData<Seed, Address>();
        foreach (var seed in Enum.GetValues<Seed>())
        foreach (var address in Enum.GetValues<Address>())
            data.Add(seed, address);
        return data;
    }

    public static TheoryData<Seed, Address> ProfileCases()
    {
        var data = new TheoryData<Seed, Address>();
        foreach (var seed in new[] { Seed.LegacyId, Seed.UuidKeyed, Seed.V3Created })
        foreach (var address in Enum.GetValues<Address>())
            data.Add(seed, address);
        return data;
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// v1 PUT /profile, DELETE /profile/{id} and DELETE /profile?keep=N on real PostgreSQL.
/// </summary>
[Trait("Category", "Integration")]
public class ProfileV1WriteIntegrationTests : ApiIntegrationTestBase
{
    private const string StoredId = "5f8d0c1e8a7b4c3d9e5f0001";

    public ProfileV1WriteIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private static object ProfileDocument(string id, string storeName, double dia, DateTimeOffset startDate) => new
    {
        _id = id,
        defaultProfile = storeName,
        startDate = startDate.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        mills = startDate.ToUnixTimeMilliseconds(),
        store = new Dictionary<string, object> { [storeName] = StoreData(dia) },
    };

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

    private static string DocumentId(int i) => $"5f8d0c1e8a7b4c3d9e5f{i:x4}";

    private static async Task PostDocumentAsync(HttpClient client, object document) =>
        (await client.PostAsJsonAsync("/api/v1/profile", document)).StatusCode.Should().Be(HttpStatusCode.OK);

    private static async Task<string> CreateV4TherapySettingsAsync(HttpClient client, string profileName, DateTimeOffset timestamp)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v4/profile/settings",
            new { timestamp = timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), profileName, dia = 4 });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task<JsonElement[]> ListedWithIdAsync(HttpClient client, string id)
    {
        var response = await client.GetAsync("/api/v1/profile?count=100");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement[]>())!
            .Where(p => p.GetProperty("_id").GetString() == id)
            .ToArray();
    }

    [Fact]
    public async Task Put_ReplacesTheStoredDocument()
    {
        var client = CreateAuthenticatedClient();
        var startDate = DateTimeOffset.UtcNow.AddHours(-1);
        (await client.PostAsJsonAsync("/api/v1/profile", ProfileDocument(StoredId, "Default", 3, startDate)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var put = await client.PutAsJsonAsync("/api/v1/profile", ProfileDocument(StoredId, "Default", 5, startDate));

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = await put.Content.ReadFromJsonAsync<JsonElement>();
        saved.GetProperty("_id").GetString().Should().Be(StoredId);
        var stored = await ListedWithIdAsync(client, StoredId);
        stored.Should().ContainSingle();
        stored[0].GetProperty("store").GetProperty("Default").GetProperty("dia").GetDouble().Should().Be(5);
    }

    [Fact]
    public async Task Put_DropsTheStoresTheBodyNoLongerCarries()
    {
        var client = CreateAuthenticatedClient();
        var startDate = DateTimeOffset.UtcNow.AddHours(-1);
        await PostDocumentAsync(client, ProfileDocument(StoredId, "Default", 3, startDate));

        var put = await client.PutAsJsonAsync("/api/v1/profile", ProfileDocument(StoredId, "Weekday", 3, startDate));

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await ListedWithIdAsync(client, StoredId);
        stored.Should().ContainSingle();
        stored[0].GetProperty("store").EnumerateObject().Select(p => p.Name).Should().Equal("Weekday");
    }

    [Fact]
    public async Task Put_WithoutAStore_IsRefused_AndLeavesTheDocument()
    {
        var client = CreateAuthenticatedClient();
        await PostDocumentAsync(client, ProfileDocument(StoredId, "Default", 3, DateTimeOffset.UtcNow));

        var put = await client.PutAsJsonAsync(
            "/api/v1/profile", new { _id = StoredId, defaultProfile = "Default", store = new { } });

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ListedWithIdAsync(client, StoredId)).Should().ContainSingle();
    }

    [Fact]
    public async Task Put_OfADeletedId_IsAConflict_AndStoresNothing()
    {
        var client = CreateAuthenticatedClient();
        var document = ProfileDocument(StoredId, "Default", 3, DateTimeOffset.UtcNow);
        await PostDocumentAsync(client, document);
        (await client.DeleteAsync($"/api/v1/profile/{StoredId}")).StatusCode.Should().Be(HttpStatusCode.OK);

        var put = await client.PutAsJsonAsync("/api/v1/profile", document);

        put.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ListedWithIdAsync(client, StoredId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Put_OfAProfileServedUnderARowId_UpdatesThatRow()
    {
        var client = CreateAuthenticatedClient();
        var rowId = await CreateV4TherapySettingsAsync(client, "V4Only", DateTimeOffset.UtcNow.AddHours(-1));
        (await ListedWithIdAsync(client, rowId)).Should().ContainSingle();

        var put = await client.PutAsJsonAsync(
            "/api/v1/profile", ProfileDocument(rowId, "V4Only", 7, DateTimeOffset.UtcNow.AddHours(-1)));

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await ListedWithIdAsync(client, rowId);
        stored.Should().ContainSingle();
        stored[0].GetProperty("store").GetProperty("V4Only").GetProperty("dia").GetDouble().Should().Be(7);
        var row = await client.GetFromJsonAsync<JsonElement>($"/api/v4/profile/settings/{rowId}");
        row.GetProperty("dia").GetDouble().Should().Be(7);
    }

    [Fact]
    public async Task DeleteById_OfAProfileServedUnderARowId_DeletesIt()
    {
        var client = CreateAuthenticatedClient();
        var rowId = await CreateV4TherapySettingsAsync(client, "V4Only", DateTimeOffset.UtcNow);

        var delete = await client.DeleteAsync($"/api/v1/profile/{rowId}");

        delete.StatusCode.Should().Be(HttpStatusCode.OK);
        (await delete.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("n").GetInt64().Should().Be(1);
        (await ListedWithIdAsync(client, rowId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Put_UnknownId_Inserts()
    {
        var client = CreateAuthenticatedClient();

        var put = await client.PutAsJsonAsync(
            "/api/v1/profile", ProfileDocument(StoredId, "Default", 4, DateTimeOffset.UtcNow));

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ListedWithIdAsync(client, StoredId)).Should().ContainSingle();
    }

    [Fact]
    public async Task Put_WithoutAnId_IsRefused()
    {
        var client = CreateAuthenticatedClient();

        var put = await client.PutAsJsonAsync("/api/v1/profile", new { defaultProfile = "Default" });

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteById_RemovesTheDocument_AndARepeatAnswersZero()
    {
        var client = CreateAuthenticatedClient();
        (await client.PostAsJsonAsync("/api/v1/profile", ProfileDocument(StoredId, "Default", 3, DateTimeOffset.UtcNow)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var first = await client.DeleteAsync($"/api/v1/profile/{StoredId}");
        var repeat = await client.DeleteAsync($"/api/v1/profile/{StoredId}");

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("n").GetInt64().Should().Be(1);
        repeat.StatusCode.Should().Be(HttpStatusCode.OK);
        (await repeat.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deletedCount").GetInt64().Should().Be(0);
        (await ListedWithIdAsync(client, StoredId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Prune_KeepsTheNewestDocuments()
    {
        var client = CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;
        var ids = Enumerable.Range(0, 12).Select(DocumentId).ToList();
        for (var i = 0; i < ids.Count; i++)
            await PostDocumentAsync(client, ProfileDocument(ids[i], "Default", 3, now.AddDays(i - ids.Count)));

        var prune = await client.DeleteAsync("/api/v1/profile?keep=10");

        prune.StatusCode.Should().Be(HttpStatusCode.OK);
        (await prune.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("n").GetInt64().Should().Be(2);
        (await ListedWithIdAsync(client, ids[0])).Should().BeEmpty();
        (await ListedWithIdAsync(client, ids[1])).Should().BeEmpty();
        (await ListedWithIdAsync(client, ids[2])).Should().ContainSingle();
        (await ListedWithIdAsync(client, ids[^1])).Should().ContainSingle();
    }

    [Fact]
    public async Task Prune_RanksProfileDocumentsOnly_NotProfileSwitchSnapshots()
    {
        var client = CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 11; i++)
            await PostDocumentAsync(client, ProfileDocument(DocumentId(i), "Default", 3, now.AddDays(i - 30)));

        var switches = Enumerable.Range(0, 10).Select(i => new
        {
            eventType = "Profile Switch",
            profile = "Default",
            profileJson = JsonSerializer.Serialize(StoreData(4)),
            created_at = now.AddMinutes(-i).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            mills = now.AddMinutes(-i).ToUnixTimeMilliseconds(),
            enteredBy = "AndroidAPS",
        }).ToArray();
        (await client.PostAsJsonAsync("/api/v1/treatments", switches)).StatusCode.Should().Be(HttpStatusCode.OK);

        var prune = await client.DeleteAsync("/api/v1/profile?keep=10");

        prune.StatusCode.Should().Be(HttpStatusCode.OK);
        (await prune.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("n").GetInt64().Should().Be(1);
        (await ListedWithIdAsync(client, DocumentId(0))).Should().BeEmpty();
        for (var i = 1; i < 11; i++)
            (await ListedWithIdAsync(client, DocumentId(i))).Should().ContainSingle($"document {i} is among the newest 10");
    }

    [Fact]
    public async Task Prune_DeletesAProfileServedUnderARowId()
    {
        var client = CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;
        var rowId = await CreateV4TherapySettingsAsync(client, "V4Only", now.AddDays(-60));
        for (var i = 0; i < 10; i++)
            await PostDocumentAsync(client, ProfileDocument(DocumentId(i), "Default", 3, now.AddDays(i - 20)));

        var prune = await client.DeleteAsync("/api/v1/profile?keep=10");

        prune.StatusCode.Should().Be(HttpStatusCode.OK);
        (await prune.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("n").GetInt64().Should().Be(1);
        (await ListedWithIdAsync(client, rowId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Prune_KeepWithoutANumber_IsRefused()
    {
        var client = CreateAuthenticatedClient();

        var prune = await client.DeleteAsync("/api/v1/profile?keep=all");

        prune.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

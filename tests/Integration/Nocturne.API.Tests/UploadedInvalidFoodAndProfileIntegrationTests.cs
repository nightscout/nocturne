using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// A food or a profile document uploaded with <c>isValid: false</c> is stored deleted, as Nightscout
/// stores it: no search serves it, v3 history serves it as deleted, and an upload of its id without
/// <c>isValid</c> brings it back. Under the id of a live document it deletes that document.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class UploadedInvalidFoodAndProfileIntegrationTests : ApiIntegrationTestBase
{
    public UploadedInvalidFoodAndProfileIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private readonly DateTimeOffset _at = DateTimeOffset.UtcNow.AddHours(-1);

    private JsonObject Document(string collection, string? id, bool? isValid = null, double value = 12)
    {
        var document = collection == "food"
            ? new JsonObject
            {
                ["type"] = "food",
                ["name"] = "Apple",
                ["carbs"] = value,
                ["date"] = _at.ToUnixTimeMilliseconds(),
            }
            : new JsonObject
            {
                ["defaultProfile"] = "Default",
                ["startDate"] = _at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                ["mills"] = _at.ToUnixTimeMilliseconds(),
                ["store"] = new JsonObject
                {
                    ["Default"] = new JsonObject
                    {
                        ["dia"] = value,
                        ["carbratio"] = Schedule(10),
                        ["sens"] = Schedule(50),
                        ["basal"] = Schedule(1.0),
                        ["target_low"] = Schedule(80),
                        ["target_high"] = Schedule(120),
                        ["units"] = "mg/dl",
                        ["timezone"] = "UTC",
                    },
                },
            };
        if (id is not null)
            document["_id"] = id;
        if (isValid is { } valid)
            document["isValid"] = valid;
        return document;

        static JsonArray Schedule(double value) =>
            [new JsonObject { ["time"] = "00:00", ["value"] = value, ["timeAsSeconds"] = 0 }];
    }

    private static double ValueOf(string collection, JsonNode document) => collection == "food"
        ? document["carbs"]!.GetValue<double>()
        : document["store"]!["Default"]!["dia"]!.GetValue<double>();

    private static string NewId() => Guid.NewGuid().ToString("N")[..24];

    private static async Task UploadAsync(HttpClient client, string collection, JsonObject document, bool v3)
    {
        var response = v3
            ? await client.PostAsJsonAsync($"/api/v3/{collection}", document)
            : await client.PostAsJsonAsync($"/api/v1/{collection}", new JsonArray(document.DeepClone()));
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonNode> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    /// <summary>The ids v1 search and v3 search serve.</summary>
    private sealed record Live(string[] V1, string[] V3)
    {
        public static Live None { get; } = new([], []);
    }

    private static async Task<Live> LiveAsync(HttpClient client, string collection)
    {
        static string[] Ids(JsonArray documents, string key) =>
            [.. documents.Select(d => d![key]!.GetValue<string>()).Order()];

        return new Live(
            Ids((await GetAsync(client, $"/api/v1/{collection}?count=100")).AsArray(), "_id"),
            Ids((await GetAsync(client, $"/api/v3/{collection}?limit=100"))["result"]!.AsArray(), "identifier"));
    }

    private static void ShouldServe(Live live, string id) =>
        live.Should().BeEquivalentTo(new Live([id], [id]), o => o.WithStrictOrdering());

    private async Task<JsonArray> HistoryAsync(HttpClient client, string collection)
    {
        await SettleHistoryAsync();
        return (await GetAsync(client, $"/api/v3/{collection}/history/0?limit=100"))["result"]!.AsArray();
    }

    private static bool IsDeleted(JsonNode? document) => document?["isValid"]?.GetValue<bool>() == false;

    private static bool Is(JsonNode? document, string id) => document?["identifier"]?.GetValue<string>() == id;

    [Theory]
    [InlineData("food", false)]
    [InlineData("food", true)]
    [InlineData("profile", false)]
    [InlineData("profile", true)]
    public async Task An_upload_with_isValid_false_is_stored_deleted(string collection, bool v3)
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();

        await UploadAsync(client, collection, Document(collection, id, isValid: false), v3);

        (await LiveAsync(client, collection)).Should().BeEquivalentTo(Live.None);
        var deleted = (await HistoryAsync(client, collection)).Should().ContainSingle().Subject!;
        IsDeleted(deleted).Should().BeTrue();
        Is(deleted, id).Should().BeTrue();
        ValueOf(collection, deleted).Should().Be(12);
    }

    [Theory]
    [InlineData("food", false)]
    [InlineData("food", true)]
    [InlineData("profile", false)]
    [InlineData("profile", true)]
    public async Task A_resend_without_isValid_brings_it_back(string collection, bool v3)
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, collection, Document(collection, id, isValid: false), v3);
        (await LiveAsync(client, collection)).Should().BeEquivalentTo(Live.None);

        await UploadAsync(client, collection, Document(collection, id, value: 15), v3);

        ShouldServe(await LiveAsync(client, collection), id);
        var served = (await GetAsync(client, $"/api/v1/{collection}?count=100")).AsArray().Single()!;
        ValueOf(collection, served).Should().Be(15);
    }

    [Theory]
    [InlineData("food", false)]
    [InlineData("food", true)]
    [InlineData("profile", false)]
    [InlineData("profile", true)]
    public async Task A_resend_with_isValid_false_stores_nothing_more(string collection, bool v3)
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, collection, Document(collection, id, isValid: false), v3);

        await UploadAsync(client, collection, Document(collection, id, isValid: false, value: 15), v3);

        (await LiveAsync(client, collection)).Should().BeEquivalentTo(Live.None);
        var deleted = (await HistoryAsync(client, collection)).Should().ContainSingle().Subject!;
        IsDeleted(deleted).Should().BeTrue();
        ValueOf(collection, deleted).Should().Be(12);
    }

    [Theory]
    [InlineData("food", false)]
    [InlineData("food", true)]
    [InlineData("profile", false)]
    [InlineData("profile", true)]
    public async Task An_upload_with_isValid_false_deletes_the_live_document_its_id_names(string collection, bool v3)
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        var other = NewId();
        await UploadAsync(client, collection, Document(collection, id), v3);
        await UploadAsync(client, collection, Document(collection, other), v3);

        await UploadAsync(client, collection, Document(collection, id, isValid: false), v3);

        ShouldServe(await LiveAsync(client, collection), other);
        (await HistoryAsync(client, collection)).Should().Contain(d => IsDeleted(d) && Is(d, id));

        await UploadAsync(client, collection, Document(collection, id), v3);

        (await LiveAsync(client, collection)).V1.Should().BeEquivalentTo([id, other]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_upload_with_isValid_false_under_the_served_identifier_deletes_a_profile_stored_under_a_minted_id(bool v3)
    {
        var client = CreateAuthenticatedClient();
        await UploadAsync(client, "profile", Document("profile", id: null), v3: false);
        var minted = (await LiveAsync(client, "profile")).V1.Should().ContainSingle().Subject;
        var served = (await LiveAsync(client, "profile")).V3.Should().ContainSingle().Subject;
        served.Should().NotBe(minted);

        await UploadAsync(client, "profile", Document("profile", served, isValid: false), v3);

        (await LiveAsync(client, "profile")).Should().BeEquivalentTo(Live.None);
        var deleted = (await HistoryAsync(client, "profile")).Should().ContainSingle().Subject!;
        IsDeleted(deleted).Should().BeTrue();
        Is(deleted, served).Should().BeTrue();
    }

    [Fact]
    public async Task A_v3_create_that_deletes_a_live_document_answers_as_a_deduplication()
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, "food", Document("food", id), v3: true);

        var response = await client.PostAsJsonAsync("/api/v3/food", Document("food", id, isValid: false));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["isDeduplication"]!.GetValue<bool>().Should().BeTrue();
        body["identifier"]!.GetValue<string>().Should().Be(id);
    }

    [Theory]
    [InlineData("food")]
    [InlineData("profile")]
    public async Task A_v3_update_that_sets_isValid_false_is_refused(string collection)
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, collection, Document(collection, id), v3: true);

        var put = await client.PutAsJsonAsync($"/api/v3/{collection}/{id}", Document(collection, id, isValid: false));

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonNode.Parse(await put.Content.ReadAsStringAsync())!["message"]!.GetValue<string>()
            .Should().Be("Field isValid cannot be modified by the client");
        ShouldServe(await LiveAsync(client, collection), id);
    }
}

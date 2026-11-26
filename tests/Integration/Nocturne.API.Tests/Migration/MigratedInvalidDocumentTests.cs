using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Nocturne.API.Services.Migration;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Migration;

/// <summary>
/// A food or a profile document Nightscout holds with <c>isValid: false</c> is imported deleted, as an
/// upload of it is stored: no search serves it, v3 history serves it as deleted, and a re-run of the
/// migration over the same documents writes nothing new. Both migration paths read such documents:
/// MongoDB reads the collection whole, and Nightscout's v1 <c>food.json</c> and <c>profile.json</c>
/// filter nothing by <c>isValid</c>.
/// </summary>
[Collection(ApiIntegrationShards.Four)]
[Trait("Category", "Integration")]
public class MigratedInvalidDocumentTests : ApiIntegrationTestBase, IClassFixture<MigrationTestFixture>
{
    private const string FoodId = "65f0000000000000000000f1";
    private const string ProfileId = "65f0000000000000000000a1";
    private const string StartDate = "2026-06-01T00:00:00.000Z";
    private const long Mills = 1780272000000;
    private static readonly string[] Collections = ["food", "profile"];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly MigrationTestFixture _migration;

    public MigratedInvalidDocumentTests(
        ApiIntegrationTestFixture fixture, MigrationTestFixture migration, ITestOutputHelper output)
        : base(fixture, output)
    {
        _migration = migration;
    }

    private static JsonObject Store(bool nullDia = false) => new()
    {
        ["Default"] = new JsonObject
        {
            ["dia"] = nullDia ? null : 3,
            ["carbratio"] = Schedule(10),
            ["sens"] = Schedule(50),
            ["basal"] = Schedule(1),
            ["target_low"] = Schedule(80),
            ["target_high"] = Schedule(120),
            ["units"] = "mg/dl",
            ["timezone"] = "UTC",
        },
    };

    private static JsonArray Schedule(double value) =>
        [new JsonObject { ["time"] = "00:00", ["value"] = value, ["timeAsSeconds"] = 0 }];

    [Theory]
    [InlineData(false, 12, false)]
    [InlineData("false", "12", true)]
    public async Task A_mongo_migration_imports_them_deleted_and_a_rerun_writes_nothing_new(
        object isValid, object carbs, bool oddlyTyped)
    {
        var databaseName = $"nightscout_invalid_{Guid.NewGuid():N}";
        var database = new MongoClient(_migration.MongoConnectionString).GetDatabase(databaseName);
        await database.GetCollection<BsonDocument>("food").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.Parse(FoodId),
            ["type"] = "food",
            ["name"] = "Apple",
            ["carbs"] = BsonValue.Create(carbs),
            ["fat"] = oddlyTyped ? BsonNull.Value : 0,
            ["isValid"] = BsonValue.Create(isValid),
        });
        await database.GetCollection<BsonDocument>("profile").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.Parse(ProfileId),
            ["defaultProfile"] = "Default",
            ["startDate"] = oddlyTyped ? BsonNull.Value : StartDate,
            ["mills"] = BsonValue.Create(oddlyTyped ? Mills.ToString() : Mills),
            ["units"] = "mg/dl",
            ["store"] = BsonDocument.Parse(Store(nullDia: oddlyTyped).ToJsonString()),
            ["isValid"] = BsonValue.Create(isValid),
        });
        var request = new StartMigrationRequest
        {
            Mode = MigrationMode.MongoDb,
            MongoConnectionString = _migration.MongoConnectionString,
            MongoDatabaseName = databaseName,
            Collections = [.. Collections],
        };

        await RunToCompletionAsync(request);
        await ShouldHoldThemDeletedAsync(oddlyTyped);

        await RunToCompletionAsync(request);
        await ShouldHoldThemDeletedAsync(oddlyTyped);
    }

    [Theory]
    [InlineData(false, 12, false)]
    [InlineData("false", "12", true)]
    public async Task An_api_migration_imports_them_deleted_and_a_rerun_writes_nothing_new(
        object isValid, object carbs, bool oddlyTyped)
    {
        var food = new JsonArray(new JsonObject
        {
            ["_id"] = FoodId,
            ["type"] = "food",
            ["name"] = "Apple",
            ["carbs"] = JsonSerializer.SerializeToNode(carbs),
            ["fat"] = oddlyTyped ? null : 0,
            ["isValid"] = JsonSerializer.SerializeToNode(isValid),
        });
        var profile = new JsonArray(ProfileDocument(isValid, oddlyTyped));
        await using var source = await NightscoutServingAsync(() => food, () => profile);
        var request = new StartMigrationRequest
        {
            Mode = MigrationMode.Api,
            NightscoutUrl = source.Url,
            Collections = [.. Collections],
        };

        await RunToCompletionAsync(request);
        await ShouldHoldThemDeletedAsync(oddlyTyped);

        await RunToCompletionAsync(request);
        await ShouldHoldThemDeletedAsync(oddlyTyped);
    }

    /// <summary>
    /// A profile Nightscout now holds deleted deletes the one an earlier run imported, as the system,
    /// so a later run that reads it valid again writes it again.
    /// </summary>
    [Fact]
    public async Task An_api_migration_deletes_a_profile_Nightscout_now_holds_deleted_and_a_valid_rerun_writes_it_again()
    {
        var profile = ProfileDocument(isValid: null);
        await using var source = await NightscoutServingAsync(() => [], () => [profile.DeepClone()]);
        var request = new StartMigrationRequest
        {
            Mode = MigrationMode.Api,
            NightscoutUrl = source.Url,
            Collections = ["profile"],
        };
        await RunToCompletionAsync(request);
        (await LiveProfileIdsAsync()).Should().Equal(ProfileId);

        profile["isValid"] = false;
        await RunToCompletionAsync(request);

        (await LiveProfileIdsAsync()).Should().BeEmpty();
        (await GetAsync("/api/v3/profile?limit=100"))["result"]!.AsArray().Should().BeEmpty();
        await using (var db = Fixture.CreateDbContext(Fixture.TenantId))
        {
            var stored = await db.TherapySettings.IncludingDeleted()
                .Select(t => new { t.DeletedAt, ByUser = EF.Property<bool>(t, NocturneDbContext.DeletedByUserProperty) })
                .SingleAsync();
            stored.DeletedAt.Should().NotBeNull();
            stored.ByUser.Should().BeFalse();
        }

        profile.Remove("isValid");
        await RunToCompletionAsync(request);

        (await LiveProfileIdsAsync()).Should().Equal(ProfileId);
    }

    private async Task<string[]> LiveProfileIdsAsync() =>
        [.. (await GetAsync("/api/v1/profile?count=100")).AsArray().Select(p => p!["_id"]!.GetValue<string>())];

    private static JsonObject ProfileDocument(object? isValid, bool oddlyTyped = false)
    {
        var profile = new JsonObject
        {
            ["_id"] = ProfileId,
            ["defaultProfile"] = "Default",
            ["startDate"] = oddlyTyped ? null : StartDate,
            ["mills"] = oddlyTyped ? JsonValue.Create(Mills.ToString()) : JsonValue.Create(Mills),
            ["units"] = "mg/dl",
            ["store"] = Store(nullDia: oddlyTyped),
        };
        if (isValid is not null)
            profile["isValid"] = JsonSerializer.SerializeToNode(isValid);
        return profile;
    }

    private async Task ShouldHoldThemDeletedAsync(bool oddlyTyped)
    {
        foreach (var (collection, id) in new[] { ("food", FoodId), ("profile", ProfileId) })
        {
            (await GetAsync($"/api/v1/{collection}?count=100")).AsArray().Should().BeEmpty(collection);
            (await GetAsync($"/api/v3/{collection}?limit=100"))["result"]!.AsArray().Should().BeEmpty(collection);

            await SettleHistoryAsync();
            var history = (await GetAsync($"/api/v3/{collection}/history/0?limit=100"))["result"]!.AsArray();
            var deleted = history.Should().ContainSingle(collection).Subject!;
            deleted["isValid"]!.GetValue<bool>().Should().BeFalse();
            deleted["identifier"]!.GetValue<string>().Should().Be(id);
        }

        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        var food = await db.Foods.IncludingDeleted().SingleAsync();
        food.Carbs.Should().Be(12);
        food.Fat.Should().Be(0);
        var therapy = await db.TherapySettings.IncludingDeleted().SingleAsync();
        therapy.Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(Mills).UtcDateTime);
        therapy.Dia.Should().Be(oddlyTyped ? 0 : 3);
    }

    private async Task<JsonNode> GetAsync(string url)
    {
        var response = await AuthenticatedClient.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private async Task RunToCompletionAsync(StartMigrationRequest request)
    {
        var start = await AuthenticatedClient.PostAsJsonAsync("/api/v4/migration/start", request);
        start.StatusCode.Should().Be(HttpStatusCode.Accepted, await start.Content.ReadAsStringAsync());
        var job = (await start.Content.ReadFromJsonAsync<MigrationJobInfo>(JsonOptions))!;
        await MigrationJobPump.RunAsync(Fixture);

        var status = await Eventually.ValueAsync(
            "the migration job to end",
            async () =>
            {
                var current = await AuthenticatedClient.GetFromJsonAsync<MigrationJobStatus>(
                    $"/api/v4/migration/{job.Id}/status", JsonOptions);
                return current!.State is MigrationJobState.Completed or MigrationJobState.Failed ? current : null;
            },
            step: TimeSpan.FromMilliseconds(250));

        status.State.Should().Be(MigrationJobState.Completed, status.ErrorMessage);
        foreach (var collection in request.Collections)
            status.CollectionProgress[collection].DocumentsFailed.Should().Be(0, collection);
    }

    /// <summary>A Nightscout that serves what <paramref name="food"/> and <paramref name="profile"/> hold at each request over v1.</summary>
    private static Task<FakeNightscout> NightscoutServingAsync(Func<JsonArray> food, Func<JsonArray> profile) =>
        FakeNightscout.StartAsync(new Dictionary<string, Func<HttpContext, Task<string>>>
        {
            ["/api/v1/food.json"] = FakeNightscout.Body(() => food().ToJsonString()),
            ["/api/v1/profile.json"] = FakeNightscout.Body(() => profile().ToJsonString()),
        });
}

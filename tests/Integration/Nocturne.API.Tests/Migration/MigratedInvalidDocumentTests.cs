using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    private static readonly string[] Collections = ["food", "profile"];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly MigrationTestFixture _migration;

    public MigratedInvalidDocumentTests(
        ApiIntegrationTestFixture fixture, MigrationTestFixture migration, ITestOutputHelper output)
        : base(fixture, output)
    {
        _migration = migration;
    }

    private static JsonObject Store() => new()
    {
        ["Default"] = new JsonObject
        {
            ["dia"] = 3,
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

    [Fact]
    public async Task A_mongo_migration_imports_them_deleted_and_a_rerun_writes_nothing_new()
    {
        var databaseName = $"nightscout_invalid_{Guid.NewGuid():N}";
        var database = new MongoClient(_migration.MongoConnectionString).GetDatabase(databaseName);
        await database.GetCollection<BsonDocument>("food").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.Parse(FoodId),
            ["type"] = "food",
            ["name"] = "Apple",
            ["carbs"] = 12,
            ["isValid"] = false,
        });
        await database.GetCollection<BsonDocument>("profile").InsertOneAsync(new BsonDocument
        {
            ["_id"] = ObjectId.Parse(ProfileId),
            ["defaultProfile"] = "Default",
            ["startDate"] = StartDate,
            ["units"] = "mg/dl",
            ["store"] = BsonDocument.Parse(Store().ToJsonString()),
            ["isValid"] = false,
        });
        var request = new StartMigrationRequest
        {
            Mode = MigrationMode.MongoDb,
            MongoConnectionString = _migration.MongoConnectionString,
            MongoDatabaseName = databaseName,
            Collections = [.. Collections],
        };

        await RunToCompletionAsync(request);
        await ShouldHoldThemDeletedAsync();

        await RunToCompletionAsync(request);
        await ShouldHoldThemDeletedAsync();
    }

    [Fact]
    public async Task An_api_migration_imports_them_deleted_and_a_rerun_writes_nothing_new()
    {
        var food = new JsonArray(new JsonObject
        {
            ["_id"] = FoodId,
            ["type"] = "food",
            ["name"] = "Apple",
            ["carbs"] = 12,
            ["isValid"] = false,
        });
        var profile = new JsonArray(ProfileDocument(isValid: false));
        await using var source = await NightscoutServingAsync(() => food, () => profile);
        var request = new StartMigrationRequest
        {
            Mode = MigrationMode.Api,
            NightscoutUrl = source.Url,
            Collections = [.. Collections],
        };

        await RunToCompletionAsync(request);
        await ShouldHoldThemDeletedAsync();

        await RunToCompletionAsync(request);
        await ShouldHoldThemDeletedAsync();
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

    private static JsonObject ProfileDocument(bool? isValid)
    {
        var profile = new JsonObject
        {
            ["_id"] = ProfileId,
            ["defaultProfile"] = "Default",
            ["startDate"] = StartDate,
            ["units"] = "mg/dl",
            ["store"] = Store(),
        };
        if (isValid is { } valid)
            profile["isValid"] = valid;
        return profile;
    }

    private async Task ShouldHoldThemDeletedAsync()
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
        (await db.Foods.IncludingDeleted().CountAsync()).Should().Be(1);
        (await db.TherapySettings.IncludingDeleted().CountAsync()).Should().Be(1);
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
    private static async Task<NightscoutSource> NightscoutServingAsync(Func<JsonArray> food, Func<JsonArray> profile)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();
        app.MapGet("/api/v1/status", () => Results.Json(new { status = "ok", name = "Nightscout", version = "15.0.0", apiEnabled = true }));
        app.MapGet("/api/v1/food.json", () => Results.Content(food().ToJsonString(), "application/json"));
        app.MapGet("/api/v1/profile.json", () => Results.Content(profile().ToJsonString(), "application/json"));
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new NightscoutSource(app, url);
    }

    private sealed record NightscoutSource(WebApplication App, string Url) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}

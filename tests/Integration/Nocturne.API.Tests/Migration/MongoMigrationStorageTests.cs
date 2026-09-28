using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;
using Nocturne.API.Services.Migration;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Migration;

/// <summary>
/// A MongoDB-mode run must store the entries, treatments and activity it reports as migrated —
/// a count with no rows behind it shows a history that looks complete and has no glucose or
/// insulin in it.
/// </summary>
[Collection("ApiIntegration")]
[Trait("Category", "Integration")]
public class MongoMigrationStorageTests : ApiIntegrationTestBase, IClassFixture<MigrationTestFixture>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly DateTime Start = new(2026, 2, 2, 2, 40, 0, DateTimeKind.Utc);

    private readonly MigrationTestFixture _migration;

    public MongoMigrationStorageTests(
        ApiIntegrationTestFixture fixture,
        MigrationTestFixture migration,
        ITestOutputHelper output) : base(fixture, output)
    {
        _migration = migration;
    }

    private static long Mills(int minutes) =>
        new DateTimeOffset(Start.AddMinutes(minutes)).ToUnixTimeMilliseconds();

    private static string Iso(int minutes) => Start.AddMinutes(minutes).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    private async Task<string> SeedAsync(params (string Collection, BsonDocument[] Documents)[] collections)
    {
        var name = $"ns_{Guid.NewGuid():N}";
        using var client = new MongoClient(_migration.MongoConnectionString);
        var database = client.GetDatabase(name);
        foreach (var (collection, documents) in collections)
            await database.GetCollection<BsonDocument>(collection).InsertManyAsync(documents);
        return name;
    }

    [Fact]
    public async Task Default_run_stores_entries_treatments_and_activity_it_counts()
    {
        var database = await SeedAsync(
            ("entries",
            [
                new() { { "_id", ObjectId.GenerateNewId() }, { "type", "sgv" }, { "sgv", 120 }, { "date", Mills(0) }, { "dateString", Iso(0) }, { "direction", "Flat" }, { "device", "synthetic-cgm" } },
                new() { { "_id", ObjectId.GenerateNewId() }, { "type", "sgv" }, { "sgv", 125 }, { "date", Mills(5) }, { "dateString", Iso(5) }, { "direction", "Flat" }, { "device", "synthetic-cgm" } },
                new() { { "_id", ObjectId.GenerateNewId() }, { "type", "mbg" }, { "mbg", 118 }, { "date", Mills(7) }, { "dateString", Iso(7) }, { "device", "synthetic-meter" } },
            ]),
            ("treatments",
            [
                new() { { "_id", ObjectId.GenerateNewId() }, { "eventType", "Correction Bolus" }, { "insulin", 1.5 }, { "created_at", Iso(10) } },
                // A BSON date rather than Nightscout's usual string, which the reader must still date.
                new() { { "_id", ObjectId.GenerateNewId() }, { "eventType", "Carb Correction" }, { "carbs", 20 }, { "created_at", new BsonDateTime(Start.AddMinutes(20)) } },
                new() { { "_id", ObjectId.GenerateNewId() }, { "eventType", "Temp Basal" }, { "absolute", 0.8 }, { "rate", 0.8 }, { "duration", 30 }, { "created_at", Iso(30) } },
            ]),
            ("activity",
            [
                new() { { "_id", ObjectId.GenerateNewId() }, { "bpm", 72 }, { "created_at", Iso(0) } },
                new() { { "_id", ObjectId.GenerateNewId() }, { "bpm", 80 }, { "created_at", Iso(5) } },
            ]));

        var status = await RunToCompletionAsync(database, []);

        status.State.Should().Be(MigrationJobState.Completed);
        status.ErrorMessage.Should().BeNull();

        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        (await db.SensorGlucose.CountAsync()).Should().Be(2);
        (await db.MeterGlucose.CountAsync()).Should().Be(1);
        (await db.Boluses.CountAsync()).Should().Be(1);
        (await db.CarbIntakes.CountAsync()).Should().Be(1);
        (await db.TempBasals.CountAsync()).Should().Be(1);
        (await db.HeartRates.CountAsync()).Should().Be(2);

        var entries = status.CollectionProgress["entries"];
        entries.IsComplete.Should().BeTrue();
        entries.DocumentsMigrated.Should().Be(3);
        entries.RecordsStored.Should().Be(3);
        entries.DocumentsFailed.Should().Be(0);

        var treatments = status.CollectionProgress["treatments"];
        treatments.DocumentsMigrated.Should().Be(3);
        treatments.RecordsStored.Should().BeGreaterThanOrEqualTo(3);

        var activity = status.CollectionProgress["activity"];
        activity.DocumentsMigrated.Should().Be(2);
        activity.RecordsStored.Should().Be(2);
    }

    [Fact]
    public async Task A_document_with_an_out_of_range_decimal_fails_alone()
    {
        var database = await SeedAsync(
            ("treatments",
            [
                new() { { "_id", ObjectId.GenerateNewId() }, { "eventType", "Correction Bolus" }, { "insulin", new BsonDecimal128(Decimal128.Parse("1E+100")) }, { "created_at", Iso(10) } },
                new() { { "_id", ObjectId.GenerateNewId() }, { "eventType", "Correction Bolus" }, { "insulin", new BsonDecimal128(2.5m) }, { "created_at", Iso(20) } },
            ]));

        var status = await RunToCompletionAsync(database, ["treatments"]);

        status.State.Should().Be(MigrationJobState.Completed);
        var treatments = status.CollectionProgress["treatments"];
        treatments.DocumentsFailed.Should().Be(1);
        treatments.DocumentsMigrated.Should().Be(1);

        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        (await db.Boluses.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_document_with_an_out_of_range_date_fails_alone()
    {
        var database = await SeedAsync(
            ("treatments",
            [
                new() { { "_id", ObjectId.GenerateNewId() }, { "eventType", "Correction Bolus" }, { "insulin", 1.5 }, { "created_at", new BsonDateTime(long.MaxValue) } },
                new() { { "_id", ObjectId.GenerateNewId() }, { "eventType", "Correction Bolus" }, { "insulin", 2.5 }, { "created_at", Iso(20) } },
            ]));

        var status = await RunToCompletionAsync(database, ["treatments"]);

        status.State.Should().Be(MigrationJobState.Completed);
        var treatments = status.CollectionProgress["treatments"];
        treatments.DocumentsFailed.Should().Be(1);
        treatments.DocumentsMigrated.Should().Be(1);

        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        (await db.Boluses.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_collection_MongoDB_mode_cannot_import_is_skipped_not_counted()
    {
        var database = await SeedAsync(
            ("settings", [new() { { "_id", ObjectId.GenerateNewId() }, { "units", "mg/dl" } }]));

        var status = await RunToCompletionAsync(database, ["settings"]);

        var settings = status.CollectionProgress["settings"];
        settings.DocumentsMigrated.Should().Be(0);
        settings.SkippedReason.Should().NotBeNullOrEmpty();
        status.ErrorMessage.Should().Contain("settings");
    }

    private async Task<MigrationJobStatus> RunToCompletionAsync(string database, List<string> collections)
    {
        var start = await AuthenticatedClient.PostAsJsonAsync("/api/v4/migration/start", new StartMigrationRequest
        {
            Mode = MigrationMode.MongoDb,
            MongoConnectionString = _migration.MongoConnectionString,
            MongoDatabaseName = database,
            Collections = collections,
        });
        start.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var job = await start.Content.ReadFromJsonAsync<MigrationJobInfo>(JsonOptions);

        for (var i = 0; i < 120; i++)
        {
            var status = await AuthenticatedClient.GetFromJsonAsync<MigrationJobStatus>(
                $"/api/v4/migration/{job!.Id}/status", JsonOptions);
            if (status!.State is MigrationJobState.Completed or MigrationJobState.Failed or MigrationJobState.Cancelled)
                return status;
            await Task.Delay(250);
        }

        throw new TimeoutException("The MongoDB migration did not finish.");
    }
}

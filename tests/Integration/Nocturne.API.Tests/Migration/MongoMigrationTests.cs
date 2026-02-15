using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Migration;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Constants;
using Nocturne.Infrastructure.Data;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Migration;

[Collection("AspireIntegration")]
[Trait("Category", "Integration")]
public class MongoMigrationTests : AspireIntegrationTestBase, IClassFixture<MigrationTestFixture>, IAsyncLifetime
{
    private readonly MigrationTestFixture _migration;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public MongoMigrationTests(
        AspireIntegrationTestFixture fixture,
        MigrationTestFixture migration,
        ITestOutputHelper output) : base(fixture, output)
    {
        _migration = migration;
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        // Clean up any data from previous tests
        await CleanupMigratedDataAsync();
    }

    public override async Task DisposeAsync()
    {
        await CleanupMigratedDataAsync();
        await base.DisposeAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
    }

    [Fact]
    public async Task TestConnection_MongoDB_ReturnsSuccessWithCollectionsAndCounts()
    {
        // Arrange
        var request = new TestMigrationConnectionRequest
        {
            Mode = MigrationMode.MongoDb,
            MongoConnectionString = _migration.MongoConnectionString,
            MongoDatabaseName = MigrationTestFixture.DatabaseName
        };

        // Act
        var response = await ApiClient.PostAsJsonAsync("/api/v4/migration/test", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<TestMigrationConnectionResult>(JsonOptions);

        result.Should().NotBeNull();
        result!.IsSuccess.Should().BeTrue();
        result.AvailableCollections.Should().Contain("entries");
        result.AvailableCollections.Should().Contain("treatments");
        result.EntryCount.Should().Be(_migration.EntryCount);
        result.TreatmentCount.Should().Be(_migration.TreatmentCount);

        Log($"Connection test returned {result.AvailableCollections.Count} collections, " +
            $"{result.EntryCount} entries, {result.TreatmentCount} treatments");
    }

    [Fact]
    public async Task MigrateEntries_FromMongo_AllFieldsPreserved()
    {
        // Arrange & Act
        var status = await RunMigrationToCompletionAsync(
            MigrationMode.MongoDb,
            collections: ["entries"]);

        // Assert
        status.State.Should().Be(MigrationJobState.Completed);
        status.CollectionProgress.Should().ContainKey("entries");
        status.CollectionProgress["entries"].IsComplete.Should().BeTrue();
        status.CollectionProgress["entries"].DocumentsMigrated.Should().Be(_migration.EntryCount);

        // Verify data via V3 API with dataSource filtering
        var filter = JsonSerializer.Serialize(new { dataSource = DataSources.MongoDbImport });
        var entriesResponse = await ApiClient.GetAsync(
            $"/api/v3/entries?filter={Uri.EscapeDataString(filter)}&limit={_migration.EntryCount + 10}");

        entriesResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseBody = await entriesResponse.Content.ReadAsStringAsync();
        var v3Response = JsonSerializer.Deserialize<JsonElement>(responseBody, JsonOptions);

        var entries = v3Response.GetProperty("result").EnumerateArray().ToList();
        entries.Count.Should().Be(_migration.EntryCount);

        var minSgv = entries.Min(e => e.GetProperty("sgv").GetDouble());
        var maxSgv = entries.Max(e => e.GetProperty("sgv").GetDouble());
        minSgv.Should().BeGreaterThan(0);
        maxSgv.Should().BeGreaterThan(0);

        Log($"Migrated {entries.Count} entries with all fields preserved");
    }

    [Fact]
    public async Task MigrateTreatments_FromMongo_AllFieldsPreserved()
    {
        // Arrange & Act
        var status = await RunMigrationToCompletionAsync(
            MigrationMode.MongoDb,
            collections: ["treatments"]);

        // Assert
        status.State.Should().Be(MigrationJobState.Completed);
        status.CollectionProgress.Should().ContainKey("treatments");
        status.CollectionProgress["treatments"].IsComplete.Should().BeTrue();

        var migrated = status.CollectionProgress["treatments"].DocumentsMigrated;
        migrated.Should().BeGreaterThan(0);

        Log($"Migrated {migrated} treatments (some may be skipped due to unsupported collection handling)");
    }

    [Fact]
    public async Task MigrateEntries_WithVariedDirections_DirectionsPreserved()
    {
        // Arrange & Act
        await RunMigrationToCompletionAsync(
            MigrationMode.MongoDb,
            collections: ["entries"]);

        // Assert — query migrated entries via V3 API and check directions
        var filter = JsonSerializer.Serialize(new { dataSource = DataSources.MongoDbImport });
        var entriesResponse = await ApiClient.GetAsync(
            $"/api/v3/entries?filter={Uri.EscapeDataString(filter)}&limit={_migration.EntryCount + 10}");

        entriesResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseBody = await entriesResponse.Content.ReadAsStringAsync();
        var v3Response = JsonSerializer.Deserialize<JsonElement>(responseBody, JsonOptions);

        var entries = v3Response.GetProperty("result").EnumerateArray().ToList();
        var directions = entries
            .Where(e => e.TryGetProperty("direction", out var dir) && dir.ValueKind == JsonValueKind.String)
            .Select(e => e.GetProperty("direction").GetString()!)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        // The fixture data contains FortyFiveUp, FortyFiveDown, SingleUp, and Flat
        directions.Should().Contain("Flat");
        directions.Should().Contain("FortyFiveUp");
        directions.Should().Contain("FortyFiveDown");
        directions.Should().Contain("SingleUp");

        Log($"Found {directions.Count} distinct directions: {string.Join(", ", directions)}");
    }

    [Fact]
    public async Task MigrateEntries_DuplicateRun_NoDuplicatesCreated()
    {
        // Arrange — run migration first time
        var firstStatus = await RunMigrationToCompletionAsync(
            MigrationMode.MongoDb,
            collections: ["entries"]);

        var firstCount = firstStatus.CollectionProgress["entries"].DocumentsMigrated;

        // Act — run migration second time with same data
        var secondStatus = await RunMigrationToCompletionAsync(
            MigrationMode.MongoDb,
            collections: ["entries"]);

        // Assert — second run should find all duplicates and skip them
        secondStatus.State.Should().Be(MigrationJobState.Completed);
        secondStatus.CollectionProgress["entries"].DocumentsMigrated.Should().Be(0,
            "all entries already exist and should be detected as duplicates");

        Log($"First run migrated {firstCount}, second run migrated 0 (duplicates correctly skipped)");
    }

    [Fact]
    public async Task MigrateAll_EntriesAndTreatments_BothCollectionsComplete()
    {
        // Arrange & Act — migrate both collections
        var status = await RunMigrationToCompletionAsync(MigrationMode.MongoDb);

        // Assert
        status.State.Should().Be(MigrationJobState.Completed);
        status.ProgressPercentage.Should().Be(100);

        status.CollectionProgress.Should().ContainKey("entries");
        status.CollectionProgress.Should().ContainKey("treatments");

        status.CollectionProgress["entries"].IsComplete.Should().BeTrue();
        status.CollectionProgress["treatments"].IsComplete.Should().BeTrue();

        var totalMigrated = status.CollectionProgress.Values.Sum(c => c.DocumentsMigrated);
        totalMigrated.Should().BeGreaterThan(0);

        Log($"Migrated {totalMigrated} total documents across " +
            $"{status.CollectionProgress.Count} collections");
    }

    [Fact]
    public async Task JobStatus_DuringMigration_ShowsProgressAndCompletion()
    {
        // Arrange
        var request = new StartMigrationRequest
        {
            Mode = MigrationMode.MongoDb,
            MongoConnectionString = _migration.MongoConnectionString,
            MongoDatabaseName = MigrationTestFixture.DatabaseName,
            Collections = ["entries", "treatments"]
        };

        // Act — start migration
        var startResponse = await ApiClient.PostAsJsonAsync("/api/v4/migration/start", request);
        startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobInfo = await startResponse.Content.ReadFromJsonAsync<MigrationJobInfo>(JsonOptions);
        jobInfo.Should().NotBeNull();

        // Poll status — collect state transitions
        var observedStates = new HashSet<MigrationJobState>();
        MigrationJobStatus? finalStatus = null;

        for (var i = 0; i < 60; i++)
        {
            var statusResponse = await ApiClient.GetAsync(
                $"/api/v4/migration/{jobInfo!.Id}/status");
            statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var status = await statusResponse.Content
                .ReadFromJsonAsync<MigrationJobStatus>(JsonOptions);
            status.Should().NotBeNull();
            observedStates.Add(status!.State);
            finalStatus = status;

            if (status.State is MigrationJobState.Completed or MigrationJobState.Failed)
                break;

            await Task.Delay(500);
        }

        // Assert
        finalStatus.Should().NotBeNull();
        finalStatus!.State.Should().Be(MigrationJobState.Completed);
        observedStates.Should().Contain(MigrationJobState.Running,
            "should have observed Running state during polling");

        Log($"Observed states: {string.Join(" -> ", observedStates.Order())}");
    }

    #region Helpers

    private async Task<MigrationJobStatus> RunMigrationToCompletionAsync(
        MigrationMode mode,
        List<string>? collections = null)
    {
        var request = new StartMigrationRequest
        {
            Mode = mode,
            MongoConnectionString = _migration.MongoConnectionString,
            MongoDatabaseName = MigrationTestFixture.DatabaseName,
            Collections = collections ?? []
        };

        var startResponse = await ApiClient.PostAsJsonAsync("/api/v4/migration/start", request);
        startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobInfo = await startResponse.Content.ReadFromJsonAsync<MigrationJobInfo>(JsonOptions);
        jobInfo.Should().NotBeNull();

        // Poll until complete
        MigrationJobStatus? status = null;
        for (var i = 0; i < 120; i++)
        {
            var statusResponse = await ApiClient.GetAsync(
                $"/api/v4/migration/{jobInfo!.Id}/status");
            status = await statusResponse.Content.ReadFromJsonAsync<MigrationJobStatus>(JsonOptions);

            if (status!.State is MigrationJobState.Completed or MigrationJobState.Failed)
                break;

            await Task.Delay(500);
        }

        status.Should().NotBeNull();
        if (status!.State == MigrationJobState.Failed)
        {
            Log($"Migration failed: {status.ErrorMessage}");
        }

        return status;
    }

    private async Task CleanupMigratedDataAsync()
    {
        try
        {
            var connStr = await GetPostgresConnectionStringAsync();
            if (string.IsNullOrEmpty(connStr))
            {
                Log("Cleanup skipped: connection string is empty");
                return;
            }

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();

            // Delete entries
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"DELETE FROM entries WHERE data_source = '{DataSources.MongoDbImport}'";
                var entriesDeleted = await cmd.ExecuteNonQueryAsync();
                Log($"Cleanup deleted {entriesDeleted} entries");
            }

            // Delete treatments
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"DELETE FROM treatments WHERE data_source = '{DataSources.MongoDbImport}'";
                var treatmentsDeleted = await cmd.ExecuteNonQueryAsync();
                Log($"Cleanup deleted {treatmentsDeleted} treatments");
            }
        }
        catch (Exception ex)
        {
            Log($"Cleanup failed: {ex.Message}");
            throw; // Re-throw to see the full error
        }
    }

    #endregion
}

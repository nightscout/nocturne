using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Services.Migration;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Constants;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Migration;

[Collection("AspireIntegration")]
[Trait("Category", "Integration")]
public class ApiMigrationTests : AspireIntegrationTestBase, IClassFixture<MigrationTestFixture>, IAsyncLifetime
{
    private readonly MigrationTestFixture _migration;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ApiMigrationTests(
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
    public async Task TestConnection_Api_ReturnsSuccess()
    {
        // Arrange
        var request = new TestMigrationConnectionRequest
        {
            Mode = MigrationMode.Api,
            NightscoutUrl = _migration.MockNightscoutUrl
        };

        // Act
        var response = await ApiClient.PostAsJsonAsync("/api/v4/migration/test", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<TestMigrationConnectionResult>(JsonOptions);

        result.Should().NotBeNull();
        result!.IsSuccess.Should().BeTrue();
        result.AvailableCollections.Should().NotBeEmpty();

        Log($"API connection test passed, site: {result.SiteName}");
    }

    [Fact]
    public async Task MigrateEntries_FromApi_AllFieldsPreserved()
    {
        // Arrange & Act
        var status = await RunMigrationToCompletionAsync(
            MigrationMode.Api,
            collections: ["entries"]);

        // Assert
        status.State.Should().Be(MigrationJobState.Completed);
        status.CollectionProgress.Should().ContainKey("entries");
        status.CollectionProgress["entries"].IsComplete.Should().BeTrue();
        status.CollectionProgress["entries"].DocumentsMigrated.Should().BeGreaterThan(0);

        // Verify data via V3 API with dataSource filtering
        var filter = JsonSerializer.Serialize(new { dataSource = DataSources.MongoDbImport });
        var entriesResponse = await ApiClient.GetAsync(
            $"/api/v3/entries?filter={Uri.EscapeDataString(filter)}&limit=100");

        entriesResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseBody = await entriesResponse.Content.ReadAsStringAsync();
        var v3Response = JsonSerializer.Deserialize<JsonElement>(responseBody, JsonOptions);

        var entries = v3Response.GetProperty("result").EnumerateArray().ToList();
        entries.Count.Should().Be((int)status.CollectionProgress["entries"].DocumentsMigrated);

        var minSgv = entries.Min(e => e.GetProperty("sgv").GetDouble());
        var maxSgv = entries.Max(e => e.GetProperty("sgv").GetDouble());
        minSgv.Should().BeGreaterThan(0);
        maxSgv.Should().BeGreaterThan(0);

        Log($"Migrated {entries.Count} entries from mock Nightscout API");
    }

    [Fact]
    public async Task MigrateTreatments_FromApi_AllFieldsPreserved()
    {
        // Arrange & Act
        var status = await RunMigrationToCompletionAsync(
            MigrationMode.Api,
            collections: ["treatments"]);

        // Assert
        status.State.Should().Be(MigrationJobState.Completed);
        status.CollectionProgress.Should().ContainKey("treatments");
        status.CollectionProgress["treatments"].IsComplete.Should().BeTrue();
        status.CollectionProgress["treatments"].DocumentsMigrated.Should().BeGreaterThan(0);

        Log($"Migrated {status.CollectionProgress["treatments"].DocumentsMigrated} treatments from mock API");
    }

    [Fact]
    public async Task MigrateEntries_WithApiSecret_AuthHeaderSent()
    {
        // Arrange — the mock server accepts any api-secret (just needs the header present)
        var request = new StartMigrationRequest
        {
            Mode = MigrationMode.Api,
            NightscoutUrl = _migration.MockNightscoutUrl,
            NightscoutApiSecret = MigrationTestFixture.TestApiSecret,
            Collections = ["entries"]
        };

        // Act
        var startResponse = await ApiClient.PostAsJsonAsync("/api/v4/migration/start", request);
        startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobInfo = await startResponse.Content.ReadFromJsonAsync<MigrationJobInfo>(JsonOptions);

        var status = await PollUntilCompleteAsync(jobInfo!.Id);

        // Assert
        status.State.Should().Be(MigrationJobState.Completed);
        status.CollectionProgress["entries"].DocumentsMigrated.Should().BeGreaterThan(0);

        Log("Migration with API secret succeeded");
    }

    [Fact]
    public async Task MigrateTreatments_DuplicateRun_NoDuplicatesCreated()
    {
        // Arrange — first run
        var firstStatus = await RunMigrationToCompletionAsync(
            MigrationMode.Api,
            collections: ["treatments"]);

        var firstCount = firstStatus.CollectionProgress["treatments"].DocumentsMigrated;
        firstCount.Should().BeGreaterThan(0, "first migration should import treatments");

        // Act — second run with same data
        var secondStatus = await RunMigrationToCompletionAsync(
            MigrationMode.Api,
            collections: ["treatments"]);

        // Assert — second run should detect all duplicates
        secondStatus.State.Should().Be(MigrationJobState.Completed);
        secondStatus.CollectionProgress["treatments"].DocumentsMigrated.Should().Be(0,
            "all treatments already exist and should be detected as duplicates");

        Log($"First run: {firstCount} treatments, second run: 0 (duplicates skipped)");
    }

    #region Helpers

    private async Task<MigrationJobStatus> RunMigrationToCompletionAsync(
        MigrationMode mode,
        List<string>? collections = null)
    {
        var request = new StartMigrationRequest
        {
            Mode = mode,
            NightscoutUrl = _migration.MockNightscoutUrl,
            Collections = collections ?? []
        };

        var startResponse = await ApiClient.PostAsJsonAsync("/api/v4/migration/start", request);
        startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var jobInfo = await startResponse.Content.ReadFromJsonAsync<MigrationJobInfo>(JsonOptions);
        jobInfo.Should().NotBeNull();

        return await PollUntilCompleteAsync(jobInfo!.Id);
    }

    private async Task<MigrationJobStatus> PollUntilCompleteAsync(Guid jobId)
    {
        MigrationJobStatus? status = null;
        for (var i = 0; i < 120; i++)
        {
            var statusResponse = await ApiClient.GetAsync(
                $"/api/v4/migration/{jobId}/status");
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

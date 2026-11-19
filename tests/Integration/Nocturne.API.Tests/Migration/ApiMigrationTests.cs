using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Services.Migration;
using Nocturne.API.Tests.Integration.Infrastructure;

using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Migration;

[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public class ApiMigrationTests : ApiIntegrationTestBase, IClassFixture<MigrationTestFixture>
{
    private readonly MigrationTestFixture _migration;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ApiMigrationTests(
        ApiIntegrationTestFixture fixture,
        MigrationTestFixture migration,
        ITestOutputHelper output) : base(fixture, output)
    {
        _migration = migration;
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
        var response = await AuthenticatedClient.PostAsJsonAsync("/api/v4/migration/test", request);

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

        // The database is emptied before each test, so every entry it holds was migrated.
        var entries = await GetV3RecordsAsync("entries");
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
        var startResponse = await AuthenticatedClient.PostAsJsonAsync("/api/v4/migration/start", request);
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

        var storedAfterFirst = (await GetV3RecordsAsync("treatments")).Count;
        storedAfterFirst.Should().BeGreaterThan(0);

        // Act — second run with same data
        var secondStatus = await RunMigrationToCompletionAsync(
            MigrationMode.Api,
            collections: ["treatments"]);

        // Assert — DocumentsMigrated counts the documents taken in, inserted or not; the second run
        // upserts each by its Nightscout _id, so the stored treatments stay as they were.
        secondStatus.State.Should().Be(MigrationJobState.Completed);
        (await GetV3RecordsAsync("treatments")).Should().HaveCount(storedAfterFirst,
            "re-importing the same treatments updates them rather than adding copies");

        Log($"First run: {firstCount} treatments, {storedAfterFirst} stored after both runs");
    }

    [Fact]
    public async Task JobStatus_DuringMigration_ShowsProgressAndCompletion()
    {
        var request = new StartMigrationRequest
        {
            Mode = MigrationMode.Api,
            NightscoutUrl = _migration.MockNightscoutUrl,
            Collections = ["entries", "treatments"]
        };

        MigrationJobInfo? jobInfo;
        using (_migration.HoldEntries())
        {
            var startResponse = await AuthenticatedClient.PostAsJsonAsync("/api/v4/migration/start", request);
            startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
            jobInfo = await startResponse.Content.ReadFromJsonAsync<MigrationJobInfo>(JsonOptions);
            jobInfo.Should().NotBeNull();

            var held = await PollUntilAsync(jobInfo!.Id, s => s.State != MigrationJobState.Pending);
            held.State.Should().Be(MigrationJobState.Running, "the job is waiting on the held entries");
        }

        var finalStatus = await PollUntilCompleteAsync(jobInfo.Id);

        finalStatus.State.Should().Be(MigrationJobState.Completed);
        finalStatus.CollectionProgress["entries"].DocumentsMigrated.Should().Be(_migration.EntryCount);
    }

    #region Helpers

    private async Task<MigrationJobStatus> PollUntilAsync(Guid jobId, Func<MigrationJobStatus, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var status = await AuthenticatedClient.GetFromJsonAsync<MigrationJobStatus>(
                $"/api/v4/migration/{jobId}/status", JsonOptions);
            if (done(status!) || DateTime.UtcNow > deadline)
                return status!;
            await Task.Delay(100);
        }
    }

    private async Task<List<JsonElement>> GetV3RecordsAsync(string collection)
    {
        var response = await AuthenticatedClient.GetAsync($"/api/v3/{collection}?limit=1000");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), JsonOptions);
        return body.GetProperty("result").EnumerateArray().ToList();
    }

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

        var startResponse = await AuthenticatedClient.PostAsJsonAsync("/api/v4/migration/start", request);
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
            var statusResponse = await AuthenticatedClient.GetAsync(
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

    #endregion
}

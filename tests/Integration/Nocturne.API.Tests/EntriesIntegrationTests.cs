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
/// Integration tests for Entry CRUD operations against the API running in-process on real PostgreSQL.
/// Tests the complete request/response cycle for v1 entries endpoints against
/// the full distributed application stack.
/// </summary>
[Trait("Category", "Integration")]
[Parity]
public class EntriesIntegrationTests : ApiIntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public EntriesIntegrationTests(
        ApiIntegrationTestFixture fixture,
        ITestOutputHelper output
    )
        : base(fixture, output) { }

    private static Entry CreateTestEntry(
        double sgv = 120,
        string direction = "Flat",
        long? mills = null
    )
    {
        var now = DateTimeOffset.UtcNow;
        return new Entry
        {
            Sgv = sgv,
            Type = "sgv",
            Mills = mills ?? now.ToUnixTimeMilliseconds(),
            DateString = (mills.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(mills.Value)
                : now
            ).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Direction = direction,
            Device = "test-device",
        };
    }

    #region POST /api/v1/entries

    [Fact]
    public async Task PostEntry_Single_ShouldCreateAndReturnEntry()
    {
        // Arrange
        using var client = CreateAuthenticatedClient();
        var entry = CreateTestEntry(sgv: 140);

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/entries", new[] { entry });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        Log($"POST single entry response: {content}");

        content.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task PostEntries_Array_ShouldCreateMultipleEntries()
    {
        // Arrange
        using var client = CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entries = new[]
        {
            CreateTestEntry(sgv: 100, mills: now - 300000),
            CreateTestEntry(sgv: 110, mills: now - 200000),
            CreateTestEntry(sgv: 120, mills: now - 100000),
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/entries", entries);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify all entries were created by fetching them back
        var getResponse = await client.GetAsync("/api/v1/entries?count=10");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var fetched = await getResponse.Content.ReadFromJsonAsync<Entry[]>(JsonOptions);
        fetched.Should().NotBeNull();
        fetched!.Length.Should().BeGreaterThanOrEqualTo(3);

        Log($"Created {entries.Length} entries, fetched {fetched.Length} back");
    }

    #endregion

    #region GET /api/v1/entries

    [Fact]
    public async Task GetEntries_ShouldReturnEntriesWithPagination()
    {
        // Arrange
        using var client = CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entries = new[]
        {
            CreateTestEntry(sgv: 100, mills: now - 300000),
            CreateTestEntry(sgv: 110, mills: now - 200000),
            CreateTestEntry(sgv: 120, mills: now - 100000),
            CreateTestEntry(sgv: 130, mills: now),
        };

        await client.PostAsJsonAsync("/api/v1/entries", entries);

        // Act - request with count=2
        var response = await client.GetAsync("/api/v1/entries?count=2");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var fetched = await response.Content.ReadFromJsonAsync<Entry[]>(JsonOptions);
        fetched.Should().NotBeNull();
        fetched!.Length.Should().Be(2);
    }

    [Fact]
    public async Task GetEntries_WithSkip_ShouldOffsetResults()
    {
        // Arrange
        using var client = CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entries = new[]
        {
            CreateTestEntry(sgv: 100, mills: now - 300000),
            CreateTestEntry(sgv: 110, mills: now - 200000),
            CreateTestEntry(sgv: 120, mills: now - 100000),
            CreateTestEntry(sgv: 130, mills: now),
        };

        await client.PostAsJsonAsync("/api/v1/entries", entries);

        // Act - get first page
        var firstPage = await client.GetAsync("/api/v1/entries?count=2&skip=0");
        var secondPage = await client.GetAsync("/api/v1/entries?count=2&skip=2");

        // Assert
        firstPage.StatusCode.Should().Be(HttpStatusCode.OK);
        secondPage.StatusCode.Should().Be(HttpStatusCode.OK);

        var firstEntries = await firstPage.Content.ReadFromJsonAsync<Entry[]>(JsonOptions);
        var secondEntries = await secondPage.Content.ReadFromJsonAsync<Entry[]>(JsonOptions);

        firstEntries.Should().NotBeNull();
        secondEntries.Should().NotBeNull();
        firstEntries!.Length.Should().Be(2);
        secondEntries!.Length.Should().Be(2);

        // Pages should contain different entries
        var firstIds = firstEntries.Select(e => e.Id).ToHashSet();
        var secondIds = secondEntries.Select(e => e.Id).ToHashSet();
        firstIds.Intersect(secondIds).Should().BeEmpty("pages should not overlap");
    }

    [Fact]
    public async Task GetEntries_EmptyDatabase_ShouldReturnEmptyArray()
    {
        // Act
        var response = await ApiClient.GetAsync("/api/v1/entries?count=10");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var fetched = await response.Content.ReadFromJsonAsync<Entry[]>(JsonOptions);
        fetched.Should().NotBeNull();
        fetched!.Should().BeEmpty();
    }

    #endregion

    #region GET /api/v1/entries/current

    [Fact]
    public async Task GetCurrentEntry_ShouldReturnMostRecentEntry()
    {
        // Arrange
        using var client = CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entries = new[]
        {
            CreateTestEntry(sgv: 100, mills: now - 300000),
            CreateTestEntry(sgv: 150, mills: now - 100000),
            CreateTestEntry(sgv: 200, mills: now),
        };

        await client.PostAsJsonAsync("/api/v1/entries", entries);

        // Act
        var response = await client.GetAsync("/api/v1/entries/current");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        Log($"GET current entry response: {content}");

        var result = await response.Content.ReadFromJsonAsync<Entry[]>(JsonOptions);
        result.Should().NotBeNull();
        result!.Should().NotBeEmpty();

        // The most recent entry should have sgv=200
        result.First().Sgv.Should().Be(200);
    }

    [Fact]
    public async Task GetCurrentEntry_EmptyDatabase_ShouldReturnSuccessfully()
    {
        // Act
        var response = await ApiClient.GetAsync("/api/v1/entries/current");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    #endregion

    #region GET /api/v1/entries/{id}

    [Fact]
    public async Task GetEntryById_ShouldReturnSpecificEntry()
    {
        // Arrange
        using var client = CreateAuthenticatedClient();
        var entry = CreateTestEntry(sgv: 175);

        var createResponse = await client.PostAsJsonAsync("/api/v1/entries", new[] { entry });
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Get all entries to find the created entry's ID
        var listResponse = await client.GetAsync("/api/v1/entries?count=1");
        var entries = await listResponse.Content.ReadFromJsonAsync<Entry[]>(JsonOptions);
        entries.Should().NotBeNull();
        entries!.Should().NotBeEmpty();

        var entryId = entries.First().Id;
        entryId.Should().NotBeNullOrEmpty();

        // Act
        var response = await client.GetAsync($"/api/v1/entries/{entryId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var fetched = await response.Content.ReadFromJsonAsync<Entry[]>(JsonOptions);
        fetched.Should().NotBeNull();
        fetched!.Should().NotBeEmpty();
        fetched.First().Sgv.Should().Be(175);
        fetched.First().Id.Should().Be(entryId);
    }

    #endregion

    #region PUT /api/v1/entries/{id}

    [Fact]
    public async Task PutEntry_ShouldUpdateExistingEntry()
    {
        // Arrange
        using var client = CreateAuthenticatedClient();
        var entry = CreateTestEntry(sgv: 100, direction: "Flat");

        await client.PostAsJsonAsync("/api/v1/entries", new[] { entry });

        var listResponse = await client.GetAsync("/api/v1/entries?count=1");
        var entries = await listResponse.Content.ReadFromJsonAsync<Entry[]>(JsonOptions);
        entries.Should().NotBeNull();
        entries!.Should().NotBeEmpty();

        var created = entries.First();
        created.Sgv = 180;
        created.Direction = "SingleUp";

        // Act
        var response = await client.PutAsJsonAsync($"/api/v1/entries/{created.Id}", created);

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

        // Verify the update persisted
        var getResponse = await client.GetAsync($"/api/v1/entries/{created.Id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = await getResponse.Content.ReadFromJsonAsync<Entry[]>(JsonOptions);
        updated.Should().NotBeNull();
        updated!.Should().NotBeEmpty();
        updated.First().Sgv.Should().Be(180);
        updated.First().Direction.Should().Be("SingleUp");

        Log($"Updated entry {created.Id}: sgv 100->180, direction Flat->SingleUp");
    }

    #endregion

    #region DELETE /api/v1/entries/{id}

    [Fact]
    public async Task DeleteEntry_ShouldRemoveEntry()
    {
        // Arrange
        using var client = CreateAuthenticatedClient();
        var entry = CreateTestEntry(sgv: 160);

        await client.PostAsJsonAsync("/api/v1/entries", new[] { entry });

        var listResponse = await client.GetAsync("/api/v1/entries?count=1");
        var entries = await listResponse.Content.ReadFromJsonAsync<Entry[]>(JsonOptions);
        entries.Should().NotBeNull();
        entries!.Should().NotBeEmpty();

        var entryId = entries.First().Id;

        // Act
        var response = await client.DeleteAsync($"/api/v1/entries/{entryId}");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

        // Verify the entry is gone
        var getResponse = await client.GetAsync("/api/v1/entries?count=10");
        var remaining = await getResponse.Content.ReadFromJsonAsync<Entry[]>(JsonOptions);
        remaining.Should().NotBeNull();
        remaining!.Select(e => e.Id).Should().NotContain(entryId);

        Log($"Deleted entry {entryId} and verified removal");
    }

    #endregion
}

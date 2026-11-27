using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// A temp basal the owner deletes sits on the deleted list until it is restored, one or several at a
/// time, and a restore is refused when a live temp basal now holds its (DataSource, SyncIdentifier).
/// </summary>
[Collection(ApiIntegrationShards.Four)]
[Trait("Category", "Integration")]
public class TempBasalRestoreIntegrationTests : ApiIntegrationTestBase
{
    private const string Route = "/api/v4/insulin/temp-basals";

    public TempBasalRestoreIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task ADeletedTempBasal_IsListedAsDeletedAndRestoredById()
    {
        var id = await CreateAsync(Source(), "tb-restore");
        await DeleteAsync(id);

        (await GetStatusAsync(id)).Should().Be(HttpStatusCode.NotFound);
        (await DeletedIdsAsync()).Should().Contain(id);

        var restored = await AuthenticatedClient.PostAsync($"{Route}/{id}/restore", null);

        restored.StatusCode.Should().Be(HttpStatusCode.OK, await restored.Content.ReadAsStringAsync());
        (await restored.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid().Should().Be(id);
        (await GetStatusAsync(id)).Should().Be(HttpStatusCode.OK);
        (await DeletedIdsAsync()).Should().NotContain(id);
    }

    [Fact]
    public async Task DeletedTempBasals_AreRestoredTogether_AndAnUnknownIdIsIgnored()
    {
        var source = Source();
        var first = await CreateAsync(source, "tb-bulk-1");
        var second = await CreateAsync(source, "tb-bulk-2", minutesAgo: 30);
        await DeleteAsync(first);
        await DeleteAsync(second);

        var response = await AuthenticatedClient.PostAsJsonAsync(
            $"{Route}/restore", new[] { first, second, Guid.NewGuid() });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("restored").EnumerateArray().Select(r => r.GetProperty("id").GetGuid())
            .Should().BeEquivalentTo([first, second]);
        result.GetProperty("conflicts").EnumerateArray().Should().BeEmpty();
        (await GetStatusAsync(first)).Should().Be(HttpStatusCode.OK);
        (await GetStatusAsync(second)).Should().Be(HttpStatusCode.OK);
        (await DeletedIdsAsync()).Should().NotContain([first, second]);
    }

    [Fact]
    public async Task ARestore_IsRefused_WhenALiveTempBasalNowHoldsTheSyncKey()
    {
        var source = Source();
        var deleted = await CreateAsync(source, "tb-held");
        await DeleteAsync(deleted);
        await ReleaseSyncKeyAsync(deleted);
        var live = await CreateAsync(source, "tb-held", minutesAgo: 5);
        live.Should().NotBe(deleted);

        var single = await AuthenticatedClient.PostAsync($"{Route}/{deleted}/restore", null);
        var bulk = await AuthenticatedClient.PostAsJsonAsync($"{Route}/restore", new[] { deleted });

        single.StatusCode.Should().Be(HttpStatusCode.Conflict, await single.Content.ReadAsStringAsync());
        bulk.StatusCode.Should().Be(HttpStatusCode.OK, await bulk.Content.ReadAsStringAsync());
        var result = await bulk.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("restored").EnumerateArray().Should().BeEmpty();
        result.GetProperty("conflicts").EnumerateArray().Select(c => c.GetGuid()).Should().Equal(deleted);
        (await GetStatusAsync(deleted)).Should().Be(HttpStatusCode.NotFound);
        (await DeletedIdsAsync()).Should().Contain(deleted);
        (await GetStatusAsync(live)).Should().Be(HttpStatusCode.OK);
    }

    private static string Source() => $"restore-{Guid.NewGuid():N}";

    private async Task<Guid> CreateAsync(string dataSource, string syncIdentifier, int minutesAgo = 60)
    {
        var response = await AuthenticatedClient.PostAsJsonAsync(Route, new
        {
            timestamp = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
            rate = 1.25,
            durationMinutes = 20,
            dataSource,
            syncIdentifier,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task DeleteAsync(Guid id) =>
        (await AuthenticatedClient.DeleteAsync($"{Route}/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

    private async Task<HttpStatusCode> GetStatusAsync(Guid id) =>
        (await AuthenticatedClient.GetAsync($"{Route}/{id}")).StatusCode;

    private async Task<List<Guid>> DeletedIdsAsync()
    {
        var response = await AuthenticatedClient.GetAsync($"{Route}/deleted?limit=1000");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data")
            .EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToList();
    }

    private async Task ReleaseSyncKeyAsync(Guid id)
    {
        await using var conn = await OpenPostgresAsync();
        await conn.PinTenantAsync(Fixture.TenantId);
        await conn.ExecuteAsync(
            "UPDATE temp_basals SET deleted_by_user = false WHERE id = @id", [("id", id)]);
    }
}

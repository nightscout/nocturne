using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// xDrip+ re-uploads the reading paired with a calibration at its original timestamp with no
/// <c>_id</c> and the recalculated value. Nightscout's v1 create upserts on <c>{sysTime, type}</c>,
/// so the re-send updates the fields it carries.
/// </summary>
[Collection(ApiIntegrationShards.Four)]
[Trait("Category", "Integration")]
public class V1EntryResendIntegrationTests : ApiIntegrationTestBase
{
    private const string Device = "xDrip-DexcomG6";

    private static readonly long Date =
        DateTimeOffset.UtcNow.AddMinutes(-20).ToUnixTimeMilliseconds() / 1000 * 1000 + 417;

    private static readonly string Iso =
        DateTimeOffset.FromUnixTimeMilliseconds(Date).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    public V1EntryResendIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task ResendAtTheSameInstantWithANewValue_UpdatesTheStoredReading()
    {
        var firstId = await PostSgvAsync(120);

        using var echo = await PostAsync(SgvJson(134));
        var echoed = echo.RootElement.EnumerateArray().Should().ContainSingle().Subject;
        echoed.GetProperty("sgv").GetDouble().Should().Be(134);
        echoed.GetProperty("_id").GetString().Should().Be(firstId);

        (await V1SgvsAtDateAsync()).Should().Equal(134d);
        (await V4SgvsAsync()).Should().Equal(134d);
    }

    [Fact]
    public async Task ResendOfAReadingStoredUnderAUuidId_UpdatesIt()
    {
        string firstId;
        using (var first = await PostAsync(SgvJson(120, id: Guid.NewGuid().ToString().ToUpperInvariant())))
            firstId = first.RootElement[0].GetProperty("_id").GetString()!;

        using var echo = await PostAsync(SgvJson(134));

        echo.RootElement[0].GetProperty("_id").GetString().Should().Be(firstId);
        echo.RootElement[0].GetProperty("sgv").GetDouble().Should().Be(134);
        (await V4SgvsAsync()).Should().Equal(134d);
    }

    [Fact]
    public async Task ResendWithoutADevice_KeepsTheStoredDevice()
    {
        await PostSgvAsync(120);

        (await PostAsync(SgvJson(134, device: null))).Dispose();

        (await V4SgvsAsync()).Should().Equal(134d);
        (await V1SgvsAtDateAsync()).Should().Equal(134d);
    }

    [Fact]
    public async Task ByteIdenticalResend_WritesNothing()
    {
        await PostSgvAsync(120);
        var before = await RowVersionAsync();

        (await PostAsync(SgvJson(120))).Dispose();

        (await RowVersionAsync()).Should().Be(before);
        (await V1SgvsAtDateAsync()).Should().Equal(120d);
    }

    [Fact]
    public async Task ResendFromAnotherDevice_IsStoredBesideTheReading()
    {
        await PostSgvAsync(120);

        (await PostAsync(SgvJson(134, "Dexcom Share"))).Dispose();

        (await V4SgvsAsync()).Should().Equal(120d);
        (await V4SgvsAsync("Dexcom Share")).Should().Equal(134d);
    }

    /// <summary>
    /// A connector writes readings with no legacy id; the legacy-id upsert a re-send goes through
    /// cannot reach one, so it must neither change nor duplicate it.
    /// </summary>
    [Fact]
    public async Task ResendOfAReadingStoredWithoutALegacyId_EchoesItUnchanged()
    {
        var created = await AuthenticatedClient.PostAsJsonAsync("/api/v4/glucose/sensor", new
        {
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(Date),
            mgdl = 120.0,
            device = Device,
        });
        created.IsSuccessStatusCode.Should().BeTrue(await created.Content.ReadAsStringAsync());

        using var echo = await PostAsync(SgvJson(134));

        echo.RootElement[0].GetProperty("sgv").GetDouble().Should().Be(120);
        (await V4SgvsAsync()).Should().Equal(120d);
    }

    [Fact]
    public async Task UpdatedReading_IsDeletedByTheEchoedId()
    {
        await PostSgvAsync(120);
        using var echo = await PostAsync(SgvJson(134));
        var id = echo.RootElement[0].GetProperty("_id").GetString();

        (await AuthenticatedClient.DeleteAsync($"/api/v1/entries/{id}")).IsSuccessStatusCode.Should().BeTrue();

        (await V1SgvsAtDateAsync()).Should().BeEmpty();
        (await V4SgvsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ResendCarryingTheDeletedReadingsId_StaysDeleted()
    {
        var id = await PostSgvAsync(120);
        (await AuthenticatedClient.DeleteAsync($"/api/v1/entries/{id}")).IsSuccessStatusCode.Should().BeTrue();

        using var echo = await PostAsync(SgvJson(134, id: id));

        (await V1SgvsAtDateAsync()).Should().BeEmpty();
        (await V4SgvsAsync()).Should().BeEmpty();
    }

    private static string SgvJson(int sgv, string? device = Device, string? id = null)
    {
        var keys = (id is null ? "" : $"\"_id\":\"{id}\",") + (device is null ? "" : $"\"device\":\"{device}\",");
        return $$"""
            [{{{keys}}"date":{{Date}},"dateString":"{{Iso}}","sysTime":"{{Iso}}","type":"sgv","sgv":{{sgv}},"direction":"Flat","noise":1,"filtered":0,"unfiltered":0,"rssi":100}]
            """;
    }

    private async Task<string> PostSgvAsync(int sgv)
    {
        using var echo = await PostAsync(SgvJson(sgv));
        return echo.RootElement[0].GetProperty("_id").GetString()!;
    }

    private async Task<JsonDocument> PostAsync(string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await AuthenticatedClient.PostAsync("/api/v1/entries", content);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text);
    }

    private async Task<List<double>> V1SgvsAtDateAsync()
    {
        var rows = await AuthenticatedClient.GetFromJsonAsync<JsonElement>(
            $"/api/v1/entries/sgv.json?find[date]={Date}&count=10");
        return rows.EnumerateArray().Select(r => r.GetProperty("sgv").GetDouble()).ToList();
    }

    private async Task<List<double>> V4SgvsAsync(string device = Device)
    {
        var page = await AuthenticatedClient.GetFromJsonAsync<JsonElement>("/api/v4/glucose/sensor?limit=100");
        return page.GetProperty("data").EnumerateArray()
            .Where(g => g.GetProperty("device").GetString() == device)
            .Select(g => g.GetProperty("mgdl").GetDouble())
            .ToList();
    }

    private async Task<string> RowVersionAsync()
    {
        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tenant, false);"
            + "SELECT xmin::text FROM sensor_glucose WHERE device = @device AND deleted_at IS NULL;";
        cmd.Parameters.AddWithValue("tenant", Fixture.TenantId.ToString());
        cmd.Parameters.AddWithValue("device", Device);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.NextResultAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        var version = reader.GetString(0);
        (await reader.ReadAsync()).Should().BeFalse();
        return version;
    }
}

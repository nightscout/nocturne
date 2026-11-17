using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Constants;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Loop's glucose upload and read-back as NightscoutKit sends them: <c>GlucoseEntry.dictionaryRepresentation</c>
/// serialised by <c>JSONSerialization</c> (a fractional-millisecond <c>date</c>, no <c>_id</c>), posted to
/// <c>/api/v1/entries</c> with the SHA1 of the secret in <c>api-secret</c>, and read with
/// <c>fetchGlucose</c>'s <c>find[dateString]</c> range.
/// </summary>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public class LoopEntriesUploadIntegrationTests : ApiIntegrationTestBase
{
    public LoopEntriesUploadIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private async Task<HttpClient> CreateLoopClientAsync()
    {
        const string secret = "loop-uploader-secret";
        var hashed = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(secret)));
        await using (var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync()))
        {
            await conn.OpenAsync();
            await AuthTestHelpers.SeedApiSecretGrantAsync(conn, Fixture.TenantId, Fixture.OwnerSubjectId, hashed);
        }

        var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi, "http");
        client.DefaultRequestHeaders.Add("api-secret", hashed);
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        return client;
    }

    private static string IsoSeconds(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string LoopEntryJson(DateTimeOffset at, double fractionMs, int sgv, string device) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $$"""
            {"date":{{at.ToUnixTimeMilliseconds()}}{{fractionMs.ToString(".###", CultureInfo.InvariantCulture)}},"dateString":"{{IsoSeconds(at)}}","device":"{{device}}","type":"sgv","sgv":{{sgv}},"trend":4,"direction":"Flat","trendRate":0.29999999999999999,"isCalibration":false}
            """);

    private static string LoopMeterJson(DateTimeOffset at, int mbg) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $$"""
            {"date":{{at.ToUnixTimeMilliseconds()}}.5,"dateString":"{{IsoSeconds(at)}}","device":"loop://iPhone","type":"mbg","mbg":{{mbg}},"isCalibration":false}
            """);

    private static async Task<JsonDocument> PostOkAsync(HttpClient client, string body, int expectedEcho)
    {
        using var post = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/v1/entries", post);
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        var echo = JsonDocument.Parse(content);
        echo.RootElement.GetArrayLength().Should().Be(expectedEcho);
        echo.RootElement.EnumerateArray()
            .Should()
            .OnlyContain(e => !string.IsNullOrEmpty(e.GetProperty("_id").GetString()));
        return echo;
    }

    private static async Task<int> CountAsync(HttpClient client, string type)
    {
        var response = await client.GetAsync($"/api/v1/entries/{type}.json?count=5000&find[date][$gte]=0");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var rows = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return rows.RootElement.GetArrayLength();
    }

    [Fact]
    public async Task LoopUpload_IsStored_AndServedOnEveryReadLoopAndTheDashboardUse()
    {
        using var client = await CreateLoopClientAsync();
        var now = DateTimeOffset.UtcNow;
        var t1 = now.AddMinutes(-15);
        var t2 = now.AddMinutes(-10);
        var t3 = now.AddMinutes(-5);

        var body =
            "["
            + LoopEntryJson(t1, 0.123, 101, "Dexcom G7 DXCMxx")
            + ","
            + LoopEntryJson(t2, 0.456, 102, "Dexcom G7 DXCMxx")
            + ","
            + LoopEntryJson(t3, 0.789, 103, "loop://iPhone")
            + "]";

        using var post = new StringContent(body, Encoding.UTF8, "application/json");
        var postResponse = await client.PostAsync("/api/v1/entries", post);
        var postContent = await postResponse.Content.ReadAsStringAsync();
        Log($"POST: {(int)postResponse.StatusCode} {postContent}");

        postResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var echo = JsonDocument.Parse(postContent))
        {
            echo.RootElement.GetArrayLength().Should().Be(3);
            foreach (var item in echo.RootElement.EnumerateArray())
                item.GetProperty("_id").GetString().Should().NotBeNullOrEmpty();
        }

        // NightscoutKit fetchGlucose
        var from = Uri.EscapeDataString(IsoSeconds(now.AddMinutes(-30)));
        var to = Uri.EscapeDataString(IsoSeconds(now.AddMinutes(1)));
        var fetch = await client.GetAsync(
            $"/api/v1/entries?find[dateString][$gte]={from}&find[dateString][$lte]={to}&count=100");
        await AssertLoopReadAsync(fetch, t1, t2, t3);

        await AssertLoopReadAsync(await client.GetAsync("/api/v1/entries/sgv.json?count=10"), t1, t2, t3);
        await AssertLoopReadAsync(await client.GetAsync("/api/v1/entries.json?count=10"), t1, t2, t3);

        var dashboard = await client.GetAsync(
            $"/api/v4/chartdata/dashboard?startTime={now.AddHours(-1).ToUnixTimeMilliseconds()}"
                + $"&endTime={now.AddMinutes(1).ToUnixTimeMilliseconds()}&includeHealthSeries=false");
        var dashboardContent = await dashboard.Content.ReadAsStringAsync();
        dashboard.StatusCode.Should().Be(HttpStatusCode.OK, dashboardContent);
        using (var chart = JsonDocument.Parse(dashboardContent))
        {
            chart.RootElement.GetProperty("glucoseData")
                .EnumerateArray()
                .Select(p => p.GetProperty("sgv").GetDouble())
                .Should()
                .Contain([101d, 102d, 103d]);
        }
    }

    /// <summary>
    /// Loop sends at most <c>glucoseDataLimit</c> (1000) readings from its last acknowledged anchor and
    /// re-sends the same batch until one is answered 200 with one object per reading, so a full batch
    /// of readings already stored has to be acknowledged for the anchor to move on.
    /// </summary>
    [Fact]
    public async Task LoopResendingAFullBatchAlreadyStored_IsAcknowledged_AndStoresEachReadingOnce()
    {
        using var client = await CreateLoopClientAsync();
        var newest = DateTimeOffset.UtcNow.AddMinutes(-5);
        var body =
            "["
            + string.Join(
                ",",
                Enumerable.Range(0, 1000)
                    .Select(i => LoopEntryJson(newest.AddMinutes(-5.0 * i), 0.25, 80 + i % 150, "Dexcom G7 DXCMxx")))
            + "]";

        for (var attempt = 0; attempt < 2; attempt++)
            (await PostOkAsync(client, body, 1000)).Dispose();

        (await CountAsync(client, "sgv")).Should().Be(1000);
    }

    /// <summary>
    /// A user-entered reading reaches Loop's glucose batch as an <c>mbg</c> entry alongside the CGM's
    /// <c>sgv</c> entries, and a retried batch carries the readings it already sent ahead of new ones.
    /// </summary>
    [Fact]
    public async Task LoopBatchMixingMeterAndSensorReadings_StoredAndNew_EchoesOneObjectPerReading()
    {
        using var client = await CreateLoopClientAsync();
        var now = DateTimeOffset.UtcNow;

        var first =
            "["
            + LoopEntryJson(now.AddMinutes(-20), 0.1, 110, "Dexcom G7 DXCMxx")
            + "," + LoopMeterJson(now.AddMinutes(-18), 115)
            + "," + LoopEntryJson(now.AddMinutes(-15), 0.2, 112, "Dexcom G7 DXCMxx")
            + "]";
        using (var echo = await PostOkAsync(client, first, 3))
        {
            echo.RootElement.EnumerateArray()
                .Select(e => e.GetProperty("type").GetString())
                .Should()
                .Equal("sgv", "mbg", "sgv");
        }

        var retried =
            "["
            + LoopEntryJson(now.AddMinutes(-20), 0.1, 110, "Dexcom G7 DXCMxx")
            + "," + LoopMeterJson(now.AddMinutes(-18), 115)
            + "," + LoopEntryJson(now.AddMinutes(-15), 0.2, 112, "Dexcom G7 DXCMxx")
            + "," + LoopEntryJson(now.AddMinutes(-10), 0.3, 114, "Dexcom G7 DXCMxx")
            + "," + LoopMeterJson(now.AddMinutes(-8), 120)
            + "," + LoopEntryJson(now.AddMinutes(-5), 0.4, 116, "Dexcom G7 DXCMxx")
            + "]";
        using (var echo = await PostOkAsync(client, retried, 6))
        {
            echo.RootElement.EnumerateArray()
                .Select(e => e.GetProperty("type").GetString())
                .Should()
                .Equal("sgv", "mbg", "sgv", "sgv", "mbg", "sgv");
        }

        (await CountAsync(client, "sgv")).Should().Be(4);
        (await CountAsync(client, "mbg")).Should().Be(2);
    }

    private async Task AssertLoopReadAsync(
        HttpResponseMessage response,
        DateTimeOffset oldest,
        DateTimeOffset middle,
        DateTimeOffset newest)
    {
        var content = await response.Content.ReadAsStringAsync();
        Log($"GET {response.RequestMessage?.RequestUri?.PathAndQuery}: {(int)response.StatusCode} {content}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(content);
        var rows = doc.RootElement.EnumerateArray().ToList();
        rows.Should().HaveCount(3);

        rows.Select(r => r.GetProperty("sgv").GetDouble()).Should().Equal(103d, 102d, 101d);
        rows.Select(r => r.GetProperty("date").GetInt64())
            .Should()
            .Equal(
                newest.ToUnixTimeMilliseconds() + 1,
                middle.ToUnixTimeMilliseconds(),
                oldest.ToUnixTimeMilliseconds());
        rows.Should().OnlyContain(r => !string.IsNullOrEmpty(r.GetProperty("_id").GetString()));
        rows.Select(r => r.GetProperty("direction").GetString()).Should().OnlyContain(d => d == "Flat");
    }
}

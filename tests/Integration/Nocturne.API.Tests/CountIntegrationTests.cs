using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// <c>/api/v1/count/{storage}/where</c> against the API running in-process on real PostgreSQL: the
/// legacy aggregate shape and the bracketed <c>find[...]</c> filters.
/// </summary>
[Trait("Category", "Integration")]
public class CountIntegrationTests : ApiIntegrationTestBase
{
    public CountIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Theory]
    [InlineData("/api/v1/count/entries/where")]
    [InlineData("/api/v1/count/treatments/where")]
    [InlineData("/api/v1/count/devicestatus/where")]
    [InlineData("/api/v1/count/food/where")]
    [InlineData("/api/v1/count/profile/where")]
    public async Task Count_NothingStored_AnswersAnEmptyArray(string path)
    {
        (await GetRawAsync(path)).Should().Be("[]");
    }

    [Fact]
    public async Task CountEntries_AnswersTheAggregateRow()
    {
        await SeedEntriesAsync();

        (await GetRawAsync("/api/v1/count/entries/where")).Should().Be("""[{"_id":null,"count":3}]""");
    }

    [Theory]
    [InlineData("find[type]=mbg", 1)]
    [InlineData("find[type]=sgv", 2)]
    [InlineData("find%5Btype%5D=sgv", 2)]
    [InlineData("find[sgv][$gte]=150", 1)]
    [InlineData("find[type]=sgv&find[sgv][$lt]=150", 1)]
    public async Task CountEntries_BracketedFind_Filters(string query, long expected)
    {
        await SeedEntriesAsync();

        var rows = await GetRowsAsync($"/api/v1/count/entries/where?{query}");

        rows.Should().ContainSingle().Which.GetProperty("count").GetInt64().Should().Be(expected);
    }

    [Fact]
    public async Task CountEntries_BracketedFindMatchingNothing_AnswersAnEmptyArray()
    {
        await SeedEntriesAsync();

        (await GetRawAsync("/api/v1/count/entries/where?find[type]=cal")).Should().Be("[]");
    }

    [Fact]
    public async Task CountTreatments_AnswersTheAggregateRow()
    {
        var now = DateTimeOffset.UtcNow;
        var treatments = new[]
        {
            new { eventType = "Note", notes = "synthetic a", created_at = now.AddMinutes(-10).ToString("O") },
            new { eventType = "Note", notes = "synthetic b", created_at = now.AddMinutes(-5).ToString("O") },
        };
        (await AuthenticatedClient.PostAsJsonAsync("/api/v1/treatments", treatments)).EnsureSuccessStatusCode();

        (await GetRawAsync("/api/v1/count/treatments/where?find[eventType]=Note"))
            .Should().Be("""[{"_id":null,"count":2}]""");
    }

    [Fact]
    public async Task CountDeviceStatus_BracketedFind_Filters()
    {
        var now = DateTimeOffset.UtcNow;
        var statuses = new[]
        {
            new { device = "synthetic://pump", created_at = now.AddHours(-3).ToString("O"), pump = new { reservoir = 100 } },
            new { device = "synthetic://pump", created_at = now.AddMinutes(-5).ToString("O"), pump = new { reservoir = 90 } },
        };
        foreach (var status in statuses)
            (await AuthenticatedClient.PostAsJsonAsync("/api/v1/devicestatus", status)).EnsureSuccessStatusCode();

        var since = Uri.EscapeDataString(now.AddHours(-1).ToString("O"));
        var until = Uri.EscapeDataString(now.AddHours(-2).ToString("O"));

        (await GetRawAsync("/api/v1/count/devicestatus/where")).Should().Be("""[{"_id":null,"count":2}]""");
        (await GetRawAsync($"/api/v1/count/devicestatus/where?find[created_at][$gte]={since}"))
            .Should().Be("""[{"_id":null,"count":1}]""");
        (await GetRawAsync($"/api/v1/count/devicestatus/where?find[created_at][$gte]={now.AddHours(1).ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}"))
            .Should().Be("[]");
        (await GetRawAsync($"/api/v1/count/devicestatus/where?find[created_at][$lt]={until}"))
            .Should().Be("""[{"_id":null,"count":1}]""");
    }

    [Theory]
    [InlineData("", 3)]
    [InlineData("find[device]=synthetic://loop-a", 2)]
    [InlineData("find[device]=synthetic://loop-b", 1)]
    [InlineData("find[device]=synthetic://none", 0)]
    public async Task CountDeviceStatus_EqualsTheListLength(string query, int expected)
    {
        var now = DateTimeOffset.UtcNow;
        object Loop(DateTimeOffset at) => new { name = "Loop", timestamp = at.ToString("O"), iob = new { iob = 1.5, timestamp = at.ToString("O") } };
        var statuses = new object[]
        {
            new { device = "synthetic://loop-a", created_at = now.AddMinutes(-15).ToString("O"), loop = Loop(now.AddMinutes(-15)), pump = new { reservoir = 100 } },
            new { device = "synthetic://loop-a", created_at = now.AddMinutes(-10).ToString("O"), loop = Loop(now.AddMinutes(-10)) },
            new { device = "synthetic://loop-b", created_at = now.AddMinutes(-5).ToString("O"), pump = new { reservoir = 90 } },
        };
        foreach (var status in statuses)
            (await AuthenticatedClient.PostAsJsonAsync("/api/v1/devicestatus", status)).EnsureSuccessStatusCode();

        var separator = query.Length == 0 ? "" : "&";
        var list = await GetRowsAsync($"/api/v1/devicestatus?count=100{separator}{query}");
        var counted = await GetRowsAsync($"/api/v1/count/devicestatus/where?{query}");

        list.Should().HaveCount(expected);
        var count = counted.Length == 0 ? 0 : counted.Single().GetProperty("count").GetInt64();
        count.Should().Be(list.Length);
    }

    private async Task SeedEntriesAsync()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entries = new object[]
        {
            new { type = "sgv", sgv = 120, date = now - 600_000, device = "synthetic" },
            new { type = "sgv", sgv = 180, date = now - 300_000, device = "synthetic" },
            new { type = "mbg", mbg = 140, date = now - 150_000, device = "synthetic" },
        };
        (await AuthenticatedClient.PostAsJsonAsync("/api/v1/entries", entries)).EnsureSuccessStatusCode();
    }

    private async Task<string> GetRawAsync(string path)
    {
        var response = await AuthenticatedClient.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<JsonElement[]> GetRowsAsync(string path)
    {
        var body = await GetRawAsync(path);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array, body);
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    }
}

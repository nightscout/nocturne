using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.V4;

/// <summary>
/// A member or credential limited to the last 24 hours reads no older night of compression lows:
/// neither the suggestions already saved, nor a detection it runs itself (#2156).
/// </summary>
[Trait("Category", "Integration")]
public class CompressionLowHistoryClampIntegrationTests : ApiIntegrationTestBase
{
    private static readonly double[] Night =
    [
        .. Enumerable.Repeat(110d, 12),
        110, 110, 110, 100, 85, 65, 60, 80, 105, 110, 110, 110,
        .. Enumerable.Repeat(110d, 12),
    ];

    private const long FiveMinutes = 5 * 60 * 1000;

    public CompressionLowHistoryClampIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    /// <summary>
    /// The night three days ago, with a compression low at 03:00 UTC, read once so a test that spans
    /// midnight UTC seeds and detects the same night. With no timezone in the settings or the profile,
    /// detection reads the night in UTC.
    /// </summary>
    private readonly DateOnly _oldNight = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3));

    private async Task SeedOldNightAsync(HttpClient owner)
    {
        var start = new DateTimeOffset(_oldNight.AddDays(1).ToDateTime(new TimeOnly(1, 30)), TimeSpan.Zero)
            .ToUnixTimeMilliseconds();
        var entries = Night.Select((sgv, i) => new Entry
        {
            Sgv = sgv,
            Type = "sgv",
            Mills = start + i * FiveMinutes,
            DateString = DateTimeOffset.FromUnixTimeMilliseconds(start + i * FiveMinutes)
                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Direction = "Flat",
            Device = "test-device",
        }).ToArray();

        var response = await owner.PostAsJsonAsync("/api/v1/entries", entries);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<int> DetectAsync(HttpClient client, DateOnly nightOf)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v4/compression-lows/detect", new { nightOf = nightOf.ToString("yyyy-MM-dd") });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["totalSuggestionsCreated"]!.GetValue<int>();
    }

    private static async Task<JsonArray> SuggestionsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v4/compression-lows/suggestions");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
    }

    [Fact]
    public async Task A_clamped_member_reads_no_suggestion_of_an_older_night()
    {
        using var owner = CreateAuthenticatedClient();
        using var clamped = await CreateHistoryClampedClientAsync();
        await SeedOldNightAsync(owner);

        (await DetectAsync(owner, _oldNight)).Should().BeGreaterThan(0);
        var saved = await SuggestionsAsync(owner);
        saved.Should().NotBeEmpty();

        (await SuggestionsAsync(clamped)).Should().BeEmpty(
            "a suggestion is a nadir, drop rate and recovery of a night outside the caller's 24 hours");
        var byId = await clamped.GetAsync($"/api/v4/compression-lows/suggestions/{saved[0]!["id"]!.GetValue<string>()}");
        byId.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_clamped_member_detecting_an_older_night_reads_none_of_it()
    {
        using var owner = CreateAuthenticatedClient();
        using var clamped = await CreateHistoryClampedClientAsync();
        await SeedOldNightAsync(owner);

        (await DetectAsync(clamped, _oldNight)).Should().Be(0,
            "detection runs as the caller, so it sees no reading of a night outside its 24 hours");
        (await SuggestionsAsync(owner)).Should().BeEmpty();

        (await DetectAsync(owner, _oldNight)).Should().BeGreaterThan(0,
            "the same night yields suggestions to a reader who may see it");
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Models.Responses;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// A full-history public share is not history-clamped, so it reads and writes the tenant-keyed
/// caches. Row level security hides from a share every category it was not granted, and
/// <c>notes</c>, <c>state_spans</c> and the therapy tables from every share, so whatever an owner's
/// request leaves in those caches must not reach a share.
/// </summary>
/// <remarks>
/// Each test anchors on a cold-cache share read, which only the database answers, and expects the
/// share's read after the owner's to match it.
/// </remarks>
[Trait("Category", "Integration")]
public class ShareReadsOwnerCacheIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private static readonly string[] ShareScopes = [Scope.GlucoseRead, Scope.TreatmentsRead, Scope.ReportsRead];

    [Fact]
    public async Task Share_does_not_read_owner_cached_notes_and_profile_switches_from_v1_treatments()
    {
        var now = DateTimeOffset.UtcNow;
        var treatments = new object[]
        {
            new { eventType = "Correction Bolus", insulin = 1.5, created_at = now.AddMinutes(-30).ToString("O") },
            new { eventType = "Note", notes = "owner-only note", created_at = now.AddMinutes(-20).ToString("O") },
            new { eventType = "Profile Switch", profile = "OwnerNight", duration = 60, created_at = now.AddMinutes(-10).ToString("O") },
        };
        (await AuthenticatedClient.PostAsJsonAsync("/api/v1/treatments", treatments)).EnsureSuccessStatusCode();
        using var share = await CreateFullHistoryShareClientAsync();
        const string path = "/api/v1/treatments?count=10";

        ClearCaches();
        var cold = await EventTypesAsync(share, path);
        cold.Should().Equal(["Correction Bolus"], "row level security shows a share only the bolus");

        ClearCaches();
        var owner = await EventTypesAsync(AuthenticatedClient, path);
        owner.Should().BeEquivalentTo(["Correction Bolus", "Note", "Profile Switch"]);

        var warm = await EventTypesAsync(share, path);
        warm.Should().Equal(cold, "the owner's cached treatments must not reach the share");
    }

    [Fact]
    public async Task Share_does_not_read_the_owner_cached_therapy_timezone_from_hourly_patterns()
    {
        var profile = new
        {
            defaultProfile = "Default",
            store = new Dictionary<string, object>
            {
                ["Default"] = new
                {
                    dia = 4,
                    carbratio = new[] { new { time = "00:00", value = 10, timeAsSeconds = 0 } },
                    sens = new[] { new { time = "00:00", value = 50, timeAsSeconds = 0 } },
                    basal = new[] { new { time = "00:00", value = 0.85, timeAsSeconds = 0 } },
                    target_low = new[] { new { time = "00:00", value = 80, timeAsSeconds = 0 } },
                    target_high = new[] { new { time = "00:00", value = 120, timeAsSeconds = 0 } },
                    units = "mg/dl",
                    timezone = "Pacific/Auckland",
                },
            },
            startDate = DateTime.UtcNow.AddDays(-1).ToString("o"),
        };
        (await AuthenticatedClient.PostAsJsonAsync("/api/v1/profile", profile)).EnsureSuccessStatusCode();
        using var share = await CreateFullHistoryShareClientAsync();
        var path = "/api/v4/statistics/hourly-patterns"
            + $"?startDate={DateTime.UtcNow.AddDays(-1):yyyy-MM-ddTHH:mm:ssZ}&endDate={DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}";

        ClearCaches();
        var cold = await TimeZoneUnavailableReasonAsync(share, path);
        cold.Should().NotBe("null", "row level security hides the therapy settings from a share");

        ClearCaches();
        (await TimeZoneUnavailableReasonAsync(AuthenticatedClient, path)).Should().Be("null");

        var warm = await TimeZoneUnavailableReasonAsync(share, path);
        warm.Should().Be(cold, "the owner's cached therapy settings must not reach the share");
    }

    [Fact]
    public async Task Glucose_only_share_does_not_read_the_owner_cached_treatment_summary_from_multi_period_statistics()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = Enumerable.Range(1, 12)
            .Select(i => new { type = "sgv", sgv = 100 + i, date = now.AddMinutes(-5 * i).ToUnixTimeMilliseconds() })
            .ToArray();
        (await AuthenticatedClient.PostAsJsonAsync("/api/v1/entries", entries)).EnsureSuccessStatusCode();
        var bolus = new[] { new { eventType = "Correction Bolus", insulin = 2.0, created_at = now.AddMinutes(-15).ToString("O") } };
        (await AuthenticatedClient.PostAsJsonAsync("/api/v1/treatments", bolus)).EnsureSuccessStatusCode();
        using var share = await CreateFullHistoryShareClientAsync([Scope.GlucoseRead]);
        const string path = "/api/v4/statistics/periods";

        ClearCaches();
        var cold = await LastDayBolusCountAsync(share, path);
        cold.Should().BeNull("a glucose-only share reads no treatment summary");

        ClearCaches();
        (await LastDayBolusCountAsync(AuthenticatedClient, path)).Should().Be(1);

        var warm = await LastDayBolusCountAsync(share, path);
        warm.Should().Be(cold, "the owner's cached treatment summary must not reach the share");
    }

    private Task<HttpClient> CreateFullHistoryShareClientAsync() => CreateFullHistoryShareClientAsync(ShareScopes);

    private async Task<HttpClient> CreateFullHistoryShareClientAsync(string[] scopes)
    {
        using var owner = AuthTestHelpers.CreateBearerClient(
            Fixture, await AuthTestHelpers.IssueSessionTokenAsync(Fixture, Fixture.OwnerSubjectId));
        var link = await (await owner.PostAsync("/api/v4/share/rotate", null))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<ShareLinkDto>();
        (await owner.PutAsJsonAsync("/api/v4/share/scopes", new { scopes }))
            .EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync("/api/v4/share/full-history", new { fullHistory = true }))
            .EnsureSuccessStatusCode();

        var client = Fixture.CreateApexClient();
        client.DefaultRequestHeaders.Host = new Uri(link!.Url!).Authority;
        return client;
    }

    private void ClearCaches() => ((MemoryCache)Fixture.Services.GetRequiredService<IMemoryCache>()).Clear();

    private static async Task<List<string?>> EventTypesAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.EnumerateArray()
            .Select(treatment => treatment.TryGetProperty("eventType", out var type) ? type.GetString() : null)
            .ToList();
    }

    private static async Task<int?> LastDayBolusCountAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(body);
        using var document = JsonDocument.Parse(body);
        var lastDay = document.RootElement.GetProperty("lastDay");
        lastDay.GetProperty("hasSufficientData").GetBoolean().Should().BeTrue(body);
        return lastDay.TryGetProperty("treatmentSummary", out var summary) && summary.ValueKind != JsonValueKind.Null
            ? summary.GetProperty("bolusCount").GetInt32()
            : null;
    }

    private static async Task<string> TimeZoneUnavailableReasonAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("timeZoneUnavailableReason", out var reason)
            ? reason.GetRawText()
            : "null";
    }
}

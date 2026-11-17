using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Authorization;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// The statistics endpoints admitted on <c>glucose.read</c> or <c>reports.read</c> also summarise insulin and carbs.
/// Category row level security applies only to shares, so for a member token nothing below the
/// controller hides the treatments: the response must be narrowed by the caller's scopes.
/// </summary>
[Collection(ApiIntegrationShards.One)]
[Trait("Category", "Integration")]
public class StatisticsTreatmentScopeIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private const string PeriodsPath = "/api/v4/statistics/periods";

    [Fact]
    public async Task Glucose_only_token_reads_multi_period_statistics_without_treatment_fields()
    {
        await SeedGlucoseAndTreatmentsAsync();
        using var glucoseOnly = await CreateTokenClientAsync(Scope.GlucoseRead);

        ClearCaches();
        var lastDay = await LastDayAsync(glucoseOnly);

        Absent(lastDay, "treatmentSummary").Should().BeTrue();
        Absent(lastDay, "insulinDelivery").Should().BeTrue();
        lastDay.GetProperty("treatmentCount").GetInt32().Should().Be(0);
        lastDay.GetProperty("entryCount").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Token_with_treatments_read_still_reads_treatment_fields()
    {
        await SeedGlucoseAndTreatmentsAsync();
        using var both = await CreateTokenClientAsync(Scope.GlucoseRead, Scope.TreatmentsRead);

        ClearCaches();
        var lastDay = await LastDayAsync(both);

        lastDay.GetProperty("treatmentSummary").GetProperty("bolusCount").GetInt32().Should().Be(1);
        lastDay.GetProperty("insulinDelivery").ValueKind.Should().Be(JsonValueKind.Object);
        lastDay.GetProperty("treatmentCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Glucose_only_token_does_not_read_owner_cached_treatment_fields()
    {
        await SeedGlucoseAndTreatmentsAsync();
        using var glucoseOnly = await CreateTokenClientAsync(Scope.GlucoseRead);

        ClearCaches();
        var owner = await LastDayAsync(AuthenticatedClient);
        owner.GetProperty("treatmentSummary").GetProperty("bolusCount").GetInt32().Should().Be(1);

        var warm = await LastDayAsync(glucoseOnly);
        Absent(warm, "treatmentSummary").Should().BeTrue();
        Absent(warm, "insulinDelivery").Should().BeTrue();

        var ownerAgain = await LastDayAsync(AuthenticatedClient);
        ownerAgain.GetProperty("treatmentSummary").GetProperty("bolusCount").GetInt32()
            .Should().Be(1, "redacting a reader's copy must leave the cached value whole");
    }

    [Fact]
    public async Task Reports_only_token_reads_multi_period_treatment_aggregates()
    {
        await SeedGlucoseAndTreatmentsAsync();
        using var reportsOnly = await CreateTokenClientAsync(Scope.ReportsRead);

        ClearCaches();
        var lastDay = await LastDayAsync(reportsOnly);

        lastDay.GetProperty("treatmentSummary").GetProperty("bolusCount").GetInt32().Should().Be(1);
        lastDay.GetProperty("insulinDelivery").ValueKind.Should().Be(JsonValueKind.Object);
        lastDay.GetProperty("treatmentCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Reports_only_token_reads_punch_card_insulin_and_carbs()
    {
        await SetPatientTimezoneAsync("UTC");
        await SeedGlucoseAndTreatmentsAsync();
        using var reportsOnly = await CreateTokenClientAsync(Scope.ReportsRead);
        var today = DateTime.UtcNow.Date;
        var path = $"/api/v4/statistics/punch-card?startDate={today.AddDays(-1):yyyy-MM-dd}&endDate={today.AddDays(1):yyyy-MM-dd}";

        var response = await PunchCardAsync(reportsOnly, path);

        response.GetProperty("globalMaxCarbs").GetDouble().Should().BeGreaterThan(0);
        response.GetProperty("globalMaxInsulin").GetDouble().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Glucose_only_token_reads_punch_card_without_insulin_or_carbs()
    {
        await SetPatientTimezoneAsync("UTC");
        await SeedGlucoseAndTreatmentsAsync();
        using var glucoseOnly = await CreateTokenClientAsync(Scope.GlucoseRead);
        var today = DateTime.UtcNow.Date;
        var path = $"/api/v4/statistics/punch-card?startDate={today.AddDays(-1):yyyy-MM-dd}&endDate={today.AddDays(1):yyyy-MM-dd}";

        var owner = await PunchCardAsync(AuthenticatedClient, path);
        owner.GetProperty("globalMaxCarbs").GetDouble().Should().BeGreaterThan(0);
        owner.GetProperty("globalMaxInsulin").GetDouble().Should().BeGreaterThan(0);

        var response = await PunchCardAsync(glucoseOnly, path);
        response.GetProperty("globalMaxCarbs").GetDouble().Should().Be(0);
        response.GetProperty("globalMaxInsulin").GetDouble().Should().Be(0);
        response.GetProperty("globalMaxCarbInsulinDiff").GetDouble().Should().Be(0);
        var days = response.GetProperty("months").EnumerateArray()
            .SelectMany(month => month.GetProperty("days").EnumerateArray())
            .ToList();
        days.Sum(day => day.GetProperty("totalReadings").GetInt32()).Should().BeGreaterThan(0);
        days.Should().OnlyContain(day =>
            day.GetProperty("totalCarbs").GetDouble() == 0
            && day.GetProperty("totalInsulin").GetDouble() == 0
            && day.GetProperty("totalBolus").GetDouble() == 0
            && day.GetProperty("totalBasal").GetDouble() == 0
            && day.GetProperty("carbToInsulinRatio").ValueKind == JsonValueKind.Null);
    }

    private Task SetPatientTimezoneAsync(string timezone) =>
        WithTenantScopeAsync(async services =>
        {
            var patients = services.GetRequiredService<IPatientRecordRepository>();
            var patient = await patients.GetOrCreateAsync();
            patient.Timezone = timezone;
            return await patients.UpdateAsync(patient, WriteOrigin.Live);
        });

    private async Task SeedGlucoseAndTreatmentsAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = Enumerable.Range(1, 12)
            .Select(i => new { type = "sgv", sgv = 100 + i, date = now.AddMinutes(-5 * i).ToUnixTimeMilliseconds() })
            .ToArray();
        (await AuthenticatedClient.PostAsJsonAsync("/api/v1/entries", entries)).EnsureSuccessStatusCode();
        var treatments = new object[]
        {
            new { eventType = "Correction Bolus", insulin = 2.0, created_at = now.AddMinutes(-15).ToString("O") },
            new { eventType = "Carb Correction", carbs = 30, created_at = now.AddMinutes(-25).ToString("O") },
        };
        (await AuthenticatedClient.PostAsJsonAsync("/api/v1/treatments", treatments)).EnsureSuccessStatusCode();
    }

    private async Task<HttpClient> CreateTokenClientAsync(params string[] scopes)
    {
        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (subjectId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Statistics scope member");
        var accessToken = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);

        using var member = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, accessToken);
        var response = await member.PostAsJsonAsync("/api/auth/direct-grants", new { label = "statistics-scope", scopes });
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(body);
        var token = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("token").GetString()!;
        return AuthTestHelpers.CreateBearerClient(Fixture, token);
    }

    private static bool Absent(JsonElement element, string property) =>
        !element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null;

    private void ClearCaches() => ((MemoryCache)Fixture.Services.GetRequiredService<IMemoryCache>()).Clear();

    private static async Task<JsonElement> LastDayAsync(HttpClient client)
    {
        var response = await client.GetAsync(PeriodsPath);
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(body);
        var lastDay = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("lastDay");
        lastDay.GetProperty("hasSufficientData").GetBoolean().Should().BeTrue(body);
        return lastDay;
    }

    private static async Task<JsonElement> PunchCardAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(body);
        return JsonSerializer.Deserialize<JsonElement>(body);
    }
}

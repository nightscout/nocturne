using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Analytics;

/// <summary>
/// <c>GET /api/v4/loop-analysis/daily-profile</c> end to end on PostgreSQL: a week of algorithm
/// temp basals at 1.5 U/h against a 1.0 U/h schedule in Sydney, compared with another week.
/// </summary>
[Trait("Category", "Integration")]
public class LoopAnalysisDailyProfileIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : LoopAnalysisIntegrationTestBase(fixture, output)
{
    private const string Path = "/api/v4/loop-analysis/daily-profile";

    private static string Query(string query) => $"{Path}?{query}";

    [Fact]
    public async Task Returns_the_primary_and_the_previous_week()
    {
        await SeedProfileAsync("Australia/Sydney");
        await SeedWeekAsync(PreviousWeekStart, tempRate: 0.5);
        await SeedWeekAsync(WeekStart, tempRate: 1.5);
        await SeedTemporaryTargetsAsync(WeekStart);

        var body = await GetOkAsync(AuthenticatedClient, Query($"{Week}&compare=previous"));

        body.GetProperty("historyClamped").GetBoolean().Should().BeFalse();
        Absent(body, "timeZoneUnavailableReason").Should().BeTrue();
        body.GetProperty("ignoredFilters").GetArrayLength().Should().Be(0);
        body.GetProperty("withheldSeries").GetArrayLength().Should().Be(0);

        var primaryRange = body.GetProperty("primaryRange");
        primaryRange.GetProperty("startDate").GetString().Should().Be("2024-06-10");
        primaryRange.GetProperty("endDate").GetString().Should().Be("2024-06-16");
        primaryRange.GetProperty("from").GetDateTime().ToUniversalTime().Should().Be(WeekStart);
        primaryRange.GetProperty("shortened").GetBoolean().Should().BeFalse();
        var comparisonRange = body.GetProperty("comparisonRange");
        comparisonRange.GetProperty("startDate").GetString().Should().Be("2024-06-03");
        comparisonRange.GetProperty("endDate").GetString().Should().Be("2024-06-09");

        var applied = body.GetProperty("appliedFilters");
        applied.GetProperty("days").GetArrayLength().Should().Be(7);
        applied.GetProperty("excludeIllness").GetBoolean().Should().BeTrue();
        applied.GetProperty("excludeOpenLoop").GetBoolean().Should().BeTrue();
        applied.GetProperty("excludeOverrides").GetBoolean().Should().BeFalse();

        var primary = body.GetProperty("primary");
        primary.GetProperty("slots").GetArrayLength().Should().Be(288);
        primary.GetProperty("days").GetArrayLength().Should().Be(7);
        body.GetProperty("profileNames").EnumerateArray().Select(n => n.GetString()).Should().Equal("Default");

        var ten = primary.GetProperty("slots")[120];
        ten.GetProperty("glucose").GetProperty("p50").GetDouble().Should().Be(120);
        ten.GetProperty("scheduledBasal").GetProperty("p50").GetDouble().Should().Be(1.0);
        ten.GetProperty("deliveredBasal").GetProperty("p50").GetDouble().Should().Be(1.5);
        ten.GetProperty("deliveredBasal").GetProperty("n").GetInt32().Should().Be(7);
        ten.GetProperty("netLoopCorrection").GetProperty("mean").GetDouble().Should().Be(0.5);
        ten.GetProperty("targetLow").GetDouble().Should().Be(144.146, "an 8 mmol/L temporary target ran at 10:00 every day");
        primary.GetProperty("slots")[132].GetProperty("targetLow").GetDouble().Should().Be(90);

        var three = primary.GetProperty("slots")[36];
        three.GetProperty("netLoopCorrection").GetProperty("automaticBolusMean").GetDouble()
            .Should().Be(2.4, "0.2 U over five minutes every day");
        primary.GetProperty("slots")[96].GetProperty("manualBoluses").GetProperty("meanPerDay").GetDouble().Should().Be(3);

        var summary = primary.GetProperty("summary");
        summary.GetProperty("daysWithData").GetInt32().Should().Be(7);
        summary.GetProperty("deliveredToScheduledRatio").GetDouble().Should().Be(1.5);
        summary.GetProperty("netLoopCorrectionPerDay").GetDouble().Should().Be(12.2);
        var insulin = summary.GetProperty("insulin");
        insulin.GetProperty("total").GetProperty("mean").GetDouble().Should().Be(39.2, "36 U basal, 0.2 U automatic and 3 U manual a day");
        insulin.GetProperty("total").GetProperty("n").GetInt32().Should().Be(7);
        insulin.GetProperty("deliveredBasal").GetDouble().Should().Be(36);
        insulin.GetProperty("bolus").GetDouble().Should().Be(3.2);

        var comparison = body.GetProperty("comparison");
        comparison.GetProperty("slots")[120].GetProperty("netLoopCorrection").GetProperty("mean").GetDouble().Should().Be(-0.5);
        comparison.GetProperty("days").GetArrayLength().Should().Be(7);
    }

    [Fact]
    public async Task Fetches_a_custom_comparison_that_does_not_touch_the_primary_on_its_own()
    {
        await SeedProfileAsync("Australia/Sydney");
        await SeedWeekAsync(PreviousWeekStart.AddDays(-7), tempRate: 0.5);
        await SeedWeekAsync(WeekStart, tempRate: 1.5);

        var body = await GetOkAsync(AuthenticatedClient,
            Query($"{Week}&compare=custom&compareStartDate=2024-05-27&compareEndDate=2024-06-02"));

        body.GetProperty("primary").GetProperty("slots")[120].GetProperty("netLoopCorrection")
            .GetProperty("mean").GetDouble().Should().Be(0.5);
        var comparison = body.GetProperty("comparison");
        comparison.GetProperty("slots")[120].GetProperty("netLoopCorrection").GetProperty("mean").GetDouble().Should().Be(-0.5);
        comparison.GetProperty("summary").GetProperty("daysWithData").GetInt32().Should().Be(7);
    }

    [Fact]
    public async Task Keeps_the_most_recent_ninety_days_of_a_longer_range()
    {
        await SeedProfileAsync("Australia/Sydney");

        var body = await GetOkAsync(AuthenticatedClient, Query("startDate=2024-01-01&endDate=2024-06-30&compare=previous"));

        var primaryRange = body.GetProperty("primaryRange");
        primaryRange.GetProperty("shortened").GetBoolean().Should().BeTrue();
        primaryRange.GetProperty("startDate").GetString().Should().Be("2024-04-02");
        body.GetProperty("comparisonRange").GetProperty("endDate").GetString().Should().Be("2024-04-01");
        body.GetProperty("primary").GetProperty("days").GetArrayLength().Should().Be(90);
    }

    [Fact]
    public async Task Accepts_ninety_local_days_across_a_fall_back_transition()
    {
        await SeedProfileAsync("Australia/Sydney");

        var body = await GetOkAsync(AuthenticatedClient, Query("startDate=2024-03-01&endDate=2024-05-29"));

        var primaryRange = body.GetProperty("primaryRange");
        primaryRange.GetProperty("shortened").GetBoolean().Should().BeFalse();
        (primaryRange.GetProperty("to").GetDateTime() - primaryRange.GetProperty("from").GetDateTime())
            .Should().Be(TimeSpan.FromDays(90) + TimeSpan.FromHours(1));
        body.GetProperty("primary").GetProperty("days").GetArrayLength().Should().Be(90);
    }

    [Theory]
    [InlineData("startDate=2024-06-16&endDate=2024-06-10")]
    [InlineData("endDate=2024-06-16")]
    [InlineData("startDate=2024-06-10&endDate=2024-06-16&compare=custom")]
    [InlineData("startDate=2024-06-10&endDate=2024-06-16&compareStartDate=2024-06-01&compareEndDate=2024-06-07")]
    public async Task Rejects_dates_that_do_not_make_a_range(string query)
    {
        var response = await AuthenticatedClient.GetAsync(Query(query));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(new[] { Scope.ReportsRead, Scope.GlucoseRead, Scope.TreatmentsRead }, true, true, false)]
    [InlineData(new[] { Scope.ReportsRead, Scope.TreatmentsRead, Scope.TherapyRead, Scope.DevicesRead }, false, true, true)]
    [InlineData(new[] { Scope.ReportsRead, Scope.GlucoseRead, Scope.TherapyRead }, true, false, true)]
    public async Task Narrows_each_category_independently(string[] scopes, bool glucose, bool treatments, bool therapy)
    {
        await SeedProfileAsync("Australia/Sydney");
        await SeedWeekAsync(WeekStart, tempRate: 1.5);
        using var client = await CreateTokenClientAsync(scopes);

        var body = await GetOkAsync(client, Query($"{Week}&excludeIllness=true"));

        var ten = body.GetProperty("primary").GetProperty("slots")[120];
        Absent(ten, "glucose").Should().Be(!glucose);
        Absent(ten, "deliveredBasal").Should().Be(!treatments);
        Absent(ten, "iob").Should().Be(!treatments);
        Absent(ten, "scheduledBasal").Should().Be(!(therapy && treatments));
        Absent(ten, "targetLow").Should().Be(!(therapy && treatments));
        Absent(body.GetProperty("primary").GetProperty("summary"), "insulin").Should().Be(!treatments);

        var withheld = body.GetProperty("withheldSeries").EnumerateArray().Select(s => s.GetString()).ToList();
        withheld.Contains("Glucose").Should().Be(!glucose);
        withheld.Contains("DeliveredBasal").Should().Be(!treatments);
        withheld.Contains("InsulinSummary").Should().Be(!treatments);
        withheld.Contains("ScheduledBasal").Should().Be(!(therapy && treatments));
        withheld.Contains("ProfileNames").Should().Be(!therapy);

        var ignored = body.GetProperty("ignoredFilters").EnumerateArray().Select(f => f.GetString()).ToList();
        ignored.Should().Equal(treatments ? [] : ["excludeIllness"], "only a parameter the caller sent is reported");
        body.GetProperty("appliedFilters").GetProperty("excludeIllness").GetBoolean().Should().Be(treatments);
    }

    [Fact]
    public async Task A_history_clamped_member_is_analysed_over_its_last_24_hours_whatever_it_asks_for()
    {
        await SeedProfileAsync("Australia/Sydney");
        using var clamped = await CreateHistoryClampedClientAsync();
        var now = DateTime.UtcNow;

        var body = await GetOkAsync(clamped, Query($"{Week}&compare=previous"));

        body.GetProperty("historyClamped").GetBoolean().Should().BeTrue();
        var primaryRange = body.GetProperty("primaryRange");
        primaryRange.GetProperty("from").GetDateTime().ToUniversalTime().Should().BeCloseTo(now.AddHours(-24), TimeSpan.FromMinutes(5));
        primaryRange.GetProperty("to").GetDateTime().ToUniversalTime().Should().BeCloseTo(now, TimeSpan.FromMinutes(5));
        Absent(body, "comparisonRange").Should().BeTrue();
        Absent(body, "comparison").Should().BeTrue();
        body.GetProperty("ignoredFilters").EnumerateArray().Select(f => f.GetString())
            .Should().BeEquivalentTo("startDate", "endDate", "compare");
        body.GetProperty("primary").GetProperty("days").GetArrayLength().Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task A_share_gets_its_reason_and_no_slots()
    {
        await SeedProfileAsync("Australia/Sydney");
        await SeedWeekAsync(WeekStart, tempRate: 1.5);
        using var share = await CreateShareClientAsync();

        var body = await GetOkAsync(share, Query(Week));

        body.GetProperty("timeZoneUnavailableReason").GetString().Should().Be("Share");
        Absent(body, "primaryRange").Should().BeTrue();
        body.GetProperty("primary").GetProperty("slots").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Returns_no_slots_without_a_tenant_timezone()
    {
        await SeedWeekAsync(WeekStart, tempRate: 1.5);

        var body = await GetOkAsync(AuthenticatedClient, Query($"{Week}&compare=previous"));

        body.GetProperty("timeZoneUnavailableReason").GetString().Should().Be("NotConfigured");
        Absent(body, "primaryRange").Should().BeTrue();
        body.GetProperty("primary").GetProperty("slots").GetArrayLength().Should().Be(0);
        body.GetProperty("primary").GetProperty("days").GetArrayLength().Should().Be(0);
        Absent(body, "comparison").Should().BeTrue();
    }

    /// <summary>
    /// Seven days from <paramref name="start"/>: glucose at 120 and an algorithm temp at
    /// <paramref name="tempRate"/> every five minutes, a 0.2 U automatic bolus at local 03:00 and a
    /// 3 U manual bolus at local 08:00.
    /// </summary>
    private async Task SeedWeekAsync(DateTime start, double tempRate)
    {
        using var scope = TenantScope();
        await using var context = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();

        for (var t = start; t < start.AddDays(7); t = t.AddMinutes(5))
        {
            context.SensorGlucose.Add(new SensorGlucoseEntity { Id = Guid.NewGuid(), Timestamp = t, Mgdl = 120, DataSource = "test" });
            context.TempBasals.Add(new TempBasalEntity
            {
                Id = Guid.NewGuid(), Timestamp = t, EndTimestamp = t.AddMinutes(30), Rate = tempRate,
                Origin = "Algorithm", Device = "pump", DataSource = "test",
            });
        }
        for (var day = 0; day < 7; day++)
        {
            var midnight = start.AddDays(day);
            context.Boluses.Add(new BolusEntity { Id = Guid.NewGuid(), Timestamp = midnight.AddHours(3), Insulin = 0.2, BolusKind = "Algorithm", DataSource = "test" });
            context.Boluses.Add(new BolusEntity { Id = Guid.NewGuid(), Timestamp = midnight.AddHours(8), Insulin = 3, BolusKind = "Manual", DataSource = "test" });
        }
        await context.SaveChangesAsync();
    }

    /// <summary>A 30-minute 8 mmol/L temporary target at local 10:00 on each of seven days.</summary>
    private async Task SeedTemporaryTargetsAsync(DateTime start)
    {
        var targets = Enumerable.Range(0, 7).Select(day => new
        {
            eventType = "Temporary Target",
            targetTop = 8.0,
            targetBottom = 8.0,
            units = "mmol",
            duration = 30,
            created_at = start.AddDays(day).AddHours(10).ToString("O"),
        });
        var response = await AuthenticatedClient.PostAsJsonAsync("/api/v1/treatments", targets);
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
    }
}

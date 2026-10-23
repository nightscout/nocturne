using System.Net;
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
/// <c>GET /api/v4/loop-analysis/loop-health</c> end to end on PostgreSQL: a week of Trio loop
/// cycles every five minutes in Sydney, whose suggestions record no limits, with a gap across one
/// midnight, a temp cut to maxSafeBasal and a microbolus at local 03:00 each day.
/// </summary>
[Trait("Category", "Integration")]
public class LoopAnalysisLoopHealthIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : LoopAnalysisIntegrationTestBase(fixture, output)
{
    private const string Path = "/api/v4/loop-analysis/loop-health";

    // Local 23:30 on the third day to 01:00 on the fourth.
    private static readonly DateTime GapStart = WeekStart.AddDays(2).AddHours(23.5);
    private static readonly DateTime GapEnd = WeekStart.AddDays(3).AddHours(1);

    private static string Query(string query) => $"{Path}?{query}";

    [Fact]
    public async Task Returns_the_primary_and_the_adjacent_comparison()
    {
        await SeedProfileAsync("Australia/Sydney");
        await SeedWeekAsync(PreviousWeekStart, gap: false);
        await SeedWeekAsync(WeekStart, gap: true);

        var body = await GetOkAsync(AuthenticatedClient, Query($"{Week}&compare=previous"));
        body.GetProperty("profileNames").EnumerateArray().Select(n => n.GetString()).Should().Equal("Default");

        body.GetProperty("historyClamped").GetBoolean().Should().BeFalse();
        Absent(body, "timeZoneUnavailableReason").Should().BeTrue();
        body.GetProperty("withheldSeries").GetArrayLength().Should().Be(0);
        body.GetProperty("comparisonRange").GetProperty("startDate").GetString().Should().Be("2024-06-03");
        var primary = body.GetProperty("primary");
        primary.GetProperty("days").GetArrayLength().Should().Be(7);

        var gaps = primary.GetProperty("cadence").GetProperty("loopGaps");
        gaps.GetProperty("count").GetInt32().Should().Be(1, "the gap across midnight counts once");
        gaps.GetProperty("totalMinutes").GetDouble().Should().Be(90);
        var gap = gaps.GetProperty("gaps")[0];
        gap.GetProperty("date").GetString().Should().Be("2024-06-12");
        gap.GetProperty("startMinuteOfDay").GetInt32().Should().Be(23 * 60 + 30);

        var maxBasal = primary.GetProperty("maxBasal");
        maxBasal.GetProperty("capKnown").GetProperty("percent").GetDouble().Should().Be(100);
        maxBasal.GetProperty("atCap").GetProperty("percent").GetDouble().Should().BeGreaterThan(0);
        maxBasal.GetProperty("atCap").GetProperty("hours")[3].GetProperty("percent").GetDouble()
            .Should().Be(50, "a capped temp held for half an hour on each of seven days");

        var limits = primary.GetProperty("limits");
        Absent(limits, "orefMaxBasal").Should().BeTrue("the suggestions omit the key, so the limit is unknown");
        Absent(limits, "orefMaxIob").Should().BeTrue();
        Absent(limits, "loopMaximumBasalRate").Should().BeTrue();
        primary.GetProperty("unknown").GetProperty("loopMaximumBasalRate").GetString()
            .Should().Be("NotRecordedByThisSystem", "reasons go out by name, as every Loop Analysis enum does");

        var boluses = primary.GetProperty("automaticBoluses");
        boluses.GetProperty("perDay").GetProperty("overall").GetProperty("mean").GetDouble().Should().Be(1);
        boluses.GetProperty("size").GetProperty("overall").GetProperty("p50").GetDouble().Should().Be(0.2);
        boluses.GetProperty("perDay").GetProperty("hours")[3].GetProperty("value").GetProperty("mean").GetDouble()
            .Should().Be(1);

        primary.GetProperty("sensitivityRatio").GetProperty("hours")[3].GetProperty("value").GetProperty("p50")
            .GetDouble().Should().Be(1.1);
        primary.GetProperty("suggestedNotEnacted").GetProperty("percent").GetDouble().Should().Be(0);
        primary.GetProperty("activity").GetProperty("pump").GetProperty("hours")[12].GetProperty("percent")
            .GetDouble().Should().Be(100);
        Absent(primary, "atMaxIob").Should().BeFalse("Trio is oref");

        var comparison = body.GetProperty("comparison");
        comparison.GetProperty("cadence").GetProperty("loopGaps").GetProperty("count").GetInt32().Should().Be(0);
        comparison.GetProperty("days").GetArrayLength().Should().Be(7);
    }

    [Fact]
    public async Task Keeps_the_most_recent_ninety_days_of_a_longer_range()
    {
        await SeedProfileAsync("Australia/Sydney");

        var body = await GetOkAsync(AuthenticatedClient, Query("startDate=2024-01-01&endDate=2024-06-30"));

        body.GetProperty("primaryRange").GetProperty("shortened").GetBoolean().Should().BeTrue();
        body.GetProperty("primaryRange").GetProperty("startDate").GetString().Should().Be("2024-04-02");
    }

    [Fact]
    public async Task Rejects_dates_that_do_not_make_a_range()
    {
        var response = await AuthenticatedClient.GetAsync(Query("startDate=2024-06-16&endDate=2024-06-10"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Narrows_the_response_to_the_callers_scopes()
    {
        await SeedProfileAsync("Australia/Sydney");
        await SeedWeekAsync(WeekStart, gap: true);
        using var client = await CreateTokenClientAsync(Scope.ReportsRead, Scope.GlucoseRead, Scope.TreatmentsRead);

        var body = await GetOkAsync(client, Query(Week));

        body.GetProperty("withheldSeries").EnumerateArray().Select(s => s.GetString())
            .Should().Contain(["LoopGaps", "MaxBasal", "SensitivityRatio"]).And.NotContain("ZeroDelivery");
        var primary = body.GetProperty("primary");
        Absent(primary.GetProperty("cadence"), "loopGaps").Should().BeTrue("the loop cycles are devices");
        Absent(primary, "maxBasal").Should().BeTrue();
        Absent(primary, "sensitivityRatio").Should().BeTrue();
        Absent(primary, "automaticBoluses").Should().BeFalse();
        Absent(primary, "zeroDelivery").Should().BeFalse();
    }

    [Fact]
    public async Task A_history_clamped_member_is_analysed_over_its_last_24_hours_only()
    {
        await SeedProfileAsync("Australia/Sydney");
        using var clamped = await CreateHistoryClampedClientAsync();
        var now = DateTime.UtcNow;

        var body = await GetOkAsync(clamped, Query($"{Week}&compare=previous"));

        body.GetProperty("historyClamped").GetBoolean().Should().BeTrue();
        Absent(body, "comparison").Should().BeTrue();
        body.GetProperty("primary").GetProperty("from").GetDateTime().ToUniversalTime()
            .Should().BeCloseTo(now.AddHours(-24), TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task A_share_gets_its_reason_and_no_days()
    {
        await SeedProfileAsync("Australia/Sydney");
        await SeedWeekAsync(WeekStart, gap: false);
        using var share = await CreateShareClientAsync();

        var body = await GetOkAsync(share, Query(Week));

        body.GetProperty("timeZoneUnavailableReason").GetString().Should().Be("Share");
        body.GetProperty("primary").GetProperty("days").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Returns_no_figures_without_a_tenant_timezone()
    {
        await SeedWeekAsync(WeekStart, gap: true);

        var body = await GetOkAsync(AuthenticatedClient, Query($"{Week}&compare=previous"));

        body.GetProperty("timeZoneUnavailableReason").GetString().Should().Be("NotConfigured");
        Absent(body, "primaryRange").Should().BeTrue();
        var primary = body.GetProperty("primary");
        primary.GetProperty("days").GetArrayLength().Should().Be(0);
        Absent(primary, "maxBasal").Should().BeTrue();
        Absent(primary.GetProperty("cadence"), "loopGaps").Should().BeTrue();
    }

    /// <summary>
    /// Seven days from <paramref name="start"/>: a Trio cycle enacting a 1.2 U/h temp and a reading
    /// every five minutes, except, with <paramref name="gap"/>, between <see cref="GapStart"/> and
    /// <see cref="GapEnd"/>. At local 03:00 each day the cycle's temp was cut to maxSafeBasal and an
    /// 0.2 U microbolus was given, and the cycles of the next half hour held that temp, enacting
    /// nothing.
    /// </summary>
    private async Task SeedWeekAsync(DateTime start, bool gap)
    {
        using var scope = TenantScope();
        await using var context = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();

        for (var t = start; t < start.AddDays(7); t = t.AddMinutes(5))
        {
            context.SensorGlucose.Add(new SensorGlucoseEntity { Id = Guid.NewGuid(), Timestamp = t, Mgdl = 120, DataSource = "test" });
            if (gap && t > GapStart && t < GapEnd)
                continue;

            var minuteOfDay = (t - start).TotalMinutes % (24 * 60);
            var capped = minuteOfDay == 3 * 60;
            if (minuteOfDay is > 3 * 60 and < 3 * 60 + 30)
            {
                context.ApsSnapshots.Add(new ApsSnapshotEntity
                {
                    Id = Guid.NewGuid(), Timestamp = t, AidAlgorithm = "Trio", Enacted = false, Iob = 1.0,
                    SensitivityRatio = 1.1, DataSource = "test",
                    SuggestedJson = """{"reason":"Eventual BG 170 >= 100, temp 2 >~ req 1.9U/hr. "}""",
                });
                continue;
            }

            var rate = capped ? 2.0 : 1.2;
            var reason = capped
                ? "Eventual BG 180 >= 100, insulinReq 0.9. Microbolusing 0.2U. adj. req. rate: 3.1 to maxSafeBasal: 2.0, temp 1.2 < 2.0U/hr."
                : "Eventual BG 120 >= 100, temp 1.2 ~ req 1.2U/hr.";
            context.ApsSnapshots.Add(new ApsSnapshotEntity
            {
                Id = Guid.NewGuid(), Timestamp = t, AidAlgorithm = "Trio", Enacted = true, EnactedRate = rate,
                EnactedDuration = 30, Iob = 1.0, SensitivityRatio = 1.1, DataSource = "test",
                SuggestedJson = $$"""{"reason":"{{reason}}","rate":{{rate}},"duration":30}""",
            });
            context.TempBasals.Add(new TempBasalEntity
            {
                Id = Guid.NewGuid(), Timestamp = t, EndTimestamp = t.AddMinutes(30), Rate = rate,
                Origin = "Algorithm", Device = "pump", DataSource = "test",
            });
            if (capped)
                context.Boluses.Add(new BolusEntity
                {
                    Id = Guid.NewGuid(), Timestamp = t, Insulin = 0.2, BolusKind = "Algorithm", DataSource = "test",
                });
        }
        await context.SaveChangesAsync();
    }
}

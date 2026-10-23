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
/// <c>GET /api/v4/loop-analysis/settings-evidence</c> end to end on PostgreSQL: a Sydney week over
/// an algorithm temp at the 1.0 U/h schedule, glucose resting at 110, a 40 g breakfast bolused 4 U
/// at 08:00 and a 2 U correction at 14:00 from 200 down to 120 by 18:00, each day.
/// </summary>
[Trait("Category", "Integration")]
public class LoopAnalysisSettingsEvidenceIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : LoopAnalysisIntegrationTestBase(fixture, output)
{
    private const string Path = "/api/v4/loop-analysis/settings-evidence";

    private static string Query(string query) => $"{Path}?{query}";

    private static JsonElementBlock Block(System.Text.Json.JsonElement section, string key, string value) =>
        new(section.EnumerateArray().Single(b => b.GetProperty(key).ToString() == value));

    private readonly record struct JsonElementBlock(System.Text.Json.JsonElement Element)
    {
        public double? Observed => Element.TryGetProperty("observed", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.Number ? o.GetDouble() : null;
        public int N => Element.GetProperty("n").GetInt32();
        public int Rejected(string rule) =>
            Element.GetProperty("rejections").EnumerateArray().Single(r => r.GetProperty("rule").GetString() == rule).GetProperty("count").GetInt32();
        public string? Unavailable => Element.TryGetProperty("unavailableReason", out var u) ? u.GetString() : null;
    }

    [Fact]
    public async Task Reads_the_basal_isf_and_carb_ratio_and_lists_episodes_for_the_primary_range_only()
    {
        await SeedProfileAsync("Australia/Sydney");
        await SeedDaysAsync(PreviousWeekStart.AddDays(-1), 16);

        var body = await GetOkAsync(AuthenticatedClient, Query($"{Week}&compare=previous"));

        body.GetProperty("withheldSeries").GetArrayLength().Should().Be(0);
        var primary = body.GetProperty("primary");
        primary.GetProperty("basal").GetArrayLength().Should().Be(12);

        var basal = Block(primary.GetProperty("basal"), "startHour", "2");
        basal.N.Should().Be(7);
        basal.Observed.Should().BeApproximately(1.0, 0.05);
        basal.Element.GetProperty("days").GetArrayLength().Should().Be(7);
        basal.Element.GetProperty("days")[0].GetProperty("date").GetString().Should().Be("2024-06-10");

        var isf = Block(primary.GetProperty("isf"), "block", "Midday");
        isf.N.Should().Be(7);
        isf.Observed.Should().BeInRange(30, 50, "an 80 mg/dL drop over about 2 U that acted");
        Block(primary.GetProperty("isf"), "block", "Morning").Unavailable.Should().Be("NoCandidates");

        var cr = Block(primary.GetProperty("carbRatio"), "block", "Morning");
        cr.N.Should().Be(7);
        cr.Observed.Should().BeApproximately(10, 1, "40 g over the 4 U it needed, glucose ending where it started");
        cr.Element.GetProperty("episodes")[0].GetProperty("time").GetString().Should().Be("08:00");
        Block(primary.GetProperty("carbRatio"), "block", "Evening").Unavailable.Should().Be("NoAnnouncedMeals");

        var comparison = body.GetProperty("comparison");
        Block(comparison.GetProperty("basal"), "startHour", "2").N.Should().Be(7);
        Block(comparison.GetProperty("basal"), "startHour", "2").Element.GetProperty("days").GetArrayLength()
            .Should().Be(0, "only the primary range's episodes are listed");
    }

    [Fact]
    public async Task Reads_the_hours_before_the_range_that_decide_whether_its_first_night_was_fasting()
    {
        await SeedProfileAsync("Australia/Sydney");
        await SeedDaysAsync(WeekStart.AddDays(-1), 8);
        await SeedCarbsAsync(WeekStart.AddHours(-3), 30);

        var body = await GetOkAsync(AuthenticatedClient, Query(Week));

        var midnight = Block(body.GetProperty("primary").GetProperty("basal"), "startHour", "0");
        midnight.Rejected("CarbsBefore").Should().Be(1, "a snack at 21:00 the evening before the range is within four hours of its first block");
        midnight.N.Should().Be(6);
    }

    [Fact]
    public async Task Reads_back_to_the_start_of_a_climb_that_peaked_within_four_hours_of_the_first_night()
    {
        await SeedProfileAsync("Australia/Sydney");
        await SeedDaysAsync(WeekStart.AddDays(-1), 8, Climb(WeekStart.AddHours(-5), WeekStart.AddHours(-3.5)));

        var body = await GetOkAsync(AuthenticatedClient, Query(Week));

        var midnight = Block(body.GetProperty("primary").GetProperty("basal"), "startHour", "0");
        midnight.Rejected("RiseBefore").Should().Be(1, "the climb peaked 3.5 h before the range, though it started 5 h before");
        midnight.N.Should().Be(6);
    }

    [Fact]
    public async Task Reads_back_to_the_carbs_that_caused_a_climb_before_the_first_night()
    {
        await SeedProfileAsync("Australia/Sydney");
        await SeedDaysAsync(WeekStart.AddDays(-1), 8, Climb(WeekStart.AddHours(-5.5), WeekStart.AddHours(-3.9)));
        await SeedCarbsAsync(WeekStart.AddHours(-6.25), 10);

        var body = await GetOkAsync(AuthenticatedClient, Query(Week));

        var midnight = Block(body.GetProperty("primary").GetProperty("basal"), "startHour", "0");
        midnight.Rejected("RiseBefore").Should().Be(0, "carbs 45 minutes before the climb caused it: it is that meal's, logged 6.25 h before the range");
        midnight.Rejected("CarbsBefore").Should().Be(0);
        midnight.N.Should().Be(7);
    }

    [Fact]
    public async Task Withholds_the_whole_tab_from_a_history_clamped_member_rather_than_qualify_what_the_clamp_hides()
    {
        await SeedProfileAsync("Australia/Sydney");
        var now = DateTime.UtcNow;
        var start = now.AddDays(-2).AddTicks(-(now.AddDays(-2).Ticks % TimeSpan.TicksPerHour));
        await SeedDaysAsync(start, 3);
        await SeedCarbsAsync(now.AddHours(-24.5), 30);
        using var clamped = await CreateHistoryClampedClientAsync();

        var body = await GetOkAsync(clamped, Query($"{Week}&compare=previous"));

        body.GetProperty("historyClamped").GetBoolean().Should().BeTrue();
        body.GetProperty("withheldReason").GetString().Should().Be("HistoryClamped");
        Absent(body, "primary").Should().BeTrue("a snack just before the clamp is hidden, so a night after it would qualify as fasting");
        Absent(body, "comparison").Should().BeTrue();
        body.GetProperty("rules").GetProperty("basalCarbFreeMs").GetInt64().Should().Be(4 * 3_600_000);
    }

    [Theory]
    [InlineData(new[] { Scope.ReportsRead, Scope.GlucoseRead, Scope.TreatmentsRead, Scope.DevicesRead }, Scope.TherapyRead)]
    [InlineData(new[] { Scope.ReportsRead, Scope.GlucoseRead, Scope.TherapyRead }, Scope.TreatmentsRead)]
    [InlineData(new[] { Scope.ReportsRead, Scope.TreatmentsRead, Scope.TherapyRead }, Scope.GlucoseRead)]
    public async Task Withholds_the_whole_tab_without_glucose_treatments_and_therapy(string[] scopes, string missing)
    {
        await SeedProfileAsync("Australia/Sydney");
        await SeedDaysAsync(WeekStart, 7);
        using var client = await CreateTokenClientAsync(scopes);

        var body = await GetOkAsync(client, Query($"{Week}&compare=previous"));

        body.GetProperty("withheldSeries").EnumerateArray().Select(s => s.GetString()).Should().Contain("SettingsEvidence", $"without {missing}");
        body.GetProperty("withheldReason").GetString().Should().Be("ReadScopes");
        body.GetProperty("profileNames").EnumerateArray().Select(s => s.GetString()).Should().Equal(
            missing == Scope.TherapyRead ? [] : ["Default"], "the profile picker lists what the caller may see");
        Absent(body, "primary").Should().BeTrue();
        Absent(body, "comparison").Should().BeTrue();
        Absent(body, "primaryRange").Should().BeFalse();
    }

    [Fact]
    public async Task Serves_the_tab_with_glucose_treatments_and_therapy_alone()
    {
        await SeedProfileAsync("Australia/Sydney");
        await SeedDaysAsync(WeekStart.AddDays(-1), 8);
        using var client = await CreateTokenClientAsync(Scope.ReportsRead, Scope.GlucoseRead, Scope.TreatmentsRead, Scope.TherapyRead);

        var body = await GetOkAsync(client, Query(Week));

        body.GetProperty("withheldSeries").EnumerateArray().Select(s => s.GetString()).Should().NotContain("SettingsEvidence");
        Block(body.GetProperty("primary").GetProperty("basal"), "startHour", "2").N.Should().Be(7);
    }

    [Fact]
    public async Task A_share_gets_its_reason_and_no_evidence()
    {
        await SeedProfileAsync("Australia/Sydney");
        await SeedDaysAsync(WeekStart, 7);
        using var share = await CreateShareClientAsync();

        var body = await GetOkAsync(share, Query($"{Week}&compare=previous"));

        body.GetProperty("timeZoneUnavailableReason").GetString().Should().Be("Share");
        body.GetProperty("withheldSeries").EnumerateArray().Select(s => s.GetString()).Should().Contain("SettingsEvidence");
        Absent(body, "primaryRange").Should().BeTrue();
        Absent(body, "primary").Should().BeTrue();
        Absent(body, "comparison").Should().BeTrue();
    }

    [Fact]
    public async Task Returns_no_ranges_without_a_tenant_timezone()
    {
        await SeedDaysAsync(WeekStart, 7);

        var body = await GetOkAsync(AuthenticatedClient, Query(Week));

        body.GetProperty("timeZoneUnavailableReason").GetString().Should().Be("NotConfigured");
        Absent(body, "primary").Should().BeTrue();
        Absent(body, "primaryRange").Should().BeTrue();
    }

    /// <summary>
    /// <paramref name="days"/> days from <paramref name="start"/>, local midnight: a reading every
    /// five minutes at 110, at 200 from 13:00 and falling evenly from 14:00 to 120 at 18:00, under a
    /// 2 U correction at 14:00; a 40 g breakfast with a 4 U bolus at 08:00; an algorithm temp at the
    /// 1.0 U/h schedule throughout. <paramref name="glucose"/> overrides the reading where it gives one.
    /// </summary>
    private async Task SeedDaysAsync(DateTime start, int days, Func<DateTime, double?>? glucose = null)
    {
        using var scope = TenantScope();
        await using var context = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();

        static double Glucose(double minuteOfDay) => minuteOfDay switch
        {
            >= 780 and < 840 => 200,
            >= 840 and < 1080 => 200 - (minuteOfDay - 840) * 80 / 240,
            _ => 110,
        };

        for (var t = start; t < start.AddDays(days); t = t.AddMinutes(5))
        {
            context.SensorGlucose.Add(new SensorGlucoseEntity
            {
                Id = Guid.NewGuid(), Timestamp = t, Mgdl = glucose?.Invoke(t) ?? Glucose((t - start).TotalMinutes % 1440), DataSource = "test",
            });
            context.TempBasals.Add(new TempBasalEntity
            {
                Id = Guid.NewGuid(), Timestamp = t, EndTimestamp = t.AddMinutes(30), Rate = 1.0,
                Origin = "Algorithm", Device = "pump", DataSource = "test",
            });
        }
        for (var day = 0; day < days; day++)
        {
            var breakfast = start.AddDays(day).AddHours(8);
            context.CarbIntakes.Add(new CarbIntakeEntity { Id = Guid.NewGuid(), Timestamp = breakfast, Carbs = 40, DataSource = "test" });
            context.Boluses.Add(new BolusEntity { Id = Guid.NewGuid(), Timestamp = breakfast, Insulin = 4, BolusKind = "Manual", DataSource = "test" });
            context.Boluses.Add(new BolusEntity
            {
                Id = Guid.NewGuid(), Timestamp = start.AddDays(day).AddHours(14), Insulin = 2, BolusKind = "Manual", DataSource = "test",
            });
        }
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// A meal-like climb from the 110 resting level at <paramref name="start"/>: 50 mg/dL in its first
    /// 40 minutes, on to 200 at <paramref name="peak"/>, then back down to 110 over 90 minutes.
    /// </summary>
    private static Func<DateTime, double?> Climb(DateTime start, DateTime peak) => t =>
    {
        var minutes = (t - start).TotalMinutes;
        var toPeak = (peak - start).TotalMinutes;
        return minutes switch
        {
            < 0 => null,
            <= 40 => 110 + minutes * 50 / 40,
            _ when minutes <= toPeak => 160 + (minutes - 40) * 40 / (toPeak - 40),
            _ when minutes <= toPeak + 90 => 200 - (minutes - toPeak) * 90 / 90,
            _ => null,
        };
    };

    private async Task SeedCarbsAsync(DateTime at, double grams)
    {
        using var scope = TenantScope();
        await using var context = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();
        context.CarbIntakes.Add(new CarbIntakeEntity { Id = Guid.NewGuid(), Timestamp = at, Carbs = grams, DataSource = "test" });
        await context.SaveChangesAsync();
    }
}

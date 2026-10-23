using System.Net;
using System.Text.Json;
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
/// <c>GET /api/v4/loop-analysis/prediction-accuracy</c> end to end on PostgreSQL: a week of Loop
/// predictions every five minutes against flat glucose in Sydney, compared with the week before.
/// </summary>
[Trait("Category", "Integration")]
public class LoopAnalysisPredictionAccuracyIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : LoopAnalysisIntegrationTestBase(fixture, output)
{
    private const string Path = "/api/v4/loop-analysis/prediction-accuracy";
    private const int SnapshotsPerWeek = 7 * 288;

    private static string Query(string query) => $"{Path}?{query}";

    [Fact]
    public async Task Returns_the_primary_and_the_previous_week()
    {
        await SeedProfileAsync("Australia/Sydney");
        await SeedWeekAsync(PreviousWeekStart, predicted: 110);
        await SeedWeekAsync(WeekStart, predicted: 130);

        var body = await GetOkAsync(AuthenticatedClient, Query($"{Week}&compare=previous"));
        body.GetProperty("profileNames").EnumerateArray().Select(n => n.GetString()).Should().Equal("Default");

        body.GetProperty("historyClamped").GetBoolean().Should().BeFalse();
        Absent(body, "timeZoneUnavailableReason").Should().BeTrue();
        body.GetProperty("withheldSeries").GetArrayLength().Should().Be(0);
        body.GetProperty("comparisonRange").GetProperty("startDate").GetString().Should().Be("2024-06-03");

        var primary = body.GetProperty("primary");
        primary.GetProperty("predictions").GetInt32().Should().Be(SnapshotsPerWeek, "the unenacted twin in each bucket is thinned away");
        primary.GetProperty("algorithms").EnumerateArray().Select(a => a.GetString()).Should().Equal("Loop");
        var curve = primary.GetProperty("curves").EnumerateArray().Should().ContainSingle().Subject;
        curve.GetProperty("curve").GetString().Should().Be("Default");
        curve.GetProperty("byHour").GetArrayLength().Should().Be(24);
        curve.GetProperty("byContext").GetArrayLength().Should().Be(4);

        var thirty = Horizon(curve, 30);
        thirty.GetProperty("error").GetProperty("mean").GetDouble().Should().Be(10);
        thirty.GetProperty("mae").GetDouble().Should().Be(10);
        thirty.GetProperty("settingsSignal").GetBoolean().Should().BeTrue();
        Horizon(curve, 90).GetProperty("settingsSignal").GetBoolean().Should().BeFalse();

        var twoHours = Horizon(curve, 120);
        twoHours.GetProperty("unmatched").GetInt32().Should().Be(24, "the last two hours' predictions reach past the last reading");
        twoHours.GetProperty("error").GetProperty("n").GetInt32().Should().Be(SnapshotsPerWeek - 24);

        var comparison = Horizon(body.GetProperty("comparison").GetProperty("curves")[0], 120);
        comparison.GetProperty("error").GetProperty("mean").GetDouble().Should().Be(-10);
        comparison.GetProperty("unmatched").GetInt32().Should().Be(0, "the readings after the comparison are the primary week's");
    }

    [Fact]
    public async Task Rejects_dates_that_do_not_make_a_range()
    {
        var response = await AuthenticatedClient.GetAsync(Query("startDate=2024-06-16&endDate=2024-06-10"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(new[] { Scope.ReportsRead, Scope.GlucoseRead, Scope.TreatmentsRead }, false, false)]
    [InlineData(new[] { Scope.ReportsRead, Scope.DevicesRead, Scope.TreatmentsRead }, false, false)]
    [InlineData(new[] { Scope.ReportsRead, Scope.GlucoseRead, Scope.DevicesRead }, true, false)]
    [InlineData(new[] { Scope.ReportsRead, Scope.GlucoseRead, Scope.DevicesRead, Scope.TreatmentsRead }, true, true)]
    public async Task Narrows_the_response_to_the_callers_scopes(string[] scopes, bool curves, bool contexts)
    {
        await SeedProfileAsync("Australia/Sydney");
        await SeedWeekAsync(WeekStart, predicted: 130);
        using var client = await CreateTokenClientAsync(scopes);

        var body = await GetOkAsync(client, Query(Week));

        var withheld = body.GetProperty("withheldSeries").EnumerateArray().Select(s => s.GetString()).ToList();
        withheld.Contains("PredictionCurves").Should().Be(!curves);
        withheld.Contains("PredictionContexts").Should().Be(!scopes.Contains(Scope.TreatmentsRead));

        var reported = body.GetProperty("primary").GetProperty("curves");
        (reported.GetArrayLength() > 0).Should().Be(curves);
        if (curves)
            (reported[0].GetProperty("byContext").GetArrayLength() > 0).Should().Be(contexts);
    }

    [Fact]
    public async Task A_history_clamped_member_is_analysed_over_its_last_24_hours_whatever_it_asks_for()
    {
        await SeedProfileAsync("Australia/Sydney");
        using var clamped = await CreateHistoryClampedClientAsync();
        var now = DateTime.UtcNow;

        var body = await GetOkAsync(clamped, Query($"{Week}&compare=previous"));

        body.GetProperty("historyClamped").GetBoolean().Should().BeTrue();
        body.GetProperty("primaryRange").GetProperty("from").GetDateTime().ToUniversalTime()
            .Should().BeCloseTo(now.AddHours(-24), TimeSpan.FromMinutes(5));
        Absent(body, "comparison").Should().BeTrue();
    }

    [Fact]
    public async Task Returns_no_curves_without_a_tenant_timezone()
    {
        await SeedWeekAsync(WeekStart, predicted: 130);

        var body = await GetOkAsync(AuthenticatedClient, Query($"{Week}&compare=previous"));

        body.GetProperty("timeZoneUnavailableReason").GetString().Should().Be("NotConfigured");
        Absent(body, "primaryRange").Should().BeTrue();
        body.GetProperty("primary").GetProperty("curves").GetArrayLength().Should().Be(0);
        body.GetProperty("primary").GetProperty("predictions").GetInt32().Should().Be(0);
        Absent(body, "comparison").Should().BeTrue();
    }

    [Fact]
    public async Task A_share_gets_its_reason_and_no_curves()
    {
        await SeedProfileAsync("Australia/Sydney");
        await SeedWeekAsync(WeekStart, predicted: 130);
        using var share = await CreateShareClientAsync();

        var body = await GetOkAsync(share, Query(Week));

        body.GetProperty("timeZoneUnavailableReason").GetString().Should().Be("Share");
        body.GetProperty("primary").GetProperty("curves").GetArrayLength().Should().Be(0);
    }

    private static JsonElement Horizon(JsonElement curve, int minutes) =>
        curve.GetProperty("horizons").EnumerateArray().Single(h => h.GetProperty("minutes").GetInt32() == minutes);

    /// <summary>
    /// Seven days from <paramref name="start"/>: glucose at 120 every five minutes, and at each a
    /// Loop snapshot predicting a flat <paramref name="predicted"/> for two hours, enacted, beside an
    /// unenacted one a minute later predicting 400 that thinning must drop.
    /// </summary>
    private async Task SeedWeekAsync(DateTime start, double predicted)
    {
        using var scope = TenantScope();
        await using var context = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();

        static string Flat(double mgdl) => "[" + string.Join(",", Enumerable.Repeat(mgdl, 25)) + "]";
        for (var t = start; t < start.AddDays(7); t = t.AddMinutes(5))
        {
            context.SensorGlucose.Add(new SensorGlucoseEntity { Id = Guid.NewGuid(), Timestamp = t, Mgdl = 120, DataSource = "test" });
            context.ApsSnapshots.Add(new ApsSnapshotEntity
            {
                Id = Guid.NewGuid(), Timestamp = t, PredictedStartTimestamp = t, Device = "loop://iPhone",
                AidAlgorithm = "Loop", Enacted = true, PredictedDefaultJson = Flat(predicted), DataSource = "test",
            });
            context.ApsSnapshots.Add(new ApsSnapshotEntity
            {
                Id = Guid.NewGuid(), Timestamp = t.AddMinutes(1), PredictedStartTimestamp = t.AddMinutes(1),
                Device = "loop://iPhone", AidAlgorithm = "Loop", Enacted = false, PredictedDefaultJson = Flat(400),
                DataSource = "test",
            });
        }
        await context.SaveChangesAsync();
    }
}

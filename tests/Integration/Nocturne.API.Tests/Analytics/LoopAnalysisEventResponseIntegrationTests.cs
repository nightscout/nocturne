using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Analytics;

/// <summary>
/// <c>GET /api/v4/loop-analysis/event-response</c> end to end on PostgreSQL: a Sydney week of a
/// 45 g lunch at 12:00 and an unannounced rise at 19:00 each day, over an algorithm temp of
/// 1.5 U/h against a 1.0 U/h schedule.
/// </summary>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public class LoopAnalysisEventResponseIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : LoopAnalysisIntegrationTestBase(fixture, output)
{
    private const string Path = "/api/v4/loop-analysis/event-response";
    private const string Cohorts = "anchor1=Meal&anchor2=UnannouncedRise&fromMinute2=1080&toMinute2=1200";

    private static string Query(string query) => $"{Path}?{query}";

    [Fact]
    public async Task Aligns_meals_and_unannounced_rises_and_compares_the_previous_week()
    {
        await SeedProfileAsync("Australia/Sydney");
        await SeedWeekAsync(PreviousWeekStart);
        await SeedWeekAsync(WeekStart);

        var body = await GetOkAsync(AuthenticatedClient, Query($"{Week}&compare=previous&{Cohorts}"));

        body.GetProperty("withheldSeries").GetArrayLength().Should().Be(0);
        body.GetProperty("ignoredFilters").GetArrayLength().Should().Be(0);
        body.GetProperty("unavailableAnchors").GetArrayLength().Should().Be(0);
        var definition = body.GetProperty("definition");
        definition.GetProperty("cohorts").GetArrayLength().Should().Be(2);
        definition.GetProperty("minutesAfter").GetInt32().Should().Be(300);

        var primary = body.GetProperty("primary");
        var meals = primary.GetProperty("cohorts")[0];
        meals.GetProperty("events").GetInt32().Should().Be(7);
        var slots = meals.GetProperty("slots");
        slots.GetArrayLength().Should().Be(72);
        body.GetProperty("slotMinutes")[24].GetInt32().Should().Be(60);
        slots[24].GetProperty("glucoseChange").GetProperty("p50").GetDouble().Should().Be(80);
        slots[24].GetProperty("glucoseChange").GetProperty("n").GetInt32().Should().Be(7);
        meals.GetProperty("metrics").GetProperty("peakRise").GetProperty("p50").GetDouble().Should().Be(80);
        meals.GetProperty("metrics").GetProperty("gramsPerUnit").GetProperty("p50").GetDouble()
            .Should().Be(6, "45 g over 4.5 U of bolus and 3 U of temp above schedule");

        var rises = primary.GetProperty("cohorts")[1];
        rises.GetProperty("events").GetInt32().Should().Be(7, "the lunch rise is announced and so is not one");
        rises.GetProperty("metrics").GetProperty("netInsulinFirst30").GetProperty("p50").GetDouble()
            .Should().Be(1.75, "five 0.3 U boluses and 0.5 U/h above schedule in the first half hour");

        var events = primary.GetProperty("events");
        events.GetArrayLength().Should().Be(14);
        events[0].GetProperty("date").GetString().Should().Be("2024-06-10");
        events[0].GetProperty("time").GetString().Should().Be("12:00");
        events[0].GetProperty("anchor").GetString().Should().Be("Meal");
        events[1].GetProperty("time").GetString().Should().Be("19:00");

        body.GetProperty("comparison").GetProperty("cohorts")[0].GetProperty("events").GetInt32().Should().Be(7);
        body.GetProperty("comparison").GetProperty("events").GetArrayLength().Should().Be(0, "only the primary range's events are listed");
    }

    [Theory]
    [InlineData(new[] { Scope.ReportsRead, Scope.GlucoseRead, Scope.DevicesRead }, false, true)]
    [InlineData(new[] { Scope.ReportsRead, Scope.TreatmentsRead, Scope.DevicesRead }, true, false)]
    [InlineData(new[] { Scope.ReportsRead, Scope.GlucoseRead, Scope.TreatmentsRead }, true, true)]
    public async Task Narrows_to_the_anchors_and_series_the_caller_can_read(string[] scopes, bool treatments, bool glucose)
    {
        await SeedProfileAsync("Australia/Sydney");
        await SeedWeekAsync(WeekStart);
        using var client = await CreateTokenClientAsync(scopes);

        var body = await GetOkAsync(client, Query($"{Week}&anchor1=Meal&isolatedOnly=true"));

        var meals = treatments && glucose;
        var ignored = body.GetProperty("ignoredFilters").EnumerateArray().Select(f => f.GetString()).ToList();
        ignored.Should().BeEquivalentTo([
            .. meals ? Array.Empty<string>() : ["anchor1"],
            .. treatments ? Array.Empty<string>() : ["isolatedOnly"],
        ]);
        var unavailable = body.GetProperty("unavailableAnchors").EnumerateArray()
            .Select(a => a.GetProperty("anchor").GetString()).ToList();
        unavailable.Contains("Meal").Should().Be(!meals, "every anchor needs glucose");
        unavailable.Contains("UnannouncedRise").Should().Be(!meals);
        unavailable.Contains("SensorStart").Should().Be(!(glucose && scopes.Contains(Scope.DevicesRead)));

        var withheld = body.GetProperty("withheldSeries").EnumerateArray().Select(s => s.GetString()).ToList();
        withheld.Contains("EventList").Should().Be(!treatments);
        withheld.Contains("EventGlucose").Should().Be(!glucose);

        var cohorts = body.GetProperty("primary").GetProperty("cohorts");
        cohorts.GetArrayLength().Should().Be(meals ? 1 : 0);
        body.GetProperty("primary").GetProperty("events").GetArrayLength().Should().Be(meals ? 7 : 0);
        if (meals)
        {
            var slot = cohorts[0].GetProperty("slots")[24];
            Absent(slot, "glucose").Should().BeFalse();
            Absent(slot, "iob").Should().BeFalse();
        }
    }

    [Fact]
    public async Task For_a_clamped_member_drops_a_rise_whose_carbs_fall_before_the_clamp()
    {
        await SeedProfileAsync("Australia/Sydney");
        var now = DateTime.UtcNow;
        var edge = new DateTime(now.Ticks - now.Ticks % TimeSpan.FromMinutes(5).Ticks, DateTimeKind.Utc).AddHours(-24);
        var nearEdge = edge.AddHours(1);
        var inside = edge.AddHours(8);

        using (var scope = TenantScope())
        {
            await using var context = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();
            // A climb of 3 mg/dL a minute to 170, held for half an hour, then back to 110 at 1 a minute.
            static double Rise(DateTime t, DateTime from)
            {
                var minutes = (t - from).TotalMinutes;
                return minutes <= 0 ? 110 : minutes <= 60 ? Math.Min(170, 110 + minutes * 3) : Math.Max(110, 170 - (minutes - 60));
            }
            for (var t = edge.AddHours(-3); t < now; t = t.AddMinutes(5))
                context.SensorGlucose.Add(new SensorGlucoseEntity
                {
                    Id = Guid.NewGuid(), Timestamp = t, DataSource = "test",
                    Mgdl = t < inside.AddHours(-2) ? Rise(t, nearEdge) : Rise(t, inside),
                });
            // Eaten for the first rise, but before the clamp edge, where the member cannot see it.
            context.CarbIntakes.Add(new CarbIntakeEntity { Id = Guid.NewGuid(), Timestamp = edge.AddMinutes(-30), Carbs = 40, DataSource = "test" });
            await context.SaveChangesAsync();
        }

        using var clamped = await CreateHistoryClampedClientAsync();
        var body = await GetOkAsync(clamped, Query($"{RecentDays(3)}&anchor1=UnannouncedRise"));

        body.GetProperty("historyClamped").GetBoolean().Should().BeTrue();
        var cohort = body.GetProperty("primary").GetProperty("cohorts")[0];
        cohort.GetProperty("beforeVisibleHistory").GetInt32().Should().Be(1, "the rise's carbs are hidden, not absent");
        cohort.GetProperty("events").GetInt32().Should().Be(1, "a rise with all it needs inside the last 24 hours still counts");
        var at = body.GetProperty("primary").GetProperty("events")[0].GetProperty("at").GetDateTime().ToUniversalTime();
        at.Should().BeCloseTo(inside, TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task A_meal_before_the_range_announces_a_rise_at_its_start()
    {
        await SeedProfileAsync("Australia/Sydney");
        var announced = WeekStart;
        var unannounced = WeekStart.AddDays(1);

        using (var scope = TenantScope())
        {
            await using var context = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();
            // A climb of 3 mg/dL a minute from 110 to 200, held for an hour, then back to 110 at 1 a minute.
            static double Rise(DateTime t, DateTime from)
            {
                var minutes = (t - from).TotalMinutes;
                return minutes <= 0 ? 110 : minutes <= 90 ? Math.Min(200, 110 + minutes * 3) : Math.Max(110, 200 - (minutes - 90));
            }
            for (var t = WeekStart.AddHours(-4); t < WeekStart.AddDays(2); t = t.AddMinutes(5))
                context.SensorGlucose.Add(new SensorGlucoseEntity
                {
                    Id = Guid.NewGuid(), Timestamp = t, DataSource = "test",
                    Mgdl = t < unannounced.AddHours(-2) ? Rise(t, announced) : Rise(t, unannounced),
                });
            // Absorbed before the rise, so the entry announces it and carbs on board do not.
            context.CarbIntakes.Add(new CarbIntakeEntity
            {
                Id = Guid.NewGuid(), Timestamp = announced.AddMinutes(-150), Carbs = 10, DataSource = "test",
            });
            await context.SaveChangesAsync();
        }

        var body = await GetOkAsync(AuthenticatedClient, Query($"{Week}&anchor1=UnannouncedRise"));

        var cohort = body.GetProperty("primary").GetProperty("cohorts")[0];
        cohort.GetProperty("events").GetInt32().Should().Be(1, "carbs 2.5 hours before a rise announce it, even before the range");
        var at = body.GetProperty("primary").GetProperty("events")[0].GetProperty("at").GetDateTime().ToUniversalTime();
        at.Should().BeCloseTo(unannounced, TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task A_share_gets_its_reason_and_no_cohorts()
    {
        await SeedProfileAsync("Australia/Sydney");
        await SeedWeekAsync(WeekStart);
        using var share = await CreateShareClientAsync();

        var body = await GetOkAsync(share, Query($"{Week}&{Cohorts}"));

        body.GetProperty("timeZoneUnavailableReason").GetString().Should().Be("Share");
        Absent(body, "primaryRange").Should().BeTrue();
        body.GetProperty("primary").GetProperty("cohorts").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Returns_no_cohorts_without_a_tenant_timezone()
    {
        await SeedWeekAsync(WeekStart);

        var body = await GetOkAsync(AuthenticatedClient, Query($"{Week}&{Cohorts}"));

        body.GetProperty("timeZoneUnavailableReason").GetString().Should().Be("NotConfigured");
        body.GetProperty("primary").GetProperty("cohorts").GetArrayLength().Should().Be(0);
        body.GetProperty("definition").GetProperty("cohorts").GetArrayLength().Should().Be(2);
    }

    [Theory]
    [InlineData("minutesAfter=500", "minutesAfter")]
    [InlineData("minutesBefore=45", "minutesBefore")]
    [InlineData("anchor1=Meal&minGrams1=20&maxGrams1=5000", "maxGrams1")]
    [InlineData("anchor1=Meal&minGrams1=-5", "minGrams1")]
    public async Task Applies_the_default_for_a_value_outside_the_limits_and_names_it_ignored(string events, string parameter)
    {
        await SeedProfileAsync("Australia/Sydney");

        var body = await GetOkAsync(AuthenticatedClient, Query($"{Week}&{events}"));

        body.GetProperty("ignoredFilters").EnumerateArray().Select(f => f.GetString()).Should().Equal(parameter);
        var definition = body.GetProperty("definition");
        definition.GetProperty("minutesBefore").GetInt32().Should().Be(60);
        definition.GetProperty("minutesAfter").GetInt32().Should().Be(300);
        if (parameter.Contains("Grams"))
        {
            var cohort = definition.GetProperty("cohorts")[0];
            var grams = cohort.TryGetProperty(parameter[..^1], out var value) ? value.ValueKind : JsonValueKind.Null;
            grams.Should().Be(JsonValueKind.Null);
        }
        var limits = body.GetProperty("limits");
        limits.GetProperty("maxCohorts").GetInt32().Should().Be(3);
        limits.GetProperty("minutesAfterChoices").EnumerateArray().Select(m => m.GetInt32()).Should().Contain(300);
    }

    [Theory]
    [InlineData("minutesAfter=five")]
    [InlineData("fromMinute1=1440")]
    [InlineData("anchor1=CorrectionBolus&minGrams1=20")]
    [InlineData("fromMinute2=60")]
    public async Task Rejects_a_definition_it_cannot_compute(string events)
    {
        var response = await AuthenticatedClient.GetAsync(Query($"{Week}&{events}"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Filters_a_meal_cohort_by_food_and_weekday_and_counts_carbs_only_meals_apart()
    {
        await SeedProfileAsync("Australia/Sydney");
        await SeedWeekAsync(WeekStart);
        var (pasta, rice) = await SeedFoodsAsync();

        var byFood = await GetOkAsync(AuthenticatedClient, Query($"{Week}&anchor1=Meal&foodIds1={pasta}"));
        var byCategory = await GetOkAsync(AuthenticatedClient, Query($"{Week}&anchor1=Meal&foodCategory1=grains"));
        var byDay = await GetOkAsync(AuthenticatedClient, Query($"{Week}&anchor1=Meal&dayOfWeek1=Monday&dayOfWeek1=Sunday"));

        var cohort = byFood.GetProperty("primary").GetProperty("cohorts")[0];
        cohort.GetProperty("events").GetInt32().Should().Be(2, "Monday's and Tuesday's lunches carry pasta");
        cohort.GetProperty("mealsWithFoodRecord").GetInt32().Should().Be(3);
        cohort.GetProperty("mealsWithoutFoodRecord").GetInt32().Should().Be(4, "the other lunches have carbs only, so they are counted, not dropped silently");
        byFood.GetProperty("definition").GetProperty("cohorts")[0].GetProperty("foodIds")[0].GetGuid().Should().Be(pasta);

        var grains = byCategory.GetProperty("primary").GetProperty("cohorts")[0];
        grains.GetProperty("events").GetInt32().Should().Be(1);
        grains.GetProperty("mealsWithoutFoodRecord").GetInt32().Should().Be(4);
        _ = rice;

        var days = byDay.GetProperty("primary").GetProperty("cohorts")[0];
        days.GetProperty("events").GetInt32().Should().Be(2);
        days.GetProperty("mealsWithFoodRecord").GetInt32().Should().Be(0, "no food filter, no food count");
        byDay.GetProperty("primary").GetProperty("events").EnumerateArray().Select(e => e.GetProperty("date").GetString())
            .Should().Equal("2024-06-10", "2024-06-16");
    }

    [Fact]
    public async Task Withholds_a_food_filtered_cohort_without_food_read_and_says_so()
    {
        await SeedProfileAsync("Australia/Sydney");
        await SeedWeekAsync(WeekStart);
        var (pasta, _) = await SeedFoodsAsync();
        using var client = await CreateTokenClientAsync(Scope.ReportsRead, Scope.GlucoseRead, Scope.TreatmentsRead);

        var body = await GetOkAsync(client, Query($"{Week}&anchor1=Meal&foodIds1={pasta}"));

        body.GetProperty("ignoredFilters").EnumerateArray().Select(f => f.GetString()).Should().Equal("foodIds1");
        body.GetProperty("unavailableFilters").EnumerateArray().Select(f => f.GetProperty("filter").GetString())
            .Should().Contain(["foodIds", "foodCategory"]);
        body.GetProperty("primary").GetProperty("cohorts")[0].GetProperty("events").GetInt32()
            .Should().Be(0, "a filter the caller may not apply leaves the cohort empty, never every meal");
        body.GetProperty("definition").GetProperty("cohorts")[0].GetProperty("foodFilterWithheld").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("anchor1=CorrectionBolus&foodCategory1=Pasta")]
    [InlineData("dayOfWeek2=Monday")]
    public async Task Rejects_a_food_or_weekday_filter_it_cannot_apply(string events)
    {
        var response = await AuthenticatedClient.GetAsync(Query($"{Week}&{events}"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Reads_each_carb_intakes_foods_and_counts_intakes_per_food_since_a_date()
    {
        await SeedWeekAsync(WeekStart);
        var (pasta, rice) = await SeedFoodsAsync();
        using var scope = TenantScope();
        var repository = scope.ServiceProvider.GetRequiredService<Nocturne.Core.Contracts.Repositories.ITreatmentFoodRepository>();
        Guid[] intakes;
        await using (var context = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync())
            intakes = [.. await context.CarbIntakes.OrderBy(c => c.Timestamp).Select(c => c.Id).ToListAsync()];

        var foods = await repository.GetCarbIntakeFoodsAsync(intakes);
        var counts = await repository.CountCarbIntakesByFoodAsync(WeekStart.AddDays(1));

        foods.Should().HaveCount(4);
        foods.Where(f => f.FoodId == pasta).Should().HaveCount(2).And.OnlyContain(f => f.FoodName == "Penne" && f.FoodCategory == "Pasta");
        foods.Should().ContainSingle(f => f.FoodId == null, "a record of unspecified carbs names no food");
        counts.Should().BeEquivalentTo(new Dictionary<Guid, int> { [pasta] = 1, [rice] = 1 }, "Monday's lunch is before the date");
    }

    /// <summary>
    /// Penne (Pasta) on Monday's and Tuesday's lunches, rice (Grains) with a record of unspecified
    /// carbs on Wednesday's; the other lunches carry no food record.
    /// </summary>
    private async Task<(Guid Pasta, Guid Rice)> SeedFoodsAsync()
    {
        using var scope = TenantScope();
        await using var context = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();
        var pasta = new Nocturne.Infrastructure.Data.Entities.FoodEntity { Id = Guid.NewGuid(), Name = "Penne", Category = "Pasta", Carbs = 30, Portion = 100 };
        var rice = new Nocturne.Infrastructure.Data.Entities.FoodEntity { Id = Guid.NewGuid(), Name = "Rice", Category = "Grains", Carbs = 28, Portion = 100 };
        context.Foods.AddRange(pasta, rice);
        var lunches = await context.CarbIntakes.OrderBy(c => c.Timestamp).Select(c => c.Id).ToListAsync();
        void Record(Guid intake, Guid? food, decimal carbs) => context.TreatmentFoods.Add(new Nocturne.Infrastructure.Data.Entities.TreatmentFoodEntity
        {
            Id = Guid.NewGuid(), CarbIntakeId = intake, FoodId = food, Carbs = carbs, Portions = 1,
        });
        Record(lunches[0], pasta.Id, 45);
        Record(lunches[1], pasta.Id, 45);
        Record(lunches[2], rice.Id, 30);
        Record(lunches[2], null, 15);
        await context.SaveChangesAsync();
        return (pasta.Id, rice.Id);
    }

    /// <summary>
    /// The custom-report datasets that draw the events one by one, over the seeded week: each event,
    /// the spread of a figure, and when the events happen.
    /// </summary>
    [Fact]
    public async Task The_report_datasets_of_each_event_draw_the_weeks_meals_and_rises()
    {
        await SeedProfileAsync("Australia/Sydney");
        await SeedWeekAsync(WeekStart);
        object Layer(string name, string mark, object? bindings = null, string? outcome = null) => new Dictionary<string, object?>
        {
            ["dataset"] = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["cohorts"] = new object[] { new { anchor = "Meal" }, new { anchor = "UnannouncedRise" } },
                ["outcome"] = outcome,
            }.Where(p => p.Value is not null).ToDictionary(),
            ["mark"] = mark,
            ["bindings"] = bindings,
        }.Where(p => p.Value is not null).ToDictionary();
        object Panel(string id, object layer) => new { id, kind = "chart", tracks = new[] { new { layers = new[] { layer } } } };
        var spec = new
        {
            version = 1,
            range = new { from = WeekStart.ToString("o"), to = WeekStart.AddDays(7).ToString("o") },
            panels = new[]
            {
                Panel("each", Layer("event_outcomes", "points", new { x = "timeOfDay", y = new[] { "peakRise" } })),
                Panel("spread", Layer("event_outcome_spread", "bars", outcome: "peakRise")),
                Panel("hours", Layer("event_occurrence", "bars")),
            },
        };

        var response = await AuthenticatedClient.PostAsJsonAsync("/api/v4/reports/custom/run", new { spec });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        body.GetProperty("errors").GetArrayLength().Should().Be(0, body.ToString());
        var layers = body.GetProperty("result").GetProperty("panels").EnumerateArray()
            .ToDictionary(p => p.GetProperty("id").GetString()!, p => p.GetProperty("tracks")[0].GetProperty("layers")[0]);
        layers.Values.Should().OnlyContain(l => l.GetProperty("status").GetString() == "ok");

        var each = layers["each"];
        each.GetProperty("xKind").GetString().Should().Be("time_of_day");
        var points = each.GetProperty("rows").EnumerateArray().ToList();
        points.Should().HaveCount(14, "seven lunches and seven rises");
        points.Select(r => r.GetProperty("timeOfDay").GetInt32()).Distinct().Should().BeEquivalentTo([720, 1140]);
        points.Where(r => r.GetProperty("timeOfDay").GetInt32() == 720).Select(r => r.GetProperty("peakRise").GetDouble())
            .Should().OnlyContain(v => v == 80);
        each.GetProperty("sentence").GetString().Should().Be("14 events, each drawn as a point.", "no line is fitted along the time of day");

        var spread = layers["spread"];
        var ranges = spread.GetProperty("rows").EnumerateArray().ToList();
        ranges.Sum(r => r.GetProperty("events1").GetInt32()).Should().Be(7);
        ranges.Sum(r => r.GetProperty("events2").GetInt32()).Should().Be(7);
        spread.GetProperty("facts")[0].GetProperty("value").GetString().Should().Be("80");

        var hours = layers["hours"].GetProperty("rows").EnumerateArray().ToList();
        hours.Should().HaveCount(24);
        hours[12].GetProperty("events1").GetInt32().Should().Be(7);
        hours[19].GetProperty("events2").GetInt32().Should().Be(7);
        hours.Sum(r => r.GetProperty("events1").GetInt32() + r.GetProperty("events2").GetInt32()).Should().Be(14);

        using var glucoseOnly = await CreateTokenClientAsync(Scope.ReportsRead, Scope.GlucoseRead);
        var narrowed = await (await glucoseOnly.PostAsJsonAsync("/api/v4/reports/custom/run", new { spec })).Content.ReadFromJsonAsync<JsonElement>();
        var narrowedEach = narrowed.GetProperty("result").GetProperty("panels")[0].GetProperty("tracks")[0].GetProperty("layers")[0];
        narrowedEach.GetProperty("rows").GetArrayLength().Should().Be(0, "the event list needs treatments.read");
        narrowedEach.GetProperty("facts").GetArrayLength().Should().Be(0, "no false count of 0 events");
        narrowedEach.GetProperty("summary").GetString().Should().StartWith("Not shown:").And.Contain("treatments");
    }

    /// <summary>
    /// Over the seeded week: the days filtered by their carbs, every lunch bolus as an event, and six
    /// groups in one report panel.
    /// </summary>
    [Fact]
    public async Task Report_day_filters_new_anchors_and_six_groups_run_over_the_seeded_week()
    {
        await SeedProfileAsync("Australia/Sydney");
        await SeedWeekAsync(WeekStart);
        object Panel(string id, object dataset, string kind = "table") => new { id, kind, dataset };
        var range = new { from = WeekStart.ToString("o"), to = WeekStart.AddDays(7).ToString("o") };
        var spec = new
        {
            version = 1,
            range,
            panels = new[]
            {
                Panel("carbDays", new { name = "daily_summary", days = new { kind = "carbs", minCarbs = 40 } }),
                Panel("otherDays", new { name = "daily_summary", days = new { kind = "carbs", minCarbs = 40, others = true } }),
                Panel("boluses", new { name = "event_response", cohorts = new[] { new { anchor = "AnyBolus" } } }),
                Panel("six", new
                {
                    name = "event_response",
                    cohorts = new object[]
                    {
                        new { anchor = "Meal" }, new { anchor = "UnannouncedRise" }, new { anchor = "AnyBolus" },
                        new { anchor = "HighStart" }, new { anchor = "LowTreatment" }, new { anchor = "CorrectionBolus" },
                    },
                }),
            },
        };

        var response = await AuthenticatedClient.PostAsJsonAsync("/api/v4/reports/custom/run", new { spec });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("errors").GetArrayLength().Should().Be(0, body.ToString());
        var panels = body.GetProperty("result").GetProperty("panels").EnumerateArray().ToDictionary(p => p.GetProperty("id").GetString()!);
        panels["carbDays"].GetProperty("rows").GetArrayLength().Should().Be(7, panels["carbDays"].GetProperty("summary").GetString());
        panels["carbDays"].GetProperty("summary").GetString().Should().StartWith("Only days with 40 g of carbs or more: 7 of 7 days.");
        panels["otherDays"].GetProperty("rows").GetArrayLength().Should().Be(0);
        panels["boluses"].GetProperty("facts")[0].GetProperty("value").GetString().Should().Be("7", panels["boluses"].GetProperty("summary").GetString());
        panels["six"].GetProperty("cohorts").GetArrayLength().Should().Be(6);
        panels["six"].GetProperty("summary").GetString().Should().Contain("Cohort 6 (Correction bolus: all)");
    }

    /// <summary>
    /// Seven days from <paramref name="start"/>, local midnight: glucose every five minutes at 110,
    /// up 80 an hour after a 45 g lunch with a 4.5 U bolus at 12:00 and back by 15:00, and climbing
    /// 3 mg/dL a minute from 19:00 to 200 with no carbs, answered by six 0.3 U automatic boluses;
    /// an algorithm temp of 1.5 U/h throughout.
    /// </summary>
    private async Task SeedWeekAsync(DateTime start)
    {
        using var scope = TenantScope();
        await using var context = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();

        static double Glucose(double minuteOfDay) => minuteOfDay switch
        {
            >= 720 and <= 780 => 110 + (minuteOfDay - 720) * 80 / 60,
            > 780 and <= 900 => 190 - (minuteOfDay - 780) * 80 / 120,
            > 1140 => Math.Min(200, 110 + (minuteOfDay - 1140) * 3),
            _ => 110,
        };

        for (var t = start; t < start.AddDays(7); t = t.AddMinutes(5))
        {
            context.SensorGlucose.Add(new SensorGlucoseEntity
            {
                Id = Guid.NewGuid(), Timestamp = t, Mgdl = Glucose((t - start).TotalMinutes % 1440), DataSource = "test",
            });
            context.TempBasals.Add(new TempBasalEntity
            {
                Id = Guid.NewGuid(), Timestamp = t, EndTimestamp = t.AddMinutes(30), Rate = 1.5,
                Origin = "Algorithm", Device = "pump", DataSource = "test",
            });
        }
        for (var day = 0; day < 7; day++)
        {
            var lunch = start.AddDays(day).AddHours(12);
            context.CarbIntakes.Add(new CarbIntakeEntity { Id = Guid.NewGuid(), Timestamp = lunch, Carbs = 45, DataSource = "test" });
            context.Boluses.Add(new BolusEntity { Id = Guid.NewGuid(), Timestamp = lunch, Insulin = 4.5, BolusKind = "Manual", DataSource = "test" });
            var rise = start.AddDays(day).AddHours(19);
            for (var i = 1; i <= 6; i++)
                context.Boluses.Add(new BolusEntity
                {
                    Id = Guid.NewGuid(), Timestamp = rise.AddMinutes(5 * i), Insulin = 0.3, BolusKind = "Algorithm", DataSource = "test",
                });
        }
        await context.SaveChangesAsync();
    }
}

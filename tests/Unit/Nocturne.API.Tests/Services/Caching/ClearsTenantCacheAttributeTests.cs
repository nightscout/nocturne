using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Controllers.V4.Devices;
using Nocturne.API.Controllers.V4.Glucose;
using Nocturne.API.Controllers.V4.Treatments;
using Nocturne.API.Services.Caching;

namespace Nocturne.API.Tests.Services.Caching;

[Trait("Category", "Unit")]
public class ClearsTenantCacheAttributeTests
{
    private readonly Mock<ITenantCache> _cache = new();

    private async Task RunAsync(
        ClearsTenantCacheAttribute filter,
        string method,
        IActionResult? result,
        Exception? exception = null,
        params DateTime[] footprint)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(_cache.Object).BuildServiceProvider(),
        };
        http.Request.Method = method;
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var executing = new ActionExecutingContext(action, [], new Dictionary<string, object?>(), new object());
        var executed = new ActionExecutedContext(action, [], new object()) { Result = result, Exception = exception };

        await filter.OnActionExecutionAsync(executing, () =>
        {
            TenantCacheWriteFootprint.Of(http)?.AddRange(footprint);
            return Task.FromResult(executed);
        });
    }

    private Task RunAsync(string method, IActionResult? result, Exception? exception = null) =>
        RunAsync(new ClearsTenantCacheAttribute(TenantCacheFamily.Statistics, TenantCacheFamily.DataOverview),
            method, result, exception);

    private IEnumerable<string> Cleared() =>
        _cache.Invocations
            .Where(i => i.Method.Name == nameof(ITenantCache.RemoveFamilyAsync))
            .Select(i => (string)i.Arguments[0]);

    public static TheoryData<Type, string[]> FamiliesByController() => new()
    {
        { typeof(SensorGlucoseController), [TenantCacheFamily.Statistics, TenantCacheFamily.EHbA1c] },
        { typeof(MeterGlucoseController), [TenantCacheFamily.EHbA1c] },
        { typeof(DeviceEventController), [TenantCacheFamily.Statistics] },
        { typeof(BolusController), [TenantCacheFamily.Statistics] },
        { typeof(BasalInjectionController), [TenantCacheFamily.Statistics] },
        { typeof(NutritionController), [TenantCacheFamily.Statistics] },
        { typeof(TempBasalController), [TenantCacheFamily.Statistics] },
    };

    [Theory]
    [MemberData(nameof(FamiliesByController))]
    public void Each_v4_writer_clears_the_families_its_records_feed(Type controller, string[] families) =>
        controller.GetCustomAttribute<ClearsTenantCacheAttribute>()!.Families
            .Should().BeEquivalentTo(families);

    /// <summary>
    /// The legacy read families are cleared by the repositories on every write
    /// (<see cref="LegacyReadCacheInvalidation"/>), so a controller naming one would be a second,
    /// partial source of the same rule.
    /// </summary>
    [Fact]
    public void No_controller_names_a_legacy_read_family()
    {
        var controllers = typeof(SensorGlucoseController).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<ClearsTenantCacheAttribute>() is not null)
            .ToList();

        controllers.Should().NotBeEmpty();
        controllers.Where(t => t.GetCustomAttribute<ClearsTenantCacheAttribute>()!.Families
                .Intersect([TenantCacheFamily.Entries, TenantCacheFamily.Treatments]).Any())
            .Should().BeEmpty();
    }

    [Fact]
    public async Task A_successful_write_clears_each_declared_family()
    {
        await RunAsync(HttpMethods.Post, new CreatedResult("/x", new object()));

        Cleared().Should().BeEquivalentTo(TenantCacheFamily.Statistics, TenantCacheFamily.DataOverview);
        _cache.Verify(c => c.RemoveTenantAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_typed_result_without_a_status_counts_as_success()
    {
        await RunAsync(HttpMethods.Put, new ObjectResult(new object()));

        Cleared().Should().HaveCount(2);
    }

    [Fact]
    public async Task A_read_clears_nothing()
    {
        await RunAsync(HttpMethods.Get, new OkObjectResult(new object()));

        Cleared().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_refused_write_clears_nothing(IActionResult result)
    {
        await RunAsync(HttpMethods.Post, result);

        Cleared().Should().BeEmpty();
    }

    public static TheoryData<IActionResult> Refusals() =>
    [
        new ForbidResult(),
        new StatusCodeResult(StatusCodes.Status403Forbidden),
        new BadRequestObjectResult(new ValidationProblemDetails()),
        new ConflictObjectResult(new object()),
        new ObjectResult(new ProblemDetails()),
    ];

    [Fact]
    public async Task A_write_that_threw_clears_nothing()
    {
        await RunAsync(HttpMethods.Delete, null, new InvalidOperationException());

        Cleared().Should().BeEmpty();
    }

    [Fact]
    public async Task A_glucose_write_clears_only_the_ehba1c_years_its_timestamps_reach()
    {
        await RunAsync(
            new ClearsTenantCacheAttribute(TenantCacheFamily.EHbA1c), HttpMethods.Post, new CreatedResult("/x", null),
            footprint: [new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)]);

        Cleared().Should().Equal("ehba1c:2026");
    }

    [Fact]
    public async Task A_reading_late_in_a_year_also_clears_the_next_year_whose_window_it_falls_in()
    {
        await RunAsync(
            new ClearsTenantCacheAttribute(TenantCacheFamily.EHbA1c), HttpMethods.Post, new CreatedResult("/x", null),
            footprint: [new DateTime(2024, 11, 15, 0, 0, 0, DateTimeKind.Utc)]);

        Cleared().Should().Equal("ehba1c:2024", "ehba1c:2025");
    }

    [Fact]
    public async Task A_reading_at_new_year_utc_also_clears_the_year_it_is_still_in_locally()
    {
        await RunAsync(
            new ClearsTenantCacheAttribute(TenantCacheFamily.EHbA1c), HttpMethods.Post, new CreatedResult("/x", null),
            footprint: [new DateTime(2025, 1, 1, 3, 0, 0, DateTimeKind.Utc)]);

        Cleared().Should().Equal("ehba1c:2024", "ehba1c:2025");
    }

    [Fact]
    public async Task A_moved_reading_clears_the_years_of_both_its_old_and_its_new_timestamp()
    {
        await RunAsync(
            new ClearsTenantCacheAttribute(TenantCacheFamily.EHbA1c), HttpMethods.Post, new CreatedResult("/x", null),
            footprint:
            [
                new DateTime(2022, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            ]);

        Cleared().Should().Equal("ehba1c:2022", "ehba1c:2026");
    }

    [Fact]
    public async Task A_write_that_records_no_timestamps_clears_every_ehba1c_year()
    {
        await RunAsync(
            new ClearsTenantCacheAttribute(TenantCacheFamily.Statistics, TenantCacheFamily.EHbA1c),
            HttpMethods.Delete, new NoContentResult());

        Cleared().Should().BeEquivalentTo(TenantCacheFamily.Statistics, TenantCacheFamily.EHbA1c);
    }

    [Theory]
    [MemberData(nameof(ExtremeTimestamps))]
    public async Task A_timestamp_at_the_edge_of_the_calendar_clears_every_ehba1c_year(DateTime extreme)
    {
        await RunAsync(
            new ClearsTenantCacheAttribute(TenantCacheFamily.EHbA1c), HttpMethods.Post, new CreatedResult("/x", null),
            footprint: [new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), extreme]);

        Cleared().Should().Equal(TenantCacheFamily.EHbA1c);
    }

    public static TheoryData<DateTime> ExtremeTimestamps() =>
    [
        DateTime.MinValue,
        DateTime.MaxValue,
        DateTime.MaxValue.AddDays(-50),
    ];
}

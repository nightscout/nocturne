using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Controllers.V4.Glucose;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.API.Controllers.V4.Treatments;
using V1EntriesController = Nocturne.API.Controllers.V1.EntriesController;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// Pins the <see cref="ResponseCacheAttribute"/> posture of every cache-annotated read, so a
/// duration cannot be reintroduced on an endpoint that must not carry one.
/// </summary>
public class ResponseCachePolicyTests
{
    private static ResponseCacheAttribute CacheOn(Type controller, string action)
    {
        var attribute = controller.GetMethod(action)!.GetCustomAttribute<ResponseCacheAttribute>();
        attribute.Should().NotBeNull($"{controller.Name}.{action} declares a cache posture");
        return attribute!;
    }

    /// <summary>
    /// Per-user therapy and glucose reads the caller can mutate. Rationale at
    /// <see cref="ProfileController.GetProfileSummary"/>.
    /// </summary>
    [Theory]
    [InlineData(typeof(BolusController), nameof(BolusController.GetAll))]
    [InlineData(typeof(TempBasalController), nameof(TempBasalController.GetAll))]
    [InlineData(typeof(SensorGlucoseController), nameof(SensorGlucoseController.GetAll))]
    [InlineData(typeof(MeterGlucoseController), nameof(MeterGlucoseController.GetAll))]
    [InlineData(typeof(CalibrationController), nameof(CalibrationController.GetAll))]
    [InlineData(typeof(StateSpansController), nameof(StateSpansController.GetStateSpans))]
    [InlineData(typeof(StateSpansController), nameof(StateSpansController.GetPumpModes))]
    [InlineData(typeof(StateSpansController), nameof(StateSpansController.GetConnectivity))]
    [InlineData(typeof(StateSpansController), nameof(StateSpansController.GetOverrides))]
    [InlineData(typeof(StateSpansController), nameof(StateSpansController.GetTemporaryTargets))]
    [InlineData(typeof(StateSpansController), nameof(StateSpansController.GetProfiles))]
    [InlineData(typeof(StateSpansController), nameof(StateSpansController.GetExercise))]
    [InlineData(typeof(StateSpansController), nameof(StateSpansController.GetIllness))]
    [InlineData(typeof(StateSpansController), nameof(StateSpansController.GetTravel))]
    [InlineData(typeof(StateSpansController), nameof(StateSpansController.GetActivities))]
    [InlineData(typeof(StateSpansController), nameof(StateSpansController.GetStateSpan))]
    [InlineData(typeof(V1EntriesController), nameof(V1EntriesController.GetCurrentEntry))]
    [InlineData(typeof(V1EntriesController), nameof(V1EntriesController.GetEntries))]
    [InlineData(typeof(ProfileController), nameof(ProfileController.GetProfileSummary))]
    [InlineData(typeof(PredictionController), nameof(PredictionController.GetProfileSnapshot))]
    public void MutableReads_AreNeverStored(Type controller, string action)
    {
        var cache = CacheOn(controller, action);

        cache.NoStore.Should().BeTrue();
        cache.Location.Should().Be(ResponseCacheLocation.None);
        cache.Duration.Should().Be(0);
    }

    /// <summary>
    /// Per-scope redaction makes the body depend on the caller's credential, so a redacted
    /// response must not sit in the shared response cache, whose key is host + query + Cookie: a
    /// credential presenting neither a cookie nor an <c>Authorization</c> header (the legacy
    /// <c>api-secret</c> header) would otherwise be served another caller's unredacted body.
    /// </summary>
    [Theory]
    [InlineData(typeof(ChartDataController), nameof(ChartDataController.GetDashboardChartData))]
    [InlineData(typeof(ActogramController), nameof(ActogramController.GetActogram))]
    public void RedactedResponses_AreNotSharedCacheable(Type controller, string action)
    {
        CacheOn(controller, action).Location.Should().Be(ResponseCacheLocation.Client);
    }

    /// <summary>
    /// Aggregates over time-series data, which the 24-hour history clamp narrows per caller, so
    /// they keep their staleness window in the caller's own cache only: a clamped and an unclamped
    /// <c>api-secret</c> caller present the same shared-cache key.
    /// </summary>
    [Theory]
    [InlineData(typeof(DataOverviewController), nameof(DataOverviewController.GetAvailableYears), 300)]
    [InlineData(typeof(DataOverviewController), nameof(DataOverviewController.GetDailySummary), 180)]
    [InlineData(typeof(DataOverviewController), nameof(DataOverviewController.GetGriTimeline), 300)]
    [InlineData(typeof(DataOverviewController), nameof(DataOverviewController.GetEHbA1cTimeline), 300)]
    [InlineData(typeof(StatisticsController), nameof(StatisticsController.GetRangeAnalytics), 60)]
    [InlineData(typeof(StatisticsController), nameof(StatisticsController.GetWeekdayAverages), 60)]
    [InlineData(typeof(SensorIntegrityController), nameof(SensorIntegrityController.Analyze), 60)]
    [InlineData(typeof(CgmComparisonController), nameof(CgmComparisonController.Compare), 60)]
    [InlineData(typeof(ChartDataController), nameof(ChartDataController.GetBasalSeries), 60)]
    public void ClampNarrowedReads_KeepTheirWindowInTheCallersCacheOnly(Type controller, string action, int seconds)
    {
        var cache = CacheOn(controller, action);

        cache.NoStore.Should().BeFalse();
        cache.Location.Should().Be(ResponseCacheLocation.Client);
        cache.Duration.Should().Be(seconds);
    }

    /// <summary>
    /// Every attribute-cached read in the API serves tenant data some caller may see narrowed, by
    /// scope or by the history clamp, so no <see cref="ResponseCacheAttribute"/> may allow the
    /// shared response cache. Inherited actions count, so an abstract base cannot hide one.
    /// </summary>
    [Fact]
    public void NoResponseCacheAttribute_AllowsTheSharedCache()
    {
        var shared = typeof(ChartDataController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.DeclaringType != typeof(ControllerBase) && m.DeclaringType != typeof(object))
                .Select(m => (Action: $"{t.Name}.{m.Name}",
                    Cache: m.GetCustomAttribute<ResponseCacheAttribute>() ?? t.GetCustomAttribute<ResponseCacheAttribute>(inherit: true))))
            .Where(x => x.Cache is { NoStore: false, Location: ResponseCacheLocation.Any })
            .Select(x => x.Action)
            .ToList();

        shared.Should().BeEmpty();
    }
}

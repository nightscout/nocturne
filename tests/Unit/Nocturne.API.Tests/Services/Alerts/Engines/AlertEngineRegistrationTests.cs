using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Repositories;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

/// <summary>
/// <see cref="ServiceRegistrationExtensions.AddAlertEvaluationEngine"/>: every seam resolves to the
/// Rust engine, and a native library that fails its probe stops startup.
/// </summary>
public class AlertEngineRegistrationTests
{
    private static readonly Func<NativeProbeResult> Present = () => new NativeProbeResult(null, "0.1.0", "2025b");

    private static ServiceProvider BuildProvider(Func<NativeProbeResult> nativeProbe)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Mock.Of<IAlertTrackerRepository>());
        services.AddSingleton<AlertRuleEvaluationGate>();
        services.AddAlertEvaluationEngine(nativeProbe);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void The_engine_seams_resolve_to_the_rust_engine()
    {
        using var provider = BuildProvider(Present);
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IAlertEvaluationEngine>()
            .Should().BeOfType<RustBackedAlertEngine>();
        scope.ServiceProvider.GetRequiredService<IAlertReplayEngine>()
            .Should().BeOfType<RustAlertReplayEngine>();
        scope.ServiceProvider.GetRequiredService<IExcursionTracker>()
            .Should().BeOfType<ExcursionTracker>()
            .Which.Decider.Should().BeOfType<RustExcursionDecider>();
    }

    [Fact]
    public void The_native_library_records_what_its_probe_found()
    {
        using var provider = BuildProvider(Present);

        provider.GetRequiredService<AlertEngineNativeLibrary>()
            .Should().Be(new AlertEngineNativeLibrary("0.1.0", "2025b"));
    }

    [Fact]
    public void A_native_library_that_fails_its_probe_stops_startup()
    {
        using var provider = BuildProvider(() => NativeProbeResult.Unavailable("not found"));

        var act = () => provider.GetRequiredService<AlertEngineNativeLibrary>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*failed its probe: not found*");
    }

    [Fact]
    public void A_probe_that_throws_stops_startup()
    {
        var act = () => AlertEngineNativeLibrary.Load(
            () => throw new DllNotFoundException("nocturne_alerts"), new ListLogger<object>());

        act.Should().Throw<InvalidOperationException>().WithInnerException<DllNotFoundException>();
    }
}

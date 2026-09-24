using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Repositories;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

/// <summary>
/// The <c>Alerts:Engine</c> flag: default resolves the managed engine; <c>rust</c> refuses to
/// start without a native library that passes its probe; <c>shadow</c> falls back to managed
/// with an Error; unknown values fall back to managed.
/// </summary>
public class AlertEngineSelectionTests
{
    private static readonly Func<NativeProbeResult> Present = () => NativeProbeResult.Available;
    private static readonly Func<NativeProbeResult> Absent = () => NativeProbeResult.Unavailable("not found");

    // -----------------------------------------------------------------------
    // Selector unit tests
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("managed")]
    [InlineData(" Managed ")]
    public void Default_and_managed_resolve_managed_without_probing(string? configured)
    {
        var logger = new ListLogger<object>();
        var probed = false;

        var selection = AlertEngineSelector.Select(configured, () => { probed = true; return NativeProbeResult.Available; }, logger);

        selection.Mode.Should().Be(AlertEngineMode.Managed);
        probed.Should().BeFalse("managed mode must not touch the native library");
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [Theory]
    [InlineData("rust", "Rust")]
    [InlineData("shadow", "Shadow")]
    [InlineData("RUST", "Rust")]
    public void Rust_and_shadow_resolve_when_the_native_library_is_available(string configured, string expected)
    {
        var logger = new ListLogger<object>();

        var selection = AlertEngineSelector.Select(configured, Present, logger);

        selection.Mode.Should().Be(Enum.Parse<AlertEngineMode>(expected));
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void Rust_refuses_to_start_when_the_native_library_fails_its_probe()
    {
        var act = () => AlertEngineSelector.Select("rust", Absent, new ListLogger<object>());

        act.Should().Throw<InvalidOperationException>().WithMessage("*failed its probe: not found*");
    }

    [Fact]
    public void Rust_refuses_to_start_when_the_probe_throws()
    {
        var act = () => AlertEngineSelector.Select("rust", () => throw new DllNotFoundException("nope"), new ListLogger<object>());

        act.Should().Throw<InvalidOperationException>().WithInnerException<DllNotFoundException>();
    }

    [Fact]
    public void Shadow_falls_back_to_managed_with_an_error_when_the_native_library_fails_its_probe()
    {
        var logger = new ListLogger<object>();

        var selection = AlertEngineSelector.Select("shadow", Absent, logger);

        selection.Mode.Should().Be(AlertEngineMode.Managed);
        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Error && e.Message.Contains("shadow comparison is off"));
    }

    [Fact]
    public void Shadow_falls_back_to_managed_with_an_error_when_the_probe_throws()
    {
        var logger = new ListLogger<object>();

        var selection = AlertEngineSelector.Select("shadow", () => throw new DllNotFoundException("nope"), logger);

        selection.Mode.Should().Be(AlertEngineMode.Managed);
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && e.Exception is DllNotFoundException);
    }

    [Fact]
    public void Unknown_values_fall_back_to_managed_with_a_warning()
    {
        var logger = new ListLogger<object>();

        var selection = AlertEngineSelector.Select("kotlin", Present, logger);

        selection.Mode.Should().Be(AlertEngineMode.Managed);
        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Unknown Alerts:Engine value"));
    }

    // -----------------------------------------------------------------------
    // DI registration tests
    // -----------------------------------------------------------------------

    private static ServiceProvider BuildProvider(string? engineFlag, Func<NativeProbeResult> nativeProbe)
    {
        var configValues = new Dictionary<string, string?>();
        if (engineFlag is not null) configValues["Alerts:Engine"] = engineFlag;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Mock.Of<IConditionTimerStore>());
        services.AddSingleton(Mock.Of<IAlertTrackerRepository>());
        services.AddSingleton(Mock.Of<IExcursionTracker>());
        services.AddSingleton<AlertRuleEvaluationGate>();
        services.AddAlertEvaluators();
        services.AddScoped<ConditionEvaluatorRegistry>();
        services.AddAlertEvaluationEngine(configuration, nativeProbe);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Default_registration_resolves_the_managed_engine()
    {
        using var provider = BuildProvider(engineFlag: null, nativeProbe: Present);
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IAlertEvaluationEngine>()
            .Should().BeOfType<ManagedAlertEngine>();
    }

    [Fact]
    public void Rust_flag_without_the_native_library_fails_to_resolve_the_selection()
    {
        using var provider = BuildProvider("rust", nativeProbe: Absent);

        var act = () => provider.GetRequiredService<AlertEngineSelection>();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Rust_flag_with_the_native_library_resolves_the_rust_engine()
    {
        using var provider = BuildProvider("rust", nativeProbe: Present);
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IAlertEvaluationEngine>()
            .Should().BeOfType<RustBackedAlertEngine>();
    }

    [Fact]
    public void Shadow_flag_with_the_native_library_resolves_the_shadow_engine()
    {
        using var provider = BuildProvider("shadow", nativeProbe: Present);
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IAlertEvaluationEngine>()
            .Should().BeOfType<ShadowAlertEngine>();
    }
}

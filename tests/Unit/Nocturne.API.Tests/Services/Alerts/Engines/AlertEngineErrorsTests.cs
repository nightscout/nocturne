using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Tests.Services.BackgroundServices;
using Nocturne.Core.Alerts.Native;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

public class AlertEngineErrorsTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 8, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Start);

    [Fact]
    public void A_native_failure_is_counted_with_its_operation_and_engine_and_rethrown()
    {
        using var factory = new TestMeterFactory();
        var measurements = new List<(long Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (factory.Owns(instrument.Meter) && instrument.Name == "alerts.engine.errors")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            measurements.Add((value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        listener.Start();
        var errors = new AlertEngineErrors(factory, _time);

        var act = () => errors.Track<int>("evaluate_node", AlertEngineErrors.RustEngine,
            () => throw new RustAlertEngineException("bad"));

        act.Should().Throw<RustAlertEngineException>();
        measurements.Should().ContainSingle();
        measurements[0].Value.Should().Be(1);
        measurements[0].Tags.Should().Contain("operation", "evaluate_node").And.Contain("engine", "rust");
        errors.Latest.Should().Be(new AlertEngineError(Start, "evaluate_node", "rust"));
    }

    [Fact]
    public void A_successful_call_records_nothing()
    {
        using var factory = new TestMeterFactory();
        var errors = new AlertEngineErrors(factory, _time);

        errors.Track("evaluate", AlertEngineErrors.RustEngine, () => 42).Should().Be(42);

        errors.Latest.Should().BeNull();
    }

    private async Task<HealthCheckResult> CheckAsync(AlertEngineSelection selection, AlertEngineErrors errors) =>
        await new AlertEngineHealthCheck(selection, errors, _time)
            .CheckHealthAsync(new HealthCheckContext());

    [Fact]
    public async Task The_rust_engine_is_degraded_within_the_window_of_its_latest_failure()
    {
        using var factory = new TestMeterFactory();
        var errors = new AlertEngineErrors(factory, _time);
        var rust = new AlertEngineSelection(AlertEngineMode.Rust, "rust");

        (await CheckAsync(rust, errors)).Status.Should().Be(HealthStatus.Healthy);

        errors.Record("evaluate", AlertEngineErrors.RustEngine);
        (await CheckAsync(rust, errors)).Status.Should().Be(HealthStatus.Degraded);

        _time.Advance(AlertEngineHealthCheck.FailingWindow);
        (await CheckAsync(rust, errors)).Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Shadow_that_fell_back_to_managed_is_degraded()
    {
        using var factory = new TestMeterFactory();
        var errors = new AlertEngineErrors(factory, _time);

        (await CheckAsync(new AlertEngineSelection(AlertEngineMode.Managed, "shadow"), errors))
            .Status.Should().Be(HealthStatus.Degraded);
        (await CheckAsync(new AlertEngineSelection(AlertEngineMode.Managed, "managed"), errors))
            .Status.Should().Be(HealthStatus.Healthy);
    }
}

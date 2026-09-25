using System.Diagnostics.Metrics;
using FluentAssertions;
using Nocturne.API.Services.BackgroundServices;
using Xunit;

namespace Nocturne.API.Tests.Services.BackgroundServices;

public class ConnectorSyncMetricsTests
{
    [Fact]
    public void SlotWait_IsRecordedTaggedWithTheConnector()
    {
        using var factory = new TestMeterFactory();
        using var listener = new ConnectorMetricListener(factory);
        var metrics = new ConnectorSyncMetrics(factory, new ConnectorSyncBudget());

        metrics.RecordSlotWait("TestConnector", TimeSpan.FromSeconds(1.5));

        listener.SlotWaits.Should().ContainSingle();
        listener.SlotWaits[0].Value.Should().BeApproximately(1.5, 1e-6);
        listener.SlotWaits[0].Connector.Should().Be("TestConnector");
    }

    [Fact]
    public void SyncDuration_IsRecordedTaggedWithTheConnectorAndOutcome()
    {
        using var factory = new TestMeterFactory();
        using var listener = new ConnectorMetricListener(factory);
        var metrics = new ConnectorSyncMetrics(factory, new ConnectorSyncBudget());

        metrics.RecordSyncDuration("TestConnector", "failure", TimeSpan.FromSeconds(2));

        listener.Durations.Should().ContainSingle();
        listener.Durations[0].Value.Should().BeApproximately(2, 1e-6);
        listener.Durations[0].Connector.Should().Be("TestConnector");
        listener.Durations[0].Outcome.Should().Be("failure");
    }

    /// <summary>
    /// The gauge reads the budget, so a leased slot is visible without the budget pushing to it.
    /// </summary>
    [Fact]
    public async Task InFlightGauge_ReportsTheBudgetsLeasedCount()
    {
        var budget = new ConnectorSyncBudget(slots: 3);
        using var factory = new TestMeterFactory();
        using var listener = new ConnectorMetricListener(factory);
        var _ = new ConnectorSyncMetrics(factory, budget);

        listener.CollectObservable();
        listener.InFlight.Last().Should().Be(0);

        var lease = await budget.AcquireAsync(CancellationToken.None);

        listener.CollectObservable();
        listener.InFlight.Last().Should().Be(1);

        lease.Dispose();

        listener.CollectObservable();
        listener.InFlight.Last().Should().Be(0);
    }
}

/// <summary>Creates meters the test owns and disposes, standing in for the DI <c>IMeterFactory</c>.</summary>
internal sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = [];

    public bool Owns(Meter meter) => _meters.Contains(meter);

    public Meter Create(MeterOptions options)
    {
        var meter = new Meter(options);
        _meters.Add(meter);
        return meter;
    }

    public void Dispose()
    {
        foreach (var meter in _meters)
            meter.Dispose();
    }
}

/// <summary>
/// Collects measurements from the meters one <see cref="TestMeterFactory"/> created. Listening by meter
/// name alone would also hear the instruments of tests running in parallel.
/// </summary>
internal sealed class ConnectorMetricListener : IDisposable
{
    private readonly MeterListener _listener = new();

    public List<(double Value, string? Connector, string? Outcome)> Durations { get; } = [];

    public List<(double Value, string? Connector)> SlotWaits { get; } = [];

    public List<int> InFlight { get; } = [];

    public ConnectorMetricListener(TestMeterFactory factory)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (factory.Owns(instrument.Meter))
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            var (connector, outcome) = ReadTags(tags);
            if (instrument.Name == "connector.sync.duration")
                Durations.Add((value, connector, outcome));
            else if (instrument.Name == "connector.sync.slot_wait")
                SlotWaits.Add((value, connector));
        });

        _listener.SetMeasurementEventCallback<int>((instrument, value, _, _) =>
        {
            if (instrument.Name == "connector.budget.in_flight")
                InFlight.Add(value);
        });

        _listener.Start();
    }

    public void CollectObservable() => _listener.RecordObservableInstruments();

    public void Dispose() => _listener.Dispose();

    private static (string? Connector, string? Outcome) ReadTags(
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        string? connector = null;
        string? outcome = null;

        foreach (var tag in tags)
        {
            if (tag.Key == "connector")
                connector = (string?)tag.Value;
            else if (tag.Key == "outcome")
                outcome = (string?)tag.Value;
        }

        return (connector, outcome);
    }
}

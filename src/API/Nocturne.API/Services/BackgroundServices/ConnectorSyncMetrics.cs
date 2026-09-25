using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.Metrics;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// The connector syncs' OpenTelemetry instruments: how long a tenant's sync took and how it ended,
/// how long it waited for a budget slot, and the budget's live occupancy. Operators tuning
/// <see cref="ConnectorSyncBudget.SlotsKey"/> or the Npgsql pool cap have these to graph; the poll
/// loop's spans alone do not separate one tenant's sync from the whole tick.
/// </summary>
/// <remarks>
/// A singleton, so the observable gauge is registered once rather than per poller. No tenant
/// identity is a tag: a tag value is a time series, and a tenant id would both explode the series
/// count and label the metric with a person.
/// </remarks>
public sealed class ConnectorSyncMetrics
{
    public const string MeterName = "Nocturne.Connectors";

    private readonly Histogram<double> _syncDuration;
    private readonly Histogram<double> _slotWait;

    public ConnectorSyncMetrics(IMeterFactory meterFactory, ConnectorSyncBudget budget)
    {
        var meter = meterFactory.Create(MeterName);

        _syncDuration = meter.CreateHistogram<double>(
            "connector.sync.duration",
            unit: "s",
            description: "Duration of a tenant's connector sync.");

        _slotWait = meter.CreateHistogram<double>(
            "connector.sync.slot_wait",
            unit: "s",
            description: "Time a tenant's sync waited for a connector sync budget slot.");

        meter.CreateObservableGauge(
            "connector.budget.in_flight",
            () => budget.InFlight,
            description: "Tenant syncs currently holding a budget slot.");
    }

    /// <param name="connector">The connector's section name; never a tenant.</param>
    /// <param name="outcome">One of <c>success</c>, <c>failure</c>, <c>timeout</c>, <c>cancelled</c>.</param>
    /// <param name="duration">How long the tenant's sync ran.</param>
    public void RecordSyncDuration(string connector, string outcome, TimeSpan duration) =>
        _syncDuration.Record(
            duration.TotalSeconds,
            new KeyValuePair<string, object?>("connector", connector),
            new KeyValuePair<string, object?>("outcome", outcome));

    public void RecordSlotWait(string connector, TimeSpan wait) =>
        _slotWait.Record(wait.TotalSeconds, new KeyValuePair<string, object?>("connector", connector));
}

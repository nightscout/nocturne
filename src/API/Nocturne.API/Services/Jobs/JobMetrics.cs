using System.Diagnostics.Metrics;

namespace Nocturne.API.Services.Jobs;

/// <summary>
/// The scheduled jobs' OpenTelemetry instruments: how long each run took and how it ended, and how
/// many ledger rows are failing.
/// </summary>
/// <remarks>
/// A tag is a job name or an outcome, never a tenant: a tag value is a time series, and a tenant id
/// would both explode the series count and label the metric with a person. The failing count is the
/// one <see cref="JobScheduler"/> last read, so the gauge's callback never reaches the database.
/// </remarks>
public sealed class JobMetrics
{
    public const string MeterName = "Nocturne.Jobs";

    private readonly Histogram<double> _runDuration;
    private int _failing;

    public JobMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _runDuration = meter.CreateHistogram<double>(
            "job.run.duration",
            unit: "s",
            description: "Duration of one run of a scheduled job for one ledger row.");

        meter.CreateObservableGauge(
            "job.failing",
            () => Volatile.Read(ref _failing),
            description: "Ledger rows whose last run failed.");
    }

    public void RecordRun(string job, string outcome, TimeSpan duration) =>
        _runDuration.Record(
            duration.TotalSeconds,
            new KeyValuePair<string, object?>("job", job),
            new KeyValuePair<string, object?>("outcome", outcome));

    public void SetFailing(int count) => Volatile.Write(ref _failing, count);
}

using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Nocturne.Core.Alerts.Native;

namespace Nocturne.API.Services.Alerts.Engines;

/// <summary>
/// Counts native alert engine failures (<see cref="RustAlertEngineException"/>) and remembers the
/// latest for <see cref="AlertEngineHealthCheck"/>. A failure skips only the rule or tracker
/// operation it happened on, which is otherwise visible only as a log line per rule per tick.
/// </summary>
/// <remarks>
/// No rule or tenant identity is a tag. A tag value is a time series, and a tenant id would label
/// the metric with a person.
/// </remarks>
internal sealed class AlertEngineErrors
{
    public const string MeterName = "Nocturne.Alerts";

    /// <summary>The <c>engine</c> tag for the authoritative Rust engine.</summary>
    public const string RustEngine = "rust";

    /// <summary>The <c>engine</c> tag for the Rust engine running under shadow mode.</summary>
    public const string ShadowEngine = "shadow";

    private readonly Counter<long> _errors;
    private readonly TimeProvider _timeProvider;
    private AlertEngineError? _latest;

    public AlertEngineErrors(IMeterFactory meterFactory, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _errors = meterFactory.Create(MeterName).CreateCounter<long>(
            "alerts.engine.errors",
            description: "Native alert engine calls that failed, skipping the rule they evaluated.");
    }

    /// <summary>The most recent failure, or <see langword="null"/> when none has happened.</summary>
    public AlertEngineError? Latest => Volatile.Read(ref _latest);

    /// <param name="operation">
    /// The native entry point: <c>evaluate</c>, <c>evaluate_node</c>, <c>tracker_process</c>,
    /// <c>tracker_force_close</c> or <c>tracker_close_elapsed_hysteresis</c>.
    /// </param>
    /// <param name="engine"><see cref="RustEngine"/> or <see cref="ShadowEngine"/>.</param>
    public void Record(string operation, string engine)
    {
        _errors.Add(1,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("engine", engine));
        Volatile.Write(ref _latest, new AlertEngineError(_timeProvider.GetUtcNow(), operation, engine));
    }

    /// <summary>Runs a native engine call, recording a <see cref="RustAlertEngineException"/> before rethrowing it.</summary>
    public T Track<T>(string operation, string engine, Func<T> call)
    {
        try
        {
            return call();
        }
        catch (RustAlertEngineException)
        {
            Record(operation, engine);
            throw;
        }
    }
}

/// <summary>One recorded native alert engine failure.</summary>
internal sealed record AlertEngineError(DateTimeOffset At, string Operation, string Engine);

/// <summary>
/// Reports Degraded while the Rust alert engine is failing. That is a native error within
/// <see cref="FailingWindow"/>, or shadow mode having fallen back to managed at startup.
/// </summary>
internal sealed class AlertEngineHealthCheck(
    AlertEngineSelection selection,
    AlertEngineErrors errors,
    TimeProvider timeProvider) : IHealthCheck
{
    /// <summary>
    /// How long after its latest failure the engine still counts as failing. Several sweep
    /// ticks, so a rule failing on every tick keeps the check Degraded continuously.
    /// </summary>
    public static readonly TimeSpan FailingWindow = TimeSpan.FromMinutes(15);

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (selection.Mode == AlertEngineMode.Managed)
        {
            return Task.FromResult(selection.Configured == "shadow"
                ? HealthCheckResult.Degraded("Alerts:Engine=shadow is configured but the native library failed its probe; the managed engine serves alerts without comparison")
                : HealthCheckResult.Healthy("Managed alert engine"));
        }

        if (errors.Latest is { } latest && timeProvider.GetUtcNow() - latest.At < FailingWindow)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"The {latest.Engine} alert engine failed '{latest.Operation}' at {latest.At:O}",
                data: new Dictionary<string, object>
                {
                    ["operation"] = latest.Operation,
                    ["engine"] = latest.Engine,
                    ["at"] = latest.At,
                }));
        }

        return Task.FromResult(HealthCheckResult.Healthy($"Alert engine: {selection.Configured}"));
    }
}

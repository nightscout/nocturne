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
/// <para>
/// A <see cref="RustAlertEngineException.IsConditionRejection"/> is the stored rule's fault, not
/// a failure. It is counted under <c>outcome=condition_rejected</c>, so one malformed stored rule
/// does not keep the engine reported as failing.
/// </para>
/// </remarks>
internal sealed class AlertEngineErrors
{
    public const string MeterName = "Nocturne.Alerts";

    /// <summary>The <c>engine</c> tag for the authoritative Rust engine.</summary>
    public const string RustEngine = "rust";

    /// <summary>The <c>engine</c> tag for the Rust engine running under shadow mode.</summary>
    public const string ShadowEngine = "shadow";

    private const int WindowMinutes = 15;

    private readonly Counter<long> _errors;
    private readonly TimeProvider _timeProvider;
    private readonly object _windowLock = new();
    private readonly long[] _bucketMinute = new long[WindowMinutes];
    private readonly long[] _bucketCalls = new long[WindowMinutes];
    private readonly long[] _bucketFailures = new long[WindowMinutes];
    private AlertEngineError? _latest;

    public AlertEngineErrors(IMeterFactory meterFactory, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _errors = meterFactory.Create(MeterName).CreateCounter<long>(
            "alerts.engine.errors",
            description: "Native alert engine calls that failed, skipping the rule they evaluated.");
        Array.Fill(_bucketMinute, long.MinValue);
    }

    /// <summary>How far back <see cref="Window"/> counts.</summary>
    public static TimeSpan WindowLength { get; } = TimeSpan.FromMinutes(WindowMinutes);

    /// <summary>The most recent failure, or <see langword="null"/> when none has happened.</summary>
    public AlertEngineError? Latest => Volatile.Read(ref _latest);

    /// <summary>Native calls and failures over the last <see cref="WindowLength"/>.</summary>
    public (long Calls, long Failures) Window()
    {
        var now = Minute();
        long calls = 0, failures = 0;
        lock (_windowLock)
        {
            for (var i = 0; i < WindowMinutes; i++)
            {
                if (now - _bucketMinute[i] >= WindowMinutes) continue;
                calls += _bucketCalls[i];
                failures += _bucketFailures[i];
            }
        }
        return (calls, failures);
    }

    /// <param name="operation">
    /// The native entry point: <c>evaluate</c>, <c>evaluate_node</c>, <c>tracker_process</c>,
    /// <c>tracker_force_close</c> or <c>tracker_close_elapsed_hysteresis</c>.
    /// </param>
    /// <param name="engine"><see cref="RustEngine"/> or <see cref="ShadowEngine"/>.</param>
    public void Record(string operation, string engine)
    {
        _errors.Add(1,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("engine", engine),
            new KeyValuePair<string, object?>("outcome", "failed"));
        Count(failed: true);
        Volatile.Write(ref _latest, new AlertEngineError(_timeProvider.GetUtcNow(), operation, engine));
    }

    /// <summary>
    /// Runs a native engine call, recording a <see cref="RustAlertEngineException"/> before
    /// rethrowing it.
    /// </summary>
    public T Track<T>(string operation, string engine, Func<T> call)
    {
        T result;
        try
        {
            result = call();
        }
        catch (RustAlertEngineException ex) when (ex.IsConditionRejection)
        {
            _errors.Add(1,
                new KeyValuePair<string, object?>("operation", operation),
                new KeyValuePair<string, object?>("engine", engine),
                new KeyValuePair<string, object?>("outcome", "condition_rejected"));
            Count(failed: false);
            throw;
        }
        catch (RustAlertEngineException)
        {
            Record(operation, engine);
            throw;
        }
        Count(failed: false);
        return result;
    }

    private long Minute() => _timeProvider.GetUtcNow().ToUnixTimeSeconds() / 60;

    private void Count(bool failed)
    {
        var minute = Minute();
        var i = (int)(minute % WindowMinutes);
        lock (_windowLock)
        {
            if (_bucketMinute[i] != minute)
            {
                _bucketMinute[i] = minute;
                _bucketCalls[i] = 0;
                _bucketFailures[i] = 0;
            }
            _bucketCalls[i]++;
            if (failed) _bucketFailures[i]++;
        }
    }
}

/// <summary>One recorded native alert engine failure.</summary>
internal sealed record AlertEngineError(DateTimeOffset At, string Operation, string Engine);

/// <summary>
/// Reports Unhealthy while most native calls fail: <see cref="UnhealthyFailureRate"/> of the calls
/// in <see cref="AlertEngineErrors.WindowLength"/>, and at least <see cref="UnhealthyMinimumFailures"/>.
/// Reports Degraded while any failed within <see cref="FailingWindow"/>, or when shadow mode fell
/// back to managed at startup.
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
    public static readonly TimeSpan FailingWindow = AlertEngineErrors.WindowLength;

    public const double UnhealthyFailureRate = 0.5;

    public const long UnhealthyMinimumFailures = 5;

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
            var (calls, failures) = errors.Window();
            var data = new Dictionary<string, object>
            {
                ["operation"] = latest.Operation,
                ["engine"] = latest.Engine,
                ["at"] = latest.At,
                ["calls"] = calls,
                ["failures"] = failures,
            };
            var failing = failures >= UnhealthyMinimumFailures && failures >= calls * UnhealthyFailureRate;
            var description =
                $"The {latest.Engine} alert engine failed {failures} of {calls} calls in the last {FailingWindow.TotalMinutes:0} minutes, latest '{latest.Operation}' at {latest.At:O}";
            return Task.FromResult(failing
                ? HealthCheckResult.Unhealthy(description, data: data)
                : HealthCheckResult.Degraded(description, data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy($"Alert engine: {selection.Configured}"));
    }
}

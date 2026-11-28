using Microsoft.Extensions.Options;
using Nocturne.Core.Models;
using Nocturne.Services.Demo.Configuration;

namespace Nocturne.Services.Demo.Services;

/// <summary>
/// Current state of the demo data service lifecycle.
/// </summary>
public enum DemoServiceState
{
    Stopped,
    Provisioning,
    Running,
    Paused,
}

/// <summary>
/// Background service that provisions the demo tenant, regenerates its historical data when
/// <see cref="ShouldRegenerateOnStartup"/> says so and on the reset schedule, returns the clinic persona's views and
/// rules to their seeds on the visitor reset schedule, and continuously generates real-time entries at configured
/// intervals.
/// All data persistence is performed via HTTP calls to the Nocturne API.
/// </summary>
public class DemoDataHostedService : BackgroundService
{
    private readonly ILogger<DemoDataHostedService> _logger;
    private readonly DemoModeConfiguration _config;
    private readonly IDemoDataGenerator _generator;
    private readonly DemoServiceHealthCheck _healthCheck;
    private readonly DemoApiClient _apiClient;
    private readonly TimeProvider _time;

    private volatile DemoServiceState _state = DemoServiceState.Stopped;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private TaskCompletionSource _resumeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private const int StaleAfterMissedReadings = 3;

    public DemoServiceState State => _state;

    public DemoDataHostedService(
        DemoApiClient apiClient,
        IOptions<DemoModeConfiguration> config,
        IDemoDataGenerator generator,
        DemoServiceHealthCheck healthCheck,
        TimeProvider time,
        ILogger<DemoDataHostedService> logger
    )
    {
        _apiClient = apiClient;
        _logger = logger;
        _config = config.Value;
        _generator = generator;
        _healthCheck = healthCheck;
        _time = time;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Enabled)
        {
            _logger.LogInformation("Demo mode is disabled, service will not run");
            return;
        }

        // Provision the demo tenant
        _state = DemoServiceState.Provisioning;
        var tenantState = await RetryAsync(_apiClient.ProvisionAsync, 10, "Provision", stoppingToken);
        if (tenantState == null)
        {
            _logger.LogError("Failed to provision demo tenant after retries, service will not run");
            _state = DemoServiceState.Stopped;
            return;
        }

        _state = DemoServiceState.Running;
        ((DemoDataGenerator)_generator).IsRunning = true;

        try
        {
            var lastResetUtc = tenantState.LastResetAt;
            var history = await RetryAsync(TryReadGlucoseHistoryAsync, 5, "Glucose history read", stoppingToken);
            if (ShouldRegenerateOnStartup(_config, UtcNow, lastResetUtc, history))
            {
                lastResetUtc = await RegenerateAndRecordResetAsync(stoppingToken);
            }
            else if (history?.Newest is { } newest)
            {
                _logger.LogInformation(
                    "Keeping the existing demo data, last reading at {LatestReading}", newest.TimestampUtc);
                _generator.SeedCurrentGlucose(newest.Mgdl);
            }
            else
            {
                _logger.LogWarning("Could not read the demo glucose history, keeping the existing data");
            }

            var nextVisitorResetUtc = NextVisitorResetDue(_config, tenantState.NextVisitorResetAt, UtcNow);
            if (tenantState.NextVisitorResetAt is null && nextVisitorResetUtc is { } firstVisitorReset)
                await RecordNextVisitorResetAsync(firstVisitorReset, stoppingToken);

            await GenerateAndPostEntryAsync(stoppingToken);

            var generationInterval = TimeSpan.FromMinutes(_config.IntervalMinutes);
            var nextGenerationUtc = UtcNow.Add(generationInterval);
            var nextResetUtc = NextResetDue(_config, lastResetUtc);

            while (!stoppingToken.IsCancellationRequested)
            {
                // If paused, wait for resume signal
                if (_state == DemoServiceState.Paused)
                {
                    try
                    {
                        await Task.WhenAny(_resumeSignal.Task, Task.Delay(Timeout.Infinite, stoppingToken));
                        if (stoppingToken.IsCancellationRequested)
                            break;
                        continue;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                var now = UtcNow;
                var delay = DelayUntilNextWake(now, nextGenerationUtc, nextResetUtc, nextVisitorResetUtc);

                try
                {
                    await Task.Delay(delay, _time, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Demo data generation service is stopping");
                    break;
                }

                if (_state != DemoServiceState.Running)
                    continue;

                try
                {
                    now = UtcNow;

                    if (nextResetUtc is { } resetDue && now >= resetDue)
                    {
                        nextResetUtc = NextResetDue(_config, await RegenerateAndRecordResetAsync(stoppingToken));
                        now = UtcNow;
                    }

                    if (nextVisitorResetUtc is { } visitorResetDue && now >= visitorResetDue)
                    {
                        nextVisitorResetUtc = await ResetVisitorStateAndRecordAsync(stoppingToken);
                        now = UtcNow;
                    }

                    if (now >= nextGenerationUtc)
                    {
                        await GenerateAndPostEntryAsync(stoppingToken);
                        nextGenerationUtc = now.Add(generationInterval);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Demo data generation service is stopping");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error generating demo data");
                }
            }
        }
        finally
        {
            _healthCheck.IsHealthy = false;
            ((DemoDataGenerator)_generator).IsRunning = false;
            _state = DemoServiceState.Stopped;
        }
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// A start regenerates when forced, when the periodic reset fell due while the service was
    /// down, when the tenant has no readings, when the newest reading is old enough that carrying
    /// on from now would leave a visible gap, or when the history does not reach back past the
    /// last reset, which means the seed after that reset failed or was cut short. Otherwise it
    /// keeps the data, so a redeploy neither rewrites the whole backfill nor restarts the reset
    /// schedule. A null <paramref name="history"/> could not be read and keeps the data, because
    /// a failed read must never wipe fresh data.
    /// </summary>
    internal static bool ShouldRegenerateOnStartup(
        DemoModeConfiguration config,
        DateTime now,
        DateTime? lastResetUtc,
        GlucoseHistory? history)
    {
        if (config.ClearOnStartup || config.RegenerateOnStartup)
            return true;

        if (NextResetDue(config, lastResetUtc) is { } due && now >= due)
            return true;

        if (history is null)
            return false;

        if (history is not { Oldest: { } oldest, Newest: { } newest })
            return true;

        var interval = TimeSpan.FromMinutes(config.IntervalMinutes);
        if (now - newest.TimestampUtc > interval * StaleAfterMissedReadings)
            return true;

        return lastResetUtc is { } reset && oldest.TimestampUtc > reset - interval;
    }

    /// <summary>
    /// The periodic reset is due one reset interval after the last one, which the API records on
    /// the demo tenant, so the schedule survives a restart. A tenant with no recorded reset is due
    /// at once. Null when periodic resets are off.
    /// </summary>
    internal static DateTime? NextResetDue(DemoModeConfiguration config, DateTime? lastResetUtc) =>
        config.ResetIntervalMinutes > 0
            ? (lastResetUtc ?? DateTime.MinValue).AddMinutes(config.ResetIntervalMinutes)
            : null;

    /// <summary>
    /// The visitor reset is due when the API's record says, so the schedule survives a restart, and one visitor reset
    /// interval from now when it records none. Null when visitor resets are off.
    /// </summary>
    internal static DateTime? NextVisitorResetDue(DemoModeConfiguration config, DateTime? recorded, DateTime now) =>
        config.VisitorResetIntervalMinutes > 0
            ? recorded ?? now.AddMinutes(config.VisitorResetIntervalMinutes)
            : null;

    internal static TimeSpan DelayUntilNextWake(
        DateTime now,
        DateTime nextGenerationUtc,
        DateTime? nextResetUtc,
        DateTime? nextVisitorResetUtc)
    {
        var nextWakeUtc = nextGenerationUtc;
        foreach (var due in new[] { nextResetUtc, nextVisitorResetUtc })
        {
            if (due is { } at && at < nextWakeUtc)
                nextWakeUtc = at;
        }

        var delay = nextWakeUtc - now;
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    /// <summary>
    /// Pauses real-time data generation. The service remains provisioned.
    /// </summary>
    public void Pause()
    {
        if (_state == DemoServiceState.Running)
        {
            _state = DemoServiceState.Paused;
            _logger.LogInformation("Demo service paused");
        }
    }

    /// <summary>
    /// Resumes real-time data generation after a pause.
    /// </summary>
    public void Resume()
    {
        if (_state == DemoServiceState.Paused)
        {
            _state = DemoServiceState.Running;
            // Signal the paused loop to continue
            _resumeSignal.TrySetResult();
            _resumeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _logger.LogInformation("Demo service resumed");
        }
    }

    /// <summary>
    /// Resets the demo tenant via the API, clearing its data and configuration.
    /// </summary>
    public async Task WipeAsync(CancellationToken ct)
    {
        _logger.LogInformation("Resetting demo tenant");
        await _apiClient.ResetAsync(ct);
        _logger.LogInformation("Demo tenant reset complete");
    }

    /// <summary>
    /// Stops the demo service and marks it as inactive.
    /// </summary>
    public void Stop()
    {
        Pause();
        _state = DemoServiceState.Stopped;
        _healthCheck.IsHealthy = false;
        ((DemoDataGenerator)_generator).IsRunning = false;
        _logger.LogInformation("Demo service stopped");
    }

    /// <summary>
    /// Wipes data, regenerates historical data, and resumes generation.
    /// </summary>
    public async Task ReconfigureAsync(CancellationToken ct)
    {
        _logger.LogInformation("Reconfiguring demo service (wipe + regenerate + resume)");
        Pause();
        await RegenerateDataAsync(ct);
        Resume();
    }

    /// <summary>
    /// Resets the tenant and seeds the full sample set server-side — glucose,
    /// treatments, device status, therapy profile, and every lifestyle type —
    /// through the demo admin endpoint (the same seeder the dev tools use).
    /// </summary>
    /// <returns>Whether both the reset and the seed succeeded.</returns>
    public async Task<bool> RegenerateDataAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Regenerating demo data - resetting the tenant first");
        var succeeded = true;

        // Reset the tenant via API: clears data and any configuration a visitor changed
        try
        {
            await _apiClient.ResetAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to reset the demo tenant (may not exist yet), continuing with regeneration");
            succeeded = false;
        }

        var startTime = UtcNow;

        // Seeding failure is non-fatal — realtime ticks keep building a live
        // chart even without history. The filter keeps real shutdown
        // cancellation propagating, but an HttpClient timeout
        // (TaskCanceledException with an uncancelled stoppingToken) must not
        // fault ExecuteAsync or be mistaken for shutdown by the reset loop's
        // OperationCanceledException handler.
        try
        {
            await _apiClient.SeedAsync(_config.BackfillDays, cancellationToken);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Failed to seed the demo sample set");
            succeeded = false;
        }

        // Continue the realtime stream from the seeded history's latest value.
        if ((await TryReadGlucoseHistoryAsync(cancellationToken))?.Newest is { } newest)
            _generator.SeedCurrentGlucose(newest.Mgdl);

        _logger.LogInformation(
            "Completed demo data regeneration in {Duration}",
            UtcNow - startTime);
        return succeeded;
    }

    /// <summary>
    /// Regenerates, then schedules the next periodic reset from the reset time the API stamped
    /// on the tenant. A failed regeneration records nothing, and the next attempt falls one reset
    /// interval after this one began.
    /// </summary>
    /// <returns>The time the next periodic reset is measured from.</returns>
    private async Task<DateTime> RegenerateAndRecordResetAsync(CancellationToken cancellationToken)
    {
        var startedAt = UtcNow;
        if (!await RegenerateDataAsync(cancellationToken))
            return startedAt;

        var lastResetUtc = (await _apiClient.GetStatusAsync(cancellationToken))?.LastResetAt ?? startedAt;
        if (NextResetDue(_config, lastResetUtc) is not { } nextResetUtc)
            return lastResetUtc;

        try
        {
            await _apiClient.UpdateStatusAsync(nextResetAt: nextResetUtc, ct: cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to update demo status after reset");
        }

        return lastResetUtc;
    }

    /// <summary>
    /// Runs the visitor reset, then records the next one an interval from now, whether or not this one succeeded, so a
    /// failing reset is retried on the schedule rather than in a loop.
    /// </summary>
    /// <returns>When the next visitor reset is due.</returns>
    private async Task<DateTime> ResetVisitorStateAndRecordAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _apiClient.ResetVisitorStateAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to reset the clinic persona's views and rules");
        }

        var next = UtcNow.AddMinutes(_config.VisitorResetIntervalMinutes);
        await RecordNextVisitorResetAsync(next, cancellationToken);
        return next;
    }

    private async Task RecordNextVisitorResetAsync(DateTime nextVisitorResetUtc, CancellationToken cancellationToken)
    {
        try
        {
            await _apiClient.UpdateStatusAsync(nextVisitorResetAt: nextVisitorResetUtc, ct: cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to record the next visitor reset");
        }
    }

    private async Task<GlucoseHistory?> TryReadGlucoseHistoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _apiClient.GetGlucoseHistoryAsync(cancellationToken);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to read the demo glucose history");
            return null;
        }
    }

    private async Task GenerateAndPostEntryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var entry = _generator.GenerateCurrentEntry();

            _logger.LogInformation(
                "Demo data: Generated entry SGV={Sgv}, Direction={Direction}",
                entry.Sgv,
                entry.Direction
            );

            await _apiClient.PostCurrentEntryAsync(entry, cancellationToken);

            var treatments = _generator.GenerateCurrentTreatments(entry).ToList();
            if (treatments.Count > 0)
            {
                await _apiClient.PostCurrentTreatmentsAsync(treatments, cancellationToken);
            }

            var deviceStatus = _generator.GenerateCurrentDeviceStatus(entry, treatments);
            await _apiClient.PostDeviceStatusAsync(deviceStatus, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate and post demo entry");
            throw;
        }
    }

    private async Task<T?> RetryAsync<T>(
        Func<CancellationToken, Task<T?>> attempt,
        int maxAttempts,
        string operation,
        CancellationToken ct)
        where T : class
    {
        var delay = TimeSpan.FromSeconds(2);

        for (var i = 1; i <= maxAttempts; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (await attempt(ct) is { } result)
                return result;

            if (i == maxAttempts)
                break;

            _logger.LogWarning(
                "{Operation} attempt {Attempt}/{MaxAttempts} failed, retrying in {Delay}",
                operation, i, maxAttempts, delay);

            try
            {
                await Task.Delay(delay, _time, ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            // Exponential backoff capped at 30 seconds
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
        }

        return null;
    }
}

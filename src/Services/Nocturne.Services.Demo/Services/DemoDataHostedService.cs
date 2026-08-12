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
/// Background service that provisions the demo tenant, generates historical data on startup,
/// and continuously generates real-time entries at configured intervals.
/// All data persistence is performed via HTTP calls to the Nocturne API.
/// </summary>
public class DemoDataHostedService : BackgroundService
{
    private readonly ILogger<DemoDataHostedService> _logger;
    private readonly DemoModeConfiguration _config;
    private readonly IDemoDataGenerator _generator;
    private readonly DemoServiceHealthCheck _healthCheck;
    private readonly DemoApiClient _apiClient;

    private volatile DemoServiceState _state = DemoServiceState.Stopped;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private TaskCompletionSource _resumeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DemoServiceState State => _state;

    public DemoDataHostedService(
        DemoApiClient apiClient,
        IOptions<DemoModeConfiguration> config,
        IDemoDataGenerator generator,
        DemoServiceHealthCheck healthCheck,
        ILogger<DemoDataHostedService> logger
    )
    {
        _apiClient = apiClient;
        _logger = logger;
        _config = config.Value;
        _generator = generator;
        _healthCheck = healthCheck;
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
        var tenantState = await ProvisionWithRetryAsync(stoppingToken);
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
            // Clear and regenerate on startup if configured
            if (_config.ClearOnStartup || _config.RegenerateOnStartup)
            {
                await RegenerateDataAsync(stoppingToken);
            }

            // Generate initial entry immediately
            await GenerateAndPostEntryAsync(stoppingToken);

            // Schedule generation and optional reset intervals
            var generationInterval = TimeSpan.FromMinutes(_config.IntervalMinutes);
            var resetInterval = _config.ResetIntervalMinutes > 0
                ? TimeSpan.FromMinutes(_config.ResetIntervalMinutes)
                : (TimeSpan?)null;

            var nextGenerationUtc = DateTime.UtcNow.Add(generationInterval);
            DateTime? nextResetUtc = resetInterval.HasValue
                ? DateTime.UtcNow.Add(resetInterval.Value)
                : null;

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

                var now = DateTime.UtcNow;
                var nextWakeUtc = nextGenerationUtc;
                if (nextResetUtc.HasValue && nextResetUtc.Value < nextWakeUtc)
                {
                    nextWakeUtc = nextResetUtc.Value;
                }

                var delay = nextWakeUtc - now;
                if (delay < TimeSpan.Zero)
                {
                    delay = TimeSpan.Zero;
                }

                try
                {
                    await Task.Delay(delay, stoppingToken);
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
                    now = DateTime.UtcNow;

                    if (nextResetUtc.HasValue && now >= nextResetUtc.Value)
                    {
                        await RegenerateDataAsync(stoppingToken);
                        now = DateTime.UtcNow;
                        nextResetUtc = now.Add(resetInterval!.Value);

                        try
                        {
                            await _apiClient.UpdateStatusAsync(
                                nextResetAt: nextResetUtc.Value,
                                lastResetAt: now,
                                ct: stoppingToken);
                        }
                        catch (HttpRequestException ex)
                        {
                            _logger.LogWarning(ex, "Failed to update demo status after reset");
                        }
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
    public async Task RegenerateDataAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Regenerating demo data - resetting the tenant first");

        // Reset the tenant via API: clears data and any configuration a visitor changed
        try
        {
            await _apiClient.ResetAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to reset the demo tenant (may not exist yet), continuing with regeneration");
        }

        var startTime = DateTime.UtcNow;

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
        }

        // Continue the realtime stream from the seeded history's latest value.
        var latestGlucose = await _apiClient.GetLatestGlucoseAsync(cancellationToken);
        if (latestGlucose is { } glucose)
            _generator.SeedCurrentGlucose(glucose);

        _logger.LogInformation(
            "Completed demo data regeneration in {Duration}",
            DateTime.UtcNow - startTime);
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

    private async Task<DemoTenantState?> ProvisionWithRetryAsync(CancellationToken ct)
    {
        const int maxRetries = 10;
        var delay = TimeSpan.FromSeconds(2);

        for (var i = 0; i < maxRetries; i++)
        {
            ct.ThrowIfCancellationRequested();

            var state = await _apiClient.ProvisionAsync(ct);
            if (state != null)
                return state;

            _logger.LogWarning(
                "Provision attempt {Attempt}/{MaxRetries} failed, retrying in {Delay}",
                i + 1, maxRetries, delay);

            try
            {
                await Task.Delay(delay, ct);
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

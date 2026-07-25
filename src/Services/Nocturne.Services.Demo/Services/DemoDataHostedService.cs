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
    /// Clears all demo data and regenerates historical data via the API using streaming pattern.
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

        // Ensure demo PatientInsulin record exists
        try
        {
            await _apiClient.EnsurePatientInsulinAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to ensure PatientInsulin (endpoint may not exist yet)");
        }

        // Generate and post data using streaming pattern to minimize memory usage
        var startTime = DateTime.UtcNow;
        const int batchSize = 1000;

        // Stream and post entries in batches
        var entryCount = 0;
        var entryBatch = new List<Entry>(batchSize);
        Entry? latestEntry = null;

        foreach (var entry in _generator.GenerateHistoricalEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();
            entryBatch.Add(entry);
            latestEntry = entry;

            if (entryBatch.Count >= batchSize)
            {
                await _apiClient.PostEntriesAsync(entryBatch, cancellationToken);
                entryCount += entryBatch.Count;
                entryBatch.Clear();
            }
        }

        // Post remaining entries
        if (entryBatch.Count > 0)
        {
            await _apiClient.PostEntriesAsync(entryBatch, cancellationToken);
            entryCount += entryBatch.Count;
            entryBatch.Clear();
        }

        if (latestEntry is not null)
        {
            var seedGlucose = latestEntry.Sgv ?? latestEntry.Mgdl;
            _generator.SeedCurrentGlucose(seedGlucose);
        }

        _logger.LogInformation("Posted {Count} entries using streaming pattern", entryCount);

        // Stream and post treatments in batches
        var treatmentCount = 0;
        var treatmentBatch = new List<Treatment>(batchSize);

        foreach (var treatment in _generator.GenerateHistoricalTreatments())
        {
            cancellationToken.ThrowIfCancellationRequested();
            treatmentBatch.Add(treatment);

            if (treatmentBatch.Count >= batchSize)
            {
                await _apiClient.PostTreatmentsAsync(treatmentBatch, cancellationToken);
                treatmentCount += treatmentBatch.Count;
                treatmentBatch.Clear();
            }
        }

        // Post remaining treatments
        if (treatmentBatch.Count > 0)
        {
            await _apiClient.PostTreatmentsAsync(treatmentBatch, cancellationToken);
            treatmentCount += treatmentBatch.Count;
            treatmentBatch.Clear();
        }

        _logger.LogInformation("Posted {Count} treatments using streaming pattern", treatmentCount);

        // Non-glucose sample set (device changes, sleep, activity, trackers,
        // alert rules + alarm history) seeds server-side against the data just
        // posted. Failure is non-fatal — the demo still has glucose/treatments.
        // The filter keeps real shutdown cancellation propagating, but an
        // HttpClient timeout (TaskCanceledException with an uncancelled
        // stoppingToken) must not fault ExecuteAsync or be mistaken for
        // shutdown by the reset loop's OperationCanceledException handler.
        try
        {
            await _apiClient.SeedExtrasAsync(_config.BackfillDays, cancellationToken);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to seed demo extras (endpoint may not exist yet or timed out)");
        }

        var duration = DateTime.UtcNow - startTime;
        _logger.LogInformation(
            "Completed demo data regeneration: {Entries} entries, {Treatments} treatments in {Duration}",
            entryCount,
            treatmentCount,
            duration
        );
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

            await _apiClient.PostEntriesAsync(new[] { entry }, cancellationToken);

            var treatments = _generator.GenerateCurrentTreatments(entry).ToList();
            if (treatments.Count > 0)
            {
                await _apiClient.PostTreatmentsAsync(treatments, cancellationToken);
            }
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

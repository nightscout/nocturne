using Microsoft.Extensions.Options;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data;
using Nocturne.Services.Demo.Configuration;

namespace Nocturne.Services.Demo.Services;

/// <summary>
/// Background service that generates demo data on startup and continues
/// generating real-time entries at configured intervals.
/// </summary>
public class DemoDataHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DemoDataHostedService> _logger;
    private readonly DemoModeConfiguration _config;
    private readonly IDemoDataGenerator _generator;
    private readonly DemoServiceHealthCheck _healthCheck;

    public DemoDataHostedService(
        IServiceProvider serviceProvider,
        IOptions<DemoModeConfiguration> config,
        IDemoDataGenerator generator,
        DemoServiceHealthCheck healthCheck,
        ILogger<DemoDataHostedService> logger
    )
    {
        _serviceProvider = serviceProvider;
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

        // Mark the service as running
        ((DemoDataGenerator)_generator).IsRunning = true;

        try
        {
            // Clear and regenerate on startup if configured
            if (_config.ClearOnStartup || _config.RegenerateOnStartup)
            {
                await RegenerateDataAsync(stoppingToken);
            }

            // Generate initial entry immediately
            await GenerateAndSaveEntryAsync(stoppingToken);

            // Schedule generation and optional reset intervals.
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

                try
                {
                    now = DateTime.UtcNow;

                    if (nextResetUtc.HasValue && now >= nextResetUtc.Value)
                    {
                        await RegenerateDataAsync(stoppingToken);
                        now = DateTime.UtcNow;
                        nextResetUtc = now.Add(resetInterval!.Value);
                    }

                    if (now >= nextGenerationUtc)
                    {
                        await GenerateAndSaveEntryAsync(stoppingToken);
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
                    // Continue running even if one generation fails
                }
            }
        }
        finally
        {
            // Mark as unhealthy when stopping - this signals the API to clean up
            _healthCheck.IsHealthy = false;
            ((DemoDataGenerator)_generator).IsRunning = false;
        }
    }

    /// <summary>
    /// Clears all demo data and regenerates historical data using streaming pattern.
    /// </summary>
    public async Task RegenerateDataAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Regenerating demo data - clearing existing data first");

        using var scope = _serviceProvider.CreateScope();
        SetAuditContext(scope.ServiceProvider);
        var sensorGlucoseRepository = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        var entryService = scope.ServiceProvider.GetRequiredService<IDemoEntryService>();
        var treatmentService = scope.ServiceProvider.GetRequiredService<IDemoTreatmentService>();

        // Clear existing demo data
        var entriesDeleted = await sensorGlucoseRepository.DeleteBySourceAsync(
            DataSources.DemoService,
            cancellationToken
        );
        var treatmentsDeleted = await treatmentService.DeleteAllDemoTreatmentsAsync(
            cancellationToken
        );

        _logger.LogInformation(
            "Cleared {Entries} demo sensor glucose records and {Treatments} demo treatments",
            entriesDeleted,
            treatmentsDeleted
        );

        // Create synthetic PatientInsulin record for demo mode
        await EnsureDemoPatientInsulinAsync(scope.ServiceProvider, cancellationToken);

        // Generate and save data using streaming pattern to minimize memory usage
        var startTime = DateTime.UtcNow;
        const int batchSize = 1000;

        // Stream and save entries in batches
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
                await entryService.CreateEntriesAsync(entryBatch, cancellationToken);
                entryCount += entryBatch.Count;
                entryBatch.Clear();
            }
        }

        // Save remaining entries
        if (entryBatch.Count > 0)
        {
            await entryService.CreateEntriesAsync(entryBatch, cancellationToken);
            entryCount += entryBatch.Count;
            entryBatch.Clear();
        }

        if (latestEntry is not null)
        {
            var seedGlucose = latestEntry.Sgv ?? latestEntry.Mgdl;
            _generator.SeedCurrentGlucose(seedGlucose);
        }

        _logger.LogInformation("Saved {Count} entries using streaming pattern", entryCount);

        // Stream and save treatments in batches
        var treatmentCount = 0;
        var treatmentBatch = new List<Treatment>(batchSize);

        foreach (var treatment in _generator.GenerateHistoricalTreatments())
        {
            cancellationToken.ThrowIfCancellationRequested();
            treatmentBatch.Add(treatment);

            if (treatmentBatch.Count >= batchSize)
            {
                await treatmentService.CreateTreatmentsAsync(treatmentBatch, cancellationToken);
                treatmentCount += treatmentBatch.Count;
                treatmentBatch.Clear();
            }
        }

        // Save remaining treatments
        if (treatmentBatch.Count > 0)
        {
            await treatmentService.CreateTreatmentsAsync(treatmentBatch, cancellationToken);
            treatmentCount += treatmentBatch.Count;
            treatmentBatch.Clear();
        }

        _logger.LogInformation("Saved {Count} treatments using streaming pattern", treatmentCount);

        var duration = DateTime.UtcNow - startTime;
        _logger.LogInformation(
            "Completed demo data regeneration: {Entries} entries, {Treatments} treatments in {Duration}",
            entryCount,
            treatmentCount,
            duration
        );
    }

    /// <summary>
    /// Ensures a synthetic PatientInsulin record exists for demo mode so the UI
    /// shows the insulin management experience correctly.
    /// </summary>
    private async Task EnsureDemoPatientInsulinAsync(
        IServiceProvider scopedProvider,
        CancellationToken cancellationToken
    )
    {
        var insulinRepository = scopedProvider.GetRequiredService<IPatientInsulinRepository>();

        // Check if a current insulin already exists (idempotent)
        var existing = await insulinRepository.GetCurrentAsync(cancellationToken);
        if (existing.Any())
        {
            _logger.LogDebug("Demo PatientInsulin record already exists, skipping creation");
            return;
        }

        var now = DateTime.UtcNow;
        var demoInsulin = new PatientInsulin
        {
            Id = Guid.CreateVersion7(),
            FormulationId = "humalog",
            Name = "Humalog (Demo)",
            InsulinCategory = InsulinCategory.RapidActing,
            Dia = _config.InsulinDurationMinutes / 60.0,
            Peak = (int)_config.InsulinPeakMinutes,
            Curve = "rapid-acting",
            Concentration = 100,
            Role = InsulinRole.Both,
            IsPrimary = true,
            IsCurrent = true,
            StartDate = DateOnly.FromDateTime(now),
            CreatedAt = now,
            ModifiedAt = now,
        };

        await insulinRepository.CreateAsync(demoInsulin, cancellationToken);
        _logger.LogInformation(
            "Created demo PatientInsulin: {Name} (DIA={Dia}h, Peak={Peak}min)",
            demoInsulin.Name,
            demoInsulin.Dia,
            demoInsulin.Peak
        );
    }

    private async Task GenerateAndSaveEntryAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        SetAuditContext(scope.ServiceProvider);
        var entryService = scope.ServiceProvider.GetRequiredService<IDemoEntryService>();
        var treatmentService = scope.ServiceProvider.GetRequiredService<IDemoTreatmentService>();

        try
        {
            var entry = _generator.GenerateCurrentEntry();

            _logger.LogInformation(
                "Demo data: Generated entry SGV={Sgv}, Direction={Direction}",
                entry.Sgv,
                entry.Direction
            );

            await entryService.CreateEntriesAsync(new[] { entry }, cancellationToken);

            var treatments = _generator.GenerateCurrentTreatments(entry).ToList();
            if (treatments.Count > 0)
            {
                await treatmentService.CreateTreatmentsAsync(treatments, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate and save demo entry");
            throw;
        }
    }

    /// <summary>
    /// Attaches a system audit context to the scoped DbContext so the
    /// MutationAuditInterceptor can attribute mutations to this service.
    /// </summary>
    private static void SetAuditContext(IServiceProvider scopeProvider)
    {
        var dbContext = scopeProvider.GetRequiredService<NocturneDbContext>();
        dbContext.AuditContext = SystemAuditContext.ForService("service:demo-generator");
    }
}

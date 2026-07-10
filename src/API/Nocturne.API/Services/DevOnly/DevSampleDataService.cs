using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Infrastructure.Data;
using Nocturne.Services.Demo.Configuration;
using Nocturne.Services.Demo.Services;

namespace Nocturne.API.Services.DevOnly;

/// <summary>
/// Populates a tenant with realistic sample data using the demo service's oref
/// pharmacokinetic generator, written through the normal ingestion services
/// (<see cref="IEntryService"/> / <see cref="ITreatmentService"/>) so device
/// attribution, the v4 canonical glucose stream, and RLS tenant context are
/// handled exactly like production writes. Development-only: consumed by the
/// dev-only admin endpoints, which do not exist outside Development.
/// </summary>
public class DevSampleDataService
{
    private readonly ITenantAccessor _tenantAccessor;
    private readonly NocturneDbContext _db;
    private readonly IEntryService _entryService;
    private readonly ITreatmentService _treatmentService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DevSampleDataService> _logger;

    private const int BatchSize = 500;
    private const int MaxDays = 90;

    /// <summary>
    /// The generator stamps records with DataSources.DemoService, which
    /// DataSources.IsEphemeral hides from every non-demo tenant's reads —
    /// seeded data must carry a non-ephemeral source to be visible.
    /// </summary>
    private const string SampleDataSource = "dev-sample";

    public DevSampleDataService(
        ITenantAccessor tenantAccessor,
        NocturneDbContext db,
        IEntryService entryService,
        ITreatmentService treatmentService,
        ILoggerFactory loggerFactory,
        ILogger<DevSampleDataService> logger)
    {
        _tenantAccessor = tenantAccessor;
        _db = db;
        _entryService = entryService;
        _treatmentService = treatmentService;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>
    /// Generates and persists <paramref name="days"/> days of CGM entries and
    /// treatments for the tenant. Returns the persisted record counts.
    /// </summary>
    public async Task<(int Entries, int Treatments)> SeedAsync(
        TenantContext tenant, int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, MaxDays);

        var config = new DemoModeConfiguration { BackfillDays = days };
        var generator = new DemoDataGenerator(
            Options.Create(config),
            _loggerFactory.CreateLogger<DemoDataGenerator>(),
            _loggerFactory);

        // Ingestion services resolve the tenant through ITenantAccessor for
        // factory-created contexts, and through the request-scoped context's
        // TenantId (normally pinned by tenant resolution middleware, which
        // dev-only routes bypass) for entity stamping and the RLS GUC.
        _tenantAccessor.SetTenant(tenant);
        _db.TenantId = tenant.TenantId;

        var entries = generator.GenerateHistoricalEntries()
            .Select(e =>
            {
                e.DataSource = SampleDataSource;
                return e;
            });

        var entryCount = 0;
        foreach (var batch in entries.Chunk(BatchSize))
        {
            await _entryService.CreateEntriesAsync(batch, ct);
            entryCount += batch.Length;
        }

        // "Scheduled Basal" is a demo-service event type the treatment
        // decomposer doesn't recognize — it decomposes to nothing and logs a
        // warning per record.
        var treatments = generator.GenerateHistoricalTreatments()
            .Where(t => t.EventType != "Scheduled Basal")
            .Select(t =>
            {
                t.DataSource = SampleDataSource;
                return t;
            });

        var treatmentCount = 0;
        foreach (var batch in treatments.Chunk(BatchSize))
        {
            await _treatmentService.CreateTreatmentsAsync(batch, ct);
            treatmentCount += batch.Length;
        }

        _logger.LogInformation(
            "Seeded {Entries} entries and {Treatments} treatments ({Days} days) into tenant {Slug}",
            entryCount, treatmentCount, days, tenant.Slug);

        return (entryCount, treatmentCount);
    }
}

using Nocturne.API.Services.Audit;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.ConnectorPublishing;

/// <summary>
/// Publishes treatment data received from connectors into both the legacy v1-v3 treatment store
/// (via <see cref="ITreatmentService"/>) and the v4 event-centric repositories for boluses, carb
/// intakes, BG checks, bolus calculations, and temporary basals.
/// </summary>
/// <seealso cref="ITreatmentPublisher"/>
internal sealed class TreatmentPublisher : ITreatmentPublisher
{
    private readonly ITenantDbContextFactory _contextFactory;
    private readonly ITreatmentService _treatmentService;
    private readonly IBolusRepository _bolusRepository;
    private readonly ICarbIntakeRepository _carbIntakeRepository;
    private readonly IBGCheckRepository _bgCheckRepository;
    private readonly IBolusCalculationRepository _bolusCalculationRepository;
    private readonly ITempBasalRepository _tempBasalRepository;
    private readonly IBasalInjectionRepository _basalInjectionRepository;
    private readonly IPatientInsulinRepository _patientInsulinRepository;
    private readonly IBasalRateResolver _basalRateResolver;
    private readonly ITherapySettingsResolver _therapySettingsResolver;
    private readonly IAuditContext _auditContext;
    private readonly ILogger<TreatmentPublisher> _logger;

    public TreatmentPublisher(
        ITenantDbContextFactory contextFactory,
        ITreatmentService treatmentService,
        IBolusRepository bolusRepository,
        ICarbIntakeRepository carbIntakeRepository,
        IBGCheckRepository bgCheckRepository,
        IBolusCalculationRepository bolusCalculationRepository,
        ITempBasalRepository tempBasalRepository,
        IBasalInjectionRepository basalInjectionRepository,
        IPatientInsulinRepository patientInsulinRepository,
        IBasalRateResolver basalRateResolver,
        ITherapySettingsResolver therapySettingsResolver,
        IAuditContext auditContext,
        ILogger<TreatmentPublisher> logger)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _treatmentService = treatmentService ?? throw new ArgumentNullException(nameof(treatmentService));
        _bolusRepository = bolusRepository ?? throw new ArgumentNullException(nameof(bolusRepository));
        _carbIntakeRepository = carbIntakeRepository ?? throw new ArgumentNullException(nameof(carbIntakeRepository));
        _bgCheckRepository = bgCheckRepository ?? throw new ArgumentNullException(nameof(bgCheckRepository));
        _bolusCalculationRepository = bolusCalculationRepository ?? throw new ArgumentNullException(nameof(bolusCalculationRepository));
        _tempBasalRepository = tempBasalRepository ?? throw new ArgumentNullException(nameof(tempBasalRepository));
        _basalInjectionRepository = basalInjectionRepository ?? throw new ArgumentNullException(nameof(basalInjectionRepository));
        _patientInsulinRepository = patientInsulinRepository ?? throw new ArgumentNullException(nameof(patientInsulinRepository));
        _basalRateResolver = basalRateResolver ?? throw new ArgumentNullException(nameof(basalRateResolver));
        _therapySettingsResolver = therapySettingsResolver ?? throw new ArgumentNullException(nameof(therapySettingsResolver));
        _auditContext = auditContext;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> PublishTreatmentsAsync(
        IEnumerable<Treatment> treatments,
        string source,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _treatmentService.CreateTreatmentsAsync(treatments, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish treatments for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishBolusesAsync(
        IEnumerable<Bolus> records,
        string source,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var recordList = records.ToList();
            if (recordList.Count == 0) return true;

            await ResolvePatientInsulinsForBolusesAsync(recordList, cancellationToken);
            using (SystemAuditScope.Push(_auditContext))
                await _bolusRepository.BulkCreateAsync(recordList, cancellationToken);
            _logger.LogDebug("Published {Count} Bolus records for {Source}", recordList.Count, source);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish Bolus records for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishCarbIntakesAsync(
        IEnumerable<CarbIntake> records,
        string source,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var recordList = records.ToList();
            if (recordList.Count == 0) return true;

            using (SystemAuditScope.Push(_auditContext))
                await _carbIntakeRepository.BulkCreateAsync(recordList, cancellationToken);
            _logger.LogDebug("Published {Count} CarbIntake records for {Source}", recordList.Count, source);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish CarbIntake records for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishBGChecksAsync(
        IEnumerable<BGCheck> records,
        string source,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var recordList = records.ToList();
            if (recordList.Count == 0) return true;

            using (SystemAuditScope.Push(_auditContext))
                await _bgCheckRepository.BulkCreateAsync(recordList, cancellationToken);
            _logger.LogDebug("Published {Count} BGCheck records for {Source}", recordList.Count, source);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish BGCheck records for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishBolusCalculationsAsync(
        IEnumerable<BolusCalculation> records,
        string source,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var recordList = records.ToList();
            if (recordList.Count == 0) return true;

            using (SystemAuditScope.Push(_auditContext))
                await _bolusCalculationRepository.BulkCreateAsync(recordList, cancellationToken);
            _logger.LogDebug("Published {Count} BolusCalculation records for {Source}", recordList.Count, source);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish BolusCalculation records for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishTempBasalsAsync(
        IEnumerable<TempBasal> records,
        string source,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var recordList = records.ToList();
            if (recordList.Count == 0) return true;

            var minTimestamp = recordList.Min(r => r.StartTimestamp);
            var maxTimestamp = recordList.Max(r => r.StartTimestamp);

            // Connector resync: the delete-then-reinsert is a system sweep, so its audit rows
            // must carry AuthType IS NULL. The delete the dedup discriminator reads — not just the
            // insert — has to be inside the scope; otherwise a resync invoked under an actor
            // context (e.g. a manual sync) would write a user-attributed delete row and
            // permanently block re-importing those temp basals.
            using (SystemAuditScope.Push(_auditContext))
            {
                await _tempBasalRepository.DeleteBySourceAndDateRangeAsync(
                    source, minTimestamp, maxTimestamp, cancellationToken);

                var reclassifiedCount = await ReclassifyScheduledAlgorithmicBasalsAsync(
                    recordList, cancellationToken);
                if (reclassifiedCount > 0)
                    _logger.LogInformation(
                        "Reclassified {Count}/{Total} TempBasal records from Scheduled to Algorithm "
                        + "(rate differs from programmed basal schedule) for {Source}",
                        reclassifiedCount, recordList.Count, source);

                await _tempBasalRepository.BulkCreateAsync(recordList, cancellationToken);
            }
            _logger.LogDebug("Published {Count} TempBasal records for {Source}", recordList.Count, source);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish TempBasal records for {Source}", source);
            return false;
        }
    }

    /// <summary>
    /// Connectors that flatten algorithm-driven adjustments (e.g. Tandem Control-IQ via Glooko's
    /// ScheduledBasal stream) emit <see cref="TempBasalOrigin.Scheduled"/> records whose
    /// <see cref="TempBasal.Rate"/> reflects what the pump actually delivered, not the user's
    /// programmed basal profile. Compare each Scheduled record's rate against the resolved
    /// schedule rate; when they diverge, reclassify as <see cref="TempBasalOrigin.Algorithm"/>
    /// so downstream chart code emits the correct overlay. In either case, overwrite
    /// <see cref="TempBasal.ScheduledRate"/> with the resolved programmed rate (some connectors
    /// copy Rate into ScheduledRate, which makes the chart's reference line track the algorithm).
    /// </summary>
    private async Task<int> ReclassifyScheduledAlgorithmicBasalsAsync(
        List<TempBasal> records,
        CancellationToken cancellationToken)
    {
        // Floating-point noise guard. Real pump precision is ≥0.025 U/hr; algorithm-driven
        // adjustments differ by far more.
        const double rateTolerance = 0.005;

        var scheduledRecords = records
            .Where(r => r.Origin == TempBasalOrigin.Scheduled)
            .ToList();
        if (scheduledRecords.Count == 0) return 0;

        // Without therapy settings on file, the resolver falls back to a hardcoded default and
        // would mass-reclassify every record. Skip — we don't yet know what the schedule is.
        if (!await _therapySettingsResolver.HasDataAsync(cancellationToken))
            return 0;

        var minMills = scheduledRecords.Min(r => r.StartMills);
        var maxMills = scheduledRecords.Max(r => r.StartMills);

        var resolve = await _basalRateResolver.BuildResolverAsync(minMills, maxMills, cancellationToken);

        var reclassified = 0;
        foreach (var record in scheduledRecords)
        {
            var programmedRate = resolve(record.StartMills);
            record.ScheduledRate = programmedRate;

            if (Math.Abs(record.Rate - programmedRate) > rateTolerance)
            {
                record.Origin = TempBasalOrigin.Algorithm;
                reclassified++;
            }
        }

        return reclassified;
    }

    public async Task<bool> PublishBasalInjectionsAsync(
        IEnumerable<BasalInjection> records,
        string source,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var recordList = records.ToList();
            if (recordList.Count == 0) return true;

            await ResolvePatientInsulinsForBasalInjectionsAsync(recordList, cancellationToken);
            using (SystemAuditScope.Push(_auditContext))
                await _basalInjectionRepository.BulkCreateAsync(recordList, cancellationToken);

            _logger.LogDebug("Published {Count} BasalInjection records for {Source}", recordList.Count, source);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish BasalInjection records for {Source}", source);
            return false;
        }
    }

    public async Task<bool> PublishDecompositionBatchesAsync(
        IEnumerable<DecompositionBatch> batches,
        string source,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var batchList = batches.ToList();
            if (batchList.Count == 0) return true;

            await using var ctx = await _contextFactory.CreateAsync(cancellationToken);
            foreach (var batch in batchList)
            {
                ctx.DecompositionBatches.Add(new DecompositionBatchEntity
                {
                    Id = batch.Id,
                    TenantId = ctx.TenantId,
                    Source = batch.Source,
                    SourceRecordId = batch.SourceRecordId,
                    CreatedAt = batch.CreatedAt,
                });
            }

            await ctx.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Published {Count} DecompositionBatch records for {Source}", batchList.Count, source);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish DecompositionBatch records for {Source}", source);
            return false;
        }
    }

    public async Task<DateTime?> GetLatestTreatmentTimestampAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        // TODO: Filter by source to support multi-connector catch-up. Currently returns global latest.
        var latest = (await _treatmentService.GetTreatmentsAsync(
                count: 1,
                skip: 0,
                cancellationToken: cancellationToken))
            .FirstOrDefault();

        if (latest == null)
            return null;

        if (!string.IsNullOrEmpty(latest.CreatedAt)
            && DateTime.TryParse(latest.CreatedAt, out var createdAt))
            return createdAt;

        if (latest.Mills > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds(latest.Mills).UtcDateTime;

        return null;
    }

    // ── Patient Insulin resolution helpers ──────────────────────────────

    /// <summary>
    /// For boluses that carry an <see cref="TreatmentInsulinContext"/> with a placeholder
    /// <c>PatientInsulinId</c> (Guid.Empty), resolves or auto-creates the corresponding
    /// <see cref="PatientInsulin"/> record and updates the context in place.
    /// </summary>
    private async Task ResolvePatientInsulinsForBolusesAsync(
        List<Bolus> records, CancellationToken ct)
    {
        var needsResolution = records
            .Where(r => r.InsulinContext is { PatientInsulinId: var id } && id == Guid.Empty)
            .ToList();

        if (needsResolution.Count == 0) return;

        var cache = await BuildPatientInsulinCacheAsync(ct);

        foreach (var bolus in needsResolution)
        {
            var resolved = await ResolveOrCreatePatientInsulinAsync(
                bolus.InsulinContext!, InsulinRole.Bolus, cache, ct);
            bolus.InsulinContext = resolved;
        }
    }

    /// <summary>
    /// For basal injections that carry an <see cref="TreatmentInsulinContext"/> with a placeholder
    /// <c>PatientInsulinId</c> (Guid.Empty), resolves or auto-creates the corresponding
    /// <see cref="PatientInsulin"/> record and updates the context in place.
    /// </summary>
    private async Task ResolvePatientInsulinsForBasalInjectionsAsync(
        List<BasalInjection> records, CancellationToken ct)
    {
        var needsResolution = records
            .Where(r => r.InsulinContext.PatientInsulinId == Guid.Empty)
            .ToList();

        if (needsResolution.Count == 0) return;

        var cache = await BuildPatientInsulinCacheAsync(ct);

        foreach (var injection in needsResolution)
        {
            var resolved = await ResolveOrCreatePatientInsulinAsync(
                injection.InsulinContext, InsulinRole.Basal, cache, ct);
            injection.InsulinContext = resolved;
        }
    }

    /// <summary>
    /// Builds a lookup of existing patient insulins keyed by (name, role).
    /// A <see cref="InsulinRole.Both"/> entry satisfies either Basal or Bolus lookups.
    /// </summary>
    private async Task<List<PatientInsulin>> BuildPatientInsulinCacheAsync(CancellationToken ct)
    {
        var existing = await _patientInsulinRepository.GetAllAsync(ct);
        return existing.ToList();
    }

    /// <summary>
    /// Finds an existing <see cref="PatientInsulin"/> by name and compatible role, or creates one
    /// from the <see cref="TreatmentInsulinContext"/> catalog data. Returns a new context with the
    /// real <c>PatientInsulinId</c> populated.
    /// </summary>
    private async Task<TreatmentInsulinContext> ResolveOrCreatePatientInsulinAsync(
        TreatmentInsulinContext context,
        InsulinRole role,
        List<PatientInsulin> cache,
        CancellationToken ct)
    {
        var name = context.InsulinName;
        if (string.IsNullOrWhiteSpace(name) || name == "Unknown")
            return context;

        // Match by name AND compatible role (exact match or Role.Both)
        var existing = cache.FirstOrDefault(i =>
            i.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            (i.Role == role || i.Role == InsulinRole.Both));

        if (existing != null)
            return context with { PatientInsulinId = existing.Id };

        // Auto-create a PatientInsulin from the catalog data in the context
        var formulation = InsulinCatalog.GetAll()
            .FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        // Check if a primary already exists for this role (including Role.Both entries)
        var hasPrimary = cache.Any(i =>
            i.IsPrimary && (i.Role == role || i.Role == InsulinRole.Both));

        var newInsulin = new PatientInsulin
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            InsulinCategory = formulation?.Category ?? (role == InsulinRole.Basal
                ? InsulinCategory.LongActing
                : InsulinCategory.RapidActing),
            FormulationId = formulation?.Id,
            Dia = context.Dia,
            Peak = context.Peak,
            Curve = context.Curve,
            Concentration = context.Concentration,
            Role = role == InsulinRole.Basal ? InsulinRole.Basal : InsulinRole.Bolus,
            IsCurrent = true,
            IsPrimary = !hasPrimary,
        };

        PatientInsulin created;
        using (SystemAuditScope.Push(_auditContext))
            created = await _patientInsulinRepository.CreateAsync(newInsulin, ct);
        cache.Add(created);

        _logger.LogInformation(
            "Auto-created PatientInsulin '{Name}' (role={Role}, id={Id}) from connector import",
            created.Name, role, created.Id);

        return context with { PatientInsulinId = created.Id };
    }
}

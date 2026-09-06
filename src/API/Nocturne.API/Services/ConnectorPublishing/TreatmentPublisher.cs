using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Services.ConnectorPublishing;

/// <summary>
/// Publishes treatment data received from connectors into both the legacy v1-v3 treatment store
/// (via <see cref="ITreatmentService"/>) and the v4 event-centric repositories for boluses, carb
/// intakes, BG checks, bolus calculations, and temporary basals.
/// </summary>
/// <seealso cref="ITreatmentPublisher"/>
internal sealed class TreatmentPublisher : ConnectorPublisherBase, ITreatmentPublisher
{
    private readonly ITenantDbContextFactory _contextFactory;
    private readonly ITreatmentService _treatmentService;
    private readonly IBolusRepository _bolusRepository;
    private readonly ICarbIntakeRepository _carbIntakeRepository;
    private readonly IBGCheckRepository _bgCheckRepository;
    private readonly IBolusCalculationRepository _bolusCalculationRepository;
    private readonly ITempBasalRepository _tempBasalRepository;
    private readonly IBasalInjectionRepository _basalInjectionRepository;
    private readonly INoteRepository _noteRepository;
    private readonly IDeviceEventRepository _deviceEventRepository;
    private readonly IPatientInsulinRepository _patientInsulinRepository;
    private readonly IBasalRateResolver _basalRateResolver;
    private readonly ITherapySettingsResolver _therapySettingsResolver;
    private readonly IPatientDeviceStamper _patientDeviceStamper;

    public TreatmentPublisher(
        ITenantDbContextFactory contextFactory,
        ITreatmentService treatmentService,
        IBolusRepository bolusRepository,
        ICarbIntakeRepository carbIntakeRepository,
        IBGCheckRepository bgCheckRepository,
        IBolusCalculationRepository bolusCalculationRepository,
        ITempBasalRepository tempBasalRepository,
        IBasalInjectionRepository basalInjectionRepository,
        INoteRepository noteRepository,
        IDeviceEventRepository deviceEventRepository,
        IPatientInsulinRepository patientInsulinRepository,
        IBasalRateResolver basalRateResolver,
        ITherapySettingsResolver therapySettingsResolver,
        IPatientDeviceStamper patientDeviceStamper,
        IAuditContext auditContext,
        ILogger<TreatmentPublisher> logger)
        : base(auditContext, logger)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _treatmentService = treatmentService ?? throw new ArgumentNullException(nameof(treatmentService));
        _bolusRepository = bolusRepository ?? throw new ArgumentNullException(nameof(bolusRepository));
        _carbIntakeRepository = carbIntakeRepository ?? throw new ArgumentNullException(nameof(carbIntakeRepository));
        _bgCheckRepository = bgCheckRepository ?? throw new ArgumentNullException(nameof(bgCheckRepository));
        _bolusCalculationRepository = bolusCalculationRepository ?? throw new ArgumentNullException(nameof(bolusCalculationRepository));
        _tempBasalRepository = tempBasalRepository ?? throw new ArgumentNullException(nameof(tempBasalRepository));
        _basalInjectionRepository = basalInjectionRepository ?? throw new ArgumentNullException(nameof(basalInjectionRepository));
        _noteRepository = noteRepository ?? throw new ArgumentNullException(nameof(noteRepository));
        _deviceEventRepository = deviceEventRepository ?? throw new ArgumentNullException(nameof(deviceEventRepository));
        _patientInsulinRepository = patientInsulinRepository ?? throw new ArgumentNullException(nameof(patientInsulinRepository));
        _basalRateResolver = basalRateResolver ?? throw new ArgumentNullException(nameof(basalRateResolver));
        _therapySettingsResolver = therapySettingsResolver ?? throw new ArgumentNullException(nameof(therapySettingsResolver));
        _patientDeviceStamper = patientDeviceStamper ?? throw new ArgumentNullException(nameof(patientDeviceStamper));
    }

    public async Task<bool> PublishTreatmentsAsync(
        IEnumerable<Treatment> treatments,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
    {
        try
        {
            await _treatmentService.CreateTreatmentsAsync(treatments, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to publish treatments for {Source}", source);
            return false;
        }
    }

    public Task<bool> PublishBolusesAsync(
        IEnumerable<Bolus> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
        => PublishAsync(
            records, _bolusRepository, source, origin, cancellationToken,
            beforeWrite: async recordList =>
            {
                await ResolvePatientInsulinsForBolusesAsync(recordList, origin, cancellationToken);
                await _patientDeviceStamper.StampAsync(
                    recordList, DeviceAttributionCategories.Bolus, source, cancellationToken);
            });

    public Task<bool> PublishCarbIntakesAsync(
        IEnumerable<CarbIntake> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
        => PublishAsync(records, _carbIntakeRepository, source, origin, cancellationToken);

    public Task<bool> PublishBGChecksAsync(
        IEnumerable<BGCheck> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
        => PublishAsync(records, _bgCheckRepository, source, origin, cancellationToken);

    public Task<bool> PublishBolusCalculationsAsync(
        IEnumerable<BolusCalculation> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
        => PublishAsync(records, _bolusCalculationRepository, source, origin, cancellationToken);

    public Task<bool> PublishTempBasalsAsync(
        IEnumerable<TempBasal> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
        => PublishAsync(
            records, _tempBasalRepository, source, origin, cancellationToken,
            beforeWrite: recordList => ReconcileTempBasalWindowAsync(recordList, source, cancellationToken));

    /// <summary>
    /// Attributes the incoming temp basals and reconciles the source's window against them:
    /// soft-delete only the rows this source no longer reports, leaving still-reported rows active
    /// so <c>BulkCreateAsync</c> (which skips already-active legacy ids) makes an unchanged resync a
    /// no-op rather than a delete-the-window-then-reinsert sweep.
    /// </summary>
    private async Task ReconcileTempBasalWindowAsync(
        List<TempBasal> recordList, string source, CancellationToken cancellationToken)
    {
        await _patientDeviceStamper.StampAsync(
            recordList, DeviceAttributionCategories.TempBasal, source, cancellationToken);

        var incomingLegacyIds = recordList
            .Select(r => r.LegacyId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToHashSet();

        await _tempBasalRepository.SoftDeleteAbsentBySourceAndDateRangeAsync(
            source,
            recordList.Min(r => r.StartTimestamp),
            recordList.Max(r => r.StartTimestamp),
            incomingLegacyIds,
            cancellationToken);

        var reclassifiedCount = await ReclassifyScheduledAlgorithmicBasalsAsync(recordList, cancellationToken);
        if (reclassifiedCount > 0)
            Logger.LogInformation(
                "Reclassified {Count}/{Total} TempBasal records from Scheduled to Algorithm "
                + "(rate differs from programmed basal schedule) for {Source}",
                reclassifiedCount, recordList.Count, source);
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

    public Task<bool> PublishBasalInjectionsAsync(
        IEnumerable<BasalInjection> records,
        string source,
        WriteOrigin origin, CancellationToken cancellationToken = default)
        => PublishAsync(
            records, _basalInjectionRepository, source, origin, cancellationToken,
            beforeWrite: async recordList =>
            {
                await ResolvePatientInsulinsForBasalInjectionsAsync(recordList, origin, cancellationToken);
                await _patientDeviceStamper.StampAsync(
                    recordList, DeviceAttributionCategories.BasalInjection, source, cancellationToken);
            });

    /// <inheritdoc cref="ConnectorPublisherBase.LatestTimestampAsync" />
    /// <remarks>The v1 <c>treatments</c> collection spans every decomposed treatment type.</remarks>
    public Task<DateTime?> GetLatestTreatmentTimestampAsync(
        string source,
        CancellationToken cancellationToken = default)
        => LatestTimestampAsync(
            () => _bolusRepository.GetLatestTimestampAsync(source, cancellationToken),
            () => _carbIntakeRepository.GetLatestTimestampAsync(source, cancellationToken),
            () => _bgCheckRepository.GetLatestTimestampAsync(source, cancellationToken),
            () => _bolusCalculationRepository.GetLatestTimestampAsync(source, cancellationToken),
            () => _tempBasalRepository.GetLatestTimestampAsync(source, cancellationToken),
            () => _basalInjectionRepository.GetLatestTimestampAsync(source, cancellationToken),
            () => _noteRepository.GetLatestTimestampAsync(source, cancellationToken),
            () => _deviceEventRepository.GetLatestTimestampAsync(source, cancellationToken));

    // ── Patient Insulin resolution helpers ──────────────────────────────

    /// <summary>
    /// For boluses that carry an <see cref="TreatmentInsulinContext"/> with a placeholder
    /// <c>PatientInsulinId</c> (Guid.Empty), resolves or auto-creates the corresponding
    /// <see cref="PatientInsulin"/> record and updates the context in place.
    /// </summary>
    private async Task ResolvePatientInsulinsForBolusesAsync(
        List<Bolus> records, WriteOrigin origin, CancellationToken ct)
    {
        var needsResolution = records
            .Where(r => r.InsulinContext is { PatientInsulinId: var id } && id == Guid.Empty)
            .ToList();

        if (needsResolution.Count == 0) return;

        var cache = await BuildPatientInsulinCacheAsync(ct);

        foreach (var bolus in needsResolution)
        {
            var resolved = await ResolveOrCreatePatientInsulinAsync(
                bolus.InsulinContext!, InsulinRole.Bolus, cache, origin, ct);
            bolus.InsulinContext = resolved;
        }
    }

    /// <summary>
    /// For basal injections that carry an <see cref="TreatmentInsulinContext"/> with a placeholder
    /// <c>PatientInsulinId</c> (Guid.Empty), resolves or auto-creates the corresponding
    /// <see cref="PatientInsulin"/> record and updates the context in place.
    /// </summary>
    private async Task ResolvePatientInsulinsForBasalInjectionsAsync(
        List<BasalInjection> records, WriteOrigin origin, CancellationToken ct)
    {
        // A null context is the uploader shape (no insulin catalog knowledge) and stays null;
        // only the placeholder Guid.Empty context is resolved. Mirrors the bolus path above.
        var needsResolution = records
            .Where(r => r.InsulinContext is { PatientInsulinId: var id } && id == Guid.Empty)
            .ToList();

        if (needsResolution.Count == 0) return;

        var cache = await BuildPatientInsulinCacheAsync(ct);

        foreach (var injection in needsResolution)
        {
            var resolved = await ResolveOrCreatePatientInsulinAsync(
                injection.InsulinContext!, InsulinRole.Basal, cache, origin, ct);
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
        WriteOrigin origin,
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

        var created = await _patientInsulinRepository.CreateAsync(newInsulin, origin, ct);
        cache.Add(created);

        Logger.LogInformation(
            "Auto-created PatientInsulin '{Name}' (role={Role}, id={Id}) from connector import",
            created.Name, role, created.Id);

        return context with { PatientInsulinId = created.Id };
    }
}

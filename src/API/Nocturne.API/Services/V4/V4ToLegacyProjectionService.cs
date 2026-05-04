using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Entries;
using Nocturne.API.Services.Treatments;
using Nocturne.Connectors.Core.Constants;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Mappers.V4;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Projects V4 granular records back into the legacy <see cref="Entry"/> and <see cref="Treatment"/>
/// shapes for v1/v2/v3 API compatibility. Only records written directly to V4 tables (those whose
/// <c>LegacyId</c> is <see langword="null"/> — they have no corresponding row in the legacy
/// entries/treatments tables) are projected.
/// </summary>
/// <remarks>
/// This service is the read side of the dual-path architecture.
/// The write side (legacy record → V4 typed record) is handled by <see cref="DecompositionPipeline"/>.
/// Projection covers: <see cref="SensorGlucose"/> → <see cref="Entry"/>,
/// <see cref="Bolus"/> and <see cref="CarbIntake"/> → <see cref="Treatment"/>,
/// <see cref="DeviceEvent"/> → <see cref="Treatment"/> using the legacy event-type map.
/// </remarks>
/// <seealso cref="IV4ToLegacyProjectionService"/>
/// <seealso cref="DecompositionPipeline"/>
public class V4ToLegacyProjectionService : IV4ToLegacyProjectionService
{
    private readonly ISensorGlucoseRepository _sensorGlucoseRepository;
    private readonly IBolusRepository _bolusRepository;
    private readonly ICarbIntakeRepository _carbIntakeRepository;
    private readonly IBGCheckRepository _bgCheckRepository;
    private readonly INoteRepository _noteRepository;
    private readonly IDeviceEventRepository _deviceEventRepository;
    private readonly ITempBasalRepository _tempBasalRepository;
    private readonly IBolusCalculationRepository _bolusCalculationRepository;
    private readonly ITreatmentFoodService _treatmentFoodService;
    private readonly NocturneDbContext _dbContext;
    private readonly ILogger<V4ToLegacyProjectionService> _logger;

    // DeviceEventType → legacy Nightscout eventType string (reverse of TreatmentTypes.DeviceEventTypeMap)
    private static readonly Dictionary<DeviceEventType, string> DeviceEventTypeToString =
        new()
        {
            [DeviceEventType.SensorStart] = TreatmentTypes.SensorStart,
            [DeviceEventType.SensorChange] = TreatmentTypes.SensorChange,
            [DeviceEventType.SensorStop] = TreatmentTypes.SensorStop,
            [DeviceEventType.SiteChange] = TreatmentTypes.SiteChange,
            [DeviceEventType.InsulinChange] = TreatmentTypes.InsulinChange,
            [DeviceEventType.PumpBatteryChange] = TreatmentTypes.PumpBatteryChange,
            [DeviceEventType.PodChange] = TreatmentTypes.PodChange,
            [DeviceEventType.ReservoirChange] = TreatmentTypes.ReservoirChange,
            [DeviceEventType.CannulaChange] = TreatmentTypes.CannulaChange,
            [DeviceEventType.TransmitterSensorInsert] = TreatmentTypes.TransmitterSensorInsert,
        };

    public V4ToLegacyProjectionService(
        ISensorGlucoseRepository sensorGlucoseRepository,
        IBolusRepository bolusRepository,
        ICarbIntakeRepository carbIntakeRepository,
        IBGCheckRepository bgCheckRepository,
        INoteRepository noteRepository,
        IDeviceEventRepository deviceEventRepository,
        ITempBasalRepository tempBasalRepository,
        IBolusCalculationRepository bolusCalculationRepository,
        ITreatmentFoodService treatmentFoodService,
        NocturneDbContext dbContext,
        ILogger<V4ToLegacyProjectionService> logger
    )
    {
        _sensorGlucoseRepository = sensorGlucoseRepository;
        _bolusRepository = bolusRepository;
        _carbIntakeRepository = carbIntakeRepository;
        _bgCheckRepository = bgCheckRepository;
        _noteRepository = noteRepository;
        _deviceEventRepository = deviceEventRepository;
        _tempBasalRepository = tempBasalRepository;
        _bolusCalculationRepository = bolusCalculationRepository;
        _treatmentFoodService = treatmentFoodService;
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <summary>
    /// Converts nullable unix milliseconds to nullable DateTime.
    /// </summary>
    private static DateTime? MillsToDateTime(long? mills) =>
        mills.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(mills.Value).UtcDateTime : null;

    public async Task<IEnumerable<Entry>> GetProjectedEntriesAsync(
        long? fromMills,
        long? toMills,
        int limit,
        int offset,
        bool descending,
        CancellationToken ct = default
    )
    {
        IEnumerable<SensorGlucose> records;
        try
        {
            records = await _sensorGlucoseRepository.GetAsync(
                from: MillsToDateTime(fromMills),
                to: MillsToDateTime(toMills),
                device: null,
                source: null,
                limit: limit,
                offset: offset,
                descending: descending,
                nativeOnly: true,
                ct: ct
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch V4 SensorGlucose records for projection");
            return Enumerable.Empty<Entry>();
        }

        return records.Select(ProjectSensorGlucoseToEntry);
    }

    /// <inheritdoc />
    public async Task<Entry?> GetLatestProjectedEntryAsync(CancellationToken ct = default)
    {
        IEnumerable<SensorGlucose> records;
        try
        {
            records = await _sensorGlucoseRepository.GetAsync(
                from: null,
                to: null,
                device: null,
                source: null,
                limit: 1,
                offset: 0,
                descending: true,
                nativeOnly: true,
                ct: ct
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch V4 SensorGlucose records for latest projection");
            return null;
        }

        var latest = records.FirstOrDefault();
        return latest == null ? null : ProjectSensorGlucoseToEntry(latest);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Treatment>> GetProjectedTreatmentsAsync(
        long? fromMills,
        long? toMills,
        int limit,
        bool nativeOnly = true,
        CancellationToken ct = default
    )
    {
        // Fetch all V4 treatment record types sequentially.
        // These repositories share a scoped DbContext which is not thread-safe,
        // so they cannot be run concurrently via Task.WhenAll.
        var boluses = (await FetchSafe(() =>
            _bolusRepository.GetAsync(
                from: MillsToDateTime(fromMills),
                to: MillsToDateTime(toMills),
                device: null,
                source: null,
                limit: limit,
                offset: 0,
                descending: true,
                nativeOnly: nativeOnly,
                ct: ct
            )
        )).ToList();

        var carbs = (await FetchSafe(() =>
            _carbIntakeRepository.GetAsync(
                from: MillsToDateTime(fromMills),
                to: MillsToDateTime(toMills),
                device: null,
                source: null,
                limit: limit,
                offset: 0,
                descending: true,
                nativeOnly: nativeOnly,
                ct: ct
            )
        )).ToList();

        var bgChecks = (await FetchSafe(() =>
            _bgCheckRepository.GetAsync(
                from: MillsToDateTime(fromMills),
                to: MillsToDateTime(toMills),
                device: null,
                source: null,
                limit: limit,
                offset: 0,
                descending: true,
                nativeOnly: nativeOnly,
                ct: ct
            )
        )).ToList();

        var notes = (await FetchSafe(() =>
            _noteRepository.GetAsync(
                from: MillsToDateTime(fromMills),
                to: MillsToDateTime(toMills),
                device: null,
                source: null,
                limit: limit,
                offset: 0,
                descending: true,
                nativeOnly: nativeOnly,
                ct: ct
            )
        )).ToList();

        var deviceEvents = (await FetchSafe(() =>
            _deviceEventRepository.GetAsync(
                from: MillsToDateTime(fromMills),
                to: MillsToDateTime(toMills),
                device: null,
                source: null,
                limit: limit,
                offset: 0,
                descending: true,
                nativeOnly: nativeOnly,
                ct: ct
            )
        )).ToList();

        var tempBasals = (await FetchSafe(() =>
            _tempBasalRepository.GetAsync(
                from: MillsToDateTime(fromMills),
                to: MillsToDateTime(toMills),
                device: null,
                source: null,
                limit: limit,
                offset: 0,
                descending: true,
                ct: ct
            )
        )).ToList();
        if (nativeOnly)
            tempBasals = tempBasals.Where(r => r.LegacyId == null).ToList();

        var bolusCalcs = (await FetchSafe(() =>
            _bolusCalculationRepository.GetAsync(
                from: MillsToDateTime(fromMills),
                to: MillsToDateTime(toMills),
                device: null,
                source: null,
                limit: limit,
                offset: 0,
                descending: true,
                ct: ct
            )
        )).ToList();
        if (nativeOnly)
            bolusCalcs = bolusCalcs.Where(r => r.LegacyId == null).ToList();

        // Load food breakdown entries for all carb intakes to populate legacy fields
        var carbIds = carbs.Select(c => c.Id).ToList();
        var allFoodEntries = carbIds.Count > 0
            ? (await _treatmentFoodService.GetByCarbIntakeIdsAsync(carbIds, ct)).ToList()
            : [];
        var foodsByCarbId = allFoodEntries
            .GroupBy(f => f.CarbIntakeId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var treatments = new List<Treatment>();

        // --- Bolus + CarbIntake pairing ---
        // Pair by CorrelationId only. Under N:M a single correlation may have
        // multiple boluses and/or multiple carb intakes: the first bolus + first
        // carb in a correlation become the Meal Bolus projection, and any extras
        // flow through as separate Correction Bolus / Carb Correction treatments.
        var pairedCarbIds = new HashSet<Guid>();
        var pairedBolusIds = new HashSet<Guid>();

        var bolusesWithCorrelation = boluses
            .Where(b => b.CorrelationId.HasValue)
            .ToLookup(b => b.CorrelationId!.Value);
        var bolusesWithoutCorrelation = boluses.Where(b => !b.CorrelationId.HasValue).ToList();

        var carbsWithCorrelation = carbs
            .Where(c => c.CorrelationId.HasValue)
            .ToLookup(c => c.CorrelationId!.Value);
        var carbsWithoutCorrelation = carbs.Where(c => !c.CorrelationId.HasValue).ToList();

        var allCorrelationIds = bolusesWithCorrelation
            .Select(g => g.Key)
            .Union(carbsWithCorrelation.Select(g => g.Key))
            .Distinct();

        foreach (var correlationId in allCorrelationIds)
        {
            var correlatedBoluses = bolusesWithCorrelation[correlationId].ToList();
            var correlatedCarbs = carbsWithCorrelation[correlationId].ToList();

            if (correlatedBoluses.Count > 0 && correlatedCarbs.Count > 0)
            {
                // Under N:M a correlation may have multiple boluses and/or carbs.
                // The dominant-dose bolus + carb are projected as the primary Meal
                // Bolus; any extras fall through to the per-record handlers below
                // as separate Correction Bolus / Carb Correction treatments.
                // Ordering by descending Insulin/Carbs picks the "main" record a
                // human would recognise; ThenBy(Id) is a deterministic tiebreaker
                // so multiple same-timestamp, same-dose records don't produce
                // non-deterministic output across requests.
                var primaryBolus = correlatedBoluses
                    .OrderByDescending(b => b.Insulin)
                    .ThenBy(b => b.Id)
                    .First();
                var primaryCarb = correlatedCarbs
                    .OrderByDescending(c => c.Carbs)
                    .ThenBy(c => c.Id)
                    .First();
                pairedBolusIds.Add(primaryBolus.Id);
                pairedCarbIds.Add(primaryCarb.Id);
                treatments.Add(ProjectMealBolus(primaryBolus, primaryCarb, foodsByCarbId.GetValueOrDefault(primaryCarb.Id, [])));
            }
        }

        // Remaining unpaired records: any bolus or carb that either has no
        // correlation, or shares a correlation but wasn't the primary pair member.
        foreach (var bolus in bolusesWithoutCorrelation)
            treatments.Add(ProjectCorrectionBolus(bolus));

        foreach (var group in bolusesWithCorrelation)
        {
            foreach (var bolus in group.Where(b => !pairedBolusIds.Contains(b.Id)))
                treatments.Add(ProjectCorrectionBolus(bolus));
        }

        foreach (var carb in carbsWithoutCorrelation)
            treatments.Add(ProjectCarbCorrection(carb, foodsByCarbId.GetValueOrDefault(carb.Id, [])));

        foreach (var group in carbsWithCorrelation)
        {
            foreach (var carb in group.Where(c => !pairedCarbIds.Contains(c.Id)))
                treatments.Add(ProjectCarbCorrection(carb, foodsByCarbId.GetValueOrDefault(carb.Id, [])));
        }

        // --- BGCheck → Treatment ---
        foreach (var bgCheck in bgChecks)
            treatments.Add(ProjectBgCheck(bgCheck));

        // --- Note → Treatment ---
        foreach (var note in notes)
            treatments.Add(ProjectNote(note));

        // --- DeviceEvent → Treatment ---
        foreach (var deviceEvent in deviceEvents)
            treatments.Add(ProjectDeviceEvent(deviceEvent));

        // --- TempBasal → Treatment ---
        foreach (var tempBasal in tempBasals)
            treatments.Add(TempBasalToTreatmentMapper.ToTreatment(tempBasal));

        // --- BolusCalculation → Treatment ---
        foreach (var bolusCalc in bolusCalcs)
            treatments.Add(ProjectBolusCalculation(bolusCalc));

        return treatments.OrderByDescending(t => t.Mills).Take(limit);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Treatment>> GetProjectedTreatmentsModifiedSinceAsync(
        long lastModifiedMills, int limit, CancellationToken ct = default)
    {
        var threshold = DateTimeOffset.FromUnixTimeMilliseconds(lastModifiedMills).UtcDateTime;
        var treatments = new List<Treatment>();

        // Query each V4 entity table by ModifiedAt, map to domain, then project to Treatment.
        // Sequential to avoid DbContext thread-safety issues.
        var boluses = await _dbContext.Boluses.AsNoTracking()
            .Where(e => e.SysUpdatedAt >= threshold)
            .OrderBy(e => e.SysUpdatedAt)
            .Take(limit)
            .ToListAsync(ct);
        foreach (var entity in boluses)
            treatments.Add(WithSrvModified(ProjectCorrectionBolus(BolusMapper.ToDomainModel(entity)), entity.SysUpdatedAt));

        var carbIntakes = await _dbContext.CarbIntakes.AsNoTracking()
            .Where(e => e.SysUpdatedAt >= threshold)
            .OrderBy(e => e.SysUpdatedAt)
            .Take(limit)
            .ToListAsync(ct);
        foreach (var entity in carbIntakes)
            treatments.Add(WithSrvModified(ProjectCarbCorrection(CarbIntakeMapper.ToDomainModel(entity), []), entity.SysUpdatedAt));

        var bgChecks = await _dbContext.BGChecks.AsNoTracking()
            .Where(e => e.SysUpdatedAt >= threshold)
            .OrderBy(e => e.SysUpdatedAt)
            .Take(limit)
            .ToListAsync(ct);
        foreach (var entity in bgChecks)
            treatments.Add(WithSrvModified(ProjectBgCheck(BGCheckMapper.ToDomainModel(entity)), entity.SysUpdatedAt));

        var notes = await _dbContext.Notes.AsNoTracking()
            .Where(e => e.SysUpdatedAt >= threshold)
            .OrderBy(e => e.SysUpdatedAt)
            .Take(limit)
            .ToListAsync(ct);
        foreach (var entity in notes)
            treatments.Add(WithSrvModified(ProjectNote(NoteMapper.ToDomainModel(entity)), entity.SysUpdatedAt));

        var deviceEvents = await _dbContext.DeviceEvents.AsNoTracking()
            .Where(e => e.SysUpdatedAt >= threshold)
            .OrderBy(e => e.SysUpdatedAt)
            .Take(limit)
            .ToListAsync(ct);
        foreach (var entity in deviceEvents)
            treatments.Add(WithSrvModified(ProjectDeviceEvent(DeviceEventMapper.ToDomainModel(entity)), entity.SysUpdatedAt));

        var tempBasals = await _dbContext.TempBasals.AsNoTracking()
            .Where(e => e.SysUpdatedAt >= threshold)
            .OrderBy(e => e.SysUpdatedAt)
            .Take(limit)
            .ToListAsync(ct);
        foreach (var entity in tempBasals)
            treatments.Add(WithSrvModified(TempBasalToTreatmentMapper.ToTreatment(TempBasalMapper.ToDomainModel(entity)), entity.SysUpdatedAt));

        var bolusCalcs = await _dbContext.BolusCalculations.AsNoTracking()
            .Where(e => e.SysUpdatedAt >= threshold)
            .OrderBy(e => e.SysUpdatedAt)
            .Take(limit)
            .ToListAsync(ct);
        foreach (var entity in bolusCalcs)
            treatments.Add(WithSrvModified(ProjectBolusCalculation(BolusCalculationMapper.ToDomainModel(entity)), entity.SysUpdatedAt));

        return treatments.OrderBy(t => t.SrvModified ?? t.Mills).Take(limit);
    }

    // -------------------------------------------------------------------------
    // Private projection helpers
    // -------------------------------------------------------------------------

    private static Entry ProjectSensorGlucoseToEntry(SensorGlucose sg) =>
        new()
        {
            Id = sg.Id.ToString(),
            Type = "sgv",
            Mills = sg.Mills,
            Sgv = sg.Mgdl,
            Mgdl = sg.Mgdl,
            Mmol = sg.Mmol,
            Mbg = 0,
            Direction = sg.Direction?.ToString(),
            Trend = sg.Trend.HasValue ? (int?)sg.Trend.Value : null,
            TrendRate = sg.TrendRate,
            Noise = sg.Noise,
            Device = sg.Device,
            App = sg.App,
            DataSource = sg.DataSource,
        };

    private static Treatment ProjectMealBolus(Bolus bolus, CarbIntake carb, List<TreatmentFood> foods) =>
        new()
        {
            Id = bolus.Id.ToString(),
            EventType = TreatmentTypes.MealBolus,
            Mills = bolus.Mills,
            Insulin = bolus.Insulin,
            Carbs = carb.Carbs,
            FoodType = DeriveeFoodType(foods),
            Fat = DeriveTotalFat(foods),
            Protein = DeriveTotalProtein(foods),
            AbsorptionTime = carb.AbsorptionTime,
            CarbTime = carb.CarbTime.HasValue ? (int?)((int)carb.CarbTime.Value) : null,
            EnteredBy = bolus.Device,
            DataSource = bolus.DataSource,
            SyncIdentifier = bolus.SyncIdentifier,
            InsulinType = bolus.InsulinType,
            UtcOffset = bolus.UtcOffset,
        };

    private static Treatment ProjectCorrectionBolus(Bolus bolus) =>
        new()
        {
            Id = bolus.Id.ToString(),
            EventType = TreatmentTypes.CorrectionBolus,
            Mills = bolus.Mills,
            Insulin = bolus.Insulin,
            EnteredBy = bolus.Device,
            DataSource = bolus.DataSource,
            SyncIdentifier = bolus.SyncIdentifier,
            InsulinType = bolus.InsulinType,
            UtcOffset = bolus.UtcOffset,
        };

    private static Treatment ProjectCarbCorrection(CarbIntake carb, List<TreatmentFood> foods) =>
        new()
        {
            Id = carb.Id.ToString(),
            EventType = TreatmentTypes.CarbCorrection,
            Mills = carb.Mills,
            Carbs = carb.Carbs,
            FoodType = DeriveeFoodType(foods),
            Fat = DeriveTotalFat(foods),
            Protein = DeriveTotalProtein(foods),
            AbsorptionTime = carb.AbsorptionTime,
            CarbTime = carb.CarbTime.HasValue ? (int?)((int)carb.CarbTime.Value) : null,
            EnteredBy = carb.Device,
            DataSource = carb.DataSource,
            SyncIdentifier = carb.SyncIdentifier,
            UtcOffset = carb.UtcOffset,
        };

    private static string? DeriveeFoodType(List<TreatmentFood> foods)
    {
        if (foods.Count == 0) return null;

        var names = foods
            .Select(f => f.FoodName ?? f.Note)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        return names.Count > 0 ? string.Join(", ", names) : null;
    }

    private static double? DeriveTotalFat(List<TreatmentFood> foods)
    {
        var sum = foods
            .Where(f => f.FatPerPortion.HasValue && f.Portions > 0)
            .Sum(f => (double)(f.FatPerPortion!.Value * f.Portions));
        return sum > 0 ? sum : null;
    }

    private static double? DeriveTotalProtein(List<TreatmentFood> foods)
    {
        var sum = foods
            .Where(f => f.ProteinPerPortion.HasValue && f.Portions > 0)
            .Sum(f => (double)(f.ProteinPerPortion!.Value * f.Portions));
        return sum > 0 ? sum : null;
    }

    private static Treatment ProjectBgCheck(BGCheck bgCheck) =>
        new()
        {
            Id = bgCheck.Id.ToString(),
            EventType = TreatmentTypes.BgCheck,
            Mills = bgCheck.Mills,
            Glucose = bgCheck.Glucose,
            Mgdl = bgCheck.Mgdl,
            Mmol = bgCheck.Mmol,
            GlucoseType = bgCheck.GlucoseType?.ToString(),
            Units = bgCheck.Units == GlucoseUnit.Mmol ? "mmol" : "mg/dl",
            EnteredBy = bgCheck.Device,
            DataSource = bgCheck.DataSource,
            SyncIdentifier = bgCheck.SyncIdentifier,
            UtcOffset = bgCheck.UtcOffset,
        };

    private static Treatment ProjectNote(Note note) =>
        new()
        {
            Id = note.Id.ToString(),
            EventType = note.EventType ?? "Note",
            Mills = note.Mills,
            Notes = note.Text,
            IsAnnouncement = note.IsAnnouncement,
            EnteredBy = note.Device,
            DataSource = note.DataSource,
            SyncIdentifier = note.SyncIdentifier,
            UtcOffset = note.UtcOffset,
        };

    private static Treatment ProjectDeviceEvent(DeviceEvent deviceEvent)
    {
        DeviceEventTypeToString.TryGetValue(deviceEvent.EventType, out var eventTypeString);
        return new Treatment
        {
            Id = deviceEvent.Id.ToString(),
            EventType = eventTypeString ?? deviceEvent.EventType.ToString(),
            Mills = deviceEvent.Mills,
            Notes = deviceEvent.Notes,
            EnteredBy = deviceEvent.Device,
            DataSource = deviceEvent.DataSource,
            SyncIdentifier = deviceEvent.SyncIdentifier,
            UtcOffset = deviceEvent.UtcOffset,
        };
    }

    private static Treatment ProjectBolusCalculation(BolusCalculation bc) =>
        new()
        {
            Id = bc.Id.ToString(),
            EventType = "Bolus Wizard",
            Mills = bc.Mills,
            BloodGlucoseInput = bc.BloodGlucoseInput,
            BloodGlucoseInputSource = bc.BloodGlucoseInputSource,
            Carbs = bc.CarbInput,
            InsulinOnBoard = bc.InsulinOnBoard,
            InsulinRecommendationForCorrection = bc.InsulinRecommendation,
            CR = bc.CarbRatio,
            CalculationType = bc.CalculationType.HasValue
                ? (Nocturne.Core.Models.CalculationType)(int)bc.CalculationType.Value
                : null,
            InsulinRecommendationForCarbs = bc.InsulinRecommendationForCarbs,
            InsulinProgrammed = bc.InsulinProgrammed,
            EnteredInsulin = bc.EnteredInsulin,
            SplitNow = bc.SplitNow,
            SplitExt = bc.SplitExt,
            PreBolus = bc.PreBolus,
            EnteredBy = bc.Device,
            DataSource = bc.DataSource,
            UtcOffset = bc.UtcOffset,
        };

    private static Treatment WithSrvModified(Treatment treatment, DateTime modifiedAt)
    {
        treatment.SrvModified = new DateTimeOffset(modifiedAt, TimeSpan.Zero).ToUnixTimeMilliseconds();
        return treatment;
    }

    private async Task<IEnumerable<T>> FetchSafe<T>(Func<Task<IEnumerable<T>>> fetchFunc)
    {
        try
        {
            return await fetchFunc();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch V4 records of type {Type} for legacy projection", typeof(T).Name);
            return Enumerable.Empty<T>();
        }
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Constants;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;

using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Decomposes legacy <see cref="Treatment"/> records into v4 granular models based on
/// <see cref="Treatment.EventType"/>.
/// <list type="bullet">
///   <item><description>Bolus/Meal/Correction → <see cref="V4Models.Bolus"/></description></item>
///   <item><description>Carb Correction/Meal → <see cref="V4Models.CarbIntake"/></description></item>
///   <item><description>BG Check → <see cref="V4Models.BGCheck"/></description></item>
///   <item><description>Bolus Wizard → <see cref="V4Models.BolusCalculation"/> (+ optional <see cref="V4Models.Bolus"/>)</description></item>
///   <item><description>Note/Announcement → <see cref="V4Models.Note"/></description></item>
///   <item><description>Device events → <see cref="V4Models.DeviceEvent"/></description></item>
///   <item><description>TempBasal, ProfileSwitch, Override, Temporary Target → delegated to <see cref="IStateSpanService"/></description></item>
/// </list>
/// Supports idempotent create-or-update via <c>LegacyId</c> matching.
/// </summary>
/// <seealso cref="ITreatmentDecomposer"/>
/// <seealso cref="IDecomposer{T}"/>
/// <seealso cref="IStateSpanService"/>
public class TreatmentDecomposer : ITreatmentDecomposer, IDecomposer<Treatment>
{
    private readonly NocturneDbContext _dbContext;
    private readonly IBolusRepository _bolusRepository;
    private readonly ITempBasalRepository _tempBasalRepository;
    private readonly ICarbIntakeRepository _carbIntakeRepository;
    private readonly IBGCheckRepository _bgCheckRepository;
    private readonly INoteRepository _noteRepository;
    private readonly IDeviceEventRepository _deviceEventRepository;
    private readonly IBolusCalculationRepository _bolusCalculationRepository;
    private readonly IStateSpanService _stateSpanService;
    private readonly ITreatmentFoodService _treatmentFoodService;
    private readonly IDeviceService _deviceService;
    private readonly IProfileDecomposer _profileDecomposer;
    private readonly ILogger<TreatmentDecomposer> _logger;

    /// <summary>
    /// Event types that indicate a temp basal treatment (case-insensitive comparison)
    /// </summary>
    private static readonly string[] TempBasalEventTypes =
    [
        "Temp Basal",
        "Temp Basal Start",
        "TempBasal"
    ];

    /// <param name="dbContext">EF Core context used to persist <see cref="DecompositionBatchEntity"/> records and look up treatment entity PKs.</param>
    /// <param name="bolusRepository">Repository for <see cref="V4Models.Bolus"/> records.</param>
    /// <param name="tempBasalRepository">Repository for <see cref="V4Models.TempBasal"/> records.</param>
    /// <param name="carbIntakeRepository">Repository for <see cref="V4Models.CarbIntake"/> records.</param>
    /// <param name="bgCheckRepository">Repository for <see cref="V4Models.BGCheck"/> records.</param>
    /// <param name="noteRepository">Repository for <see cref="V4Models.Note"/> records.</param>
    /// <param name="deviceEventRepository">Repository for <see cref="V4Models.DeviceEvent"/> records.</param>
    /// <param name="bolusCalculationRepository">Repository for <see cref="V4Models.BolusCalculation"/> records.</param>
    /// <param name="stateSpanService">Service used to upsert state spans for TempBasal, ProfileSwitch, Override, and TemporaryTarget treatments.</param>
    /// <param name="treatmentFoodService">Service for preserving legacy <see cref="Treatment.FoodType"/> as a <see cref="TreatmentFood"/> entry.</param>
    /// <param name="deviceService">Service that resolves or creates canonical device references.</param>
    /// <param name="profileDecomposer">Decomposes inline profile JSON from profile switch treatments into V4 schedule records.</param>
    /// <param name="logger">Logger instance for this decomposer.</param>
    public TreatmentDecomposer(
        NocturneDbContext dbContext,
        IBolusRepository bolusRepository,
        ITempBasalRepository tempBasalRepository,
        ICarbIntakeRepository carbIntakeRepository,
        IBGCheckRepository bgCheckRepository,
        INoteRepository noteRepository,
        IDeviceEventRepository deviceEventRepository,
        IBolusCalculationRepository bolusCalculationRepository,
        IStateSpanService stateSpanService,
        ITreatmentFoodService treatmentFoodService,
        IDeviceService deviceService,
        IProfileDecomposer profileDecomposer,
        ILogger<TreatmentDecomposer> logger)
    {
        _dbContext = dbContext;
        _bolusRepository = bolusRepository;
        _tempBasalRepository = tempBasalRepository;
        _carbIntakeRepository = carbIntakeRepository;
        _bgCheckRepository = bgCheckRepository;
        _noteRepository = noteRepository;
        _deviceEventRepository = deviceEventRepository;
        _bolusCalculationRepository = bolusCalculationRepository;
        _stateSpanService = stateSpanService;
        _treatmentFoodService = treatmentFoodService;
        _deviceService = deviceService;
        _profileDecomposer = profileDecomposer;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<V4Models.DecompositionResult> DecomposeAsync(Treatment treatment, CancellationToken ct = default)
    {
        var batch = new DecompositionBatchEntity
        {
            TenantId = _dbContext.TenantId,
            Source = "treatment_decomposer",
            SourceRecordId = treatment.Id,
            CreatedAt = DateTime.UtcNow,
        };
        _dbContext.DecompositionBatches.Add(batch);
        await _dbContext.SaveChangesAsync(ct);

        var result = new V4Models.DecompositionResult
        {
            CorrelationId = batch.Id
        };

        var eventType = treatment.EventType?.Trim();
        var hasInsulin = treatment.Insulin is > 0;
        var hasCarbs = treatment.Carbs is > 0;

        // Determine which records to produce based on EventType
        var produceBolus = false;
        var produceCarbIntake = false;
        var produceBGCheck = false;
        var produceNote = false;
        var produceBolusCalc = false;
        var produceDeviceEvent = false;
        var delegateToStateSpan = false;
        var isProfileSwitch = false;
        var isOverride = false;
        var isTemporaryTarget = false;
        var isAnnouncement = false;
        DeviceEventType parsedDeviceEventType = default;

        if (IsTempBasal(eventType))
        {
            delegateToStateSpan = true;
        }
        else if (string.Equals(eventType, "Profile Switch", StringComparison.OrdinalIgnoreCase))
        {
            isProfileSwitch = true;
            delegateToStateSpan = true;
        }
        else if (string.Equals(eventType, "Temporary Override", StringComparison.OrdinalIgnoreCase))
        {
            isOverride = true;
            delegateToStateSpan = true;
        }
        else if (string.Equals(eventType, "Temporary Target", StringComparison.OrdinalIgnoreCase)
              || string.Equals(eventType, "Temporary Target Cancel", StringComparison.OrdinalIgnoreCase))
        {
            isTemporaryTarget = true;
            delegateToStateSpan = true;
        }
        else if (eventType != null && TreatmentTypes.DeviceEventTypeMap.TryGetValue(eventType, out parsedDeviceEventType))
        {
            produceDeviceEvent = true;
        }
        else if (string.Equals(eventType, "Meal Bolus", StringComparison.OrdinalIgnoreCase)
              || string.Equals(eventType, "Snack Bolus", StringComparison.OrdinalIgnoreCase))
        {
            produceBolus = true;
            produceCarbIntake = true;
        }
        else if (string.Equals(eventType, "Correction Bolus", StringComparison.OrdinalIgnoreCase))
        {
            produceBolus = true;
        }
        else if (string.Equals(eventType, "Carb Correction", StringComparison.OrdinalIgnoreCase))
        {
            produceCarbIntake = true;
        }
        else if (string.Equals(eventType, "BG Check", StringComparison.OrdinalIgnoreCase))
        {
            produceBGCheck = true;
        }
        else if (string.Equals(eventType, "Announcement", StringComparison.OrdinalIgnoreCase))
        {
            produceNote = true;
            isAnnouncement = true;
        }
        else if (string.Equals(eventType, "Note", StringComparison.OrdinalIgnoreCase))
        {
            produceNote = true;
        }
        else if (string.Equals(eventType, "Bolus Wizard", StringComparison.OrdinalIgnoreCase))
        {
            produceBolusCalc = true;
            // Also produce a Bolus if insulin was delivered
            if (hasInsulin)
            {
                produceBolus = true;
            }
        }

        // Override rule: if Treatment has BOTH Insulin > 0 AND Carbs > 0,
        // always produce both Bolus + CarbIntake regardless of EventType
        if (hasInsulin && hasCarbs)
        {
            produceBolus = true;
            produceCarbIntake = true;
        }

        // Produce a Note record for any treatment with non-empty Notes,
        // unless we're already producing a Note (avoids duplicate).
        if (!produceNote && !string.IsNullOrWhiteSpace(treatment.Notes))
        {
            produceNote = true;
        }

        // Handle StateSpan delegation
        if (delegateToStateSpan)
        {
            if (isProfileSwitch)
            {
                await DecomposeProfileSwitchAsync(treatment, result, ct);
            }
            else if (isOverride)
            {
                await DecomposeOverrideAsync(treatment, result, ct);
            }
            else if (isTemporaryTarget)
            {
                await DecomposeTemporaryTargetAsync(treatment, result, ct);
            }
            else
            {
                await DecomposeTempBasalAsync(treatment, result, ct);
            }
        }

        // Produce v4 records
        if (produceBolus)
        {
            await DecomposeBolusAsync(treatment, result, ct);
        }

        if (produceCarbIntake)
        {
            await DecomposeCarbIntakeAsync(treatment, result, ct);
        }

        if (produceBGCheck)
        {
            await DecomposeBGCheckAsync(treatment, result, ct);
        }

        if (produceNote)
        {
            await DecomposeNoteAsync(treatment, result, isAnnouncement, ct);
        }

        if (produceBolusCalc)
        {
            await DecomposeBolusCalculationAsync(treatment, result, ct);
        }

        if (produceDeviceEvent)
        {
            await DecomposeDeviceEventAsync(treatment, result, parsedDeviceEventType, ct);
        }

        // After all decompositions, link records via FKs
        var bolusCalc = result.CreatedRecords.OfType<V4Models.BolusCalculation>().FirstOrDefault()
            ?? result.UpdatedRecords.OfType<V4Models.BolusCalculation>().FirstOrDefault();
        var bolus = result.CreatedRecords.OfType<V4Models.Bolus>().FirstOrDefault()
            ?? result.UpdatedRecords.OfType<V4Models.Bolus>().FirstOrDefault();

        // Link Bolus -> BolusCalculation
        if (bolus != null && bolusCalc != null && bolus.BolusCalculationId != bolusCalc.Id)
        {
            bolus.BolusCalculationId = bolusCalc.Id;
            await _bolusRepository.UpdateAsync(bolus.Id, bolus, ct);
        }

        // If nothing was produced and there's no delegation, log a warning
        if (!produceBolus && !produceCarbIntake && !produceBGCheck
            && !produceNote && !produceBolusCalc && !produceDeviceEvent && !delegateToStateSpan)
        {
            _logger.LogWarning(
                "Unknown event type '{EventType}' for treatment {Id} with no insulin/carbs, skipping decomposition",
                treatment.EventType, treatment.Id);
        }

        return result;
    }

    #region Decomposition Methods

    private async Task DecomposeBolusAsync(Treatment treatment, V4Models.DecompositionResult result, CancellationToken ct)
    {
        // AAPS uses "Correction Bolus" exclusively for algorithm-delivered SMBs (BolusExtension.kt:28).
        // The isBasalInsulin flag is never set true on real AAPS treatments.
        var isAlgorithmBolus = (treatment.IsBasalInsulin == true && treatment.Insulin > 0)
            || (string.Equals(treatment.EventType, "Correction Bolus", StringComparison.OrdinalIgnoreCase) && IsAapsUpload(treatment));

        if (isAlgorithmBolus)
        {
            await DecomposeMicroBolusAsync(treatment, result, ct);
            return;
        }

        var existing = treatment.Id != null
            ? await _bolusRepository.GetByLegacyIdAsync(treatment.Id, ct)
            : null;

        var model = MapToBolus(treatment, result.CorrelationId);
        model.DeviceId = await _deviceService.ResolveAsync(
            V4Models.DeviceCategory.InsulinPump, treatment.PumpType, treatment.PumpSerial, treatment.Mills, ct);
        model.PatientDeviceId = await _deviceService.ResolvePatientDeviceAsync(model.DeviceId, treatment.Mills, ct);

        if (existing != null)
        {
            model.Id = existing.Id;
            var updated = await _bolusRepository.UpdateAsync(existing.Id, model, ct);
            result.UpdatedRecords.Add(updated);
            _logger.LogDebug("Updated existing Bolus {Id} from legacy treatment {LegacyId}", existing.Id, treatment.Id);
        }
        else
        {
            var created = await _bolusRepository.CreateAsync(model, ct);
            result.CreatedRecords.Add(created);
            _logger.LogDebug("Created Bolus from legacy treatment {LegacyId}", treatment.Id);
        }
    }

    private async Task DecomposeMicroBolusAsync(Treatment treatment, V4Models.DecompositionResult result, CancellationToken ct)
    {
        var existing = treatment.Id != null
            ? await _bolusRepository.GetByLegacyIdAsync(treatment.Id, ct)
            : null;

        var model = MapToBolus(treatment, result.CorrelationId);
        model.Kind = V4Models.BolusKind.Algorithm;
        model.Automatic = true;
        model.DeviceId = await _deviceService.ResolveAsync(
            V4Models.DeviceCategory.InsulinPump, treatment.PumpType, treatment.PumpSerial, treatment.Mills, ct);
        model.PatientDeviceId = await _deviceService.ResolvePatientDeviceAsync(model.DeviceId, treatment.Mills, ct);

        if (existing != null)
        {
            model.Id = existing.Id;
            var updated = await _bolusRepository.UpdateAsync(existing.Id, model, ct);
            result.UpdatedRecords.Add(updated);
            _logger.LogDebug("Updated existing algorithm Bolus {Id} from legacy treatment {LegacyId}", existing.Id, treatment.Id);
        }
        else
        {
            var created = await _bolusRepository.CreateAsync(model, ct);
            result.CreatedRecords.Add(created);
            _logger.LogDebug("Created algorithm Bolus from legacy treatment {LegacyId}", treatment.Id);
        }
    }

    private async Task DecomposeCarbIntakeAsync(Treatment treatment, V4Models.DecompositionResult result, CancellationToken ct)
    {
        var existing = treatment.Id != null
            ? await _carbIntakeRepository.GetByLegacyIdAsync(treatment.Id, ct)
            : null;

        var model = MapToCarbIntake(treatment, result.CorrelationId);

        Guid carbIntakeId;
        if (existing != null)
        {
            model.Id = existing.Id;
            var updated = await _carbIntakeRepository.UpdateAsync(existing.Id, model, ct);
            result.UpdatedRecords.Add(updated);
            carbIntakeId = existing.Id;
            _logger.LogDebug("Updated existing CarbIntake {Id} from legacy treatment {LegacyId}", existing.Id, treatment.Id);
        }
        else
        {
            var created = await _carbIntakeRepository.CreateAsync(model, ct);
            result.CreatedRecords.Add(created);
            carbIntakeId = created.Id;
            _logger.LogDebug("Created CarbIntake from legacy treatment {LegacyId}", treatment.Id);

            // Preserve legacy FoodType as a TreatmentFood entry (log without saving)
            if (!string.IsNullOrWhiteSpace(treatment.FoodType) && treatment.Carbs is > 0)
            {
                await _treatmentFoodService.AddAsync(new TreatmentFood
                {
                    CarbIntakeId = carbIntakeId,
                    Portions = 0m,
                    Carbs = (decimal)treatment.Carbs.Value,
                    TimeOffsetMinutes = 0,
                    Note = treatment.FoodType,
                }, ct);
            }
        }
    }

    private async Task DecomposeBGCheckAsync(Treatment treatment, V4Models.DecompositionResult result, CancellationToken ct)
    {
        var existing = treatment.Id != null
            ? await _bgCheckRepository.GetByLegacyIdAsync(treatment.Id, ct)
            : null;

        var model = MapToBGCheck(treatment, result.CorrelationId);

        if (existing != null)
        {
            model.Id = existing.Id;
            var updated = await _bgCheckRepository.UpdateAsync(existing.Id, model, ct);
            result.UpdatedRecords.Add(updated);
            _logger.LogDebug("Updated existing BGCheck {Id} from legacy treatment {LegacyId}", existing.Id, treatment.Id);
        }
        else
        {
            var created = await _bgCheckRepository.CreateAsync(model, ct);
            result.CreatedRecords.Add(created);
            _logger.LogDebug("Created BGCheck from legacy treatment {LegacyId}", treatment.Id);
        }
    }

    private async Task DecomposeNoteAsync(Treatment treatment, V4Models.DecompositionResult result, bool isAnnouncement, CancellationToken ct)
    {
        var existing = treatment.Id != null
            ? await _noteRepository.GetByLegacyIdAsync(treatment.Id, ct)
            : null;

        var model = MapToNote(treatment, result.CorrelationId, isAnnouncement);

        if (existing != null)
        {
            model.Id = existing.Id;
            var updated = await _noteRepository.UpdateAsync(existing.Id, model, ct);
            result.UpdatedRecords.Add(updated);
            _logger.LogDebug("Updated existing Note {Id} from legacy treatment {LegacyId}", existing.Id, treatment.Id);
        }
        else
        {
            var created = await _noteRepository.CreateAsync(model, ct);
            result.CreatedRecords.Add(created);
            _logger.LogDebug("Created Note from legacy treatment {LegacyId}", treatment.Id);
        }
    }

    private async Task DecomposeDeviceEventAsync(Treatment treatment, V4Models.DecompositionResult result, DeviceEventType deviceEventType, CancellationToken ct)
    {
        var existing = treatment.Id != null
            ? await _deviceEventRepository.GetByLegacyIdAsync(treatment.Id, ct)
            : null;

        var model = MapToDeviceEvent(treatment, result.CorrelationId, deviceEventType);
        model.DeviceId = await _deviceService.ResolveAsync(
            V4Models.DeviceCategory.InsulinPump, treatment.PumpType, treatment.PumpSerial, treatment.Mills, ct);
        model.PatientDeviceId = await _deviceService.ResolvePatientDeviceAsync(model.DeviceId, treatment.Mills, ct);

        if (existing != null)
        {
            model.Id = existing.Id;
            var updated = await _deviceEventRepository.UpdateAsync(existing.Id, model, ct);
            result.UpdatedRecords.Add(updated);
            _logger.LogDebug("Updated existing DeviceEvent {Id} from legacy treatment {LegacyId}", existing.Id, treatment.Id);
        }
        else
        {
            var created = await _deviceEventRepository.CreateAsync(model, ct);
            result.CreatedRecords.Add(created);
            _logger.LogDebug("Created DeviceEvent from legacy treatment {LegacyId}", treatment.Id);
        }
    }

    private async Task DecomposeBolusCalculationAsync(Treatment treatment, V4Models.DecompositionResult result, CancellationToken ct)
    {
        var existing = treatment.Id != null
            ? await _bolusCalculationRepository.GetByLegacyIdAsync(treatment.Id, ct)
            : null;

        var model = MapToBolusCalculation(treatment, result.CorrelationId);

        if (existing != null)
        {
            model.Id = existing.Id;
            var updated = await _bolusCalculationRepository.UpdateAsync(existing.Id, model, ct);
            result.UpdatedRecords.Add(updated);
            _logger.LogDebug("Updated existing BolusCalculation {Id} from legacy treatment {LegacyId}", existing.Id, treatment.Id);
        }
        else
        {
            var created = await _bolusCalculationRepository.CreateAsync(model, ct);
            result.CreatedRecords.Add(created);
            _logger.LogDebug("Created BolusCalculation from legacy treatment {LegacyId}", treatment.Id);
        }
    }

    private async Task DecomposeTempBasalAsync(Treatment treatment, V4Models.DecompositionResult result, CancellationToken ct)
    {
        var existing = treatment.Id != null
            ? await _tempBasalRepository.GetByLegacyIdAsync(treatment.Id, ct)
            : null;

        var model = MapToTempBasal(treatment, result.CorrelationId);
        model.DeviceId = await _deviceService.ResolveAsync(
            V4Models.DeviceCategory.InsulinPump, treatment.PumpType, treatment.PumpSerial, treatment.Mills, ct);
        model.PatientDeviceId = await _deviceService.ResolvePatientDeviceAsync(model.DeviceId, treatment.Mills, ct);

        if (existing != null)
        {
            model.Id = existing.Id;
            var updated = await _tempBasalRepository.UpdateAsync(existing.Id, model, ct);
            result.UpdatedRecords.Add(updated);
            _logger.LogDebug("Updated existing TempBasal {Id} from legacy treatment {LegacyId}", existing.Id, treatment.Id);
        }
        else
        {
            var created = await _tempBasalRepository.CreateAsync(model, ct);
            result.CreatedRecords.Add(created);
            _logger.LogDebug("Created TempBasal from legacy treatment {LegacyId}", treatment.Id);
        }
    }

    private async Task DecomposeProfileSwitchAsync(Treatment treatment, V4Models.DecompositionResult result, CancellationToken ct)
    {
        var stateSpan = new StateSpan
        {
            Category = StateSpanCategory.Profile,
            State = ProfileState.Active.ToString(),
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).UtcDateTime,
            EndTimestamp = treatment.Duration is > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills + (long)(treatment.Duration.Value * 60 * 1000)).UtcDateTime
                : null,
            Source = treatment.DataSource ?? treatment.EnteredBy ?? "nightscout",
            OriginalId = treatment.Id,
            Metadata = BuildProfileMetadata(treatment)
        };

        var upserted = await _stateSpanService.UpsertStateSpanAsync(stateSpan, ct);
        result.CreatedRecords.Add(upserted);
        _logger.LogDebug("Delegated ProfileSwitch treatment {LegacyId} to IStateSpanService", treatment.Id);

        // If the treatment carries inline profile JSON, decompose it into V4 schedule records
        if (!string.IsNullOrEmpty(treatment.ProfileJson))
        {
            try
            {
                var profileData = JsonSerializer.Deserialize<ProfileData>(treatment.ProfileJson);
                if (profileData != null)
                {
                    var syntheticStoreName = $"{treatment.Profile ?? "Default"}@@@@@{treatment.Mills}";
                    var syntheticProfile = new Profile
                    {
                        Id = treatment.Id,
                        Mills = treatment.Mills,
                        DefaultProfile = syntheticStoreName,
                        EnteredBy = treatment.EnteredBy,
                        Store = { [syntheticStoreName] = profileData }
                    };

                    var profileResult = await _profileDecomposer.DecomposeAsync(syntheticProfile, ct);
                    result.CreatedRecords.AddRange(profileResult.CreatedRecords);
                    result.UpdatedRecords.AddRange(profileResult.UpdatedRecords);

                    _logger.LogDebug(
                        "Decomposed inline ProfileJson from treatment {LegacyId} into {Count} V4 records",
                        treatment.Id,
                        profileResult.CreatedRecords.Count + profileResult.UpdatedRecords.Count);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "Failed to deserialize ProfileJson from treatment {LegacyId}, skipping profile decomposition",
                    treatment.Id);
            }
        }
    }

    private async Task DecomposeOverrideAsync(Treatment treatment, V4Models.DecompositionResult result, CancellationToken ct)
    {
        var stateSpan = new StateSpan
        {
            Category = StateSpanCategory.Override,
            State = OverrideState.Custom.ToString(),
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).UtcDateTime,
            EndTimestamp = treatment.Duration is > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills + (long)(treatment.Duration.Value * 60 * 1000)).UtcDateTime
                : null,
            Source = treatment.DataSource ?? treatment.EnteredBy ?? "nightscout",
            OriginalId = treatment.Id,
            Metadata = BuildOverrideMetadata(treatment)
        };

        var upserted = await _stateSpanService.UpsertStateSpanAsync(stateSpan, ct);
        result.CreatedRecords.Add(upserted);
        _logger.LogDebug("Delegated Temporary Override treatment {LegacyId} to IStateSpanService", treatment.Id);
    }

    private async Task DecomposeTemporaryTargetAsync(Treatment treatment, V4Models.DecompositionResult result, CancellationToken ct)
    {
        var isCancelled = treatment.Duration is null or 0
            || string.Equals(treatment.EventType, "Temporary Target Cancel", StringComparison.OrdinalIgnoreCase);

        var stateSpan = new StateSpan
        {
            Category = StateSpanCategory.TemporaryTarget,
            State = isCancelled
                ? TemporaryTargetState.Cancelled.ToString()
                : TemporaryTargetState.Active.ToString(),
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).UtcDateTime,
            EndTimestamp = !isCancelled && treatment.Duration is > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills + (long)(treatment.Duration.Value * 60 * 1000)).UtcDateTime
                : null,
            Source = treatment.DataSource ?? treatment.EnteredBy ?? "nightscout",
            OriginalId = treatment.Id,
            Metadata = BuildTemporaryTargetMetadata(treatment)
        };

        var upserted = await _stateSpanService.UpsertStateSpanAsync(stateSpan, ct);
        result.CreatedRecords.Add(upserted);
        _logger.LogDebug("Delegated Temporary Target treatment {LegacyId} to IStateSpanService", treatment.Id);
    }

    #endregion

    #region Mapping Methods

    internal static V4Models.TempBasal MapToTempBasal(Treatment treatment, Guid? correlationId)
    {
        var startTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).UtcDateTime;
        var durationMs = (treatment.DurationInMilliseconds ?? (long?)((treatment.Duration ?? 0) * 60 * 1000)) ?? 0;

        return new V4Models.TempBasal
        {
            Id = Guid.CreateVersion7(),
            LegacyId = treatment.Id,
            StartTimestamp = startTimestamp,
            EndTimestamp = durationMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills + durationMs).UtcDateTime : null,
            UtcOffset = treatment.UtcOffset,
            Device = treatment.EnteredBy,
            App = treatment.EnteredBy,
            DataSource = treatment.DataSource,
            CorrelationId = correlationId,
            Rate = treatment.Absolute ?? treatment.Rate ?? 0,
            ScheduledRate = null, // Not available from legacy treatments
            Origin = V4Models.TempBasalOrigin.Manual, // v1/v3 treatments default to Manual
            PumpRecordId = treatment.PumpId?.ToString(),
        };
    }

    internal static V4Models.Bolus MapToBolus(Treatment treatment, Guid? correlationId)
    {
        return new V4Models.Bolus
        {
            LegacyId = treatment.Id,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).UtcDateTime,
            Insulin = treatment.Insulin ?? 0,
            Programmed = treatment.Programmed,
            Delivered = treatment.InsulinDelivered,
            BolusType = ParseBolusType(treatment.BolusType),
            Automatic = treatment.Automatic ?? false,
            Duration = treatment.Duration,
            Device = treatment.EnteredBy,
            DataSource = treatment.DataSource,
            UtcOffset = treatment.UtcOffset,
            CorrelationId = correlationId,
            SyncIdentifier = treatment.SyncIdentifier,
            InsulinType = treatment.InsulinType,
            Unabsorbed = treatment.Unabsorbed,
            DeviceId = null, // Resolved by caller via IDeviceService
            PumpRecordId = treatment.PumpId?.ToString(),
        };
    }

    internal static V4Models.CarbIntake MapToCarbIntake(Treatment treatment, Guid? correlationId)
    {
        return new V4Models.CarbIntake
        {
            LegacyId = treatment.Id,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).UtcDateTime,
            Carbs = treatment.Carbs ?? 0,
            Device = treatment.EnteredBy,
            DataSource = treatment.DataSource,
            UtcOffset = treatment.UtcOffset,
            CorrelationId = correlationId,
            SyncIdentifier = treatment.SyncIdentifier,
            CarbTime = treatment.CarbTime,
            AbsorptionTime = treatment.AbsorptionTime,
        };
    }

    internal static V4Models.BGCheck MapToBGCheck(Treatment treatment, Guid? correlationId)
    {
        return new V4Models.BGCheck
        {
            LegacyId = treatment.Id,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).UtcDateTime,
            Glucose = treatment.Glucose ?? 0,
            GlucoseType = ParseGlucoseType(treatment.GlucoseType),
            Units = ParseGlucoseUnit(treatment.Units),
            Device = treatment.EnteredBy,
            DataSource = treatment.DataSource,
            UtcOffset = treatment.UtcOffset,
            CorrelationId = correlationId,
            SyncIdentifier = treatment.SyncIdentifier,
        };
    }

    internal static V4Models.Note MapToNote(Treatment treatment, Guid? correlationId, bool isAnnouncement)
    {
        return new V4Models.Note
        {
            LegacyId = treatment.Id,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).UtcDateTime,
            Text = treatment.Notes ?? string.Empty,
            EventType = treatment.EventType,
            IsAnnouncement = isAnnouncement || (treatment.IsAnnouncement ?? false),
            Device = treatment.EnteredBy,
            DataSource = treatment.DataSource,
            UtcOffset = treatment.UtcOffset,
            CorrelationId = correlationId,
            SyncIdentifier = treatment.SyncIdentifier,
        };
    }

    internal static V4Models.DeviceEvent MapToDeviceEvent(Treatment treatment, Guid? correlationId, DeviceEventType deviceEventType)
    {
        return new V4Models.DeviceEvent
        {
            LegacyId = treatment.Id,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).UtcDateTime,
            EventType = deviceEventType,
            Notes = treatment.Notes,
            Device = treatment.EnteredBy,
            DataSource = treatment.DataSource,
            UtcOffset = treatment.UtcOffset,
            CorrelationId = correlationId,
            SyncIdentifier = treatment.SyncIdentifier,
        };
    }

    internal static V4Models.BolusCalculation MapToBolusCalculation(Treatment treatment, Guid? correlationId)
    {
        return new V4Models.BolusCalculation
        {
            LegacyId = treatment.Id,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).UtcDateTime,
            BloodGlucoseInput = treatment.BloodGlucoseInput,
            BloodGlucoseInputSource = treatment.BloodGlucoseInputSource,
            CarbInput = treatment.Carbs,
            InsulinOnBoard = treatment.InsulinOnBoard,
            InsulinRecommendation = treatment.InsulinRecommendationForCorrection,
            CarbRatio = treatment.CR,
            CalculationType = MapCalculationType(treatment.CalculationType),
            Device = treatment.EnteredBy,
            DataSource = treatment.DataSource,
            UtcOffset = treatment.UtcOffset,
            CorrelationId = correlationId,
            InsulinRecommendationForCarbs = treatment.InsulinRecommendationForCarbs,
            InsulinProgrammed = treatment.InsulinProgrammed,
            EnteredInsulin = treatment.EnteredInsulin,
            SplitNow = treatment.SplitNow,
            SplitExt = treatment.SplitExt,
            PreBolus = treatment.PreBolus,
        };
    }

    #endregion

    #region Parse Helpers

    internal static V4Models.BolusType? ParseBolusType(string? bolusType)
    {
        if (string.IsNullOrEmpty(bolusType))
            return null;

        return bolusType.ToLowerInvariant() switch
        {
            "normal" => V4Models.BolusType.Normal,
            "square" => V4Models.BolusType.Square,
            "dual" => V4Models.BolusType.Dual,
            _ => Enum.TryParse<V4Models.BolusType>(bolusType, ignoreCase: true, out var parsed) ? parsed : null
        };
    }

    internal static V4Models.GlucoseType? ParseGlucoseType(string? glucoseType)
    {
        if (string.IsNullOrEmpty(glucoseType))
            return null;

        return glucoseType.ToLowerInvariant() switch
        {
            "finger" => V4Models.GlucoseType.Finger,
            "sensor" => V4Models.GlucoseType.Sensor,
            _ => Enum.TryParse<V4Models.GlucoseType>(glucoseType, ignoreCase: true, out var parsed) ? parsed : null
        };
    }

    internal static V4Models.GlucoseUnit? ParseGlucoseUnit(string? units)
    {
        if (string.IsNullOrEmpty(units))
            return null;

        return units.ToLowerInvariant() switch
        {
            "mg/dl" or "mgdl" or "mg" => V4Models.GlucoseUnit.MgDl,
            "mmol" or "mmol/l" => V4Models.GlucoseUnit.Mmol,
            _ => Enum.TryParse<V4Models.GlucoseUnit>(units, ignoreCase: true, out var parsed) ? parsed : null
        };
    }

    internal static V4Models.CalculationType? MapCalculationType(CalculationType? calculationType)
    {
        if (calculationType is null)
            return null;

        return calculationType.Value switch
        {
            CalculationType.Suggested => V4Models.CalculationType.Suggested,
            CalculationType.Manual => V4Models.CalculationType.Manual,
            CalculationType.Automatic => V4Models.CalculationType.Automatic,
            _ => null
        };
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Returns true if the treatment was uploaded by AAPS (AndroidAPS).
    /// AAPS sets "app": "AAPS" on all treatment uploads (NSAndroidClientImpl.kt:296).
    /// </summary>
    internal static bool IsAapsUpload(Treatment treatment)
    {
        if (treatment.AdditionalProperties is null)
            return false;

        if (!treatment.AdditionalProperties.TryGetValue("app", out var appValue))
            return false;

        // System.Text.Json deserializes unknown properties as JsonElement
        var appString = appValue switch
        {
            string s => s,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } je => je.GetString(),
            _ => null
        };

        return string.Equals(appString, "AAPS", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTempBasal(string? eventType)
    {
        if (string.IsNullOrEmpty(eventType))
            return false;

        return TempBasalEventTypes.Any(
            t => string.Equals(eventType, t, StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, object>? BuildProfileMetadata(Treatment treatment)
    {
        var metadata = new Dictionary<string, object>();

        if (!string.IsNullOrEmpty(treatment.Profile))
            metadata["profileName"] = treatment.Profile;

        if (!string.IsNullOrEmpty(treatment.ProfileJson))
            metadata["profileJson"] = treatment.ProfileJson;

        if (treatment.Percentage.HasValue)
            metadata["percentage"] = treatment.Percentage.Value;

        if (treatment.Timeshift.HasValue)
            metadata["timeshift"] = treatment.Timeshift.Value;

        if (!string.IsNullOrEmpty(treatment.EnteredBy))
            metadata["enteredBy"] = treatment.EnteredBy;

        metadata["utcOffset"] = treatment.UtcOffset ?? 0;

        return metadata.Count > 0 ? metadata : null;
    }

    private static Dictionary<string, object>? BuildOverrideMetadata(Treatment treatment)
    {
        var metadata = new Dictionary<string, object>();

        if (!string.IsNullOrEmpty(treatment.Reason))
            metadata["reason"] = treatment.Reason;

        if (!string.IsNullOrEmpty(treatment.ReasonDisplay))
            metadata["reasonDisplay"] = treatment.ReasonDisplay;

        if (treatment.TargetTop.HasValue)
            metadata["targetTop"] = treatment.TargetTop.Value;

        if (treatment.TargetBottom.HasValue)
            metadata["targetBottom"] = treatment.TargetBottom.Value;

        if (treatment.InsulinNeedsScaleFactor.HasValue)
            metadata["insulinNeedsScaleFactor"] = treatment.InsulinNeedsScaleFactor.Value;

        if (!string.IsNullOrEmpty(treatment.DurationType))
            metadata["durationType"] = treatment.DurationType;

        if (!string.IsNullOrEmpty(treatment.EnteredBy))
            metadata["enteredBy"] = treatment.EnteredBy;

        metadata["utcOffset"] = treatment.UtcOffset ?? 0;

        return metadata.Count > 0 ? metadata : null;
    }

    private static Dictionary<string, object>? BuildTemporaryTargetMetadata(Treatment treatment)
    {
        var metadata = new Dictionary<string, object>();

        if (treatment.TargetTop.HasValue)
            metadata["targetTop"] = treatment.TargetTop.Value;

        if (treatment.TargetBottom.HasValue)
            metadata["targetBottom"] = treatment.TargetBottom.Value;

        if (!string.IsNullOrEmpty(treatment.Reason))
            metadata["reason"] = treatment.Reason;

        if (!string.IsNullOrEmpty(treatment.Units))
            metadata["units"] = treatment.Units;

        if (!string.IsNullOrEmpty(treatment.EnteredBy))
            metadata["enteredBy"] = treatment.EnteredBy;

        metadata["utcOffset"] = treatment.UtcOffset ?? 0;

        return metadata.Count > 0 ? metadata : null;
    }

    #endregion

    /// <inheritdoc />
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, CancellationToken ct = default)
    {
        var deleted = 0;
        deleted += await _bolusRepository.DeleteByLegacyIdAsync(legacyId, ct);
        deleted += await _tempBasalRepository.DeleteByLegacyIdAsync(legacyId, ct);
        deleted += await _carbIntakeRepository.DeleteByLegacyIdAsync(legacyId, ct);
        deleted += await _bgCheckRepository.DeleteByLegacyIdAsync(legacyId, ct);
        deleted += await _noteRepository.DeleteByLegacyIdAsync(legacyId, ct);
        deleted += await _deviceEventRepository.DeleteByLegacyIdAsync(legacyId, ct);
        deleted += await _bolusCalculationRepository.DeleteByLegacyIdAsync(legacyId, ct);

        if (deleted > 0)
            _logger.LogDebug("Deleted {Count} v4 records for legacy treatment {LegacyId}", deleted, legacyId);

        return deleted;
    }

    /// <inheritdoc />
    public async Task<long> BulkDeleteAsync(string? find, CancellationToken ct = default)
    {
        var (fromMills, toMills) = Core.Models.Entries.EntryDomainLogic.ParseTimeRangeFromFind(find);

        // Reject implausible timestamps that clearly aren't time bounds
        // (e.g. {"carbs":{"$gte":45}} would parse from=45)
        const long MinPlausibleMills = 946684800000L; // 2000-01-01T00:00:00Z
        if (fromMills.HasValue && fromMills.Value < MinPlausibleMills)
            fromMills = null;
        if (toMills.HasValue && toMills.Value < MinPlausibleMills)
            toMills = null;

        var hasFind = !string.IsNullOrEmpty(find) && find != "{}";
        var hasTimeBounds = fromMills.HasValue || toMills.HasValue;

        if (hasFind && !hasTimeBounds)
        {
            _logger.LogWarning("BulkDelete refused: find query has no parseable time range. find={Find}", find);
            return 0;
        }

        DateTime? from = fromMills.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(fromMills.Value).UtcDateTime
            : null;
        DateTime? to = toMills.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(toMills.Value).UtcDateTime
            : null;

        long total = 0;
        total += await DeleteEntitiesByTimeRange(_dbContext.Boluses, from, to, ct);
        total += await DeleteEntitiesByTimeRange(_dbContext.CarbIntakes, from, to, ct);
        total += await DeleteEntitiesByTimeRange(_dbContext.BGChecks, from, to, ct);
        total += await DeleteEntitiesByTimeRange(_dbContext.Notes, from, to, ct);
        total += await DeleteEntitiesByTimeRange(_dbContext.DeviceEvents, from, to, ct);
        total += await DeleteEntitiesByTimeRange(_dbContext.BolusCalculations, from, to, ct);
        total += await DeleteEntitiesByTimeRange(_dbContext.TempBasals, from, to, ct);

        _logger.LogInformation("BulkDelete: removed {Total} v4 treatment records for find={Find}", total, find);
        return total;
    }

    private static async Task<int> DeleteEntitiesByTimeRange<T>(
        Microsoft.EntityFrameworkCore.DbSet<T> dbSet, DateTime? from, DateTime? to, CancellationToken ct)
        where T : class
    {
        var query = dbSet.AsQueryable();

        // All V4 entity types have a Timestamp column (point-in-time) or StartTimestamp (span-based).
        // Use the dynamic interface approach: filter via the entity's timestamp property.
        if (from.HasValue || to.HasValue)
        {
            // Use ExecuteDeleteAsync with raw filtering — entities all have Timestamp or StartTimestamp
            // mapped as the primary time column. We filter through the queryable.
            if (typeof(T).GetProperty("Timestamp") != null)
            {
                var param = System.Linq.Expressions.Expression.Parameter(typeof(T), "e");
                var timestampProp = System.Linq.Expressions.Expression.Property(param, "Timestamp");

                if (from.HasValue)
                {
                    var fromExpr = System.Linq.Expressions.Expression.Constant(from.Value, typeof(DateTime));
                    var gte = System.Linq.Expressions.Expression.GreaterThanOrEqual(timestampProp, fromExpr);
                    var lambda = System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(gte, param);
                    query = query.Where(lambda);
                }
                if (to.HasValue)
                {
                    var toExpr = System.Linq.Expressions.Expression.Constant(to.Value, typeof(DateTime));
                    var lte = System.Linq.Expressions.Expression.LessThanOrEqual(timestampProp, toExpr);
                    var lambda = System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(lte, param);
                    query = query.Where(lambda);
                }
            }
            else if (typeof(T).GetProperty("StartTimestamp") != null)
            {
                var param = System.Linq.Expressions.Expression.Parameter(typeof(T), "e");
                var timestampProp = System.Linq.Expressions.Expression.Property(param, "StartTimestamp");

                if (from.HasValue)
                {
                    var fromExpr = System.Linq.Expressions.Expression.Constant(from.Value, typeof(DateTime));
                    var gte = System.Linq.Expressions.Expression.GreaterThanOrEqual(timestampProp, fromExpr);
                    var lambda = System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(gte, param);
                    query = query.Where(lambda);
                }
                if (to.HasValue)
                {
                    var toExpr = System.Linq.Expressions.Expression.Constant(to.Value, typeof(DateTime));
                    var lte = System.Linq.Expressions.Expression.LessThanOrEqual(timestampProp, toExpr);
                    var lambda = System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(lte, param);
                    query = query.Where(lambda);
                }
            }
        }

        return await query.ExecuteDeleteAsync(ct);
    }
}

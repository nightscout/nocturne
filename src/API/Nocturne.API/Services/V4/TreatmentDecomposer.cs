using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Constants;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Repositories.V4;

using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.V4;

/// <summary>
/// Decomposes legacy Treatment records into v4 granular models.
/// Maps EventType to the appropriate v4 model(s) and delegates StateSpan-backed
/// types (TempBasal, ProfileSwitch) to IStateSpanService.
/// Supports idempotent create-or-update via LegacyId matching.
/// </summary>
public class TreatmentDecomposer : ITreatmentDecomposer
{
    private readonly BolusRepository _bolusRepository;
    private readonly CarbIntakeRepository _carbIntakeRepository;
    private readonly BGCheckRepository _bgCheckRepository;
    private readonly NoteRepository _noteRepository;
    private readonly DeviceEventRepository _deviceEventRepository;
    private readonly BolusCalculationRepository _bolusCalculationRepository;
    private readonly IStateSpanService _stateSpanService;
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

    public TreatmentDecomposer(
        BolusRepository bolusRepository,
        CarbIntakeRepository carbIntakeRepository,
        BGCheckRepository bgCheckRepository,
        NoteRepository noteRepository,
        DeviceEventRepository deviceEventRepository,
        BolusCalculationRepository bolusCalculationRepository,
        IStateSpanService stateSpanService,
        ILogger<TreatmentDecomposer> logger)
    {
        _bolusRepository = bolusRepository;
        _carbIntakeRepository = carbIntakeRepository;
        _bgCheckRepository = bgCheckRepository;
        _noteRepository = noteRepository;
        _deviceEventRepository = deviceEventRepository;
        _bolusCalculationRepository = bolusCalculationRepository;
        _stateSpanService = stateSpanService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<V4Models.DecompositionResult> DecomposeAsync(Treatment treatment, CancellationToken ct = default)
    {
        var result = new V4Models.DecompositionResult
        {
            CorrelationId = Guid.CreateVersion7()
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
        var existing = treatment.Id != null
            ? await _bolusRepository.GetByLegacyIdAsync(treatment.Id, ct)
            : null;

        var model = MapToBolus(treatment, result.CorrelationId);

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

    private async Task DecomposeCarbIntakeAsync(Treatment treatment, V4Models.DecompositionResult result, CancellationToken ct)
    {
        var existing = treatment.Id != null
            ? await _carbIntakeRepository.GetByLegacyIdAsync(treatment.Id, ct)
            : null;

        var model = MapToCarbIntake(treatment, result.CorrelationId);

        if (existing != null)
        {
            model.Id = existing.Id;
            var updated = await _carbIntakeRepository.UpdateAsync(existing.Id, model, ct);
            result.UpdatedRecords.Add(updated);
            _logger.LogDebug("Updated existing CarbIntake {Id} from legacy treatment {LegacyId}", existing.Id, treatment.Id);
        }
        else
        {
            var created = await _carbIntakeRepository.CreateAsync(model, ct);
            result.CreatedRecords.Add(created);
            _logger.LogDebug("Created CarbIntake from legacy treatment {LegacyId}", treatment.Id);
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
        var stateSpan = await _stateSpanService.CreateBasalDeliveryFromTreatmentAsync(treatment, ct);
        result.CreatedRecords.Add(stateSpan);
        _logger.LogDebug("Delegated TempBasal treatment {LegacyId} to IStateSpanService", treatment.Id);
    }

    private async Task DecomposeProfileSwitchAsync(Treatment treatment, V4Models.DecompositionResult result, CancellationToken ct)
    {
        var stateSpan = new StateSpan
        {
            Category = StateSpanCategory.Profile,
            State = ProfileState.Active.ToString(),
            StartMills = treatment.Mills,
            EndMills = treatment.Duration is > 0
                ? treatment.Mills + (long)(treatment.Duration.Value * 60 * 1000)
                : null,
            Source = treatment.DataSource ?? treatment.EnteredBy ?? "nightscout",
            OriginalId = treatment.Id,
            Metadata = BuildProfileMetadata(treatment)
        };

        var upserted = await _stateSpanService.UpsertStateSpanAsync(stateSpan, ct);
        result.CreatedRecords.Add(upserted);
        _logger.LogDebug("Delegated ProfileSwitch treatment {LegacyId} to IStateSpanService", treatment.Id);
    }

    private async Task DecomposeOverrideAsync(Treatment treatment, V4Models.DecompositionResult result, CancellationToken ct)
    {
        var stateSpan = new StateSpan
        {
            Category = StateSpanCategory.Override,
            State = OverrideState.Custom.ToString(),
            StartMills = treatment.Mills,
            EndMills = treatment.Duration is > 0
                ? treatment.Mills + (long)(treatment.Duration.Value * 60 * 1000)
                : null,
            Source = treatment.DataSource ?? treatment.EnteredBy ?? "nightscout",
            OriginalId = treatment.Id,
            Metadata = BuildOverrideMetadata(treatment)
        };

        var upserted = await _stateSpanService.UpsertStateSpanAsync(stateSpan, ct);
        result.CreatedRecords.Add(upserted);
        _logger.LogDebug("Delegated Temporary Override treatment {LegacyId} to IStateSpanService", treatment.Id);
    }

    #endregion

    #region Mapping Methods

    internal static V4Models.Bolus MapToBolus(Treatment treatment, Guid? correlationId)
    {
        return new V4Models.Bolus
        {
            LegacyId = treatment.Id,
            Mills = treatment.Mills,
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
            IsBasalInsulin = treatment.IsBasalInsulin ?? false,
            PumpId = treatment.PumpId?.ToString(),
            PumpSerial = treatment.PumpSerial,
            PumpType = treatment.PumpType,
        };
    }

    internal static V4Models.CarbIntake MapToCarbIntake(Treatment treatment, Guid? correlationId)
    {
        return new V4Models.CarbIntake
        {
            LegacyId = treatment.Id,
            Mills = treatment.Mills,
            Carbs = treatment.Carbs ?? 0,
            Protein = treatment.Protein,
            Fat = treatment.Fat,
            FoodType = treatment.FoodType,
            AbsorptionTime = treatment.AbsorptionTime,
            Device = treatment.EnteredBy,
            DataSource = treatment.DataSource,
            UtcOffset = treatment.UtcOffset,
            CorrelationId = correlationId,
            SyncIdentifier = treatment.SyncIdentifier,
            CarbTime = treatment.CarbTime,
        };
    }

    internal static V4Models.BGCheck MapToBGCheck(Treatment treatment, Guid? correlationId)
    {
        return new V4Models.BGCheck
        {
            LegacyId = treatment.Id,
            Mills = treatment.Mills,
            Glucose = treatment.Glucose ?? 0,
            GlucoseType = ParseGlucoseType(treatment.GlucoseType),
            Mgdl = treatment.Mgdl ?? treatment.Glucose ?? 0,
            Mmol = treatment.Mmol,
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
            Mills = treatment.Mills,
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
            Mills = treatment.Mills,
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
            Mills = treatment.Mills,
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

    #endregion
}

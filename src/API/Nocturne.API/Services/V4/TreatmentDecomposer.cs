using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Constants;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;

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
public class TreatmentDecomposer : DecomposerBase, ITreatmentDecomposer, IDecomposer<Treatment>
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
    private readonly IPatientDeviceStamper _patientDeviceStamper;
    private readonly IProfileDecomposer _profileDecomposer;
    private readonly IActiveProfileResolver _activeProfileResolver;
    private readonly IPatientInsulinRepository _insulinRepo;
    private readonly IAuditContext _auditContext;
    private readonly IDeduplicationService _deduplicationService;

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
        IPatientDeviceStamper patientDeviceStamper,
        IProfileDecomposer profileDecomposer,
        IActiveProfileResolver activeProfileResolver,
        IPatientInsulinRepository insulinRepo,
        IAuditContext auditContext,
        IDeduplicationService deduplicationService,
        ILogger<TreatmentDecomposer> logger)
        : base(logger)
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
        _patientDeviceStamper = patientDeviceStamper;
        _profileDecomposer = profileDecomposer;
        _activeProfileResolver = activeProfileResolver;
        _insulinRepo = insulinRepo;
        _auditContext = auditContext;
        _deduplicationService = deduplicationService;
    }

    /// <summary>
    /// Establishes the dedup identity for a treatment. Decomposition keys create-or-update on
    /// <see cref="Treatment.Id"/> (persisted as <c>LegacyId</c>), so a treatment with no <c>Id</c>
    /// has nothing to match against and is inserted again on every re-upload, producing duplicate
    /// rows. Identity is resolved in precedence order:
    /// <list type="number">
    ///   <item><description>an explicit Nightscout <c>_id</c> (unchanged);</description></item>
    ///   <item><description>the <c>syncIdentifier</c> sent by LoopKit/NightscoutKit uploaders
    ///   (xDrip4iOS, Trio, Loop) that omit <c>_id</c>;</description></item>
    ///   <item><description>a deterministic synthetic id derived from the event's defining fields
    ///   for fully identifier-less treatments (e.g. xDrip4iOS BG checks).</description></item>
    /// </list>
    /// The synthetic id keys on the exact event time, so re-uploads of one logical event collapse
    /// while genuinely distinct events (e.g. two boluses seconds apart) keep separate ids and are
    /// never merged. Requires a resolved <see cref="Treatment.Mills"/> (see the Treatment timestamp
    /// fallback); without one the treatment is left unidentified rather than risk a wrong key.
    /// A lowercase <c>id</c> is deliberately not an identity; see <see cref="TreatmentClientId"/>.
    /// </summary>
    private static void NormalizeIdentity(Treatment treatment)
    {
        if (!string.IsNullOrEmpty(treatment.Id))
            return;

        if (!string.IsNullOrEmpty(treatment.SyncIdentifier))
        {
            treatment.Id = treatment.SyncIdentifier;
            return;
        }

        if (treatment.Mills > 0 && !string.IsNullOrEmpty(treatment.EventType))
        {
            treatment.Id = ComputeSyntheticId(treatment);
        }
    }

    /// <summary>
    /// Computes a deterministic identifier for an identifier-less treatment by hashing its
    /// defining fields. Every field that distinguishes one real event from another is included
    /// (event type, exact time, source, and all dose/value fields), so only byte-identical
    /// re-uploads of the same event collapse — distinct events never share an id.
    /// </summary>
    internal static string ComputeSyntheticId(Treatment t)
    {
        var canonical = string.Join(
            "|",
            t.EventType,
            t.Mills.ToString(CultureInfo.InvariantCulture),
            t.EnteredBy,
            Fmt(t.Insulin),
            Fmt(t.Carbs),
            Fmt(t.Glucose),
            t.GlucoseType,
            Fmt(t.Duration),
            Fmt(t.Absolute),
            Fmt(t.Rate),
            Fmt(t.Percent),
            t.Notes);

        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(canonical));
        // Marker + 60 hex chars keeps the synthetic id compact (64 chars).
        return TreatmentClientId.SyntheticIdPrefix + Convert.ToHexStringLower(hash)[..60];

        static string Fmt(double? value) =>
            value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>
    /// The set of v4 records a treatment decomposes into, plus the state-span sub-kind flags
    /// and the parsed device-event type. Computed once by <see cref="ClassifyTreatment"/> and
    /// consumed by both the single and batch decomposition paths.
    /// </summary>
    private readonly record struct TreatmentClassification(
        bool ProduceBolus,
        bool ProduceCarbIntake,
        bool ProduceBGCheck,
        bool ProduceNote,
        bool ProduceBolusCalc,
        bool ProduceDeviceEvent,
        bool DelegateToStateSpan,
        bool IsProfileSwitch,
        bool IsOverride,
        bool IsTemporaryTarget,
        bool IsAnnouncement,
        DeviceEventType ParsedDeviceEventType)
    {
        /// <summary>
        /// True when no record type was selected and nothing is delegated to a state span —
        /// i.e. the treatment carries no recognized event type and no insulin/carb data.
        /// </summary>
        public bool ProducesNothing =>
            !ProduceBolus && !ProduceCarbIntake && !ProduceBGCheck
            && !ProduceNote && !ProduceBolusCalc && !ProduceDeviceEvent && !DelegateToStateSpan;
    }

    /// <summary>
    /// Classifies which v4 records a legacy <see cref="Treatment"/> decomposes into, based on its
    /// <see cref="Treatment.EventType"/> and the insulin/carb data present. This mapping is shared
    /// by <see cref="DecomposeAsync"/> and <see cref="DecomposeBatchAsync"/> so it lives in exactly
    /// one place.
    /// </summary>
    private static string? SanitizeForLog(string? value)
    {
        return value?
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty);
    }

    private TreatmentClassification ClassifyTreatment(Treatment treatment)
    {
        var eventType = treatment.EventType?.Trim();
        var sanitizedEventTypeForLog = SanitizeForLog(treatment.EventType);
        var hasInsulin = treatment.Insulin is > 0;
        var hasCarbs = treatment.Carbs is > 0;

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
              || string.Equals(eventType, "Snack Bolus", StringComparison.OrdinalIgnoreCase)
              || string.Equals(eventType, "Combo Bolus", StringComparison.OrdinalIgnoreCase))
        {
            produceBolus = true;
            produceCarbIntake = true;
        }
        else if (string.Equals(eventType, "Correction Bolus", StringComparison.OrdinalIgnoreCase)
              || string.Equals(eventType, "SMB", StringComparison.OrdinalIgnoreCase)
              || string.Equals(eventType, "Automatic Bolus", StringComparison.OrdinalIgnoreCase))
        {
            produceBolus = true;
        }
        else if (string.Equals(eventType, "Bolus", StringComparison.OrdinalIgnoreCase)
              || string.Equals(eventType, "External Insulin", StringComparison.OrdinalIgnoreCase))
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
        else if (string.Equals(eventType, "Note", StringComparison.OrdinalIgnoreCase)
              || string.Equals(eventType, "Exercise", StringComparison.OrdinalIgnoreCase))
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

        // Fallback: for unrecognized event types, produce records based on what data is present
        if (!produceBolus && !produceCarbIntake && !produceBGCheck
            && !produceNote && !produceBolusCalc && !produceDeviceEvent && !delegateToStateSpan)
        {
            if (hasInsulin)
                produceBolus = true;
            if (hasCarbs)
                produceCarbIntake = true;

            if (produceBolus || produceCarbIntake)
            {
                Logger.LogInformation(
                    "Unrecognized event type '{EventType}', producing records based on data (insulin={HasInsulin}, carbs={HasCarbs})",
                    sanitizedEventTypeForLog, hasInsulin, hasCarbs);
            }
        }

        // Produce a Note record for any treatment with non-empty Notes,
        // unless we're already producing a Note (avoids duplicate).
        if (!produceNote && !string.IsNullOrWhiteSpace(treatment.Notes))
        {
            produceNote = true;
        }

        var classification = new TreatmentClassification(
            produceBolus, produceCarbIntake, produceBGCheck, produceNote, produceBolusCalc,
            produceDeviceEvent, delegateToStateSpan, isProfileSwitch, isOverride, isTemporaryTarget,
            isAnnouncement, parsedDeviceEventType);

        return classification;
    }

    /// <summary>
    /// Clears the upstream fingerprint of rows decomposed with no connector publish under way, per
    /// <see cref="UpstreamFingerprintScope"/>.
    /// </summary>
    private static IDisposable? OpenRestatedScope(IEnumerable<Treatment> treatments)
    {
        if (UpstreamFingerprintScope.IsOpen)
            return null;

        var cleared = new Dictionary<(string? Source, string LegacyId), string?>();
        foreach (var treatment in treatments)
        {
            if (treatment.Id is { Length: > 0 } id)
                cleared[(treatment.DataSource, id)] = null;
        }

        return UpstreamFingerprintScope.Open(cleared);
    }

    /// <inheritdoc />
    public async Task<V4Models.DecompositionResult> DecomposeAsync(Treatment treatment, WriteOrigin origin, CancellationToken ct = default)
    {
        NormalizeIdentity(treatment);
        using var restated = OpenRestatedScope([treatment]);

        var result = new V4Models.DecompositionResult
        {
            CorrelationId = Guid.CreateVersion7()
        };

        var c = ClassifyTreatment(treatment);
        if (c.ProducesNothing)
        {
            result.SkippedUnsupported++;
            Logger.LogWarning(
                "Skipped a treatment whose event type Nocturne does not store: {EventType}",
                SanitizeForLog(treatment.EventType));
        }

        // Handle StateSpan delegation
        if (c.DelegateToStateSpan)
        {
            if (c.IsProfileSwitch)
            {
                await DecomposeProfileSwitchAsync(treatment, result, origin, ct);
            }
            else if (c.IsOverride)
            {
                await DecomposeOverrideAsync(treatment, result, origin, ct);
            }
            else if (c.IsTemporaryTarget)
            {
                await DecomposeTemporaryTargetAsync(treatment, result, origin, ct);
            }
            else
            {
                await DecomposeTempBasalAsync(treatment, result, origin, ct);
            }
        }

        // Produce v4 records
        if (c.ProduceBolus)
        {
            await DecomposeBolusAsync(treatment, result, origin, ct);
        }

        if (c.ProduceCarbIntake)
        {
            await DecomposeCarbIntakeAsync(treatment, result, origin, ct);
        }

        if (c.ProduceBGCheck)
        {
            await DecomposeBGCheckAsync(treatment, result, origin, ct);
        }

        if (c.ProduceNote)
        {
            await DecomposeNoteAsync(treatment, result, c.IsAnnouncement, origin, ct);
        }

        if (c.ProduceBolusCalc)
        {
            await DecomposeBolusCalculationAsync(treatment, result, origin, ct);
        }

        if (c.ProduceDeviceEvent)
        {
            await DecomposeDeviceEventAsync(treatment, result, c.ParsedDeviceEventType, origin, ct);

            if (c.ParsedDeviceEventType is DeviceEventType.PumpSuspend or DeviceEventType.PumpResume)
            {
                await DecomposePumpSuspensionFromTreatmentAsync(treatment, c.ParsedDeviceEventType, result, origin, ct);
            }
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
            await _bolusRepository.UpdateAsync(bolus.Id, bolus, origin, ct);
        }

        return result;
    }

    #region Decomposition Methods

    /// <summary>
    /// Whether the dose was delivered by an AID algorithm rather than the user, by the conventions
    /// the uploaders use: the <c>isBasalInsulin</c> flag (legacy AAPS), <c>Correction Bolus</c> from
    /// AAPS (BolusExtension.kt:28), <c>SMB</c> from Trio / iAPS, and <c>Automatic Bolus</c>.
    /// </summary>
    private static bool IsAlgorithmBolus(Treatment treatment) =>
        (treatment.IsBasalInsulin == true && treatment.Insulin > 0)
        || (string.Equals(treatment.EventType, "Correction Bolus", StringComparison.OrdinalIgnoreCase) && IsAapsUpload(treatment))
        || string.Equals(treatment.EventType, "SMB", StringComparison.OrdinalIgnoreCase)
        || string.Equals(treatment.EventType, "Automatic Bolus", StringComparison.OrdinalIgnoreCase);

    /// <summary>The pump named by the upload's pump fields, created in the device registry if new.</summary>
    private Task<Guid?> ResolvePumpDeviceAsync(Treatment treatment, CancellationToken ct) =>
        _deviceService.ResolveAsync(
            V4Models.DeviceCategory.InsulinPump, treatment.PumpType, treatment.PumpSerial, treatment.Mills, ct);

    private async Task<V4Models.Bolus> BuildBolusAsync(Treatment treatment, Guid? correlationId, CancellationToken ct)
    {
        var model = MapToBolus(treatment, correlationId);

        if (IsAlgorithmBolus(treatment))
        {
            model.Kind = V4Models.BolusKind.Algorithm;
            model.Automatic = true;
        }

        model.DeviceId = await ResolvePumpDeviceAsync(treatment, ct);
        model.PatientDeviceId = await _deviceService.ResolvePatientDeviceAsync(model.DeviceId, treatment.Mills, ct);

        return model;
    }

    private async Task DecomposeBolusAsync(Treatment treatment, V4Models.DecompositionResult result, WriteOrigin origin, CancellationToken ct)
    {
        var model = await BuildBolusAsync(treatment, result.CorrelationId, ct);

        await UpsertByLegacyIdAsync(
            _bolusRepository, treatment.Id, model, result, origin, ct,
            beforeWrite: existing => StampAttributionAsync(
                _patientDeviceStamper, model, existing, V4Models.DeviceAttributionCategories.Bolus, ct));
    }

    /// <summary>
    /// Preserves a legacy <see cref="Treatment.FoodType"/> as the carb intake's one
    /// <see cref="TreatmentFood"/> line. No-op unless the treatment names a food and carries carbs,
    /// and no-op when the carb intake already has any food line.
    /// </summary>
    /// <remarks>
    /// These rows are also the user-editable food-breakdown surface
    /// (<see cref="ITreatmentFoodService"/>, <c>/carbs/{id}/foods</c>), so re-decomposing a
    /// treatment must neither duplicate the line nor overwrite what a user has since attributed to
    /// it. Reaching a stored carb intake is routine rather than exceptional: a create that matches
    /// on the sync key upserts the stored row in place and still reports as created, so a connector
    /// replaying its catch-up overlap window arrives here on every poll. The existence check is
    /// what makes this write idempotent — the "created" signal cannot carry it, and there is no
    /// unique index to lean on because a carb intake legitimately holds many lines once a user has
    /// attributed several foods to it.
    /// </remarks>
    private async Task WriteLegacyFoodLineAsync(Guid carbIntakeId, Treatment treatment, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(treatment.FoodType) || treatment.Carbs is not > 0)
            return;

        var existingLines = await _treatmentFoodService.GetByCarbIntakeIdsAsync([carbIntakeId], ct);
        if (existingLines.Any())
            return;

        await _treatmentFoodService.AddAsync(new TreatmentFood
        {
            CarbIntakeId = carbIntakeId,
            Portions = 0m,
            Carbs = (decimal)treatment.Carbs.Value,
            TimeOffsetMinutes = 0,
            Note = treatment.FoodType,
        }, ct);
    }

    private async Task DecomposeCarbIntakeAsync(Treatment treatment, V4Models.DecompositionResult result, WriteOrigin origin, CancellationToken ct)
    {
        var upserted = await UpsertByLegacyIdAsync(
            _carbIntakeRepository, treatment.Id, MapToCarbIntake(treatment, result.CorrelationId), result, origin, ct);

        if (upserted is ({ } carbIntake, true))
            await WriteLegacyFoodLineAsync(carbIntake.Id, treatment, ct);
    }

    private async Task DecomposeBGCheckAsync(Treatment treatment, V4Models.DecompositionResult result, WriteOrigin origin, CancellationToken ct)
        => await UpsertByLegacyIdAsync(
            _bgCheckRepository, treatment.Id, MapToBGCheck(treatment, result.CorrelationId), result, origin, ct);

    private async Task DecomposeNoteAsync(Treatment treatment, V4Models.DecompositionResult result, bool isAnnouncement, WriteOrigin origin, CancellationToken ct)
        => await UpsertByLegacyIdAsync(
            _noteRepository, treatment.Id, MapToNote(treatment, result.CorrelationId, isAnnouncement), result, origin, ct);

    private async Task<V4Models.DeviceEvent> BuildDeviceEventAsync(
        Treatment treatment, Guid? correlationId, DeviceEventType deviceEventType, CancellationToken ct)
    {
        var model = MapToDeviceEvent(treatment, correlationId, deviceEventType);
        model.DeviceId = await ResolvePumpDeviceAsync(treatment, ct);
        model.PatientDeviceId = await _deviceService.ResolvePatientDeviceAsync(model.DeviceId, treatment.Mills, ct);

        return model;
    }

    private async Task DecomposeDeviceEventAsync(Treatment treatment, V4Models.DecompositionResult result, DeviceEventType deviceEventType, WriteOrigin origin, CancellationToken ct)
    {
        var model = await BuildDeviceEventAsync(treatment, result.CorrelationId, deviceEventType, ct);

        await UpsertByLegacyIdAsync(
            _deviceEventRepository, treatment.Id, model, result, origin, ct,
            beforeWrite: existing => StampAttributionAsync(
                _patientDeviceStamper, model, existing,
                V4Models.DeviceAttributionCategories.DeviceEvent(model.EventType), ct));
    }

    /// <summary>
    /// Opens or closes a <see cref="StateSpanCategory.PumpMode"/> /
    /// <see cref="PumpModeState.Suspended"/> state span when a treatment-sourced
    /// PumpSuspend or PumpResume device event is decomposed.
    /// </summary>
    private async Task DecomposePumpSuspensionFromTreatmentAsync(
        Treatment treatment,
        DeviceEventType deviceEventType,
        V4Models.DecompositionResult result,
        WriteOrigin origin, CancellationToken ct)
    {
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).UtcDateTime;

        if (deviceEventType == DeviceEventType.PumpSuspend)
        {
            var span = new StateSpan
            {
                Category = StateSpanCategory.PumpMode,
                State = PumpModeState.Suspended.ToString(),
                StartTimestamp = timestamp,
                EndTimestamp = null,
                Source = treatment.DataSource ?? treatment.EnteredBy ?? "nightscout",
                OriginalId = $"pump-suspended-tx:{treatment.Id}",
            };

            var upserted = await _stateSpanService.UpsertStateSpanAsync(span, ct);
            result.CreatedRecords.Add(upserted);
            Logger.LogDebug(
                "Opened PumpMode/Suspended StateSpan from treatment {LegacyId}",
                treatment.Id);
        }
        else if (deviceEventType == DeviceEventType.PumpResume)
        {
            var openSpans = await _stateSpanService.GetStateSpansAsync(
                category: StateSpanCategory.PumpMode,
                state: PumpModeState.Suspended.ToString(),
                active: true,
                count: 1,
                descending: true,
                cancellationToken: ct);

            var openSpan = openSpans.FirstOrDefault();
            if (openSpan is null)
            {
                Logger.LogWarning(
                    "PumpResume treatment {LegacyId} but no open PumpMode/Suspended StateSpan to close",
                    treatment.Id);
                return;
            }

            openSpan.EndTimestamp = timestamp;
            var closed = await _stateSpanService.UpsertStateSpanAsync(openSpan, ct);
            result.UpdatedRecords.Add(closed);
            Logger.LogDebug(
                "Closed PumpMode/Suspended StateSpan {SpanId} from treatment {LegacyId}",
                openSpan.Id, treatment.Id);
        }
    }

    private async Task DecomposeBolusCalculationAsync(Treatment treatment, V4Models.DecompositionResult result, WriteOrigin origin, CancellationToken ct)
        => await UpsertByLegacyIdAsync(
            _bolusCalculationRepository, treatment.Id, MapToBolusCalculation(treatment, result.CorrelationId), result, origin, ct);

    /// <summary>
    /// The insulin context is left to the caller: the batch path resolves it against a
    /// batch-local profile-switch timeline the single path cannot see.
    /// </summary>
    private async Task<V4Models.TempBasal> BuildTempBasalAsync(
        Treatment treatment, Guid? correlationId, CancellationToken ct)
    {
        var model = MapToTempBasal(treatment, correlationId);
        model.DeviceId = await ResolvePumpDeviceAsync(treatment, ct);
        model.PatientDeviceId = await _deviceService.ResolvePatientDeviceAsync(model.DeviceId, treatment.Mills, ct);

        return model;
    }

    private static V4Models.TreatmentInsulinContext? ToInsulinContext(V4Models.PatientInsulin? insulin)
        => insulin is null
            ? null
            : new V4Models.TreatmentInsulinContext
            {
                PatientInsulinId = insulin.Id,
                InsulinName = insulin.Name,
                Dia = insulin.Dia,
                Peak = insulin.Peak,
                Curve = insulin.Curve,
                Concentration = insulin.Concentration,
            };

    private async Task DecomposeTempBasalAsync(Treatment treatment, V4Models.DecompositionResult result, WriteOrigin origin, CancellationToken ct)
    {
        var existing = treatment.Id != null
            ? await _tempBasalRepository.GetByLegacyIdAsync(treatment.Id, ct)
            : null;

        var model = await BuildTempBasalAsync(treatment, result.CorrelationId, ct);
        await StampAttributionAsync(
            _patientDeviceStamper, model, existing, V4Models.DeviceAttributionCategories.TempBasal, ct);

        // Resolve insulin context: active profile switch → primary insulin → null
        model.InsulinContext = await _activeProfileResolver.GetActiveInsulinContextAsync(treatment.Mills, ct)
            ?? ToInsulinContext(await _insulinRepo.GetPrimaryBolusInsulinAsync(ct));

        if (existing != null)
        {
            model.Id = existing.Id;
            var updated = await _tempBasalRepository.UpdateAsync(existing.Id, model, origin, ct);
            result.UpdatedRecords.Add(updated);
            Logger.LogDebug("Updated existing TempBasal {Id} from legacy treatment {LegacyId}", existing.Id, treatment.Id);
        }
        else
        {
            var created = await _tempBasalRepository.CreateAsync(model, origin, ct);
            result.CreatedRecords.Add(created);
            Logger.LogDebug("Created TempBasal from legacy treatment {LegacyId}", treatment.Id);
        }
    }

    private async Task DecomposeProfileSwitchAsync(Treatment treatment, V4Models.DecompositionResult result, WriteOrigin origin, CancellationToken ct)
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
        Logger.LogDebug("Delegated ProfileSwitch treatment {LegacyId} to IStateSpanService", treatment.Id);

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

                    var profileResult = await _profileDecomposer.DecomposeAsync(syntheticProfile, origin, ct);
                    result.CreatedRecords.AddRange(profileResult.CreatedRecords);
                    result.UpdatedRecords.AddRange(profileResult.UpdatedRecords);

                    Logger.LogDebug(
                        "Decomposed inline ProfileJson from treatment {LegacyId} into {Count} V4 records",
                        treatment.Id,
                        profileResult.CreatedRecords.Count + profileResult.UpdatedRecords.Count);
                }
            }
            catch (JsonException ex)
            {
                Logger.LogWarning(ex,
                    "Failed to deserialize ProfileJson from treatment {LegacyId}, skipping profile decomposition",
                    treatment.Id);
            }
        }
    }

    private async Task DecomposeOverrideAsync(Treatment treatment, V4Models.DecompositionResult result, WriteOrigin origin, CancellationToken ct)
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
        Logger.LogDebug("Delegated Temporary Override treatment {LegacyId} to IStateSpanService", treatment.Id);
    }

    private async Task DecomposeTemporaryTargetAsync(Treatment treatment, V4Models.DecompositionResult result, WriteOrigin origin, CancellationToken ct)
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
        Logger.LogDebug("Delegated Temporary Target treatment {LegacyId} to IStateSpanService", treatment.Id);
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
            AdditionalProperties = TreatmentClientId.ToRecord(treatment),
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
            AdditionalProperties = TreatmentClientId.ToRecord(treatment),
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
            InsulinContext = ExtractAapsIcfg(treatment),
            DeviceId = null, // Resolved by caller via IDeviceService
            PumpRecordId = treatment.PumpId?.ToString(),
        };
    }

    internal static V4Models.CarbIntake MapToCarbIntake(Treatment treatment, Guid? correlationId)
    {
        return new V4Models.CarbIntake
        {
            LegacyId = treatment.Id,
            AdditionalProperties = TreatmentClientId.ToRecord(treatment),
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
            AdditionalProperties = TreatmentClientId.ToRecord(treatment),
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
            AdditionalProperties = TreatmentClientId.ToRecord(treatment),
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
            AdditionalProperties = TreatmentClientId.ToRecord(treatment),
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
            AdditionalProperties = TreatmentClientId.ToRecord(treatment),
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

    /// <summary>
    /// Extracts AAPS v4 insulin configuration from the <c>icfg</c> JSON field in
    /// <see cref="Treatment.AdditionalProperties"/> and converts it into a
    /// <see cref="V4Models.TreatmentInsulinContext"/>.
    /// </summary>
    /// <returns>
    /// A populated <see cref="V4Models.TreatmentInsulinContext"/> when the treatment carries a
    /// valid <c>icfg</c> object with positive <c>insulinEndTime</c> and <c>insulinPeakTime</c>;
    /// <c>null</c> otherwise.
    /// </returns>
    internal static V4Models.TreatmentInsulinContext? ExtractAapsIcfg(Treatment treatment)
    {
        if (treatment.AdditionalProperties is null
            || !treatment.AdditionalProperties.TryGetValue("icfg", out var icfgRaw))
            return null;

        try
        {
            if (icfgRaw is not JsonElement icfgElement || icfgElement.ValueKind != JsonValueKind.Object)
                return null;

            var label = icfgElement.TryGetProperty("insulinLabel", out var lp) ? lp.GetString() ?? "" : "";
            var endTimeMs = icfgElement.TryGetProperty("insulinEndTime", out var ep) ? ep.GetInt64() : 0L;
            var peakTimeMs = icfgElement.TryGetProperty("insulinPeakTime", out var pp) ? pp.GetInt64() : 0L;
            var concentrationRatio = icfgElement.TryGetProperty("concentration", out var cp) ? cp.GetDouble() : 1.0;

            if (endTimeMs <= 0 || peakTimeMs <= 0)
                return null;

            return new V4Models.TreatmentInsulinContext
            {
                PatientInsulinId = Guid.Empty,
                InsulinName = label,
                Dia = Math.Round(endTimeMs / 3_600_000.0, 1),
                Peak = (int)(peakTimeMs / 60_000),
                Concentration = (int)Math.Round(concentrationRatio * 100),
                Curve = "rapid-acting",
            };
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
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

        var icfg = ExtractAapsIcfg(treatment);
        if (icfg is not null)
        {
            metadata["insulinName"] = icfg.InsulinName;
            metadata["insulinDia"] = icfg.Dia.ToString("F1", CultureInfo.InvariantCulture);
            metadata["insulinPeak"] = icfg.Peak.ToString();
            metadata["insulinConcentration"] = icfg.Concentration.ToString();
            metadata["insulinCurve"] = icfg.Curve;
        }

        return metadata.Count > 0 ? metadata : null;
    }

    private static Dictionary<string, object>? BuildOverrideMetadata(Treatment treatment)
    {
        var metadata = new Dictionary<string, object>
        {
            [StateSpanMetadataExtensions.CollectionKey] = StateSpanMetadataExtensions.TreatmentsCollection,
        };

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
    public async Task<V4Models.DecompositionResult> DecomposeBatchAsync(
        IReadOnlyList<Treatment> treatments, WriteOrigin origin, CancellationToken ct = default)
    {
        if (treatments.Count == 0)
            return new V4Models.DecompositionResult();

        foreach (var treatment in treatments)
            NormalizeIdentity(treatment);
        using var restated = OpenRestatedScope(treatments);

        var correlationId = Guid.CreateVersion7();
        var result = new V4Models.DecompositionResult { CorrelationId = correlationId };

        // Typed collection lists for bulk insert
        var estimatedPerType = Math.Max(1, treatments.Count / 4);
        var bolusList = new List<V4Models.Bolus>(estimatedPerType);
        var carbList = new List<V4Models.CarbIntake>(estimatedPerType);
        var bgCheckList = new List<V4Models.BGCheck>(estimatedPerType);
        var noteList = new List<V4Models.Note>(estimatedPerType);
        var bolusCalcList = new List<V4Models.BolusCalculation>(estimatedPerType);
        var deviceEventList = new List<V4Models.DeviceEvent>(estimatedPerType);
        var tempBasalList = new List<V4Models.TempBasal>(estimatedPerType);

        // State span treatments are upserted individually (idempotent semantics)
        var stateSpanTreatments = new List<(Treatment Treatment, bool IsProfileSwitch, bool IsOverride, bool IsTemporaryTarget)>();

        // Track treatments that produce both bolus AND bolusCalculation for post-insert linking
        var bolusCalcLinkTreatmentIds = new HashSet<string>();

        // Carb-producing treatments by legacy id, for the post-insert TreatmentFood pass
        var foodLineTreatments = new Dictionary<string, Treatment>();

        var pumpSuspendResumeTreatments = new List<(Treatment Treatment, DeviceEventType EventType)>();
        var unsupportedTypes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var treatment in treatments)
        {
            NormalizeIdentity(treatment);

            var c = ClassifyTreatment(treatment);
            if (c.ProducesNothing)
            {
                result.SkippedUnsupported++;
                unsupportedTypes.Add(treatment.EventType ?? "(none)");
            }

            // Collect state span treatments for individual upsert
            if (c.DelegateToStateSpan)
            {
                // TempBasal treatments can also be bulk-inserted
                if (!c.IsProfileSwitch && !c.IsOverride && !c.IsTemporaryTarget)
                {
                    tempBasalList.Add(await BuildTempBasalAsync(treatment, correlationId, ct));
                }
                else
                {
                    stateSpanTreatments.Add((treatment, c.IsProfileSwitch, c.IsOverride, c.IsTemporaryTarget));
                }
            }

            if (c.ProduceBolus)
                bolusList.Add(await BuildBolusAsync(treatment, correlationId, ct));

            if (c.ProduceCarbIntake)
            {
                carbList.Add(MapToCarbIntake(treatment, correlationId));
                // First-wins, matching the bulk write's own keep-first dedup by legacy id, so the
                // line describes the carb intake that was actually inserted.
                if (treatment.Id is { } carbLegacyId)
                    foodLineTreatments.TryAdd(carbLegacyId, treatment);
            }

            if (c.ProduceBGCheck)
                bgCheckList.Add(MapToBGCheck(treatment, correlationId));

            if (c.ProduceNote)
                noteList.Add(MapToNote(treatment, correlationId, c.IsAnnouncement));

            if (c.ProduceBolusCalc)
                bolusCalcList.Add(MapToBolusCalculation(treatment, correlationId));

            if (c.ProduceDeviceEvent)
            {
                deviceEventList.Add(
                    await BuildDeviceEventAsync(treatment, correlationId, c.ParsedDeviceEventType, ct));

                if (c.ParsedDeviceEventType is DeviceEventType.PumpSuspend or DeviceEventType.PumpResume)
                {
                    pumpSuspendResumeTreatments.Add((treatment, c.ParsedDeviceEventType));
                }
            }

            // Track for post-insert linking
            if (c.ProduceBolus && c.ProduceBolusCalc && treatment.Id != null)
                bolusCalcLinkTreatmentIds.Add(treatment.Id);
        }

        if (result.SkippedUnsupported > 0)
        {
            Logger.LogWarning(
                "Skipped {Count} treatments whose event type Nocturne does not store: {EventTypes}",
                result.SkippedUnsupported, string.Join(", ", unsupportedTypes));
        }

        // Fallback attribution for records the serial-based DeviceId resolution left unattributed.
        if (bolusList.Count > 0)
            await _patientDeviceStamper.StampAsync(bolusList, V4Models.DeviceAttributionCategories.Bolus, batchSource: null, ct);
        if (tempBasalList.Count > 0)
            await _patientDeviceStamper.StampAsync(tempBasalList, V4Models.DeviceAttributionCategories.TempBasal, batchSource: null, ct);
        await _patientDeviceStamper.StampDeviceEventsAsync(deviceEventList, batchSource: null, ct);

        // Pre-pass: upsert profile switch StateSpans first (temp basals depend on them for insulin context)
        var batchInsulinTimeline = new SortedDictionary<long, V4Models.TreatmentInsulinContext>();
        foreach (var (treatment, isPs, _, _) in stateSpanTreatments.Where(t => t.IsProfileSwitch))
        {
            var spanResult = new V4Models.DecompositionResult { CorrelationId = correlationId };
            await DecomposeProfileSwitchAsync(treatment, spanResult, origin, ct);
            result.CreatedRecords.AddRange(spanResult.CreatedRecords);
            result.UpdatedRecords.AddRange(spanResult.UpdatedRecords);

            var icfg = ExtractAapsIcfg(treatment);
            if (icfg is not null)
                batchInsulinTimeline[treatment.Mills] = icfg;
        }

        // Resolve insulin context for each temp basal
        // primaryInsulin is fetched at most once lazily if the third tier is ever needed.
        V4Models.PatientInsulin? primaryInsulin = null;
        var primaryInsulinFetched = false;

        foreach (var tb in tempBasalList)
        {
            // Tier 1: batch-local profile switch timeline (avoids cache staleness).
            // Walk the sorted keys in reverse to find the most-recent switch at or before StartMills.
            V4Models.TreatmentInsulinContext? icfg = null;
            var matchingKey = batchInsulinTimeline.Keys
                .Reverse()
                .FirstOrDefault(key => key <= tb.StartMills);
            if (matchingKey != 0 || batchInsulinTimeline.ContainsKey(0))
                icfg = batchInsulinTimeline[matchingKey];

            // Tier 2: ActiveProfileResolver (covers profile switches from previous batches)
            if (icfg is null)
                icfg = await _activeProfileResolver.GetActiveInsulinContextAsync(tb.StartMills, ct);

            // Tier 3: primary configured insulin — fetched once per batch, not per record
            if (icfg is null)
            {
                if (!primaryInsulinFetched)
                {
                    primaryInsulin = await _insulinRepo.GetPrimaryBolusInsulinAsync(ct);
                    primaryInsulinFetched = true;
                }
                icfg = ToInsulinContext(primaryInsulin);
            }

            tb.InsulinContext = icfg;
        }

        using (SystemAttributedBatchWrites(_auditContext))
        {
            await BulkCreateAsync(_bolusRepository, bolusList, result, origin, ct);
            await BulkCreateAsync(_carbIntakeRepository, carbList, result, origin, ct);
            await BulkCreateAsync(_bgCheckRepository, bgCheckList, result, origin, ct);
            await BulkCreateAsync(_noteRepository, noteList, result, origin, ct);
            await BulkCreateAsync(_bolusCalculationRepository, bolusCalcList, result, origin, ct);
            await BulkCreateAsync(_deviceEventRepository, deviceEventList, result, origin, ct);
            await BulkCreateAsync(_tempBasalRepository, tempBasalList, result, origin, ct);
        }

        // Post-insert food pass: the carb intake's id is only known once it is persisted. This set
        // is not create-only — a sync-key upsert of a stored carb intake lands in it too — so the
        // writer, not this loop, is what keeps the line idempotent.
        foreach (var carbIntake in result.CreatedRecords.OfType<V4Models.CarbIntake>())
        {
            if (carbIntake.LegacyId is { } legacyId && foodLineTreatments.TryGetValue(legacyId, out var treatment))
                await WriteLegacyFoodLineAsync(carbIntake.Id, treatment, ct);
        }

        // Post-insert pump suspend/resume pass: sequential, order-dependent
        foreach (var (treatment, eventType) in pumpSuspendResumeTreatments.OrderBy(t => t.Treatment.Mills))
        {
            await DecomposePumpSuspensionFromTreatmentAsync(treatment, eventType, result, origin, ct);
        }

        // Upsert remaining state spans (Override, TemporaryTarget — ProfileSwitch already done in pre-pass)
        foreach (var (treatment, isPs, isOv, isTt) in stateSpanTreatments.Where(t => !t.IsProfileSwitch))
        {
            // Use a temporary result to collect records from helper methods
            var spanResult = new V4Models.DecompositionResult { CorrelationId = correlationId };

            if (isOv)
                await DecomposeOverrideAsync(treatment, spanResult, origin, ct);
            else if (isTt)
                await DecomposeTemporaryTargetAsync(treatment, spanResult, origin, ct);

            result.CreatedRecords.AddRange(spanResult.CreatedRecords);
            result.UpdatedRecords.AddRange(spanResult.UpdatedRecords);
        }

        // Post-insert linking: Bolus → BolusCalculation by matching LegacyId
        if (bolusCalcLinkTreatmentIds.Count > 0)
        {
            var persistedBoluses = result.CreatedRecords.OfType<V4Models.Bolus>()
                .Where(b => b.LegacyId != null && bolusCalcLinkTreatmentIds.Contains(b.LegacyId))
                .ToList();
            var persistedCalcs = result.CreatedRecords.OfType<V4Models.BolusCalculation>()
                .Where(c => c.LegacyId != null && bolusCalcLinkTreatmentIds.Contains(c.LegacyId))
                .ToDictionary(c => c.LegacyId!);

            foreach (var bolus in persistedBoluses)
            {
                if (persistedCalcs.TryGetValue(bolus.LegacyId!, out var calc)
                    && bolus.BolusCalculationId != calc.Id)
                {
                    bolus.BolusCalculationId = calc.Id;
                    await _bolusRepository.UpdateAsync(bolus.Id, bolus, origin, ct);
                }
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, WriteOrigin origin, CancellationToken ct = default)
    {
        // origin is accepted for interface uniformity; the v4-native delete broadcast is deferred to the glucose-unification follow-up (deletes here bypass the repository chokepoint).
        var deleted = 0;
        foreach (var table in DecomposedTables)
            deleted += (await table.SoftDeleteAsync([legacyId], source: null, $"legacy_id={legacyId}", ct)).Count;

        if (deleted > 0)
            Logger.LogDebug("Soft-deleted {Count} v4 records for legacy treatment {LegacyId}", deleted, legacyId);

        return deleted;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The repoint joins the delete's transaction only because the deduplication service shares this
    /// scope's <see cref="NocturneDbContext"/>, so a failed repoint rolls the delete back with it.
    /// </remarks>
    public async Task<int> DeleteFromSourceAsync(
        string source, IReadOnlySet<string> legacyIds, CancellationToken ct = default)
    {
        if (legacyIds.Count == 0)
            return 0;

        var ids = legacyIds.ToArray();
        var scope = $"data_source={source}";

        return await _dbContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);

            var deleted = 0;
            foreach (var table in DecomposedTables)
            {
                var result = await table.SoftDeleteAsync(ids, source, scope, ct);
                deleted += result.Count;
                await _deduplicationService.RepointPrimariesAwayFromAsync(table.RecordType, result.Entities, ct);
            }

            await transaction.CommitAsync(ct);
            return deleted;
        });
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, DateTime>> GetLegacyIdsFromSourceAsync(
        string source, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var stored = new Dictionary<string, DateTime>();
        foreach (var table in DecomposedTables)
        {
            foreach (var row in await table.StoredFromSourceAsync(source, from, to, ct))
                stored.TryAdd(row.LegacyId, row.At);
        }

        return stored;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A connector's rows carry the <see cref="UpstreamFingerprint"/> of the document they were last
    /// written from (<see cref="UpstreamFingerprintScope"/>). A stored treatment is decomposed again
    /// unless every row this source holds under its id carries the current fingerprint. An edit made
    /// in Nocturne therefore survives until the source itself changes the record, and then the
    /// source wins. When none of the rows carries a fingerprint, the record is taken as it stands:
    /// stamped here, not overwritten. That covers rows stored before fingerprints and rows another
    /// uploader restated. A treatment the user deleted stays deleted.
    /// </remarks>
    public async Task<IReadOnlyList<Treatment>> SelectForRepublishAsync(
        string source, IReadOnlyList<Treatment> treatments, CancellationToken ct = default)
    {
        var identified = treatments.Where(t => t.Id is { Length: > 0 }).ToList();
        if (identified.Count == 0)
            return [];

        var (held, deletedByUser) = await GetHeldLegacyIdsAsync(identified.Select(t => t.Id!).ToHashSet(), ct);
        var stamped = new Dictionary<string, List<string?>>();
        var heldIds = held.ToArray();
        foreach (var table in DecomposedTables)
        {
            foreach (var (legacyId, fingerprint) in await table.FingerprintsFromSourceAsync(source, heldIds, ct))
            {
                if (!stamped.TryGetValue(legacyId, out var rowFingerprints))
                    stamped[legacyId] = rowFingerprints = [];
                rowFingerprints.Add(fingerprint);
            }
        }

        var republish = new List<Treatment>();
        var baselines = new Dictionary<string, string>();
        foreach (var treatment in identified)
        {
            var id = treatment.Id!;
            var stored = held.Contains(id);
            if (deletedByUser.Contains(id) || !CanRepublish(treatment, stored))
                continue;

            var fingerprint = UpstreamFingerprint(treatment);
            var prior = stamped.GetValueOrDefault(id) ?? [];
            if (!stored)
                republish.Add(treatment);
            else if (prior.All(f => f is null))
                baselines[id] = fingerprint;
            else if (!prior.All(f => f == fingerprint))
                republish.Add(treatment);
        }

        if (baselines.Count > 0)
        {
            await _dbContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);
                foreach (var table in DecomposedTables)
                    await table.StampUnfingerprintedAsync(source, baselines, ct);
                await transaction.CommitAsync(ct);
            });
        }

        return republish;
    }

    /// <summary>
    /// A stable hash of the fields that define what an upstream treatment says. The field list is
    /// fixed: adding a field re-applies every stored record once, overwriting edits made in Nocturne.
    /// </summary>
    internal static string UpstreamFingerprint(Treatment t)
    {
        var canonical = JsonSerializer.Serialize(new object?[]
        {
            t.EventType, t.CreatedAt, t.Mills, t.EnteredBy, t.Insulin, t.Carbs, t.Protein, t.Fat,
            t.AbsorptionTime, t.Glucose, t.GlucoseType, t.Units, t.Duration, t.Absolute, t.Rate,
            t.Percent, t.Notes, t.Reason, t.TargetTop, t.TargetBottom, t.Profile, t.PreBolus,
            t.SplitNow, t.SplitExt, t.BolusType, t.Automatic, t.InsulinDelivered, t.InsulinProgrammed,
        });

        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// The subset of <paramref name="legacyIds"/> a re-upload would find stored, from any source,
    /// and of those the ones held only by a row the user deleted.
    /// </summary>
    private async Task<(IReadOnlySet<string> Held, IReadOnlySet<string> DeletedByUser)> GetHeldLegacyIdsAsync(
        HashSet<string> legacyIds, CancellationToken ct)
    {
        var rows = new List<(string Key, bool Live)>();
        foreach (var table in DecomposedTables)
            rows.AddRange(await table.BlockingAsync(legacyIds, ct));

        // Profile switches, overrides and temporary targets land as state spans keyed by OriginalId.
        var ids = legacyIds.ToArray();
        rows.AddRange((await _dbContext.StateSpans.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.TenantId == _dbContext.TenantId && s.OriginalId != null && ids.Contains(s.OriginalId))
                .WhereBlocksRecreation()
                .Select(s => new { Key = s.OriginalId!, Live = s.DeletedAt == null })
                .ToListAsync(ct))
            .Select(s => (s.Key, s.Live)));

        var blocks = RecreationBlocks<string>.From(rows);
        return (blocks.Held, blocks.DeletedByUser);
    }

    /// <summary>
    /// Whether decomposing <paramref name="treatment"/> again is safe and worthwhile. A treatment
    /// that stores nothing is not. Nor is a <paramref name="stored"/> one whose type re-derives state
    /// that later writes have moved on. A profile switch, override or temporary target resets its
    /// span's end, and a pump suspend or resume reopens or closes the current suspension.
    /// </summary>
    internal bool CanRepublish(Treatment treatment, bool stored)
    {
        var c = ClassifyTreatment(treatment);
        if (c.ProducesNothing)
            return false;

        var rewritesLaterState = c.IsProfileSwitch || c.IsOverride || c.IsTemporaryTarget
            || (c.ProduceDeviceEvent
                && c.ParsedDeviceEventType is DeviceEventType.PumpSuspend or DeviceEventType.PumpResume);

        return !stored || !rewritesLaterState;
    }

    /// <inheritdoc />
    public async Task<long> BulkDeleteAsync(string? find, WriteOrigin origin, CancellationToken ct = default)
    {
        // origin is accepted for interface uniformity; the v4-native delete broadcast is deferred to the glucose-unification follow-up (deletes here bypass the repository chokepoint).
        var findQuery = Core.Models.Queries.FindQuery.Parse(find);
        var (fromMills, toMills) = (findQuery.FromMills, findQuery.ToMills);

        // find is client-controlled; strip line breaks so it can't forge log entries
        var findForLog = find?.ReplaceLineEndings(" ");

        // This sweep deletes every record type in the window, so it can only honor pure
        // time-range queries. Field-filtered deletes must resolve matches through the filtered
        // read path (TreatmentService.DeleteTreatmentsAsync) — deleting here would wipe
        // non-matching records.
        if (findQuery.HasFieldFilters)
        {
            Logger.LogWarning("BulkDelete refused: find query carries field filters the by-time sweep cannot honor. find={Find}", findForLog);
            return 0;
        }

        var hasFind = !string.IsNullOrEmpty(find) && find != "{}";
        var hasTimeBounds = fromMills.HasValue || toMills.HasValue;

        if (hasFind && !hasTimeBounds)
        {
            Logger.LogWarning("BulkDelete refused: find query has no parseable time range. find={Find}", findForLog);
            return 0;
        }

        DateTime? from = fromMills.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(fromMills.Value).UtcDateTime
            : null;
        DateTime? to = toMills.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(toMills.Value).UtcDateTime
            : null;

        var scope = $"timestamp={from:O}..{to:O}";

        long total = 0;
        foreach (var table in DecomposedTables)
            total += await table.SoftDeleteInRangeAsync(from, to, scope, ct);

        Logger.LogInformation("BulkDelete: removed {Total} v4 treatment records for find={Find}", total, findForLog);
        return total;
    }

    /// <summary>
    /// The tables a legacy treatment decomposes into, each row keyed by the treatment's id as its
    /// <see cref="IV4Entity.LegacyId"/>. Deletes go through the audited soft-delete path: a
    /// user-issued one is attributed, and blocks a later connector resync from re-creating the row
    /// (<see cref="SoftDeleteDedupExtensions"/>).
    /// </summary>
    private IDecomposedTable[] DecomposedTables => _decomposedTables ??=
    [
        Table(RecordType.Bolus, _dbContext.Boluses, ByTimeRange, StoredAt),
        Table(RecordType.TempBasal, _dbContext.TempBasals, SpansByTimeRange,
            rows => rows.Select(e => new StoredRow(e.LegacyId!, e.StartTimestamp))),
        Table(RecordType.CarbIntake, _dbContext.CarbIntakes, ByTimeRange, StoredAt),
        Table(RecordType.BGCheck, _dbContext.BGChecks, ByTimeRange, StoredAt),
        Table(RecordType.Note, _dbContext.Notes, ByTimeRange, StoredAt),
        Table(RecordType.DeviceEvent, _dbContext.DeviceEvents, ByTimeRange, StoredAt),
        Table(RecordType.BolusCalculation, _dbContext.BolusCalculations, ByTimeRange, StoredAt),
    ];

    private IDecomposedTable[]? _decomposedTables;

    private DecomposedTable<T> Table<T>(
        RecordType recordType,
        DbSet<T> rows,
        Func<IQueryable<T>, DateTime?, DateTime?, IQueryable<T>> inRange,
        Func<IQueryable<T>, IQueryable<StoredRow>> storedAt)
        where T : class, IV4Entity, ISourcedEntity, IAuditable, IUpstreamFingerprinted
        => new(_dbContext, _auditContext, recordType, rows, inRange, storedAt);

    private static IQueryable<StoredRow> StoredAt<T>(IQueryable<T> rows) where T : IV4TimeSeriesEntity
        => rows.Select(e => new StoredRow(e.LegacyId!, e.Timestamp));

    private sealed record StoredRow(string LegacyId, DateTime At);

    private interface IDecomposedTable
    {
        RecordType RecordType { get; }

        /// <param name="source">Only this data source's rows, or every source's when null.</param>
        /// <remarks>
        /// A legacy id fans out to a handful of correlated rows, never a set. The per-record audit
        /// rows <see cref="AuditedBulkDeleteExtensions.AuditedSoftDeleteWithEntitiesAsync{T}"/>
        /// writes below its cap are the right shape for that.
        /// </remarks>
        Task<AuditedSoftDeleteResult<Guid>> SoftDeleteAsync(
            string[] legacyIds, string? source, string scope, CancellationToken ct);

        Task<int> SoftDeleteInRangeAsync(DateTime? from, DateTime? to, string scope, CancellationToken ct);

        Task<List<StoredRow>> StoredFromSourceAsync(string source, DateTime from, DateTime to, CancellationToken ct);

        /// <summary>The ids of <paramref name="legacyIds"/> that block re-creation, and whether a live row holds each.</summary>
        Task<IEnumerable<(string Key, bool Live)>> BlockingAsync(HashSet<string> legacyIds, CancellationToken ct);

        Task<List<(string LegacyId, string? Fingerprint)>> FingerprintsFromSourceAsync(
            string source, string[] legacyIds, CancellationToken ct);

        /// <summary>Stamps this source's rows that carry no fingerprint yet.</summary>
        Task StampUnfingerprintedAsync(
            string source, IReadOnlyDictionary<string, string> fingerprints, CancellationToken ct);
    }

    private sealed class DecomposedTable<T>(
        NocturneDbContext context,
        IAuditContext auditContext,
        RecordType recordType,
        DbSet<T> rows,
        Func<IQueryable<T>, DateTime?, DateTime?, IQueryable<T>> inRange,
        Func<IQueryable<T>, IQueryable<StoredRow>> storedAt) : IDecomposedTable
        where T : class, IV4Entity, ISourcedEntity, IAuditable, IUpstreamFingerprinted
    {
        public RecordType RecordType => recordType;

        public Task<AuditedSoftDeleteResult<Guid>> SoftDeleteAsync(
            string[] legacyIds, string? source, string scope, CancellationToken ct)
            => context.AuditedSoftDeleteWithIdsAsync(
                rows.Where(e => e.LegacyId != null && legacyIds.Contains(e.LegacyId)
                             && (source == null || e.DataSource == source)),
                auditContext, scope, ct);

        public Task<int> SoftDeleteInRangeAsync(DateTime? from, DateTime? to, string scope, CancellationToken ct)
            => context.AuditedSoftDeleteAsync(inRange(rows, from, to), auditContext, scope, ct);

        public Task<List<StoredRow>> StoredFromSourceAsync(
            string source, DateTime from, DateTime to, CancellationToken ct)
            => storedAt(inRange(rows.AsNoTracking(), from, to)
                    .Where(e => e.DataSource == source && e.LegacyId != null))
                .ToListAsync(ct);

        public async Task<IEnumerable<(string Key, bool Live)>> BlockingAsync(
            HashSet<string> legacyIds, CancellationToken ct)
        {
            var blocks = await context.GetBlockingLegacyIdsAsync<T>(legacyIds, ct);
            return blocks.Held.Select(id => (id, !blocks.DeletedByUser.Contains(id)));
        }

        public async Task<List<(string LegacyId, string? Fingerprint)>> FingerprintsFromSourceAsync(
            string source, string[] legacyIds, CancellationToken ct)
            => (await FromSource(rows.AsNoTracking(), source, legacyIds)
                    .Select(e => new { LegacyId = e.LegacyId!, e.UpstreamFingerprint })
                    .ToListAsync(ct))
                .Select(e => (e.LegacyId, e.UpstreamFingerprint))
                .ToList();

        public async Task StampUnfingerprintedAsync(
            string source, IReadOnlyDictionary<string, string> fingerprints, CancellationToken ct)
        {
            foreach (var (legacyId, fingerprint) in fingerprints)
            {
                await FromSource(rows, source, [legacyId])
                    .Where(e => e.UpstreamFingerprint == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(e => e.UpstreamFingerprint, fingerprint), ct);
            }
        }

        private static IQueryable<T> FromSource(IQueryable<T> query, string source, string[] legacyIds)
            => query.Where(e => e.DataSource == source && e.LegacyId != null && legacyIds.Contains(e.LegacyId));
    }

    private static IQueryable<T> ByTimeRange<T>(IQueryable<T> rows, DateTime? from, DateTime? to)
        where T : IV4TimeSeriesEntity
    {
        if (from.HasValue)
            rows = rows.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            rows = rows.Where(e => e.Timestamp <= to.Value);
        return rows;
    }

    /// <summary>
    /// <see cref="ByTimeRange{T}"/> for temp basals, which key on
    /// <see cref="TempBasalEntity.StartTimestamp"/> and so stay off <see cref="IV4TimeSeriesEntity"/>.
    /// </summary>
    private static IQueryable<TempBasalEntity> SpansByTimeRange(
        IQueryable<TempBasalEntity> rows, DateTime? from, DateTime? to)
    {
        if (from.HasValue)
            rows = rows.Where(e => e.StartTimestamp >= from.Value);
        if (to.HasValue)
            rows = rows.Where(e => e.StartTimestamp <= to.Value);
        return rows;
    }
}

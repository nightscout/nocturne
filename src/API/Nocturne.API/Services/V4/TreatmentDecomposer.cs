using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.API.Services.ProfileActivations;
using Nocturne.Connectors.Core.Constants;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Serializers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Mappers.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;

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
///   <item><description>Temp Basal → <see cref="V4Models.TempBasal"/></description></item>
///   <item><description>Profile Switch, AAPS effective profile switch → <see cref="V4Models.ProfileActivation"/></description></item>
///   <item><description>Override, Temporary Target → delegated to <see cref="IStateSpanService"/></description></item>
/// </list>
/// Supports idempotent create-or-update via <c>LegacyId</c> matching.
/// </summary>
/// <seealso cref="ITreatmentDecomposer"/>
/// <seealso cref="IDecomposer{T}"/>
/// <seealso cref="IStateSpanService"/>
public class TreatmentDecomposer : DecomposerBase, ITreatmentDecomposer, IDecomposer<Treatment>
{
    private readonly INocturneDbContextFactory _contexts;
    private readonly IBolusRepository _bolusRepository;
    private readonly ITempBasalRepository _tempBasalRepository;
    private readonly ICarbIntakeRepository _carbIntakeRepository;
    private readonly IBGCheckRepository _bgCheckRepository;
    private readonly INoteRepository _noteRepository;
    private readonly IDeviceEventRepository _deviceEventRepository;
    private readonly IDeviceEventReactor? _deviceEventReactor;
    private readonly IBolusCalculationRepository _bolusCalculationRepository;
    private readonly IStateSpanService _stateSpanService;
    private readonly IProfileActivationRepository _profileActivationRepository;
    private readonly ProfileActivationWriter _profileActivationWriter;
    private readonly ITreatmentFoodService _treatmentFoodService;
    private readonly IDeviceService _deviceService;
    private readonly IPatientDeviceStamper _patientDeviceStamper;
    private readonly IProfileDecomposer _profileDecomposer;
    private readonly IActiveProfileResolver _activeProfileResolver;
    private readonly IPatientInsulinRepository _insulinRepo;
    private readonly RelativeTempBasalRates _relativeTempBasalRates;
    private readonly IAuditContext _auditContext;

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
        INocturneDbContextFactory contexts,
        IBolusRepository bolusRepository,
        ITempBasalRepository tempBasalRepository,
        ICarbIntakeRepository carbIntakeRepository,
        IBGCheckRepository bgCheckRepository,
        INoteRepository noteRepository,
        IDeviceEventRepository deviceEventRepository,
        IBolusCalculationRepository bolusCalculationRepository,
        IStateSpanService stateSpanService,
        IProfileActivationRepository profileActivationRepository,
        ProfileActivationWriter profileActivationWriter,
        ITreatmentFoodService treatmentFoodService,
        IDeviceService deviceService,
        IPatientDeviceStamper patientDeviceStamper,
        IProfileDecomposer profileDecomposer,
        IActiveProfileResolver activeProfileResolver,
        IPatientInsulinRepository insulinRepo,
        RelativeTempBasalRates relativeTempBasalRates,
        IAuditContext auditContext,
        ILogger<TreatmentDecomposer> logger,
        IDeviceEventReactor? deviceEventReactor = null)
        : base(logger)
    {
        _deviceEventReactor = deviceEventReactor;
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _bolusRepository = bolusRepository;
        _tempBasalRepository = tempBasalRepository;
        _carbIntakeRepository = carbIntakeRepository;
        _bgCheckRepository = bgCheckRepository;
        _noteRepository = noteRepository;
        _deviceEventRepository = deviceEventRepository;
        _bolusCalculationRepository = bolusCalculationRepository;
        _stateSpanService = stateSpanService;
        _profileActivationRepository = profileActivationRepository;
        _profileActivationWriter = profileActivationWriter;
        _treatmentFoodService = treatmentFoodService;
        _deviceService = deviceService;
        _patientDeviceStamper = patientDeviceStamper;
        _profileDecomposer = profileDecomposer;
        _activeProfileResolver = activeProfileResolver;
        _insulinRepo = insulinRepo;
        _relativeTempBasalRates = relativeTempBasalRates;
        _auditContext = auditContext;
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
    /// <summary>
    /// <see cref="Treatment.Absolute"/> as it read before it stopped reading through
    /// <see cref="Treatment.Rate"/> beside a percent, so ids and fingerprints already stored still match.
    /// </summary>
    private static double? HashedAbsolute(Treatment t) => t.Absolute ?? t.Rate;

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
            Fmt(HashedAbsolute(t)),
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
        DeviceEventType ParsedDeviceEventType,
        bool KeepsUploadedEventType)
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
        else if (string.Equals(eventType, "Profile Switch", StringComparison.OrdinalIgnoreCase)
              || EffectiveProfileSwitch.Is(treatment))
        {
            isProfileSwitch = true;
            delegateToStateSpan = true;
        }
        else if (string.Equals(eventType, "Temporary Override", StringComparison.OrdinalIgnoreCase))
        {
            isOverride = true;
            delegateToStateSpan = true;
        }
        else if (string.Equals(eventType, TemporaryTargetEventType, StringComparison.OrdinalIgnoreCase)
              || string.Equals(eventType, "Temporary Target Cancel", StringComparison.OrdinalIgnoreCase))
        {
            isTemporaryTarget = true;
            delegateToStateSpan = true;
        }
        else if (eventType != null && TreatmentTypes.DeviceEventTypeMap.TryGetValue(eventType, out parsedDeviceEventType))
        {
            produceDeviceEvent = true;
        }
        else if (IsMealEventType(eventType))
        {
            // AAPS uploads one meal as two of these, one carrying only the carbs and one only the
            // insulin (CarbsExtension.kt, BolusExtension.kt); each half decomposes into what it
            // carries, so neither gains a zero-valued twin. A record carrying neither keeps both,
            // so it still round-trips as the Meal Bolus it was sent as.
            produceBolus = hasInsulin || !hasCarbs;
            produceCarbIntake = hasCarbs || !hasInsulin;
        }
        else if (string.Equals(eventType, "Correction Bolus", StringComparison.OrdinalIgnoreCase)
              || string.Equals(eventType, "SMB", StringComparison.OrdinalIgnoreCase)
              || string.Equals(eventType, "Automatic Bolus", StringComparison.OrdinalIgnoreCase)
              || string.Equals(eventType, "Bolus", StringComparison.OrdinalIgnoreCase)
              || string.Equals(eventType, "External Insulin", StringComparison.OrdinalIgnoreCase))
        {
            // As with a meal half, a dose type carrying only carbs is a carb intake, and a Carb
            // Correction carrying only insulin is a bolus, so neither drops what it was uploaded with.
            produceBolus = hasInsulin || !hasCarbs;
            produceCarbIntake = hasCarbs;
        }
        else if (string.Equals(eventType, "Carb Correction", StringComparison.OrdinalIgnoreCase))
        {
            produceBolus = hasInsulin;
            produceCarbIntake = hasCarbs || !hasInsulin;
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

        // A bolus or carb intake beside a record served under the event type (a wizard's
        // calculation, a BG check, a note) is served as it was before the type was kept.
        var keepsUploadedEventType = !produceBGCheck && !produceNote && !produceBolusCalc
            && !produceDeviceEvent && !delegateToStateSpan;

        // Produce a Note record for any treatment with non-empty Notes, unless we're already
        // producing a Note (avoids duplicate). The served state spans and device events keep their
        // notes themselves.
        if (!produceNote && !isProfileSwitch && !isOverride && !isTemporaryTarget && !produceDeviceEvent
            && !string.IsNullOrWhiteSpace(treatment.Notes))
        {
            produceNote = true;
        }

        var classification = new TreatmentClassification(
            produceBolus, produceCarbIntake, produceBGCheck, produceNote, produceBolusCalc,
            produceDeviceEvent, delegateToStateSpan, isProfileSwitch, isOverride, isTemporaryTarget,
            isAnnouncement, parsedDeviceEventType, keepsUploadedEventType);

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

    /// <summary>The tables a treatment decomposes into that carry its legacy id.</summary>
    private StoredCorrelationLookup[] TreatmentTables =>
    [
        _bolusRepository.GetCorrelationIdsByLegacyIdAsync,
        _carbIntakeRepository.GetCorrelationIdsByLegacyIdAsync,
        _bgCheckRepository.GetCorrelationIdsByLegacyIdAsync,
        _noteRepository.GetCorrelationIdsByLegacyIdAsync,
        _bolusCalculationRepository.GetCorrelationIdsByLegacyIdAsync,
        _deviceEventRepository.GetCorrelationIdsByLegacyIdAsync,
        _tempBasalRepository.GetCorrelationIdsByLegacyIdAsync,
        _profileActivationRepository.GetCorrelationIdsByLegacyIdAsync,
    ];

    /// <inheritdoc />
    public async Task<V4Models.DecompositionResult> DecomposeAsync(Treatment treatment, WriteOrigin origin, CancellationToken ct = default)
    {
        NormalizeIdentity(treatment);
        using var restated = OpenRestatedScope([treatment]);

        var result = new V4Models.DecompositionResult
        {
            CorrelationId = CorrelationIdFor(
                treatment.Id, await GetStoredCorrelationIdsAsync([treatment.Id], TreatmentTables, ct))
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

        if ((c.ProduceBolus || c.ProduceCarbIntake) && await IsHeldByTempBasalAsync(treatment, ct))
        {
            result.SkippedUnsupported++;
            return result;
        }

        // Produce v4 records
        if (c.ProduceBolus)
        {
            await DecomposeBolusAsync(treatment, result, c.KeepsUploadedEventType, origin, ct);
        }

        if (c.ProduceCarbIntake)
        {
            await DecomposeCarbIntakeAsync(treatment, result, c.KeepsUploadedEventType, origin, ct);
        }

        await DropAbsentProjectionsAsync([(treatment, c)], origin, ct);

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
    /// AAPS (BolusExtension.kt:28), <c>SMB</c> from Trio / iAPS, <c>Automatic Bolus</c>, and any
    /// treatment the uploader flags <c>automatic</c> (Loop's automatic <c>Correction Bolus</c>).
    /// </summary>
    private static bool IsAlgorithmBolus(Treatment treatment) =>
        treatment.Automatic == true
        || (treatment.IsBasalInsulin == true && treatment.Insulin > 0)
        || (string.Equals(treatment.EventType, "Correction Bolus", StringComparison.OrdinalIgnoreCase) && IsAapsUpload(treatment))
        || string.Equals(treatment.EventType, "SMB", StringComparison.OrdinalIgnoreCase)
        || string.Equals(treatment.EventType, "Automatic Bolus", StringComparison.OrdinalIgnoreCase);

    /// <summary>The pump named by the upload's pump fields, created in the device registry if new.</summary>
    private Task<Guid?> ResolvePumpDeviceAsync(Treatment treatment, CancellationToken ct) =>
        _deviceService.ResolveAsync(
            V4Models.DeviceCategory.InsulinPump, treatment.PumpType, treatment.PumpSerial, treatment.Mills, ct);

    private async Task<V4Models.Bolus> BuildBolusAsync(
        Treatment treatment, Guid? correlationId, bool keepEventType, CancellationToken ct)
    {
        var model = Mapped(() => MapToBolus(treatment, correlationId, keepEventType));

        if (IsAlgorithmBolus(treatment))
        {
            model.Kind = V4Models.BolusKind.Algorithm;
            model.Automatic = true;
        }

        model.DeviceId = await ResolvePumpDeviceAsync(treatment, ct);
        model.PatientDeviceId = await _deviceService.ResolvePatientDeviceAsync(model.DeviceId, treatment.Mills, ct);

        return model;
    }

    private async Task DecomposeBolusAsync(
        Treatment treatment, V4Models.DecompositionResult result, bool keepEventType, WriteOrigin origin, CancellationToken ct)
    {
        var model = await BuildBolusAsync(treatment, result.CorrelationId, keepEventType, ct);

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
    /// it. A stored carb intake can still reach this: a single-path create that matches on the
    /// sync key upserts the stored row in place through <c>CreateAsync</c> and reports as created.
    /// The existence check is what makes this write idempotent there, and there is no
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

    private async Task DecomposeCarbIntakeAsync(
        Treatment treatment, V4Models.DecompositionResult result, bool keepEventType, WriteOrigin origin, CancellationToken ct)
    {
        var upserted = await UpsertByLegacyIdAsync(
            _carbIntakeRepository, treatment.Id, Mapped(() => MapToCarbIntake(treatment, result.CorrelationId, keepEventType)),
            result, origin, ct);

        if (upserted is ({ } carbIntake, true))
            await WriteLegacyFoodLineAsync(carbIntake.Id, treatment, ct);
    }

    private async Task DecomposeBGCheckAsync(Treatment treatment, V4Models.DecompositionResult result, WriteOrigin origin, CancellationToken ct)
        => await UpsertByLegacyIdAsync(
            _bgCheckRepository, treatment.Id, Mapped(() => MapToBGCheck(treatment, result.CorrelationId)), result, origin, ct);

    private async Task DecomposeNoteAsync(Treatment treatment, V4Models.DecompositionResult result, bool isAnnouncement, WriteOrigin origin, CancellationToken ct)
        => await UpsertByLegacyIdAsync(
            _noteRepository, treatment.Id, Mapped(() => MapToNote(treatment, result.CorrelationId, isAnnouncement)), result, origin, ct);

    private async Task<V4Models.DeviceEvent> BuildDeviceEventAsync(
        Treatment treatment, Guid? correlationId, DeviceEventType deviceEventType, CancellationToken ct)
    {
        var model = Mapped(() => MapToDeviceEvent(treatment, correlationId, deviceEventType));
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
                Source = SpanSource(treatment),
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
            _bolusCalculationRepository, treatment.Id, Mapped(() => MapToBolusCalculation(treatment, result.CorrelationId)), result, origin, ct);

    /// <summary>
    /// The insulin context is left to the caller: the batch path resolves it against a
    /// batch-local profile-switch timeline the single path cannot see.
    /// </summary>
    private async Task<V4Models.TempBasal> BuildTempBasalAsync(
        Treatment treatment, Guid? correlationId, CancellationToken ct)
    {
        var model = Mapped(() => MapToTempBasal(treatment, correlationId));
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
        var model = await BuildTempBasalAsync(treatment, result.CorrelationId, ct);

        // Resolve insulin context: active profile switch → primary insulin → null
        model.InsulinContext = await _activeProfileResolver.GetActiveInsulinContextAsync(treatment.Mills, ct)
            ?? ToInsulinContext(await _insulinRepo.GetPrimaryBolusInsulinAsync(ct));

        await UpsertByLegacyIdAsync(
            _tempBasalRepository, treatment.Id, model, result, origin, ct,
            beforeWrite: async existing =>
            {
                SettleAgainstStored(model, treatment, existing);
                await _relativeTempBasalRates.ResolveAsync([model], ct);
                await StampAttributionAsync(
                    _patientDeviceStamper, model, existing, V4Models.DeviceAttributionCategories.TempBasal, ct);
            });
    }

    private async Task DecomposeProfileSwitchAsync(Treatment treatment, V4Models.DecompositionResult result, WriteOrigin origin, CancellationToken ct)
    {
        var model = await BuildProfileActivationAsync(treatment, result.CorrelationId, ct);
        var stored = treatment.Id is null ? null : await _profileActivationRepository.GetByLegacyIdAsync(treatment.Id, ct);

        await _profileActivationWriter.WriteAsync(model, stored, async settled =>
        {
            var upserted = await UpsertByLegacyIdAsync(
                _profileActivationRepository, treatment.Id, settled, result, origin, ct,
                beforeWrite: existing => StampAttributionAsync(
                    _patientDeviceStamper, settled, existing, V4Models.DeviceAttributionCategories.ProfileActivation, ct),
                findStored: () => Task.FromResult(stored));
            return upserted?.Record;
        }, ct);
    }

    /// <summary>
    /// The activation a Profile Switch or an AAPS effective profile switch decomposes into: its span
    /// metadata as <see cref="BuildProfileMetadata"/> and <see cref="EffectiveProfileSwitch.Metadata"/>
    /// read the upload, converted by <see cref="ProfileActivationLegacyFields.FromSpanMetadata"/>.
    /// A requested switch carries the end its own length gives it; the writer settles the rest.
    /// </summary>
    private async Task<V4Models.ProfileActivation> BuildProfileActivationAsync(
        Treatment treatment, Guid? correlationId, CancellationToken ct)
    {
        var isEffective = EffectiveProfileSwitch.Is(treatment);
        var metadata = (isEffective ? BuildEffectiveProfileMetadata(treatment) : BuildProfileMetadata(treatment)) ?? [];
        var start = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).UtcDateTime;

        var model = ProfileActivationLegacyFields.FromSpanMetadata(metadata, start);
        model.Id = Guid.CreateVersion7();
        model.LegacyId = treatment.Id;
        model.CorrelationId = correlationId;
        model.DataSource = treatment.DataSource;
        model.Device = UploadedDevice(treatment);
        model.Origin = ProfileActivationSource.OriginOf(model.App, treatment.EnteredBy, treatment.OriginalProfileName);
        if (!isEffective && model.RequestedDurationMs is > 0 and var duration)
            model.EndTimestamp = start.AddMilliseconds(duration);

        model.DeviceId = await ResolvePumpDeviceAsync(treatment, ct);
        model.PatientDeviceId = await _deviceService.ResolvePatientDeviceAsync(model.DeviceId, treatment.Mills, ct);
        return model;
    }

    private async Task DecomposeOverrideAsync(Treatment treatment, V4Models.DecompositionResult result, WriteOrigin origin, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var stateSpan = new StateSpan
        {
            Category = StateSpanCategory.Override,
            State = OverrideState.Custom.ToString(),
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).UtcDateTime,
            EndTimestamp = treatment.Duration is > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills + (long)(treatment.Duration.Value * 60 * 1000)).UtcDateTime
                : null,
            Source = SpanSource(treatment),
            OriginalId = treatment.Id,
            Metadata = BuildOverrideMetadata(treatment)
        };

        if (await UpsertTreatmentSpanAsync(db, stateSpan, result, ct))
            Logger.LogDebug("Delegated Temporary Override treatment {LegacyId} to IStateSpanService", SanitizeForLog(treatment.Id));
    }

    private async Task DecomposeTemporaryTargetAsync(Treatment treatment, V4Models.DecompositionResult result, WriteOrigin origin, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
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
            Source = SpanSource(treatment),
            OriginalId = treatment.Id,
            Metadata = BuildTemporaryTargetMetadata(treatment)
        };

        if (await UpsertTreatmentSpanAsync(db, stateSpan, result, ct))
            Logger.LogDebug("Delegated Temporary Target treatment {LegacyId} to IStateSpanService", SanitizeForLog(treatment.Id));
    }

    /// <summary>
    /// Writes the span a treatment decomposes into, upserted on the treatment id it carries as its
    /// <c>OriginalId</c>, unless the user deleted the span written under that id: a re-upload does
    /// not bring back a deleted treatment, as for the records keyed by <c>LegacyId</c>.
    /// </summary>
    /// <returns>Whether the span was written.</returns>
    private async Task<bool> UpsertTreatmentSpanAsync(
        NocturneDbContext db,
        StateSpan stateSpan, V4Models.DecompositionResult result, CancellationToken ct)
    {
        if (stateSpan.OriginalId is { Length: > 0 } originalId)
        {
            var governing = await TreatmentSpans(db.StateSpans.IgnoreQueryFilters()).AsNoTracking()
                .Where(s => s.TenantId == db.TenantId && s.OriginalId == originalId)
                .WhereBlocksRecreation()
                .Select(s => s.DeletedAt == null)
                .ToListAsync(ct);
            if (governing.Count > 0 && !governing.Contains(true))
            {
                result.SkippedDeleted++;
                return false;
            }
        }

        result.CreatedRecords.Add(await _stateSpanService.UpsertStateSpanAsync(stateSpan, ct));
        return true;
    }

    /// <summary>
    /// Who wrote a treatment's span: its data source, else its uploader, else the <c>app</c> a v3
    /// upload names (AAPS sends no <c>enteredBy</c> there).
    /// </summary>
    private static string SpanSource(Treatment treatment) =>
        treatment.DataSource
        ?? treatment.EnteredBy
        ?? UploadedString(treatment, "app")
        ?? "nightscout";

    #endregion

    #region Mapping Methods

    /// <summary>
    /// Runs one MapTo* step, so a field value it cannot read surfaces as a
    /// <see cref="TreatmentMappingException"/>, apart from the failures of the lookups and writes
    /// around it. An <see cref="ArgumentNullException"/> is a mapping bug, not the client's data,
    /// and is left as it is.
    /// </summary>
    private static T Mapped<T>(Func<T> map)
    {
        try
        {
            return map();
        }
        catch (ArgumentException ex) when (ex is not ArgumentNullException) { throw new TreatmentMappingException(ex); }
        catch (FormatException ex) { throw new TreatmentMappingException(ex); }
        catch (OverflowException ex) { throw new TreatmentMappingException(ex); }
    }

    /// <summary>
    /// A Temp Basal with no duration ends where it starts: Nightscout reads one as a cancel, which
    /// ends the temp before it and delivers nothing.
    /// </summary>
    internal static V4Models.TempBasal MapToTempBasal(Treatment treatment, Guid? correlationId)
    {
        var startTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).UtcDateTime;
        var durationMs = (treatment.DurationInMilliseconds ?? (long?)((treatment.Duration ?? 0) * 60 * 1000)) ?? 0;
        var percent = treatment.Absolute is null ? treatment.Percent : null;
        var (origin, evidence) = MapTempBasalOrigin(treatment);

        return new V4Models.TempBasal
        {
            Id = Guid.CreateVersion7(),
            LegacyId = treatment.Id,
            AdditionalProperties = TempBasalUploadedFields.Keep(
                TempBasalAutomaticFlag.Keep(
                    TreatmentUploadedTimestamp.AddTo(TreatmentClientId.ToRecord(treatment), treatment),
                    treatment.Automatic
                    ?? (evidence is V4Models.TempBasalOriginEvidence.Reported ? TempBasalAutomaticFlag.Of(origin) : null)),
                treatment),
            StartTimestamp = startTimestamp,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills + Math.Max(durationMs, 0)).UtcDateTime,
            UtcOffset = treatment.UtcOffset,
            Device = UploadedDevice(treatment),
            App = treatment.EnteredBy,
            DataSource = treatment.DataSource,
            CorrelationId = correlationId,
            Rate = percent is null ? treatment.Absolute ?? 0 : treatment.Rate,
            // An uploaded rate is the scheduled rate the uploader saw, changed by the percent
            ScheduledRate = percent is { } p && treatment.Rate is { } rate && p > -100
                ? rate * 100 / (100 + p)
                : null,
            Percent = percent,
            Origin = origin,
            OriginEvidence = evidence,
            PumpRecordId = treatment.PumpId?.ToString(),
        };
    }

    private static (V4Models.TempBasalOrigin, V4Models.TempBasalOriginEvidence) MapTempBasalOrigin(Treatment treatment)
    {
        const V4Models.TempBasalOriginEvidence reported = V4Models.TempBasalOriginEvidence.Reported;

        if (string.Equals(treatment.Reason, "suspend", StringComparison.OrdinalIgnoreCase))
            return (V4Models.TempBasalOrigin.Suspended, reported);

        if (treatment.Automatic is { } automatic)
            return (automatic ? V4Models.TempBasalOrigin.Algorithm : V4Models.TempBasalOrigin.Manual, reported);

        // An edit echoes the stored origin back next to the field it changes. An echoed default is
        // not a statement, or the edit would lock it in.
        if (TryEchoedOrigin(treatment, out var origin, out var echoedEvidence)
            && (echoedEvidence is null
                || string.Equals(echoedEvidence, nameof(V4Models.TempBasalOriginEvidence.Reported), StringComparison.OrdinalIgnoreCase)))
            return (origin, reported);

        if (treatment.BolusType is "PUMP_SUSPEND" or "EMULATED_PUMP_SUSPEND")
            return (V4Models.TempBasalOrigin.Suspended, reported);

        return (V4Models.TempBasalOriginProvenance.UploaderDefault(treatment.EnteredBy, UploadedString(treatment, "app")),
            V4Models.TempBasalOriginEvidence.Uploader);
    }

    private static bool TryEchoedOrigin(Treatment treatment, out V4Models.TempBasalOrigin origin, out string? evidence)
    {
        origin = default;
        evidence = null;
        if (treatment.AdditionalProperties is not { } props
            || !TryGetString(props, V4Models.TempBasalOriginProvenance.OriginField, out var name)
            || !TryParseDefinedName(name, out origin))
            return false;

        if (props.ContainsKey(V4Models.TempBasalOriginProvenance.EvidenceField))
        {
            if (!TryGetString(props, V4Models.TempBasalOriginProvenance.EvidenceField, out var echoed))
                return false;
            evidence = echoed;
        }

        return true;
    }

    /// <summary>
    /// What an edit echoes back of the stored row, settled for a PUT, a PATCH and a batch alike: the rate
    /// the edit did not change, and the origin as <see cref="SettleEchoedTempBasalOrigin"/> reads it.
    /// </summary>
    internal static void SettleAgainstStored(V4Models.TempBasal model, Treatment treatment, V4Models.TempBasal? stored)
    {
        RelativeTempBasalRates.ForgetEchoedRate(model, stored);
        SettleEchoedTempBasalOrigin(model, treatment, stored);
    }

    /// <summary>
    /// Decides whether an echoed origin beside echoed non-<see cref="V4Models.TempBasalOriginEvidence.Reported"/>
    /// evidence is an edit or the stored row read back. A different origin is the user's statement only when
    /// the evidence echoed with it is the stored row's, since that proves the body was read from this state;
    /// a stale echo is a default for the stored origin, which <see cref="V4Models.TempBasalOriginProvenance.OnUpdate"/>
    /// settles as it does a re-upload's. A rate the edit left unset is the stored one unless its percent changed.
    /// </summary>
    internal static void SettleEchoedTempBasalOrigin(V4Models.TempBasal model, Treatment treatment, V4Models.TempBasal? stored)
    {
        if (stored is null
            || model.OriginEvidence is V4Models.TempBasalOriginEvidence.Reported
            || !TryEchoedOrigin(treatment, out var echoed, out var echoedEvidence))
            return;

        var storedEvidence = V4Models.TempBasalOriginProvenance.EvidenceOf(stored);
        if (echoed != stored.Origin
            && string.Equals(echoedEvidence, storedEvidence.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            model.Origin = echoed;
            model.OriginEvidence = V4Models.TempBasalOriginEvidence.Reported;
            return;
        }

        var settled = V4Models.TempBasalOriginProvenance.OnUpdate(
            new V4Models.TempBasalOriginState(
                stored.Origin, storedEvidence, stored.ApsSnapshotId, stored.StartMills, stored.Rate),
            new V4Models.TempBasalOriginState(
                stored.Origin,
                V4Models.TempBasalOriginEvidence.Uploader,
                null,
                model.StartMills,
                model.Percent == stored.Percent ? model.Rate ?? stored.Rate : model.Rate));
        model.Origin = settled.Origin;
        model.OriginEvidence = settled.Evidence;
        model.ApsSnapshotId = settled.ApsSnapshotId;
    }

    internal static V4Models.Bolus MapToBolus(Treatment treatment, Guid? correlationId, bool keepEventType = true)
    {
        return new V4Models.Bolus
        {
            LegacyId = treatment.Id,
            AdditionalProperties = UploadedEventType(
                TreatmentUploadedTimestamp.AddTo(TreatmentClientId.ToRecord(treatment), treatment),
                treatment, keepEventType, TreatmentTypes.CorrectionBolus),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).UtcDateTime,
            Insulin = treatment.Insulin ?? 0,
            Programmed = treatment.Programmed,
            Delivered = treatment.InsulinDelivered,
            BolusType = ParseBolusType(treatment.BolusType),
            Automatic = treatment.Automatic ?? false,
            Duration = treatment.Duration,
            Device = UploadedDevice(treatment),
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

    internal static V4Models.CarbIntake MapToCarbIntake(Treatment treatment, Guid? correlationId, bool keepEventType = true)
    {
        return new V4Models.CarbIntake
        {
            LegacyId = treatment.Id,
            AdditionalProperties = UploadedEventType(
                TreatmentUploadedTimestamp.AddTo(TreatmentClientId.ToRecord(treatment), treatment),
                treatment, keepEventType, TreatmentTypes.CarbCorrection),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).UtcDateTime,
            Carbs = treatment.Carbs ?? 0,
            Device = UploadedDevice(treatment),
            DataSource = treatment.DataSource,
            UtcOffset = treatment.UtcOffset,
            CorrelationId = correlationId,
            SyncIdentifier = treatment.SyncIdentifier,
            CarbTime = treatment.CarbTime,
            AbsorptionTime = treatment.AbsorptionTime,
        };
    }

    private static Dictionary<string, object?>? UploadedEventType(
        Dictionary<string, object?>? record, Treatment treatment, bool keep, string servedWithout) =>
        keep ? TreatmentUploadedEventType.AddTo(record, treatment, servedWithout) : record;

    internal static V4Models.BGCheck MapToBGCheck(Treatment treatment, Guid? correlationId)
    {
        return new V4Models.BGCheck
        {
            LegacyId = treatment.Id,
            AdditionalProperties = TreatmentUploadedTimestamp.AddTo(TreatmentClientId.ToRecord(treatment), treatment),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).UtcDateTime,
            Glucose = treatment.Glucose ?? 0,
            GlucoseType = ParseGlucoseType(treatment.GlucoseType),
            Units = ParseGlucoseUnit(treatment.Units),
            Device = UploadedDevice(treatment),
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
            AdditionalProperties = TreatmentUploadedTimestamp.AddTo(TreatmentClientId.ToRecord(treatment), treatment),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).UtcDateTime,
            Text = treatment.Notes ?? string.Empty,
            EventType = treatment.EventType,
            IsAnnouncement = isAnnouncement || (treatment.IsAnnouncement ?? false),
            Device = UploadedDevice(treatment),
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
            AdditionalProperties = TreatmentUploadedTimestamp.AddTo(TreatmentClientId.ToRecord(treatment), treatment),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).UtcDateTime,
            EventType = deviceEventType,
            Notes = treatment.Notes,
            Device = UploadedDevice(treatment),
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
            AdditionalProperties = TreatmentUploadedTimestamp.AddTo(TreatmentClientId.ToRecord(treatment), treatment),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(treatment.Mills).UtcDateTime,
            BloodGlucoseInput = treatment.BloodGlucoseInput,
            BloodGlucoseInputSource = treatment.BloodGlucoseInputSource,
            CarbInput = treatment.Carbs,
            InsulinOnBoard = treatment.InsulinOnBoard,
            InsulinRecommendation = treatment.InsulinRecommendationForCorrection,
            CarbRatio = treatment.CR,
            CalculationType = MapCalculationType(treatment.CalculationType),
            Device = UploadedDevice(treatment),
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
            _ => TryParseDefinedName<V4Models.BolusType>(bolusType, out var parsed) ? parsed : null
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
            _ => TryParseDefinedName<V4Models.GlucoseType>(glucoseType, out var parsed) ? parsed : null
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
            _ => TryParseDefinedName<V4Models.GlucoseUnit>(units, out var parsed) ? parsed : null
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
    internal static bool IsAapsUpload(Treatment treatment) =>
        string.Equals(UploadedString(treatment, "app"), "AAPS", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The device a record names: its uploader, else the <c>device</c> the upload sent. The uploader
    /// comes first because it is the key a temp basal is matched to its loop's snapshots by.
    /// </summary>
    private static string? UploadedDevice(Treatment treatment) =>
        treatment.EnteredBy ?? UploadedString(treatment, "device");

    /// <summary>An uploaded field the treatment has no property for, when it is a string.</summary>
    private static string? UploadedString(Treatment treatment, string key) =>
        treatment.AdditionalProperties?.GetValueOrDefault(key) switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            _ => null
        };

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

    private static bool IsMealEventType(string? eventType) =>
        string.Equals(eventType, "Meal Bolus", StringComparison.OrdinalIgnoreCase)
        || string.Equals(eventType, "Snack Bolus", StringComparison.OrdinalIgnoreCase)
        || string.Equals(eventType, "Combo Bolus", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a bolus or carb intake would be written under a temp basal's legacy id. An AAPS that
    /// read a temp basal served with insulin holds it as a bolus under the temp basal's identifier
    /// (see <see cref="Treatment.Insulin"/>) and edits it as one; writing that edit would put a
    /// bolus beside every temp basal it touches.
    /// </summary>
    private async Task<bool> IsHeldByTempBasalAsync(Treatment treatment, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(treatment.Id)
            || await _tempBasalRepository.GetByLegacyIdAsync(treatment.Id, ct) is null)
            return false;

        Logger.LogWarning(
            "Refused to write a {EventType} under temp basal {LegacyId}",
            SanitizeForLog(treatment.EventType), SanitizeForLog(treatment.Id));
        return true;
    }

    /// <summary>
    /// Removes what an earlier upload of each treatment wrote under its id that its classification
    /// now leaves out: a Note that became an effective profile switch, the carb half of a meal edited
    /// to insulin alone, the span of a switch re-sent as a note. Only the tables the treatment no
    /// longer writes lose rows, so a re-upload of the same kind removes nothing, and every record a
    /// treatment writes beside another (a wizard's bolus, a meal's two halves, a note) is kept.
    /// </summary>
    /// <remarks>
    /// The removal is system-attributed: a user tombstone would stop a later upload that writes the
    /// table again from re-creating the row (<see cref="SoftDeleteDedupExtensions.WhereBlocksRecreation{TEntity}"/>).
    /// A treatment Nocturne does not store writes nothing and removes nothing, and a bolus or carbs
    /// under a temp basal's id leaves the temp basal (<see cref="IsHeldByTempBasalAsync"/>).
    /// </remarks>
    private async Task DropAbsentProjectionsAsync(
        IReadOnlyList<(Treatment Treatment, TreatmentClassification Classification)> written,
        WriteOrigin origin,
        CancellationToken ct)
    {
        var ids = written
            .Where(w => !string.IsNullOrEmpty(w.Treatment.Id) && !w.Classification.ProducesNothing)
            .Select(w => w.Treatment.Id!)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
            return;

        await using var db = await _contexts.CreateAsync(ct);
        var stored = new Dictionary<string, HashSet<DecomposedKind>>(StringComparer.Ordinal);
        foreach (var table in DecomposedTables)
        {
            foreach (var id in await table.LiveLegacyIdsAsync(db, ids, ct))
            {
                if (!stored.TryGetValue(id, out var tables))
                    stored[id] = tables = [];
                tables.Add(table.Kind);
            }
        }

        var spanCategories = TreatmentSpanCategories;
        var storedSpans = (await db.StateSpans.AsNoTracking()
                .Where(s => s.OriginalId != null && ids.Contains(s.OriginalId) && spanCategories.Contains(s.Category))
                .Select(s => new { s.OriginalId, s.Category })
                .ToListAsync(ct))
            .GroupBy(s => s.OriginalId!)
            .ToDictionary(g => g.Key, g => g.Select(s => s.Category).ToHashSet(), StringComparer.Ordinal);

        var tableDrops = new Dictionary<DecomposedKind, List<string>>();
        var spanDrops = new List<string>();
        var snapshotDrops = new List<string>();
        foreach (var (treatment, c) in written)
        {
            if (string.IsNullOrEmpty(treatment.Id) || c.ProducesNothing)
                continue;

            var id = treatment.Id;
            var writes = TablesWrittenBy(c);
            var held = stored.GetValueOrDefault(id) ?? [];
            if (held.Contains(DecomposedKind.TempBasal) && !writes.Contains(DecomposedKind.TempBasal)
                && (c.ProduceBolus || c.ProduceCarbIntake))
                continue;

            foreach (var table in held.Except(writes))
            {
                if (!tableDrops.TryGetValue(table, out var dropIds))
                    tableDrops[table] = dropIds = [];
                dropIds.Add(id);
            }

            if (held.Contains(DecomposedKind.ProfileActivation) && !writes.Contains(DecomposedKind.ProfileActivation))
                snapshotDrops.Add(id);

            if (storedSpans.ContainsKey(id) && !c.IsOverride && !c.IsTemporaryTarget)
                spanDrops.Add(id);
        }

        if (tableDrops.Count == 0 && spanDrops.Count == 0 && snapshotDrops.Count == 0)
            return;

        List<Guid> deviceEventIds = [];
        List<Guid> activationIds = [];
        using (SystemAttributedBatchWrites(_auditContext))
        {
            await DeleteProfileSwitchSnapshotsAsync(db, snapshotDrops, origin, ct);
            foreach (var table in DecomposedTables)
            {
                if (!tableDrops.TryGetValue(table.Kind, out var dropIds))
                    continue;

                var dropped = await table.SoftDeleteAsync(
                    db, [.. dropIds], source: null, "treatment_reclassified", DuplicateDelete.EveryCopy, ct);
                if (table.Kind == DecomposedKind.DeviceEvent)
                    deviceEventIds = dropped.Entities;
                if (table.Kind == DecomposedKind.ProfileActivation)
                    activationIds = dropped.Entities;
                Logger.LogDebug(
                    "Removed {Count} {RecordType} rows a reclassified treatment no longer writes",
                    dropped.Count, table.Kind);
            }

            foreach (var id in spanDrops)
                await _stateSpanService.DeleteStateSpanAsync(id, ct);
        }

        if (tableDrops.ContainsKey(DecomposedKind.ProfileActivation))
            await _profileActivationWriter.ResettleAfterRemovalAsync(activationIds, ct);

        if (origin == WriteOrigin.Live)
            await ReactToDeletedDeviceEventsAsync(db, deviceEventIds, ct);
    }

    /// <summary>
    /// The categories of the spans a treatment writes in place of a table row. A switch is a
    /// <see cref="V4Models.ProfileActivation"/>; the Profile spans written before it are read by nothing.
    /// </summary>
    private static readonly string[] TreatmentSpanCategories =
    [
        nameof(StateSpanCategory.Override),
        nameof(StateSpanCategory.TemporaryTarget),
    ];

    private static readonly string ProfileCategory = nameof(StateSpanCategory.Profile);

    /// <summary>The spans a treatment's write or delete can find, leaving out the Profile spans no reader reads.</summary>
    private static IQueryable<StateSpanEntity> TreatmentSpans(IQueryable<StateSpanEntity> spans) =>
        spans.Where(s => s.Category != ProfileCategory);

    /// <summary>The tables of <see cref="DecomposedTables"/> a classification writes to.</summary>
    private static HashSet<DecomposedKind> TablesWrittenBy(TreatmentClassification c)
    {
        var tables = new HashSet<DecomposedKind>();
        if (c.ProduceBolus) tables.Add(DecomposedKind.Bolus);
        if (c.ProduceCarbIntake) tables.Add(DecomposedKind.CarbIntake);
        if (c.ProduceBGCheck) tables.Add(DecomposedKind.BGCheck);
        if (c.ProduceNote) tables.Add(DecomposedKind.Note);
        if (c.ProduceBolusCalc) tables.Add(DecomposedKind.BolusCalculation);
        if (c.ProduceDeviceEvent) tables.Add(DecomposedKind.DeviceEvent);
        if (c.IsProfileSwitch) tables.Add(DecomposedKind.ProfileActivation);
        if (c.DelegateToStateSpan && !c.IsProfileSwitch && !c.IsOverride && !c.IsTemporaryTarget)
            tables.Add(DecomposedKind.TempBasal);
        return tables;
    }

    private static Dictionary<string, object>? BuildProfileMetadata(Treatment treatment)
    {
        var metadata = new Dictionary<string, object>();

        if (!string.IsNullOrEmpty(treatment.Profile))
            metadata[ProfileSpanMetadata.ProfileNameKey] = treatment.Profile;

        if (!string.IsNullOrEmpty(treatment.ProfileJson))
            metadata[ProfileSpanMetadata.ProfileJsonKey] = treatment.ProfileJson;

        if (treatment.Percentage.HasValue)
            metadata[ProfileSpanMetadata.PercentageKey] = treatment.Percentage.Value;

        if (treatment.Timeshift is { } timeshift)
        {
            var unit = ProfileSpanMetadata.UnitOfUploadedTimeshift(
                timeshift, treatment.EnteredBy, UploadedString(treatment, ProfileSpanMetadata.AppKey),
                treatment.OriginalProfileName);
            metadata[ProfileSpanMetadata.TimeshiftKey] = ProfileSpanMetadata.ToHours(timeshift, unit);
            metadata[ProfileSpanMetadata.UploadedTimeshiftKey] = timeshift;
            metadata[ProfileSpanMetadata.TimeshiftUnitKey] = ProfileSpanMetadata.UnitName(unit);
        }

        if (!string.IsNullOrEmpty(treatment.Reason))
            metadata["reason"] = treatment.Reason;

        if (!string.IsNullOrEmpty(treatment.OriginalProfileName))
            metadata[ProfileSpanMetadata.OriginalProfileNameKey] = treatment.OriginalProfileName;

        if (treatment.OriginalDuration is { } originalDuration)
            metadata[ProfileSpanMetadata.OriginalDurationKey] = originalDuration;

        if (treatment.Duration is { } duration)
            metadata[LegacyTreatmentTables.UploadedDurationKey] = duration;

        if (treatment.DurationInMilliseconds is { } durationMs)
            metadata[ProfileSpanMetadata.DurationInMillisecondsKey] = durationMs;

        AddPumpFields(metadata, treatment);
        AddServedTreatmentFields(metadata, treatment);
        AddInsulinContext(metadata, treatment);

        return metadata.Count > 0 ? metadata : null;
    }

    private static Dictionary<string, object> BuildEffectiveProfileMetadata(Treatment treatment)
    {
        var metadata = EffectiveProfileSwitch.Metadata(treatment);
        AddPumpFields(metadata, treatment);
        AddServedTreatmentFields(metadata, treatment);
        AddInsulinContext(metadata, treatment);
        return metadata;
    }

    /// <summary>The pump record ids and uploading app AAPS reads a profile switch back with.</summary>
    private static void AddPumpFields(Dictionary<string, object> metadata, Treatment treatment)
    {
        if (treatment.PumpId is { } pumpId)
            metadata[ProfileSpanMetadata.PumpIdKey] = pumpId;

        if (!string.IsNullOrEmpty(treatment.PumpType))
            metadata[ProfileSpanMetadata.PumpTypeKey] = treatment.PumpType;

        if (!string.IsNullOrEmpty(treatment.PumpSerial))
            metadata[ProfileSpanMetadata.PumpSerialKey] = treatment.PumpSerial;

        if (UploadedString(treatment, ProfileSpanMetadata.AppKey) is { Length: > 0 } app)
            metadata[ProfileSpanMetadata.AppKey] = app;
    }

    private static void AddInsulinContext(Dictionary<string, object> metadata, Treatment treatment)
    {
        var icfg = ExtractAapsIcfg(treatment);
        if (icfg is not null)
        {
            metadata["insulinName"] = icfg.InsulinName;
            metadata["insulinDia"] = icfg.Dia.ToString("F1", CultureInfo.InvariantCulture);
            metadata["insulinPeak"] = icfg.Peak.ToString();
            metadata["insulinConcentration"] = icfg.Concentration.ToString();
            metadata["insulinCurve"] = icfg.Curve;
        }
    }

    private const string TemporaryTargetEventType = "Temporary Target";

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

        if (treatment.Duration is { } duration)
            metadata[LegacyTreatmentTables.UploadedDurationKey] = duration;

        var uploaded = LegacyTreatmentTables.UploadedOverrideFields
            .Select(key => (Key: key, Value: treatment.AdditionalProperties?.GetValueOrDefault(key)))
            .Where(field => field.Value is not (null or JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }));
        foreach (var (key, value) in uploaded)
            metadata[key] = value is JsonElement element ? element.Clone() : value!;

        AddServedTreatmentFields(metadata, treatment);

        return metadata.Count > 0 ? metadata : null;
    }

    private static Dictionary<string, object>? BuildTemporaryTargetMetadata(Treatment treatment)
    {
        var metadata = new Dictionary<string, object>();

        // Nightscout stores a "Temporary Target Cancel" under that eventType, and finds it by it.
        if (treatment.EventType is { } eventType
            && !string.Equals(eventType, TemporaryTargetEventType, StringComparison.Ordinal))
            metadata[LegacyTreatmentTables.UploadedEventTypeKey] = eventType;

        if (treatment.Duration is { } duration)
            metadata[LegacyTreatmentTables.UploadedDurationKey] = duration;

        if (treatment.TargetTop.HasValue)
            metadata["targetTop"] = treatment.TargetTop.Value;

        if (treatment.TargetBottom.HasValue)
            metadata["targetBottom"] = treatment.TargetBottom.Value;

        if (!string.IsNullOrEmpty(treatment.Reason))
            metadata["reason"] = treatment.Reason;

        if (!string.IsNullOrEmpty(treatment.Units))
            metadata["units"] = treatment.Units;

        AddServedTreatmentFields(metadata, treatment);

        return metadata.Count > 0 ? metadata : null;
    }

    /// <summary>
    /// The upload fields every span <see cref="LegacyTreatmentTables.StateSpanTables"/> serves, and every
    /// activation <see cref="LegacyProfileActivationTable"/> serves, reads back. <c>notes</c> lives here rather than in a Note of its own, which would serve the treatment
    /// twice.
    /// </summary>
    private static void AddServedTreatmentFields(Dictionary<string, object> metadata, Treatment treatment)
    {
        if (!string.IsNullOrEmpty(treatment.EnteredBy))
            metadata["enteredBy"] = treatment.EnteredBy;

        if (!string.IsNullOrEmpty(treatment.Notes))
            metadata[LegacyTreatmentTables.NotesKey] = treatment.Notes;

        if (!string.IsNullOrEmpty(treatment.SyncIdentifier))
            metadata[LegacyTreatmentTables.SyncIdentifierKey] = treatment.SyncIdentifier;

        if (!string.IsNullOrEmpty(treatment.InsulinType))
            metadata[LegacyTreatmentTables.InsulinTypeKey] = treatment.InsulinType;

        if (TreatmentUploadedTimestamp.Uploaded(treatment) is { } timestamp)
            metadata[LegacyTreatmentTables.UploadedTimestampKey] = timestamp;

        metadata[StateSpanMetadataExtensions.UtcOffsetKey] = treatment.UtcOffset ?? 0;
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

        var result = new V4Models.DecompositionResult();

        // Typed collection lists for bulk insert
        var estimatedPerType = Math.Max(1, treatments.Count / 4);
        var bolusList = new List<V4Models.Bolus>(estimatedPerType);
        var carbList = new List<V4Models.CarbIntake>(estimatedPerType);
        var bgCheckList = new List<V4Models.BGCheck>(estimatedPerType);
        var noteList = new List<V4Models.Note>(estimatedPerType);
        var bolusCalcList = new List<V4Models.BolusCalculation>(estimatedPerType);
        var deviceEventList = new List<V4Models.DeviceEvent>(estimatedPerType);
        var tempBasalSources = new List<(Treatment Treatment, V4Models.TempBasal Model)>(estimatedPerType);

        // State span treatments are upserted individually (idempotent semantics)
        var stateSpanTreatments = new List<(Treatment Treatment, Guid CorrelationId, bool IsProfileSwitch, bool IsOverride, bool IsTemporaryTarget)>();

        // Track treatments that produce both bolus AND bolusCalculation for post-insert linking
        var bolusCalcLinkTreatmentIds = new HashSet<string>();

        // Carb-producing treatments by correlation id, for the post-insert TreatmentFood pass
        var foodLineTreatments = new Dictionary<Guid, Treatment>();

        var pumpSuspendResumeTreatments = new List<(Treatment Treatment, DeviceEventType EventType)>();
        var classified = new List<(Treatment Treatment, TreatmentClassification Classification)>(treatments.Count);
        var unsupportedTypes = new HashSet<string>(StringComparer.Ordinal);
        var correlationIds = await GetStoredCorrelationIdsAsync(treatments.Select(t => t.Id), TreatmentTables, ct);

        await DeferLastSeenAsync(_deviceService, async () =>
        {
            foreach (var treatment in treatments)
            {
                NormalizeIdentity(treatment);

                var correlationId = CorrelationIdFor(treatment.Id, correlationIds);
                var c = ClassifyTreatment(treatment);
                if (c.ProducesNothing)
                {
                    result.SkippedUnsupported++;
                    unsupportedTypes.Add(treatment.EventType ?? "(none)");
                }
                else
                {
                    result.CorrelationId ??= correlationId;
                }

                // Collect state span treatments for individual upsert
                if (c.DelegateToStateSpan)
                {
                    // TempBasal treatments can also be bulk-inserted
                    if (!c.IsProfileSwitch && !c.IsOverride && !c.IsTemporaryTarget)
                    {
                        tempBasalSources.Add((treatment, await BuildTempBasalAsync(treatment, correlationId, ct)));
                    }
                    else
                    {
                        stateSpanTreatments.Add((treatment, correlationId, c.IsProfileSwitch, c.IsOverride, c.IsTemporaryTarget));
                    }
                }

                if (c.ProduceBolus)
                    bolusList.Add(await BuildBolusAsync(treatment, correlationId, c.KeepsUploadedEventType, ct));

                if (c.ProduceCarbIntake)
                {
                    carbList.Add(MapToCarbIntake(treatment, correlationId, c.KeepsUploadedEventType));
                    // Keyed by the per-record correlation id, so the line describes whichever duplicate
                    // of a legacy id the bulk write kept.
                    foodLineTreatments[correlationId] = treatment;
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

                classified.Add((treatment, c));
            }
        }, ct);

        await DropAbsentProjectionsAsync(classified, origin, ct);

        if (result.SkippedUnsupported > 0)
        {
            Logger.LogWarning(
                "Skipped {Count} treatments whose event type Nocturne does not store: {EventTypes}",
                result.SkippedUnsupported, string.Join(", ", unsupportedTypes));
        }

        var tempBasalList = tempBasalSources.ConvertAll(s => s.Model);

        // Fallback attribution for records the serial-based DeviceId resolution left unattributed.
        if (bolusList.Count > 0)
            await _patientDeviceStamper.StampAsync(bolusList, V4Models.DeviceAttributionCategories.Bolus, batchSource: null, ct);
        if (tempBasalList.Count > 0)
            await _patientDeviceStamper.StampAsync(tempBasalList, V4Models.DeviceAttributionCategories.TempBasal, batchSource: null, ct);
        await _patientDeviceStamper.StampDeviceEventsAsync(deviceEventList, batchSource: null, ct);

        // Pre-pass: write the profile activations first (temp basals depend on them for insulin context)
        var batchInsulinTimeline = new SortedDictionary<long, V4Models.TreatmentInsulinContext>();
        foreach (var (treatment, correlationId, _, _, _) in stateSpanTreatments.Where(t => t.IsProfileSwitch))
        {
            var spanResult = new V4Models.DecompositionResult { CorrelationId = correlationId };
            await DecomposeProfileSwitchAsync(treatment, spanResult, origin, ct);
            result.CreatedRecords.AddRange(spanResult.CreatedRecords);
            result.UpdatedRecords.AddRange(spanResult.UpdatedRecords);
            result.SkippedDeleted += spanResult.SkippedDeleted;

            var icfg = ExtractAapsIcfg(treatment);
            if (icfg is not null)
                batchInsulinTimeline[treatment.Mills] = icfg;
        }

        var echoingStored = tempBasalSources
            .Where(s => s.Model.LegacyId is not null
                && (s.Model is { Percent: not null, Rate: not null }
                    || s.Model.OriginEvidence is not V4Models.TempBasalOriginEvidence.Reported
                    && TryEchoedOrigin(s.Treatment, out _, out _)))
            .Select(s => s.Model.LegacyId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var stored = (echoingStored.Count == 0 ? [] : await _tempBasalRepository.GetByLegacyIdsAsync(echoingStored, ct))
            .GroupBy(t => t.LegacyId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var (treatment, model) in tempBasalSources)
            SettleAgainstStored(model, treatment, model.LegacyId is { } legacyId ? stored.GetValueOrDefault(legacyId) : null);
        await _relativeTempBasalRates.ResolveAsync(tempBasalList, ct);

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
            await BulkUpsertAsync(_bolusRepository, bolusList, result, origin, ct);
            await BulkUpsertAsync(_carbIntakeRepository, carbList, result, origin, ct);
            await BulkUpsertAsync(_bgCheckRepository, bgCheckList, result, origin, ct);
            await BulkUpsertAsync(_noteRepository, noteList, result, origin, ct);
            await BulkUpsertAsync(_bolusCalculationRepository, bolusCalcList, result, origin, ct);
            await BulkUpsertAsync(_deviceEventRepository, deviceEventList, result, origin, ct);
            await BulkUpsertAsync(_tempBasalRepository, tempBasalList, result, origin, ct);
        }

        // Post-insert food pass: the carb intake's id is only known once it is persisted. Created
        // only, as on the single path: a carb intake that updated a stored row keeps whatever food
        // lines it has, including none if the user deleted the seeded one.
        foreach (var carbIntake in result.CreatedRecords.OfType<V4Models.CarbIntake>())
        {
            if (carbIntake.CorrelationId is { } correlationId && foodLineTreatments.TryGetValue(correlationId, out var treatment))
                await WriteLegacyFoodLineAsync(carbIntake.Id, treatment, ct);
        }

        // Post-insert pump suspend/resume pass: sequential, order-dependent
        foreach (var (treatment, eventType) in pumpSuspendResumeTreatments.OrderBy(t => t.Treatment.Mills))
        {
            await DecomposePumpSuspensionFromTreatmentAsync(treatment, eventType, result, origin, ct);
        }

        // Upsert remaining state spans (Override, TemporaryTarget — ProfileSwitch already done in pre-pass)
        foreach (var (treatment, correlationId, _, isOv, isTt) in stateSpanTreatments.Where(t => !t.IsProfileSwitch))
        {
            // Use a temporary result to collect records from helper methods
            var spanResult = new V4Models.DecompositionResult { CorrelationId = correlationId };

            if (isOv)
                await DecomposeOverrideAsync(treatment, spanResult, origin, ct);
            else if (isTt)
                await DecomposeTemporaryTargetAsync(treatment, spanResult, origin, ct);

            result.CreatedRecords.AddRange(spanResult.CreatedRecords);
            result.UpdatedRecords.AddRange(spanResult.UpdatedRecords);
            result.SkippedDeleted += spanResult.SkippedDeleted;
        }

        // Post-insert linking: Bolus → BolusCalculation by matching LegacyId
        if (bolusCalcLinkTreatmentIds.Count > 0)
        {
            var persisted = result.CreatedRecords.Concat(result.UpdatedRecords).ToList();
            var persistedBoluses = persisted.OfType<V4Models.Bolus>()
                .Where(b => b.LegacyId != null && bolusCalcLinkTreatmentIds.Contains(b.LegacyId))
                .ToList();
            var persistedCalcs = persisted.OfType<V4Models.BolusCalculation>()
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
    /// <remarks>
    /// The tables are swept in one transaction, as <see cref="DeleteFromSourceAsync"/>'s are: the
    /// delete takes every copy in each row's duplicate group (<see cref="DuplicateDelete.EveryCopy"/>),
    /// and a failure on a later table must not leave the treatment half deleted.
    /// </remarks>
    public async Task<int> DeleteByLegacyIdAsync(string legacyId, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        await DeleteProfileSwitchSnapshotsAsync(db, [legacyId], origin, ct);

        // These deletes bypass the repository chokepoint. Its device-event reaction is run below; the v4-native delete broadcast is deferred to the glucose-unification follow-up.
        var (deleted, deviceEventIds, activationIds) = await db.ExecuteInTransactionAsync(async token =>
        {
            var count = 0;
            List<Guid> deviceEvents = [];
            List<Guid> activations = [];
            foreach (var table in DecomposedTables)
            {
                var result = await table.SoftDeleteAsync(
                    db, [legacyId], source: null, $"legacy_id={legacyId}", DuplicateDelete.EveryCopy, token);
                count += result.Count;
                if (table.Kind == DecomposedKind.DeviceEvent)
                    deviceEvents = result.Entities;
                if (table.Kind == DecomposedKind.ProfileActivation)
                    activations = result.Entities;
            }
            return (count, deviceEvents, activations);
        }, ct: ct);

        if (deleted > 0)
        {
            Logger.LogDebug("Soft-deleted {Count} v4 records for legacy treatment {LegacyId}", deleted, legacyId);
            await _profileActivationWriter.ResettleAfterRemovalAsync(activationIds, ct);
        }

        if (origin == WriteOrigin.Live)
            await ReactToDeletedDeviceEventsAsync(db, deviceEventIds, ct);

        return deleted;
    }

    /// <summary>
    /// Deletes the <c>@@@@@</c> profile rows a Profile Switch that carried <c>profileJson</c>
    /// decomposed into before its base profile was kept on its <see cref="V4Models.ProfileActivation"/>,
    /// which sit under the treatment's id. The activation is looked up with its tombstone: a caller
    /// may have deleted it already. Runs ahead of the deletes that follow it, because the profile
    /// repositories cannot join their transaction and rows left behind by an activation already gone
    /// could never be reached again.
    /// </summary>
    private async Task DeleteProfileSwitchSnapshotsAsync(
        NocturneDbContext db, IReadOnlyCollection<string> legacyIds, WriteOrigin origin, CancellationToken ct)
    {
        var ids = legacyIds.ToArray();
        var activations = db.ProfileActivations.IgnoreQueryFilters()
            .Where(a => a.TenantId == db.TenantId && a.LegacyId != null && ids.Contains(a.LegacyId));
        await DeleteSnapshotRowsAsync(await ProfileSwitchIdsAsync(activations, ct), origin, ct);
    }

    private static async Task<List<string>> ProfileSwitchIdsAsync(
        IQueryable<ProfileActivationEntity> activations, CancellationToken ct)
    {
        var requested = nameof(V4Models.ProfileActivationPhase.Requested);
        return await activations.AsNoTracking()
            .Where(a => a.Phase == requested && a.LegacyId != null)
            .Select(a => a.LegacyId!)
            .Distinct()
            .ToListAsync(ct);
    }

    private static IQueryable<ProfileActivationEntity> ActivationsFromSource(NocturneDbContext db, string source) =>
        db.ProfileActivations.Where(a => a.DataSource == source && a.LegacyId != null);

    private async Task DeleteSnapshotRowsAsync(
        IEnumerable<string> profileSwitchIds, WriteOrigin origin, CancellationToken ct)
    {
        foreach (var id in profileSwitchIds)
            await _profileDecomposer.DeleteByLegacyIdAsync(id, origin, ct);
    }

    /// <inheritdoc />
    public async Task<int> DeleteFromSourceAsync(
        string source, IReadOnlySet<string> legacyIds, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        if (legacyIds.Count == 0)
            return 0;

        var ids = legacyIds.ToArray();
        var scope = $"data_source={source}";

        var spans = LegacyTreatmentTables.ServedStateSpansFromSource(db, source)
            .Where(s => ids.Contains(s.OriginalId!));
        await DeleteSnapshotRowsAsync(
            await ProfileSwitchIdsAsync(ActivationsFromSource(db, source).Where(a => ids.Contains(a.LegacyId!)), ct),
            WriteOrigin.Live, ct);

        var (deleted, deviceEventIds, activationIds) = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var spanKeys = spans.Select(s => s.OriginalId!);
            var count = -await db.Notes
                .Where(n => n.DataSource == source && n.LegacyId != null && spanKeys.Contains(n.LegacyId))
                .CountAsync(ct);
            List<Guid> deviceEvents = [];
            List<Guid> activations = [];
            foreach (var table in DecomposedTables)
            {
                var result = await table.SoftDeleteAsync(db, ids, source, scope, DuplicateDelete.PromoteSurvivor, ct);
                count += result.Count;
                if (table.Kind == DecomposedKind.DeviceEvent)
                    deviceEvents = result.Entities;
                if (table.Kind == DecomposedKind.ProfileActivation)
                    activations = result.Entities;
            }

            count += await db.AuditedSoftDeleteAsync(spans, _auditContext, scope, ct);

            await transaction.CommitAsync(ct);
            return (count, deviceEvents, activations);
        });

        await _profileActivationWriter.ResettleAfterRemovalAsync(activationIds, ct);
        await ReactToDeletedDeviceEventsAsync(db, deviceEventIds, ct);
        return deleted;
    }

    /// <summary>
    /// Runs the <see cref="IDeviceEventReactor"/> the repository chokepoint would have, for device
    /// events these deletes took out around it. Failures are swallowed for the reason
    /// <see cref="DeviceEventRepository"/> swallows its own.
    /// </summary>
    private async Task ReactToDeletedDeviceEventsAsync(NocturneDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (_deviceEventReactor is null || ids.Count == 0)
            return;

        try
        {
            var entities = await db.DeviceEvents.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.TenantId == db.TenantId && ids.Contains(e.Id))
                .ToListAsync(ct);
            await _deviceEventReactor.OnDeletedAsync(entities.Select(DeviceEventMapper.ToDomainModel).ToList(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Device event reaction failed for {Count} deleted event(s)", ids.Count);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, DateTime>> GetLegacyIdsFromSourceAsync(
        string source, DateTime from, DateTime to, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var stored = new Dictionary<string, DateTime>();
        foreach (var table in DecomposedTables)
        {
            foreach (var row in await table.StoredFromSourceAsync(db, source, from, to, ct))
                stored.TryAdd(row.LegacyId, row.At);
        }

        foreach (var span in await LegacyTreatmentTables.ServedStateSpansFromSource(db, source)
                     .AsNoTracking()
                     .Where(s => s.StartTimestamp >= from && s.StartTimestamp <= to)
                     .Select(s => new { Id = s.OriginalId!, At = s.StartTimestamp })
                     .ToListAsync(ct))
            stored.TryAdd(span.Id, span.At);

        return stored;
    }

    /// <inheritdoc />
    public async Task DeleteProfileSwitchSnapshotsFromSourceAsync(
        string source, WriteOrigin origin, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        await DeleteSnapshotRowsAsync(await ProfileSwitchIdsAsync(ActivationsFromSource(db, source), ct), origin, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A connector's rows can sit under an uploader's <c>id</c> where its <c>_id</c> belongs (see
    /// <see cref="UploaderIdJsonModifier"/>); read by <c>_id</c>, the same treatments would otherwise be
    /// stored a second time beside them. Equivalents sharing that <c>id</c> were merged into the row of
    /// the last one written, so the row moves to the equivalent at its time. A row the user deleted
    /// stays under the client id and its tombstone is copied onto every equivalent, because
    /// <see cref="SoftDeleteDedupExtensions.WhereBlocksRecreation{TEntity}"/> blocks only the id a
    /// tombstone carries; left in place, it still reaches an equivalent a later publish delivers.
    /// An id something already holds is left alone rather than merged into.
    /// </remarks>
    public async Task<int> RekeyClientIdRecordsAsync(
        string source, IReadOnlyList<Treatment> treatments, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateAsync(ct);
        var equivalents = new Dictionary<string, List<Treatment>>(StringComparer.Ordinal);
        foreach (var treatment in treatments)
        {
            if (treatment.Id is { Length: > 0 } id && TreatmentClientId.Of(treatment) is { Length: > 0 } clientId
                && clientId != id)
            {
                if (!equivalents.TryGetValue(clientId, out var group))
                    equivalents[clientId] = group = [];
                group.Add(treatment);
            }
        }

        if (equivalents.Count == 0)
            return 0;

        string[] clientIds = [.. equivalents.Keys];
        var stored = new List<ClientIdRow>();
        foreach (var table in DecomposedTables)
            stored.AddRange(await table.ClientIdRowsAsync(db, source, clientIds, ct));
        stored.AddRange((await StateSpansFromSource(db, source, clientIds).AsNoTracking()
                .Select(s => new { Key = s.OriginalId!, s.StartTimestamp, Live = s.DeletedAt == null })
                .ToListAsync(ct))
            .Select(s => new ClientIdRow(s.Key, s.StartTimestamp, s.Live)));
        if (stored.Count == 0)
            return 0;

        var (held, _) = await GetHeldLegacyIdsAsync(
            db,
            equivalents.Values.SelectMany(g => g).Select(t => t.Id!).ToHashSet(), ct);
        var moves = new List<(string From, string To)>();
        var copies = new List<(string From, string[] To)>();
        foreach (var rows in stored.GroupBy(r => r.LegacyId, StringComparer.Ordinal))
        {
            var group = equivalents[rows.Key];
            if (!rows.Any(r => r.Live))
            {
                string[] unheld = [.. group.Select(t => t.Id!).Where(id => !held.Contains(id)).Distinct()];
                if (unheld.Length > 0)
                    copies.Add((rows.Key, unheld));
                continue;
            }

            var storedAt = rows.Select(r => ToMills(r.At)).ToHashSet();
            var target = group.FirstOrDefault(t => storedAt.Contains(t.Mills)) ?? group[0];
            if (!held.Contains(target.Id!))
                moves.Add((rows.Key, target.Id!));
        }

        if (moves.Count == 0 && copies.Count == 0)
            return 0;

        var rekeyed = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var moved = 0;
            foreach (var (from, to) in moves)
            {
                foreach (var table in DecomposedTables)
                    moved += await table.RekeyAsync(db, source, from, to, ct);

                moved += await StateSpansFromSource(db, source, [from])
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.OriginalId, to), ct);
            }

            foreach (var (from, to) in copies)
            {
                foreach (var table in DecomposedTables)
                    await table.AddTombstoneCopiesAsync(db, source, from, to, ct);

                var spanTombstones = await StateSpansFromSource(db, source, [from]).AsNoTracking()
                    .Where(s => s.DeletedAt != null).ToListAsync(ct);
                AddTombstoneCopies(db, spanTombstones, to, (s, id) => s.OriginalId = id);
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return moved;
        });

        await _profileActivationWriter.InvalidateAsync(ct);
        return rekeyed;
    }

    private static long ToMills(DateTime at) =>
        new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>Stages a user tombstone like each of <paramref name="tombstones"/> under each of <paramref name="keys"/>.</summary>
    private static void AddTombstoneCopies<T>(
        NocturneDbContext context, IEnumerable<T> tombstones, string[] keys, Action<T, string> setKey)
        where T : class, IIdentified
    {
        foreach (var row in tombstones)
        {
            foreach (var key in keys)
            {
                var copy = (T)context.Entry(row).CurrentValues.ToObject();
                copy.Id = Guid.CreateVersion7();
                setKey(copy, key);
                context.Add(copy);
                context.Entry(copy).Property("DeletedByUser").CurrentValue = true;
            }
        }
    }

    private sealed record ClientIdRow(string LegacyId, DateTime At, bool Live);

    private static IQueryable<StateSpanEntity> StateSpansFromSource(
        NocturneDbContext db, string source, string[] originalIds) =>
        TreatmentSpans(db.StateSpans.IgnoreQueryFilters())
            .Where(s => s.TenantId == db.TenantId && s.Source == source
                     && s.OriginalId != null && originalIds.Contains(s.OriginalId))
            .WhereBlocksRecreation();

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
        await using var db = await _contexts.CreateAsync(ct);
        var identified = treatments.Where(t => t.Id is { Length: > 0 }).ToList();
        if (identified.Count == 0)
            return [];

        var (held, deletedByUser) = await GetHeldLegacyIdsAsync(db, identified.Select(t => t.Id!).ToHashSet(), ct);
        var stamped = new Dictionary<string, List<string?>>();
        var heldIds = held.ToArray();
        foreach (var table in DecomposedTables)
        {
            foreach (var (legacyId, fingerprint) in await table.FingerprintsFromSourceAsync(db, source, heldIds, ct))
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
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                foreach (var table in DecomposedTables)
                    await table.StampUnfingerprintedAsync(db, source, baselines, ct);
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
            t.AbsorptionTime, t.Glucose, t.GlucoseType, t.Units, t.Duration, HashedAbsolute(t), t.Rate,
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
        NocturneDbContext db,
        HashSet<string> legacyIds, CancellationToken ct)
    {
        var rows = new List<(string Key, bool Live)>();
        foreach (var table in DecomposedTables)
            rows.AddRange(await table.BlockingAsync(db, legacyIds, ct));

        // Overrides and temporary targets land as state spans keyed by OriginalId.
        var ids = legacyIds.ToArray();
        rows.AddRange((await TreatmentSpans(db.StateSpans.IgnoreQueryFilters()).AsNoTracking()
                .Where(s => s.TenantId == db.TenantId && s.OriginalId != null && ids.Contains(s.OriginalId))
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
        await using var db = await _contexts.CreateAsync(ct);
        // origin is accepted for interface uniformity; the v4-native delete broadcast is deferred to the glucose-unification follow-up (deletes here bypass the repository chokepoint).
        var findQuery = Core.Models.Queries.FindQuery.Parse(find);
        var (fromMills, toMills) = (findQuery.FromMills, findQuery.ToMills);

        if (findQuery.IsEmpty)
        {
            Logger.LogWarning("BulkDelete refused: the find has no conditions");
            return 0;
        }

        // This sweep deletes every record type in the window, so it can only honor pure
        // time-range queries. Field-filtered deletes must resolve matches through the filtered
        // read path (TreatmentService.DeleteTreatmentsAsync) — deleting here would wipe
        // non-matching records.
        if (findQuery.HasFieldFilters)
        {
            Logger.LogWarning("BulkDelete refused: the find carries field filters the by-time sweep cannot honor");
            return 0;
        }

        if (!fromMills.HasValue && !toMills.HasValue)
        {
            Logger.LogWarning("BulkDelete refused: the find has no parseable time range");
            return 0;
        }

        DateTime? from = fromMills.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(fromMills.Value).UtcDateTime
            : null;
        DateTime? to = toMills.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(toMills.Value).UtcDateTime
            : null;

        var scope = $"timestamp={from:O}..{to:O}";

        var profileSwitchIds = await ProfileSwitchIdsAsync(
            ByTimeRange(db.ProfileActivations.Where(a => a.LegacyId != null), from, to), ct);
        await DeleteSnapshotRowsAsync(profileSwitchIds, origin, ct);
        var activationIds = await ServedInRange(db.ProfileActivations, from, to)
            .AsNoTracking()
            .Select(a => a.Id)
            .ToListAsync(ct);

        // The served state spans go with the records: a window read serves them, so a window
        // delete that left them would leave them served.
        var total = await db.ExecuteInTransactionAsync(async token =>
        {
            // A Note written beside another record is served as part of it, so it is not counted apart.
            long deleted = -await ByTimeRange(
                    LegacyTreatmentTables.NotesServedWithAnotherRecord(db), from, to)
                .LongCountAsync(token);
            foreach (var table in DecomposedTables)
                deleted += await table.SoftDeleteInRangeAsync(db, from, to, scope, token);
            deleted += await db.AuditedSoftDeleteAsync(
                LegacyTreatmentTables.ServedStateSpansInWindow(db, from, to), _auditContext, scope, token);
            return deleted;
        }, ct: ct);

        await _profileActivationWriter.ResettleAfterRemovalAsync(activationIds, ct);
        Logger.LogInformation("BulkDelete: removed {Total} v4 treatment records from={From} to={To}", total, from, to);
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
        Table(DecomposedKind.Bolus, db => db.Boluses, ByTimeRange, StoredAt),
        Table(DecomposedKind.TempBasal, db => db.TempBasals, ByTimeRange, StoredAt),
        Table(DecomposedKind.CarbIntake, db => db.CarbIntakes, ByTimeRange, StoredAt),
        Table(DecomposedKind.BGCheck, db => db.BGChecks, ByTimeRange, StoredAt),
        Table(DecomposedKind.Note, db => db.Notes, ByTimeRange, StoredAt),
        Table(DecomposedKind.DeviceEvent, db => db.DeviceEvents, ByTimeRange, StoredAt),
        Table(DecomposedKind.BolusCalculation, db => db.BolusCalculations, ByTimeRange, StoredAt),
        Table(DecomposedKind.ProfileActivation, db => db.ProfileActivations, ServedInRange, StoredAt),
    ];

    private IDecomposedTable[]? _decomposedTables;

    /// <summary>The tables of <see cref="DecomposedTables"/>.</summary>
    private enum DecomposedKind
    {
        Bolus,
        TempBasal,
        CarbIntake,
        BGCheck,
        Note,
        DeviceEvent,
        BolusCalculation,
        ProfileActivation,
    }

    private DecomposedTable<T> Table<T>(
        DecomposedKind kind,
        Func<NocturneDbContext, DbSet<T>> rows,
        Func<IQueryable<T>, DateTime?, DateTime?, IQueryable<T>> inRange,
        Func<IQueryable<T>, IQueryable<StoredRow>> storedAt)
        where T : class, IV4Entity, ISourcedEntity, IAuditable, IUpstreamFingerprinted
        => new(_auditContext, kind, rows, inRange, storedAt);

    private static IQueryable<StoredRow> StoredAt<T>(IQueryable<T> rows) where T : IV4TimeSeriesEntity
        => rows.Select(e => new StoredRow(e.LegacyId!, e.Timestamp));

    private sealed record StoredRow(string LegacyId, DateTime At);

    private interface IDecomposedTable
    {
        DecomposedKind Kind { get; }

        /// <param name="source">Only this data source's rows, or every source's when null.</param>
        /// <remarks>
        /// A legacy id fans out to a handful of correlated rows, never a set. The per-record audit
        /// rows <see cref="AuditedBulkDeleteExtensions.AuditedSoftDeleteWithEntitiesAsync{T}"/>
        /// writes below its cap are the right shape for that.
        /// </remarks>
        Task<AuditedSoftDeleteResult<Guid>> SoftDeleteAsync(
            NocturneDbContext db, string[] legacyIds, string? source, string scope, DuplicateDelete duplicates, CancellationToken ct);

        Task<int> SoftDeleteInRangeAsync(NocturneDbContext db, DateTime? from, DateTime? to, string scope, CancellationToken ct);

        /// <summary>The ids of <paramref name="legacyIds"/> a live row of this table is stored under.</summary>
        Task<List<string>> LiveLegacyIdsAsync(NocturneDbContext db, string[] legacyIds, CancellationToken ct);

        Task<List<StoredRow>> StoredFromSourceAsync(NocturneDbContext db, string source, DateTime from, DateTime to, CancellationToken ct);

        /// <summary>The ids of <paramref name="legacyIds"/> that block re-creation, and whether a live row holds each.</summary>
        Task<IEnumerable<(string Key, bool Live)>> BlockingAsync(NocturneDbContext db, HashSet<string> legacyIds, CancellationToken ct);

        Task<List<(string LegacyId, string? Fingerprint)>> FingerprintsFromSourceAsync(
            NocturneDbContext db, string source, string[] legacyIds, CancellationToken ct);

        /// <summary>Stamps this source's rows that carry no fingerprint yet.</summary>
        Task StampUnfingerprintedAsync(
            NocturneDbContext db, string source, IReadOnlyDictionary<string, string> fingerprints, CancellationToken ct);

        /// <summary>This source's live rows and user tombstones under <paramref name="legacyIds"/>.</summary>
        Task<List<ClientIdRow>> ClientIdRowsAsync(NocturneDbContext db, string source, string[] legacyIds, CancellationToken ct);

        /// <summary>Moves this source's live rows and user tombstones from one legacy id to another.</summary>
        Task<int> RekeyAsync(NocturneDbContext db, string source, string from, string to, CancellationToken ct);

        /// <summary>Stages a copy of this source's user tombstones under <paramref name="legacyId"/> for each of <paramref name="copyTo"/>.</summary>
        Task AddTombstoneCopiesAsync(NocturneDbContext db, string source, string legacyId, string[] copyTo, CancellationToken ct);
    }

    private sealed class DecomposedTable<T>(
        IAuditContext auditContext,
        DecomposedKind kind,
        Func<NocturneDbContext, DbSet<T>> rowsOf,
        Func<IQueryable<T>, DateTime?, DateTime?, IQueryable<T>> inRange,
        Func<IQueryable<T>, IQueryable<StoredRow>> storedAt) : IDecomposedTable
        where T : class, IV4Entity, ISourcedEntity, IAuditable, IUpstreamFingerprinted
    {
        public DecomposedKind Kind => kind;

        public Task<AuditedSoftDeleteResult<Guid>> SoftDeleteAsync(
            NocturneDbContext db, string[] legacyIds, string? source, string scope, DuplicateDelete duplicates, CancellationToken ct)
            => db.AuditedSoftDeleteWithIdsAsync(
                rowsOf(db).Where(e => e.LegacyId != null && legacyIds.Contains(e.LegacyId)
                             && (source == null || e.DataSource == source)),
                auditContext, scope, ct, duplicates);

        public Task<int> SoftDeleteInRangeAsync(NocturneDbContext db, DateTime? from, DateTime? to, string scope, CancellationToken ct)
            => db.AuditedSoftDeleteAsync(inRange(rowsOf(db), from, to), auditContext, scope, ct);

        public Task<List<string>> LiveLegacyIdsAsync(NocturneDbContext db, string[] legacyIds, CancellationToken ct)
            => rowsOf(db).AsNoTracking()
                .Where(e => e.LegacyId != null && legacyIds.Contains(e.LegacyId))
                .Select(e => e.LegacyId!)
                .Distinct()
                .ToListAsync(ct);

        public Task<List<StoredRow>> StoredFromSourceAsync(
            NocturneDbContext db, string source, DateTime from, DateTime to, CancellationToken ct)
            => storedAt(inRange(rowsOf(db).AsNoTracking(), from, to)
                    .Where(e => e.DataSource == source && e.LegacyId != null))
                .ToListAsync(ct);

        public async Task<IEnumerable<(string Key, bool Live)>> BlockingAsync(
            NocturneDbContext db, HashSet<string> legacyIds, CancellationToken ct)
        {
            var blocks = await db.GetBlockingLegacyIdsAsync<T>(legacyIds, ct);
            return blocks.Held.Select(id => (id, !blocks.DeletedByUser.Contains(id)));
        }

        public async Task<List<(string LegacyId, string? Fingerprint)>> FingerprintsFromSourceAsync(
            NocturneDbContext db, string source, string[] legacyIds, CancellationToken ct)
            => (await FromSource(rowsOf(db).AsNoTracking(), source, legacyIds)
                    .Select(e => new { LegacyId = e.LegacyId!, e.UpstreamFingerprint })
                    .ToListAsync(ct))
                .Select(e => (e.LegacyId, e.UpstreamFingerprint))
                .ToList();

        public async Task StampUnfingerprintedAsync(
            NocturneDbContext db, string source, IReadOnlyDictionary<string, string> fingerprints, CancellationToken ct)
        {
            foreach (var (legacyId, fingerprint) in fingerprints)
            {
                await FromSource(rowsOf(db), source, [legacyId])
                    .Where(e => e.UpstreamFingerprint == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(e => e.UpstreamFingerprint, fingerprint), ct);
            }
        }

        public async Task<List<ClientIdRow>> ClientIdRowsAsync(NocturneDbContext db, string source, string[] legacyIds, CancellationToken ct)
        {
            var stored = FromSource(BlockingRows(db).AsNoTracking(), source, legacyIds);
            var live = await storedAt(stored.Where(e => e.DeletedAt == null)).ToListAsync(ct);
            var deleted = await storedAt(stored.Where(e => e.DeletedAt != null)).ToListAsync(ct);
            return
            [
                .. live.Select(r => new ClientIdRow(r.LegacyId, r.At, Live: true)),
                .. deleted.Select(r => new ClientIdRow(r.LegacyId, r.At, Live: false)),
            ];
        }

        public Task<int> RekeyAsync(NocturneDbContext db, string source, string from, string to, CancellationToken ct)
            => FromSource(BlockingRows(db), source, [from]).ExecuteUpdateAsync(u => u.SetProperty(e => e.LegacyId, to), ct);

        public async Task AddTombstoneCopiesAsync(NocturneDbContext db, string source, string legacyId, string[] copyTo, CancellationToken ct)
        {
            if (copyTo.Length == 0)
                return;

            var tombstones = await FromSource(BlockingRows(db).AsNoTracking(), source, [legacyId])
                .Where(e => e.DeletedAt != null).ToListAsync(ct);
            AddTombstoneCopies(db, tombstones, copyTo, (e, id) => e.LegacyId = id);
        }

        private IQueryable<T> BlockingRows(NocturneDbContext db) =>
            rowsOf(db).IgnoreQueryFilters().Where(e => e.TenantId == db.TenantId).WhereBlocksRecreation();

        private static IQueryable<T> FromSource(IQueryable<T> query, string source, string[] legacyIds)
            => query.Where(e => e.DataSource == source && e.LegacyId != null && legacyIds.Contains(e.LegacyId));
    }

    /// <summary>
    /// The activations a window delete takes: those decomposed from a treatment, which every window
    /// read serves. A connector's are not treatments.
    /// </summary>
    private static IQueryable<ProfileActivationEntity> ServedInRange(
        IQueryable<ProfileActivationEntity> rows, DateTime? from, DateTime? to) =>
        ByTimeRange(rows, from, to).Where(a => a.LegacyId != null);

    private static IQueryable<T> ByTimeRange<T>(IQueryable<T> rows, DateTime? from, DateTime? to)
        where T : IV4TimeSeriesEntity
    {
        if (from.HasValue)
            rows = rows.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue)
            rows = rows.Where(e => e.Timestamp <= to.Value);
        return rows;
    }
}

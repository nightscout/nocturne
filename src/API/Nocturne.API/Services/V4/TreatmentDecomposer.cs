using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.API.Helpers;
using Nocturne.API.Services.ProfileActivations;
using Nocturne.API.Services.Treatments;
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
/// <seealso cref="IStateSpanService"/>
internal class TreatmentDecomposer : DecomposerBase, ITreatmentDecomposer
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
    private readonly IActiveProfileResolver _activeProfileResolver;
    private readonly IPatientInsulinRepository _insulinRepo;
    private readonly RelativeTempBasalRates _relativeTempBasalRates;
    private readonly IAuditContext _auditContext;
    private readonly ILegacyReadCacheInvalidation? _legacyReads;

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
        IActiveProfileResolver activeProfileResolver,
        IPatientInsulinRepository insulinRepo,
        RelativeTempBasalRates relativeTempBasalRates,
        IAuditContext auditContext,
        ILogger<TreatmentDecomposer> logger,
        IDeviceEventReactor? deviceEventReactor = null,
        ILegacyReadCacheInvalidation? legacyReads = null)
        : base(logger)
    {
        _legacyReads = legacyReads;
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
    internal static void NormalizeIdentity(Treatment treatment)
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
    /// <see cref="Treatment.Absolute"/> as it read before it stopped reading through
    /// <see cref="Treatment.Rate"/> beside a percent, so ids and fingerprints already stored still match.
    /// </summary>
    private static double? HashedAbsolute(Treatment t) => t.Absolute ?? t.Rate;

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
    /// and the parsed device-event type, as <see cref="ClassifyTreatment"/> reads them.
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

        public bool WritesTempBasal => DelegateToStateSpan && !IsProfileSwitch && !IsOverride && !IsTemporaryTarget;
    }

    /// <summary>
    /// Classifies which v4 records a legacy <see cref="Treatment"/> decomposes into, based on its
    /// <see cref="Treatment.EventType"/> and the insulin/carb data present. This mapping is shared
    /// by <see cref="DecomposeBatchAsync"/> and <see cref="CanRepublish"/> so it lives in exactly
    /// one place.
    /// </summary>
    private TreatmentClassification ClassifyTreatment(Treatment treatment)
    {
        var eventType = treatment.EventType?.Trim();
        var sanitizedEventTypeForLog = LogText.Sanitize(treatment.EventType);
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

    private static V4Models.ProfileActivation BuildProfileActivation(Treatment treatment, Guid? correlationId)
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
        return model;
    }

    private static StateSpan BuildOverrideSpan(Treatment treatment) => new()
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

    private static StateSpan BuildTemporaryTargetSpan(Treatment treatment)
    {
        var isCancelled = treatment.Duration is null or 0
            || string.Equals(treatment.EventType, "Temporary Target Cancel", StringComparison.OrdinalIgnoreCase);

        return new StateSpan
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
            AdditionalProperties = UploadedPreBolus(
                UploadedEventType(
                    TreatmentUploadedTimestamp.AddTo(TreatmentClientId.ToRecord(treatment), treatment),
                    treatment, keepEventType, TreatmentTypes.CorrectionBolus),
                treatment, keepEventType),
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

    /// <remarks>A wizard's calculation keeps the preBolus itself.</remarks>
    private static Dictionary<string, object?>? UploadedPreBolus(
        Dictionary<string, object?>? record, Treatment treatment, bool keep) =>
        keep ? TreatmentUploadedPreBolus.AddTo(record, treatment) : record;

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

    internal static bool IsMealEventType(string? eventType) =>
        string.Equals(eventType, "Meal Bolus", StringComparison.OrdinalIgnoreCase)
        || string.Equals(eventType, "Snack Bolus", StringComparison.OrdinalIgnoreCase)
        || string.Equals(eventType, "Combo Bolus", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Removes what an earlier upload of each treatment wrote under its id that its classification
    /// now leaves out: a Note that became an effective profile switch, the carb half of a meal edited
    /// to insulin alone, the span of a switch re-sent as a note. Only the tables the treatment no
    /// longer writes lose rows, so a re-upload of the same kind removes nothing, and every record a
    /// treatment writes beside another (a wizard's bolus, a meal's two halves, a note) is kept.
    /// <paramref name="stored"/> and <paramref name="storedSpans"/> are the tables and span categories
    /// each legacy id's live rows are in.
    /// </summary>
    /// <remarks>
    /// The removal is system-attributed: a user tombstone would stop a later upload that writes the
    /// table again from re-creating the row (<see cref="SoftDeleteDedupExtensions.WhereBlocksRecreation{TEntity}"/>).
    /// A treatment Nocturne does not store writes nothing and removes nothing, and a bolus or carbs
    /// under a temp basal's id leaves the temp basal (<see cref="Writing"/>).
    /// </remarks>
    private async Task DropAbsentAsync(
        IReadOnlyList<(Treatment Treatment, TreatmentClassification Classification)> written,
        IReadOnlyDictionary<string, HashSet<DecomposedKind>> stored,
        IReadOnlyDictionary<string, HashSet<string>> storedSpans,
        WriteOrigin origin,
        CancellationToken ct)
    {
        var tableDrops = new Dictionary<DecomposedKind, List<string>>();
        var spanDrops = new List<string>();
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

            if (storedSpans.ContainsKey(id) && !c.IsOverride && !c.IsTemporaryTarget)
                spanDrops.Add(id);
        }

        if (tableDrops.Count == 0 && spanDrops.Count == 0)
            return;

        await using var db = await _contexts.CreateAsync(ct);
        List<Guid> deviceEventIds = [];
        List<Guid> activationIds = [];
        // Each table's drop commits on its own, so what committed is reported even when a later drop fails.
        List<Type> committed = [];
        try
        {
            using (SystemAttributedBatchWrites(_auditContext))
            {
                foreach (var table in DecomposedTables)
                {
                    if (!tableDrops.TryGetValue(table.Kind, out var dropIds))
                        continue;

                    var dropped = await table.SoftDeleteAsync(
                        db, [.. dropIds], source: null, "treatment_reclassified", DuplicateDelete.EveryCopy, ct);
                    committed.Add(table.Model);
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
        }
        finally
        {
            await ReportWrittenAsync(committed);
        }

        if (tableDrops.ContainsKey(DecomposedKind.ProfileActivation))
            await _profileActivationWriter.ResettleAfterRemovalAsync(activationIds, ct);

        if (origin == WriteOrigin.Live)
            await ReactToDeletedDeviceEventsAsync(db, deviceEventIds, ct);
    }

    /// <summary>
    /// The categories of the spans a treatment writes in place of a table row. A switch is a
    /// <see cref="V4Models.ProfileActivation"/>.
    /// </summary>
    private static readonly string[] TreatmentSpanCategories =
    [
        nameof(StateSpanCategory.Override),
        nameof(StateSpanCategory.TemporaryTarget),
    ];

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
            metadata[StateSpanMetadataExtensions.UploadedDurationKey] = duration;

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
            metadata[StateSpanMetadataExtensions.UploadedDurationKey] = duration;

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
            metadata[StateSpanMetadataExtensions.UploadedDurationKey] = duration;

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

    #region Batch Decomposition

    /// <inheritdoc />
    /// <remarks>
    /// Four phases, each a fixed number of queries whatever the batch's size: the preload (the rows
    /// stored under the batch's legacy ids, its pumps and their patient-device links, the meal carbs
    /// paired with its stored boluses, and the pump-mode spans around its window), the models built in
    /// memory, one bulk upsert per table (profile activations settled in memory first), then the pump
    /// suspension fold over the preloaded spans and the span writes in bulk. A legacy id the batch sends
    /// more than once is written as its last version that writes anything, which is what one at a time
    /// leaves.
    /// </remarks>
    public async Task<TreatmentDecomposition> DecomposeBatchAsync(
        IReadOnlyList<Treatment> treatments, WriteOrigin origin, CancellationToken ct = default,
        TreatmentDecomposition? into = null)
    {
        var result = into ?? new TreatmentDecomposition();
        if (treatments.Count == 0)
            return result;

        foreach (var treatment in treatments)
            NormalizeIdentity(treatment);
        using var restated = OpenRestatedScope(treatments);

        var stored = await LoadStoredAsync(treatments, ct);
        var storedCorrelations = stored.CorrelationIds();
        var correlationIds = new Dictionary<string, Guid>(storedCorrelations, StringComparer.Ordinal);
        var batch = treatments
            .Select((t, i) => new BatchTreatment(i, t, ClassifyTreatment(t), CorrelationIdFor(t.Id, correlationIds)))
            .ToList();
        result.Begin([.. batch.Select(b => b.CorrelationId)]);
        result.CorrelationId = (batch.FirstOrDefault(b => !b.Classification.ProducesNothing) ?? batch[0]).CorrelationId;

        var writing = Writing(batch, stored, result);
        var devices = await ResolvePumpsAsync(writing, ct);
        var links = await _deviceService.GetPatientDeviceLinksAsync(devices.Values, ct);
        var mealCarbs = await LoadMealCarbsAsync(writing, stored, storedCorrelations, ct);
        var spans = await PreloadSpansAsync(writing, ct);

        var models = new BatchModels();
        foreach (var treatment in writing)
        {
            try
            {
                Build(treatment, devices, links, stored, storedCorrelations, mealCarbs, models);
            }
            catch (TreatmentMappingException ex)
            {
                result.Reject(treatment.Index, ex);
            }
        }

        // A record belongs to the treatment its legacy id names, not to its group: a meal's bolus and
        // its separately keyed carbs share a group. A record whose legacy id names no treatment of the
        // batch belongs to its group's: one of a treatment sent without an id, whose group is its own,
        // or carbs written under the key of their stored meal half (Build).
        var byLegacyId = writing.Where(w => w.Treatment.Id is not null)
            .ToDictionary(w => w.Treatment.Id!, w => w.Index, StringComparer.Ordinal);
        var groups = writing.Where(w => !result.Rejected.ContainsKey(w.Index))
            .GroupBy(w => w.CorrelationId).ToDictionary(g => g.Key, g => g.Last().Index);
        int? TreatmentOf(V4Models.IV4Record record) =>
            record.LegacyId is { } id && byLegacyId.TryGetValue(id, out var index) ? index
            : record.CorrelationId is { } group && groups.TryGetValue(group, out index) ? index
            : null;

        using (origin == WriteOrigin.Live ? null : SystemAttributedBatchWrites(_auditContext))
        {
            await DropAbsentAsync(models.Classified, stored.Kinds(), stored.SpanKeys, origin, ct);
            if (models.AbsentMealCarbs.Count > 0)
                await DeleteByLegacyIdsAsync([.. models.AbsentMealCarbs.Distinct(StringComparer.Ordinal)], origin, ct);
            foreach (var meal in models.UnkeyedMealCarbs)
                await _carbIntakeRepository.UpdateAsync(meal.Id, meal, origin, ct);
            await WriteActivationsAsync(models.Activations, stored, result, TreatmentOf, origin, ct);
            await SettleTempBasalsAsync(models.TempBasals, ct);

            if (models.Boluses.Count > 0)
                await _patientDeviceStamper.StampAsync(models.Boluses, V4Models.DeviceAttributionCategories.Bolus, batchSource: null, ct);
            if (models.TempBasals.Count > 0)
                await _patientDeviceStamper.StampAsync(models.TempBasals, V4Models.DeviceAttributionCategories.TempBasal, batchSource: null, ct);
            if (models.DeviceEvents.Count > 0)
                await _patientDeviceStamper.StampDeviceEventsAsync(models.DeviceEvents, batchSource: null, ct);

            var calculations = await UpsertTableAsync(_bolusCalculationRepository, models.BolusCalculations, result, TreatmentOf, origin, ct);
            LinkCalculations(models, calculations);
            await UpsertTableAsync(_bolusRepository, models.Boluses, result, TreatmentOf, origin, ct);
            var carbs = await UpsertTableAsync(_carbIntakeRepository, models.CarbIntakes, result, TreatmentOf, origin, ct);
            await UpsertTableAsync(_bgCheckRepository, models.BGChecks, result, TreatmentOf, origin, ct);
            await UpsertTableAsync(_noteRepository, models.Notes, result, TreatmentOf, origin, ct);
            await UpsertTableAsync(_deviceEventRepository, models.DeviceEvents, result, TreatmentOf, origin, ct);
            await UpsertTableAsync(_tempBasalRepository, models.TempBasals, result, TreatmentOf, origin, ct);
            await WriteFoodLinesAsync(carbs, models.FoodLines, result, TreatmentOf, ct);

            var sources = new Dictionary<StateSpan, BatchTreatment>(ReferenceEqualityComparer.Instance);
            FoldPumpSuspensions([.. models.Suspensions.Where(s => !result.Rejected.ContainsKey(s.Treatment.Index))], spans, sources);
            foreach (var (treatment, span) in models.Spans.Where(s => !result.Rejected.ContainsKey(s.Treatment.Index)))
            {
                spans.Open(span);
                sources[span] = treatment;
            }
            foreach (var (span, source) in sources)
                result.SpanOwners[span] = source.CorrelationId;

            await spans.WriteAsync(_stateSpanService, result, result.SpanOwners, ct, (span, refusal) =>
            {
                if (sources.TryGetValue(span, out var source))
                    result.Reject(source.Index, refusal);
                else
                    ExceptionDispatchInfo.Capture(refusal).Throw();
            });
        }

        return result;
    }

    /// <summary>A treatment of a batch, with the classification and the correlation id its records are written under.</summary>
    private sealed record BatchTreatment(int Index, Treatment Treatment, TreatmentClassification Classification, Guid CorrelationId)
    {
        public DateTime Start => DateTimeOffset.FromUnixTimeMilliseconds(Treatment.Mills).UtcDateTime;
    }

    /// <summary>The models a batch builds, before any is written.</summary>
    private sealed class BatchModels
    {
        public List<V4Models.Bolus> Boluses { get; } = [];
        public List<V4Models.CarbIntake> CarbIntakes { get; } = [];
        public List<V4Models.BGCheck> BGChecks { get; } = [];
        public List<V4Models.Note> Notes { get; } = [];
        public List<V4Models.BolusCalculation> BolusCalculations { get; } = [];
        public List<V4Models.DeviceEvent> DeviceEvents { get; } = [];
        public List<V4Models.TempBasal> TempBasals { get; } = [];
        public List<(BatchTreatment Treatment, V4Models.ProfileActivation Model)> Activations { get; } = [];
        public List<(BatchTreatment Treatment, StateSpan Span)> Spans { get; } = [];
        public List<(BatchTreatment Treatment, DeviceEventType Type)> Suspensions { get; } = [];

        /// <summary>The treatments whose carbs keep their food as a line, by the group their carb intake is written under.</summary>
        public Dictionary<Guid, Treatment> FoodLines { get; } = [];

        /// <summary>The legacy ids whose bolus is linked to the calculation written beside it.</summary>
        public HashSet<string> CalculationLinks { get; } = new(StringComparer.Ordinal);

        /// <summary>The legacy ids of meal carbs a connector keyed apart from a bolus whose treatment no longer carries carbs.</summary>
        public List<string> AbsentMealCarbs { get; } = [];

        /// <summary>
        /// Meal carbs stored with no legacy id beside a bolus the batch updates, keyed to the bolus's
        /// legacy id so the batch's carbs update them in place.
        /// </summary>
        public List<V4Models.CarbIntake> UnkeyedMealCarbs { get; } = [];

        public List<(Treatment Treatment, TreatmentClassification Classification)> Classified { get; } = [];
    }

    /// <summary>
    /// The treatments of the batch that write: per legacy id the last version that writes anything,
    /// and every treatment with no legacy id. One that Nocturne does not store writes nothing and keeps
    /// nothing from being written. A bolus or carbs under a temp basal's legacy id writes nothing,
    /// whether the temp basal is stored or an earlier version in the batch: an AAPS that read a temp
    /// basal served with insulin holds it as a bolus under the temp basal's identifier (see
    /// <see cref="Treatment.Insulin"/>) and edits it as one, and writing that edit would put a bolus
    /// beside every temp basal it touches.
    /// </summary>
    private List<BatchTreatment> Writing(
        IReadOnlyList<BatchTreatment> batch, StoredRows stored, TreatmentDecomposition result)
    {
        var heldByTempBasal = stored.TempBasals.Select(t => t.LegacyId!).ToHashSet(StringComparer.Ordinal);
        var last = new Dictionary<string, BatchTreatment>(StringComparer.Ordinal);
        var writing = new List<BatchTreatment>();
        var unsupported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var treatment in batch)
        {
            var c = treatment.Classification;
            if (c.ProducesNothing)
            {
                result.SkippedUnsupported++;
                result.WritesNothing(treatment.Index);
                unsupported.Add(treatment.Treatment.EventType ?? "(none)");
                continue;
            }

            if (treatment.Treatment.Id is not { } id)
            {
                writing.Add(treatment);
                continue;
            }

            if ((c.ProduceBolus || c.ProduceCarbIntake) && heldByTempBasal.Contains(id))
            {
                Logger.LogWarning(
                    "Refused to write a {EventType} under temp basal {LegacyId}",
                    LogText.Sanitize(treatment.Treatment.EventType), LogText.Sanitize(id));
                result.SkippedUnsupported++;
                result.WritesNothing(treatment.Index);
                continue;
            }

            last[id] = treatment;
            if (c.WritesTempBasal)
                heldByTempBasal.Add(id);
            else
                heldByTempBasal.Remove(id);
        }

        if (unsupported.Count > 0)
            Logger.LogWarning(
                "Skipped {Count} treatments whose event type Nocturne does not store: {EventTypes}",
                result.SkippedUnsupported, string.Join(", ", unsupported.Select(LogText.Sanitize)));

        return [.. writing.Concat(last.Values).OrderBy(t => t.Index)];
    }

    /// <summary>The live rows stored under the batch's legacy ids, one query per table, and the span keys among them.</summary>
    private sealed record StoredRows(
        IReadOnlyList<V4Models.Bolus> Boluses,
        IReadOnlyList<V4Models.CarbIntake> CarbIntakes,
        IReadOnlyList<V4Models.BGCheck> BGChecks,
        IReadOnlyList<V4Models.Note> Notes,
        IReadOnlyList<V4Models.BolusCalculation> BolusCalculations,
        IReadOnlyList<V4Models.DeviceEvent> DeviceEvents,
        IReadOnlyList<V4Models.TempBasal> TempBasals,
        IReadOnlyList<V4Models.ProfileActivation> ProfileActivations,
        IReadOnlyDictionary<string, HashSet<string>> SpanKeys)
    {
        /// <summary>
        /// The correlation id each legacy id's rows carry, from the first table holding a non-empty one,
        /// in the order the tables are listed here.
        /// </summary>
        public Dictionary<string, Guid> CorrelationIds()
        {
            var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
            IEnumerable<V4Models.IV4Record>[] tables =
                [Boluses, CarbIntakes, BGChecks, Notes, BolusCalculations, DeviceEvents, TempBasals, ProfileActivations];
            foreach (var row in tables.SelectMany(t => t))
            {
                if (row.LegacyId is { } legacyId && row.CorrelationId is { } correlationId && correlationId != Guid.Empty)
                    ids.TryAdd(legacyId, correlationId);
            }

            return ids;
        }

        /// <summary>The tables of <see cref="DecomposedTables"/> a live row of each legacy id is stored in.</summary>
        public Dictionary<string, HashSet<DecomposedKind>> Kinds()
        {
            var kinds = new Dictionary<string, HashSet<DecomposedKind>>(StringComparer.Ordinal);
            void Add(IEnumerable<V4Models.IV4Record> rows, DecomposedKind kind)
            {
                foreach (var legacyId in rows.Select(r => r.LegacyId).OfType<string>())
                {
                    if (!kinds.TryGetValue(legacyId, out var held))
                        kinds[legacyId] = held = [];
                    held.Add(kind);
                }
            }

            Add(Boluses, DecomposedKind.Bolus);
            Add(TempBasals, DecomposedKind.TempBasal);
            Add(CarbIntakes, DecomposedKind.CarbIntake);
            Add(BGChecks, DecomposedKind.BGCheck);
            Add(Notes, DecomposedKind.Note);
            Add(DeviceEvents, DecomposedKind.DeviceEvent);
            Add(BolusCalculations, DecomposedKind.BolusCalculation);
            Add(ProfileActivations, DecomposedKind.ProfileActivation);
            return kinds;
        }
    }

    private static T? StoredUnder<T>(IEnumerable<T> rows, string? legacyId) where T : V4Models.IV4Record =>
        legacyId is null ? default : rows.FirstOrDefault(r => r.LegacyId == legacyId);

    private async Task<StoredRows> LoadStoredAsync(IReadOnlyList<Treatment> treatments, CancellationToken ct)
    {
        var ids = treatments.Select(t => t.Id).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
            return new StoredRows([], [], [], [], [], [], [], [], new Dictionary<string, HashSet<string>>());

        var spanCategories = TreatmentSpanCategories;
        Dictionary<string, HashSet<string>> spanKeys;
        await using (var db = await _contexts.CreateAsync(ct))
        {
            spanKeys = (await db.StateSpans.AsNoTracking()
                    .Where(s => s.OriginalId != null && ids.Contains(s.OriginalId) && spanCategories.Contains(s.Category))
                    .Select(s => new { s.OriginalId, s.Category })
                    .ToListAsync(ct))
                .GroupBy(s => s.OriginalId!)
                .ToDictionary(g => g.Key, g => g.Select(s => s.Category).ToHashSet(), StringComparer.Ordinal);
        }

        return new StoredRows(
            [.. await _bolusRepository.GetByLegacyIdsAsync(ids, ct)],
            [.. await _carbIntakeRepository.GetByLegacyIdsAsync(ids, ct)],
            [.. await _bgCheckRepository.GetByLegacyIdsAsync(ids, ct)],
            [.. await _noteRepository.GetByLegacyIdsAsync(ids, ct)],
            [.. await _bolusCalculationRepository.GetByLegacyIdsAsync(ids, ct)],
            [.. await _deviceEventRepository.GetByLegacyIdsAsync(ids, ct)],
            [.. await _tempBasalRepository.GetByLegacyIdsAsync(ids, ct)],
            [.. await _profileActivationRepository.GetByLegacyIdsAsync(ids, ct)],
            spanKeys);
    }

    /// <summary>
    /// The pump each writing treatment that attributes a record to one names, created in the device
    /// registry if new, in one call.
    /// </summary>
    private async Task<Dictionary<int, Guid?>> ResolvePumpsAsync(IReadOnlyList<BatchTreatment> writing, CancellationToken ct)
    {
        var attributed = writing
            .Where(w => w.Classification is { ProduceBolus: true } or { ProduceDeviceEvent: true } or { IsProfileSwitch: true }
                || w.Classification.WritesTempBasal)
            .ToList();
        if (attributed.Count == 0)
            return [];

        var ids = await _deviceService.ResolveManyAsync(
            [.. attributed.Select(w => new DeviceSighting(
                V4Models.DeviceCategory.InsulinPump, w.Treatment.PumpType, w.Treatment.PumpSerial, w.Treatment.Mills))],
            ct);
        return attributed.Zip(ids).ToDictionary(p => p.First.Index, p => p.Second);
    }

    /// <summary>
    /// Per stored legacy id whose bolus is stored under it in its group, the carb intake served with that
    /// bolus as a Meal Bolus, in one query. The treatment's carbs update that carb intake, which keeps
    /// the legacy and sync ids its source re-syncs it by, since a connector keys a meal's halves apart.
    /// </summary>
    private async Task<Dictionary<string, V4Models.CarbIntake>> LoadMealCarbsAsync(
        IReadOnlyList<BatchTreatment> writing, StoredRows stored, IReadOnlyDictionary<string, Guid> storedCorrelations,
        CancellationToken ct)
    {
        var paired = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var treatment in writing)
        {
            if (treatment.Treatment.Id is { } id
                && storedCorrelations.TryGetValue(id, out var correlationId)
                && StoredUnder(stored.Boluses, id) is { CorrelationId: { } bolusGroup }
                && bolusGroup == correlationId)
                paired[id] = correlationId;
        }

        if (paired.Count == 0)
            return [];

        var carbs = (await _carbIntakeRepository.GetByCorrelationIdsAsync(paired.Values.Distinct(), ct)).ToList();
        var meals = new Dictionary<string, V4Models.CarbIntake>(StringComparer.Ordinal);
        foreach (var (id, correlationId) in paired)
        {
            if (LegacyTreatmentTables.MealHalf(carbs.Where(c => c.CorrelationId == correlationId)) is { } meal)
                meals[id] = meal;
        }

        return meals;
    }

    /// <summary>
    /// The pump-mode spans the suspension fold reads, in one query when the batch carries a pump suspend
    /// or resume: every open one, those overlapping the batch's window, and the first after it.
    /// </summary>
    private async Task<SpanState> PreloadSpansAsync(IReadOnlyList<BatchTreatment> writing, CancellationToken ct)
    {
        var at = writing.Where(w => w.Classification is { ProduceDeviceEvent: true, ParsedDeviceEventType: DeviceEventType.PumpSuspend or DeviceEventType.PumpResume })
            .Select(w => w.Start)
            .ToList();
        return new SpanState(at.Count == 0
            ? []
            : await _stateSpanService.GetStateSpansAroundAsync(
                StateSpanCategory.PumpMode, at.Min(), at.Max(), [null], SuspensionSuccessorReach, ct));
    }

    /// <summary>How many pump-mode spans after the batch's window the suspension fold reads.</summary>
    private const int SuspensionSuccessorReach = 10;

    /// <summary>
    /// Builds every model one treatment decomposes into, adding them to <paramref name="models"/> only
    /// once all are built, so a treatment refused for its data adds none.
    /// </summary>
    private void Build(
        BatchTreatment batched,
        IReadOnlyDictionary<int, Guid?> devices,
        IPatientDeviceLinks links,
        StoredRows stored,
        IReadOnlyDictionary<string, Guid> storedCorrelations,
        IReadOnlyDictionary<string, V4Models.CarbIntake> mealCarbs,
        BatchModels models)
    {
        var (_, treatment, c, correlationId) = batched;
        var id = treatment.Id;
        var isStored = id is not null && storedCorrelations.ContainsKey(id);
        var deviceId = devices.GetValueOrDefault(batched.Index);
        var patientDeviceId = links.Resolve(deviceId, treatment.Mills);

        // A re-resolution that has since become ambiguous cannot displace the stored link.
        T Attributed<T>(T model, IEnumerable<T> storedRows, Action<T, Guid?> setDevice)
            where T : V4Models.IV4Record, V4Models.IDeviceAttributed
        {
            setDevice(model, deviceId);
            model.PatientDeviceId = patientDeviceId ?? StoredUnder(storedRows, id)?.PatientDeviceId;
            return model;
        }

        V4Models.Bolus? bolus = null;
        if (c.ProduceBolus)
        {
            bolus = Mapped(() => MapToBolus(treatment, correlationId, c.KeepsUploadedEventType));
            if (IsAlgorithmBolus(treatment))
            {
                bolus.Kind = V4Models.BolusKind.Algorithm;
                bolus.Automatic = true;
            }
            Attributed(bolus, stored.Boluses, (m, d) => m.DeviceId = d);
        }

        V4Models.CarbIntake? carbs = null;
        V4Models.CarbIntake? unkeyedMeal = null;
        if (c.ProduceCarbIntake)
        {
            carbs = Mapped(() => MapToCarbIntake(treatment, correlationId, c.KeepsUploadedEventType));
            if (isStored && StoredUnder(stored.CarbIntakes, id) is null && mealCarbs.GetValueOrDefault(id!) is { } meal)
            {
                if (string.IsNullOrEmpty(meal.LegacyId))
                    unkeyedMeal = meal;
                carbs.LegacyId = string.IsNullOrEmpty(meal.LegacyId) ? id : meal.LegacyId;
                if (!string.IsNullOrEmpty(meal.SyncIdentifier))
                    carbs.SyncIdentifier = meal.SyncIdentifier;
            }
        }

        var bgCheck = c.ProduceBGCheck ? Mapped(() => MapToBGCheck(treatment, correlationId)) : null;
        var note = c.ProduceNote ? Mapped(() => MapToNote(treatment, correlationId, c.IsAnnouncement)) : null;
        var calculation = c.ProduceBolusCalc ? Mapped(() => MapToBolusCalculation(treatment, correlationId)) : null;
        var deviceEvent = c.ProduceDeviceEvent
            ? Attributed(Mapped(() => MapToDeviceEvent(treatment, correlationId, c.ParsedDeviceEventType)), stored.DeviceEvents, (m, d) => m.DeviceId = d)
            : null;

        V4Models.TempBasal? tempBasal = null;
        if (c.WritesTempBasal)
        {
            tempBasal = Attributed(Mapped(() => MapToTempBasal(treatment, correlationId)), stored.TempBasals, (m, d) => m.DeviceId = d);
            SettleAgainstStored(tempBasal, treatment, StoredUnder(stored.TempBasals, id));
        }

        var activation = c.IsProfileSwitch
            ? Attributed(Mapped(() => BuildProfileActivation(treatment, correlationId)), stored.ProfileActivations, (m, d) => m.DeviceId = d)
            : null;
        var span = c.IsOverride ? Mapped(() => BuildOverrideSpan(treatment))
            : c.IsTemporaryTarget ? Mapped(() => BuildTemporaryTargetSpan(treatment))
            : null;

        if (bolus is not null) models.Boluses.Add(bolus);
        if (carbs is not null)
        {
            models.CarbIntakes.Add(carbs);
            models.FoodLines[correlationId] = treatment;
        }
        if (unkeyedMeal is not null)
        {
            unkeyedMeal.LegacyId = id;
            models.UnkeyedMealCarbs.Add(unkeyedMeal);
        }
        if (bgCheck is not null) models.BGChecks.Add(bgCheck);
        if (note is not null) models.Notes.Add(note);
        if (calculation is not null) models.BolusCalculations.Add(calculation);
        if (deviceEvent is not null)
        {
            models.DeviceEvents.Add(deviceEvent);
            if (c.ParsedDeviceEventType is DeviceEventType.PumpSuspend or DeviceEventType.PumpResume)
                models.Suspensions.Add((batched, c.ParsedDeviceEventType));
        }
        if (tempBasal is not null) models.TempBasals.Add(tempBasal);
        if (activation is not null) models.Activations.Add((batched, activation));
        if (span is not null) models.Spans.Add((batched, span));
        if (bolus is not null && calculation is not null && id is not null) models.CalculationLinks.Add(id);
        if (!c.ProduceCarbIntake && isStored
            && mealCarbs.GetValueOrDefault(id!) is { LegacyId: { Length: > 0 } carbsKey } && carbsKey != id)
            models.AbsentMealCarbs.Add(carbsKey);
        models.Classified.Add((treatment, c));
    }

    /// <summary>
    /// Writes the batch's profile activations through <see cref="ProfileActivationWriter.WriteBatchAsync"/>,
    /// withholding those whose legacy id the user deleted, which the store would refuse.
    /// </summary>
    private async Task WriteActivationsAsync(
        List<(BatchTreatment Treatment, V4Models.ProfileActivation Model)> activations, StoredRows stored,
        TreatmentDecomposition result, Func<V4Models.IV4Record, int?> treatmentOf, WriteOrigin origin, CancellationToken ct)
    {
        if (activations.Count == 0)
            return;

        var unstored = activations
            .Select(a => a.Model.LegacyId)
            .OfType<string>()
            .Where(id => StoredUnder(stored.ProfileActivations, id) is null)
            .ToList();
        var deleted = unstored.Count == 0
            ? new HashSet<string>()
            : await _profileActivationRepository.GetHeldLegacyIdsAsync(unstored, ct);

        var writes = new List<ProfileActivationWrite>();
        foreach (var (_, model) in activations)
        {
            if (model.LegacyId is { } legacyId && deleted.Contains(legacyId))
            {
                result.SkippedDeleted++;
                continue;
            }

            writes.Add(new ProfileActivationWrite(model, StoredUnder(stored.ProfileActivations, model.LegacyId)));
        }

        if (writes.Count == 0)
            return;

        await _patientDeviceStamper.StampAsync(
            [.. writes.Select(w => w.Activation)], V4Models.DeviceAttributionCategories.ProfileActivation, batchSource: null, ct);
        await _profileActivationWriter.WriteBatchAsync(writes, async settled =>
        {
            var written = await UpsertTableAsync(_profileActivationRepository, [.. settled], result, treatmentOf, origin, ct);
            var byId = written.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
            return [.. settled.Select(a => byId.GetValueOrDefault(a.Id))];
        }, ct);
    }

    /// <summary>
    /// Gives each temp basal the insulin of the activation running at its start, else the primary bolus
    /// insulin, then resolves the rates of those uploaded as a percent. Runs after the batch's
    /// activations are written, so a temp basal sees the switches before it in the batch.
    /// </summary>
    private async Task SettleTempBasalsAsync(List<V4Models.TempBasal> tempBasals, CancellationToken ct)
    {
        if (tempBasals.Count == 0)
            return;

        var contexts = await _activeProfileResolver.GetActiveInsulinContextsAsync(
            [.. tempBasals.Select(t => t.StartMills)], ct);
        V4Models.TreatmentInsulinContext? primary = null;
        var primaryRead = false;
        foreach (var (tempBasal, context) in tempBasals.Zip(contexts))
        {
            if (context is null && !primaryRead)
            {
                primary = ToInsulinContext(await _insulinRepo.GetPrimaryBolusInsulinAsync(ct));
                primaryRead = true;
            }

            tempBasal.InsulinContext = context ?? primary;
        }

        await _relativeTempBasalRates.ResolveAsync(tempBasals, ct);
    }

    /// <summary>Links each bolus to the calculation written beside it under its legacy id.</summary>
    private static void LinkCalculations(BatchModels models, IReadOnlyList<V4Models.BolusCalculation> written)
    {
        if (models.CalculationLinks.Count == 0)
            return;

        var calculations = written
            .Where(c => c.LegacyId is { } id && models.CalculationLinks.Contains(id))
            .GroupBy(c => c.LegacyId!)
            .ToDictionary(g => g.Key, g => g.Last().Id, StringComparer.Ordinal);
        foreach (var bolus in models.Boluses)
        {
            if (bolus.LegacyId is { } id && calculations.TryGetValue(id, out var calculationId))
                bolus.BolusCalculationId = calculationId;
        }
    }

    /// <summary>
    /// Keeps a legacy <see cref="Treatment.FoodType"/> as its carb intake's one <see cref="TreatmentFood"/>
    /// line, for the carb intakes the batch inserted that hold no line yet, in one read and one write.
    /// </summary>
    /// <remarks>
    /// These rows are also the user-editable food-breakdown surface (<see cref="ITreatmentFoodService"/>,
    /// <c>/carbs/{id}/foods</c>), so a carb intake that updated a stored row keeps whatever lines it has,
    /// including none if the user deleted the seeded one. One upserted in place on its sync key reports
    /// as inserted, so the existence check is what keeps the write idempotent there; no unique index
    /// can, since a carb intake legitimately holds many lines. A line the store refuses
    /// (<see cref="StoreRefusal"/>) rejects its treatment, as <see cref="UpsertTableAsync{TRecord}"/>
    /// does a record, and the batch's other lines are written.
    /// </remarks>
    private async Task WriteFoodLinesAsync(
        BulkWrite<V4Models.CarbIntake> carbs, IReadOnlyDictionary<Guid, Treatment> foodLines,
        TreatmentDecomposition result, Func<V4Models.IV4Record, int?> treatmentOf, CancellationToken ct)
    {
        var created = carbs.Created
            .Select(c => (Carbs: c, Treatment: c.CorrelationId is { } group ? foodLines.GetValueOrDefault(group) : null))
            .Where(c => c.Treatment is { Carbs: > 0, FoodType: { } food } && !string.IsNullOrWhiteSpace(food))
            .Where(c => !OfRejected(c.Carbs, treatmentOf, result))
            .ToList();
        if (created.Count == 0)
            return;

        var lined = (await _treatmentFoodService.GetByCarbIntakeIdsAsync([.. created.Select(c => c.Carbs.Id)], ct))
            .Select(l => l.CarbIntakeId)
            .ToHashSet();
        List<TreatmentFood> lines = [.. created.Where(c => !lined.Contains(c.Carbs.Id)).Select(c => new TreatmentFood
        {
            CarbIntakeId = c.Carbs.Id,
            Portions = 0m,
            Carbs = (decimal)c.Treatment!.Carbs!.Value,
            TimeOffsetMinutes = 0,
            Note = c.Treatment.FoodType,
        })];
        if (lines.Count == 0)
            return;

        try
        {
            await _treatmentFoodService.AddManyAsync(lines, ct);
        }
        catch (DbUpdateException ex) when (StoreRefusal.ReasonFor(ex) is not null)
        {
            if (lines.Count == 1)
            {
                Reject(lines[0], ex);
                return;
            }

            foreach (var line in lines)
            {
                try
                {
                    await _treatmentFoodService.AddManyAsync([line], ct);
                }
                catch (DbUpdateException refused) when (StoreRefusal.ReasonFor(refused) is not null)
                {
                    Reject(line, refused);
                }
            }
        }

        void Reject(TreatmentFood line, Exception refusal)
        {
            if (treatmentOf(created.First(c => c.Carbs.Id == line.CarbIntakeId).Carbs) is { } index)
                result.Reject(index, refusal);
        }
    }

    /// <summary>
    /// One table's bulk upsert. A value the store refuses fails the whole statement, so the table's
    /// records are then written one at a time, once, and each one refused rejects its treatment; the
    /// records of other tables already written for it stay written.
    /// Nothing more of a rejected treatment is written: the records of one rejected by an earlier write
    /// are dropped (<see cref="OfRejected"/>).
    /// </summary>
    private static async Task<BulkWrite<TRecord>> UpsertTableAsync<TRecord>(
        IBulkUpsertRepository<TRecord> repository, List<TRecord> records, TreatmentDecomposition result,
        Func<V4Models.IV4Record, int?> treatmentOf, WriteOrigin origin, CancellationToken ct)
        where TRecord : class, V4Models.IV4Record
    {
        records = [.. records.Where(r => !OfRejected(r, treatmentOf, result))];
        try
        {
            return await BulkUpsertAsync(repository, records, result, origin, ct);
        }
        catch (DbUpdateException ex) when (StoreRefusal.ReasonFor(ex) is not null)
        {
            if (records.Count == 1)
            {
                Reject(records[0], ex);
                return [];
            }

            List<TRecord> written = [], updated = [], unchanged = [];
            foreach (var record in records)
            {
                try
                {
                    var one = await BulkUpsertAsync(repository, [record], result, origin, ct);
                    written.AddRange(one);
                    updated.AddRange(one.Updated);
                    unchanged.AddRange(one.Unchanged);
                }
                catch (DbUpdateException refused) when (StoreRefusal.ReasonFor(refused) is not null)
                {
                    Reject(record, refused);
                }
            }

            return new BulkWrite<TRecord>(written, 0) { Updated = updated, Unchanged = unchanged };
        }

        void Reject(TRecord record, Exception refusal)
        {
            if (treatmentOf(record) is { } index)
                result.Reject(index, refusal);
        }
    }

    /// <summary>Whether <paramref name="record"/> belongs to a treatment the batch has rejected.</summary>
    private static bool OfRejected(
        V4Models.IV4Record record, Func<V4Models.IV4Record, int?> treatmentOf, TreatmentDecomposition result) =>
        treatmentOf(record) is { } index && result.Rejected.ContainsKey(index);

    /// <summary>
    /// Opens or closes a <see cref="StateSpanCategory.PumpMode"/> / <see cref="PumpModeState.Suspended"/>
    /// span for each treatment PumpSuspend or PumpResume device event, oldest first, over the spans the
    /// batch read.
    /// </summary>
    /// <remarks>
    /// A suspend opens a span keyed to it, which the store ends at the next suspension after it and
    /// which ends every open suspension before it; a suspend whose span a resume has already ended does
    /// not open it again. A resume ends the newest open suspension that started at or before it; one
    /// a suspension already ends at was folded before, and a suspension that starts after it is not its
    /// to end.
    /// </remarks>
    private void FoldPumpSuspensions(
        List<(BatchTreatment Treatment, DeviceEventType Type)> events, SpanState spans, Dictionary<StateSpan, BatchTreatment> sources)
    {
        var suspended = PumpModeState.Suspended.ToString();
        var pumpModes = spans[StateSpanCategory.PumpMode];

        // The ends the store gives a suspension it inserts, which the folds after it read but this one
        // must not write: the store records which span ended each one only when it sets the end itself.
        var storeEnds = new Dictionary<StateSpan, DateTime?>(ReferenceEqualityComparer.Instance);
        DateTime? EndOf(StateSpan span) => storeEnds.TryGetValue(span, out var end) ? end : span.EndTimestamp;

        foreach (var (batched, type) in events.OrderBy(e => e.Treatment.Treatment.Mills).ThenBy(e => e.Treatment.Index))
        {
            var treatment = batched.Treatment;
            var at = batched.Start;
            if (type == DeviceEventType.PumpSuspend)
            {
                var key = $"pump-suspended-tx:{treatment.Id}";
                var keyed = pumpModes.FirstOrDefault(s => s.OriginalId == key);
                if (keyed is not null && EndOf(keyed) is not null)
                    continue;

                var span = new StateSpan
                {
                    Category = StateSpanCategory.PumpMode,
                    State = suspended,
                    StartTimestamp = at,
                    EndTimestamp = null,
                    Source = SpanSource(treatment),
                    OriginalId = key,
                };

                if (keyed is null)
                {
                    foreach (var open in pumpModes.Where(s => s.State == suspended && EndOf(s) == null && s.StartTimestamp < at))
                        storeEnds[open] = at;
                    storeEnds[span] = pumpModes
                        .Where(s => s.State == suspended && s.StartTimestamp > at)
                        .MinBy(s => s.StartTimestamp)?.StartTimestamp;
                }

                spans.Open(span);
                sources[span] = batched;
                continue;
            }

            if (pumpModes.Any(s => s.State == suspended && s.EndTimestamp == at))
                continue;

            var closable = pumpModes
                .Where(s => s.State == suspended && EndOf(s) == null && s.StartTimestamp <= at)
                .MaxBy(s => s.StartTimestamp);
            if (closable is null)
            {
                Logger.LogWarning(
                    "PumpResume treatment {LegacyId} but no open PumpMode/Suspended StateSpan to close",
                    LogText.Sanitize(treatment.Id));
                continue;
            }

            closable.EndTimestamp = at;
            storeEnds.Remove(closable);
            spans.Change(closable);
            sources.TryAdd(closable, batched);
        }
    }

    #endregion

    /// <inheritdoc />
    /// <remarks>
    /// The tables are swept in one transaction, as <see cref="DeleteFromSourceAsync"/>'s are: the
    /// delete takes every copy in each row's duplicate group (<see cref="DuplicateDelete.EveryCopy"/>),
    /// and a failure on a later table must not leave the treatment half deleted.
    /// </remarks>
    public Task<int> DeleteByLegacyIdAsync(string legacyId, WriteOrigin origin, CancellationToken ct = default) =>
        DeleteByLegacyIdsAsync([legacyId], origin, ct);

    private async Task<int> DeleteByLegacyIdsAsync(string[] legacyIds, WriteOrigin origin, CancellationToken ct)
    {
        await using var db = await _contexts.CreateAsync(ct);
        // These deletes bypass the repository chokepoint. Its device-event reaction is run below; the v4-native delete broadcast is deferred to the glucose-unification follow-up.
        var (deleted, deviceEventIds, activationIds) = await db.ExecuteInTransactionAsync(async token =>
        {
            var count = 0;
            List<Guid> deviceEvents = [];
            List<Guid> activations = [];
            foreach (var table in DecomposedTables)
            {
                var result = await table.SoftDeleteAsync(
                    db, legacyIds, source: null, $"legacy_id={string.Join(",", legacyIds)}", DuplicateDelete.EveryCopy, token);
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
            Logger.LogDebug("Soft-deleted {Count} v4 records for {Treatments} legacy treatment(s)", deleted, legacyIds.Length);
            await ReportWrittenAsync(DecomposedTables.Select(t => t.Model));
            await _profileActivationWriter.ResettleAfterRemovalAsync(activationIds, ct);
        }

        if (origin == WriteOrigin.Live)
            await ReactToDeletedDeviceEventsAsync(db, deviceEventIds, ct);

        return deleted;
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

        await ReportWrittenAsync([.. DecomposedTables.Select(t => t.Model), typeof(StateSpan)]);
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
    /// <remarks>
    /// A connector's rows can sit under an uploader's <c>id</c> where its <c>_id</c> belongs (see
    /// <see cref="IgnoredBasePropertyModifier"/>); read by <c>_id</c>, the same treatments would otherwise be
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

            var storedAt = rows.Select(r => r.At.ToMills()).ToHashSet();
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

        await ReportWrittenAsync([.. DecomposedTables.Select(t => t.Model), typeof(StateSpan)]);
        await _profileActivationWriter.InvalidateAsync(ct);
        return rekeyed;
    }

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
        db.StateSpans.IgnoreQueryFilters()
            .Where(s => s.TenantId == db.TenantId && s.Source == source
                     && s.OriginalId != null && originalIds.Contains(s.OriginalId))
            .WhereBlocksSpanRecreation();

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

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> GetDeletedByUserAsync(
        IReadOnlyCollection<string> legacyIds, CancellationToken ct = default)
    {
        if (legacyIds.Count == 0)
            return new HashSet<string>();

        await using var db = await _contexts.CreateAsync(ct);
        return (await GetHeldLegacyIdsAsync(db, [.. legacyIds], ct)).DeletedByUser;
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
        rows.AddRange((await db.StateSpans.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.TenantId == db.TenantId && s.OriginalId != null && ids.Contains(s.OriginalId))
                .WhereBlocksSpanRecreation()
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

        await ReportWrittenAsync([.. DecomposedTables.Select(t => t.Model), typeof(StateSpan)]);
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
        Table<V4Models.Bolus, BolusEntity>(DecomposedKind.Bolus, db => db.Boluses, ByTimeRange, StoredAt),
        Table<V4Models.TempBasal, TempBasalEntity>(DecomposedKind.TempBasal, db => db.TempBasals, ByTimeRange, StoredAt),
        Table<V4Models.CarbIntake, CarbIntakeEntity>(DecomposedKind.CarbIntake, db => db.CarbIntakes, ByTimeRange, StoredAt),
        Table<V4Models.BGCheck, BGCheckEntity>(DecomposedKind.BGCheck, db => db.BGChecks, ByTimeRange, StoredAt),
        Table<V4Models.Note, NoteEntity>(DecomposedKind.Note, db => db.Notes, ByTimeRange, StoredAt),
        Table<V4Models.DeviceEvent, DeviceEventEntity>(DecomposedKind.DeviceEvent, db => db.DeviceEvents, ByTimeRange, StoredAt),
        Table<V4Models.BolusCalculation, BolusCalculationEntity>(DecomposedKind.BolusCalculation, db => db.BolusCalculations, ByTimeRange, StoredAt),
        Table<V4Models.ProfileActivation, ProfileActivationEntity>(DecomposedKind.ProfileActivation, db => db.ProfileActivations, ServedInRange, StoredAt),
    ];

    /// <summary>
    /// Tells <see cref="ILegacyReadCacheInvalidation"/> what a write here made around the
    /// repositories, which report their own writes.
    /// </summary>
    private Task ReportWrittenAsync(IEnumerable<Type> models) =>
        _legacyReads?.WrittenAsync([.. models]) ?? Task.CompletedTask;

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

    private DecomposedTable<T> Table<TModel, T>(
        DecomposedKind kind,
        Func<NocturneDbContext, DbSet<T>> rows,
        Func<IQueryable<T>, DateTime?, DateTime?, IQueryable<T>> inRange,
        Func<IQueryable<T>, IQueryable<StoredRow>> storedAt)
        where TModel : V4Models.IV4Record
        where T : class, IV4Entity, ISourcedEntity, IAuditable, IUpstreamFingerprinted
        => new(_auditContext, kind, typeof(TModel), rows, inRange, storedAt);

    private static IQueryable<StoredRow> StoredAt<T>(IQueryable<T> rows) where T : IV4TimeSeriesEntity
        => rows.Select(e => new StoredRow(e.LegacyId!, e.Timestamp));

    private sealed record StoredRow(string LegacyId, DateTime At);

    private interface IDecomposedTable
    {
        DecomposedKind Kind { get; }

        Type Model { get; }

        /// <param name="source">Only this data source's rows, or every source's when null.</param>
        /// <remarks>
        /// A legacy id fans out to a handful of correlated rows, so even a batch's ids reach few. The
        /// per-record audit rows <see cref="AuditedBulkDeleteExtensions.AuditedSoftDeleteWithEntitiesAsync{T}"/>
        /// writes below its cap are the right shape for that.
        /// </remarks>
        Task<AuditedSoftDeleteResult<Guid>> SoftDeleteAsync(
            NocturneDbContext db, string[] legacyIds, string? source, string scope, DuplicateDelete duplicates, CancellationToken ct);

        Task<int> SoftDeleteInRangeAsync(NocturneDbContext db, DateTime? from, DateTime? to, string scope, CancellationToken ct);

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
        Type model,
        Func<NocturneDbContext, DbSet<T>> rowsOf,
        Func<IQueryable<T>, DateTime?, DateTime?, IQueryable<T>> inRange,
        Func<IQueryable<T>, IQueryable<StoredRow>> storedAt) : IDecomposedTable
        where T : class, IV4Entity, ISourcedEntity, IAuditable, IUpstreamFingerprinted
    {
        public DecomposedKind Kind => kind;

        public Type Model => model;

        public Task<AuditedSoftDeleteResult<Guid>> SoftDeleteAsync(
            NocturneDbContext db, string[] legacyIds, string? source, string scope, DuplicateDelete duplicates, CancellationToken ct)
            => db.AuditedSoftDeleteWithIdsAsync(
                rowsOf(db).Where(e => e.LegacyId != null && legacyIds.Contains(e.LegacyId)
                             && (source == null || e.DataSource == source)),
                auditContext, scope, ct, duplicates);

        public Task<int> SoftDeleteInRangeAsync(NocturneDbContext db, DateTime? from, DateTime? to, string scope, CancellationToken ct)
            => db.AuditedSoftDeleteAsync(inRange(rowsOf(db), from, to), auditContext, scope, ct);

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

using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Oref;
using OrefModels = Nocturne.Core.Oref.Models;

namespace Nocturne.API.Services.Glucose;

/// <summary>
/// <see cref="IPredictionService"/> implementation that calculates glucose predictions using oref
/// algorithms. Fetches current CGM readings, treatments, and profile data, then invokes the oref
/// IOB, COB, and determine-basal pipeline to produce prediction curves.
/// </summary>
/// <seealso cref="IPredictionService"/>
public class PredictionService : IPredictionService
{
    private readonly IEntryService _entries;
    private readonly ITreatmentService _treatments;
    private readonly IBasalRateResolver _basalRate;
    private readonly ISensitivityResolver _sensitivity;
    private readonly ICarbRatioResolver _carbRatio;
    private readonly ITargetRangeResolver _targetRange;
    private readonly ITherapySettingsResolver _therapySettings;
    private readonly IPatientInsulinRepository _insulins;
    private readonly TimeProvider _time;
    private readonly ILogger<PredictionService> _logger;

    public PredictionService(
        IEntryService entries,
        ITreatmentService treatments,
        IBasalRateResolver basalRate,
        ISensitivityResolver sensitivity,
        ICarbRatioResolver carbRatio,
        ITargetRangeResolver targetRange,
        ITherapySettingsResolver therapySettings,
        IPatientInsulinRepository insulins,
        TimeProvider timeProvider,
        ILogger<PredictionService> logger)
    {
        _entries = entries;
        _treatments = treatments;
        _basalRate = basalRate;
        _sensitivity = sensitivity;
        _carbRatio = carbRatio;
        _targetRange = targetRange;
        _therapySettings = therapySettings;
        _insulins = insulins;
        _time = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<GlucosePredictionResponse> GetPredictionsAsync(
        string? profileId = null,
        DateTimeOffset? asOf = null,
        CancellationToken cancellationToken = default)
    {
        var now = asOf ?? _time.GetUtcNow();
        var nowMills = now.ToUnixTimeMilliseconds();

        var orefVersion = OrefVersion();
        _logger.LogInformation("[Predictions] Oref library version: {Version}", orefVersion ?? "unavailable");

        if (orefVersion is null)
        {
            throw new InvalidOperationException("The oref library is not available");
        }

        // Fetch recent glucose readings (last 10 entries at-or-before the anchor for delta
        // calculation). ToMills bounds the query at the anchor so an as-of replay sees only
        // readings the user would have had at that moment.
        var glucoseEntries = await _entries.QueryEntriesAsync(
            new EntryQuery { Type = "sgv", Count = 10, ToMills = nowMills },
            cancellationToken);

        if (!glucoseEntries.Any())
        {
            throw new InvalidOperationException("No glucose readings available for predictions");
        }

        // Convert to oref glucose readings
        var orefGlucose = glucoseEntries
            .Where(e => e.GlucoseMgdl() > 0)
            .OrderByDescending(e => e.Mills)
            .Select(e => new OrefModels.GlucoseReading
            {
                Sgv = e.GlucoseMgdl(),
                Date = e.Mills,
                Direction = e.Direction
            })
            .ToList();

        if (!orefGlucose.Any())
        {
            throw new InvalidOperationException("No valid glucose readings available");
        }

        // Calculate glucose status (delta, avgdelta)
        var glucoseStatus = CalculateGlucoseStatus(orefGlucose);
        _logger.LogInformation("[Predictions] GlucoseStatus: glucose={Glucose}, delta={Delta}, status={HasStatus}",
            glucoseStatus?.Glucose ?? 0, glucoseStatus?.Delta ?? 0, glucoseStatus != null);
        if (glucoseStatus == null)
        {
            throw new InvalidOperationException("The glucose status could not be calculated");
        }

        // Fetch treatments in the 24h window ending at the anchor. 24h matches what
        // SensorContextEnricher uses; oref discards entries beyond the insulin/carb tails
        // internally, so the wider window is safe and keeps the as-of and live paths
        // identically scoped.
        var treatments = await _treatments.GetTreatmentsByRangeAsync(
            fromMills: now.AddHours(-24).ToUnixTimeMilliseconds(),
            toMills: nowMills,
            cancellationToken);

        // Convert to oref treatments
        // A percent temp basal of unknown rate gives oref no delivery to work from, so it is left out
        var orefTreatments = treatments
            .Where(t => t.EventType != "Temp Basal" || (t.DeliveredRate ?? t.Rate) is not null)
            .Select(t => new OrefModels.OrefTreatment
            {
                EventType = t.EventType ?? "",
                Mills = t.Mills,
                Insulin = t.Insulin,
                Carbs = t.Carbs,
                Rate = t.DeliveredRate ?? t.Rate,
                Duration = (int?)(t.Duration ?? 0),
            })
            .ToList();

        // Get or create default profile
        var profile = await GetProfileAsync(profileId, nowMills, cancellationToken);

        // Calculate IOB
        var iobData = OrefService.CalculateIob(profile, orefTreatments, now);
        if (iobData == null)
        {
            iobData = new OrefModels.IobData { Iob = 0, Activity = 0, Time = nowMills };
        }

        // Calculate COB
        var cobResult = OrefService.CalculateCob(profile, orefGlucose, orefTreatments, now);
        var cob = cobResult?.Cob ?? 0;

        // Current temp basal (simplified - no active temp)
        var currentTemp = new OrefModels.CurrentTemp { Rate = profile.CurrentBasal, Duration = 0 };

        // Get predictions — `now` anchors oref's determine-basal time reference so an
        // as-of replay produces the forecast the user would have seen at that tick.
        var predictions = OrefService.GetPredictions(
            profile,
            glucoseStatus,
            iobData,
            currentTemp,
            currentTime: now,
            autosensRatio: 1.0,
            cob: cob);

        _logger.LogInformation(
            "[Predictions] Result: HasPredictions={HasPredictions}, MainCurve={MainLength}, IobCurve={IobLength}, UamCurve={UamLength}, CobCurve={CobLength}, ZtCurve={ZtLength}",
            predictions != null,
            predictions?.PredictedBg?.Count ?? 0,
            predictions?.PredBgsIob?.Count ?? 0,
            predictions?.PredBgsUam?.Count ?? 0,
            predictions?.PredBgsCob?.Count ?? 0,
            predictions?.PredBgsZt?.Count ?? 0);

        return new GlucosePredictionResponse
        {
            Timestamp = now,
            CurrentBg = glucoseStatus.Glucose,
            Delta = glucoseStatus.Delta,
            EventualBg = predictions?.EventualBg ?? glucoseStatus.Glucose,
            Iob = predictions?.Iob ?? iobData.Iob,
            Cob = predictions?.Cob ?? cob,
            SensitivityRatio = predictions?.SensitivityRatio,
            IntervalMinutes = 5,
            Predictions = new PredictionCurves
            {
                Default = predictions?.PredictedBg,
                IobOnly = predictions?.PredBgsIob,
                Uam = predictions?.PredBgsUam,
                Cob = predictions?.PredBgsCob,
                ZeroTemp = predictions?.PredBgsZt
            }
        };
    }

    /// <summary>
    /// Build an oref profile from V4 resolvers, anchored at <paramref name="nowMills"/> so
    /// schedule-driven settings (basal, sensitivity, carb ratio, target) reflect the
    /// active segment at that instant rather than at wall-clock now.
    /// </summary>
    private async Task<OrefModels.OrefProfile> GetProfileAsync(
        string? profileId, long nowMills, CancellationToken cancellationToken)
    {
        // Resolve insulin pharmacokinetics from active bolus insulin
        var bolusInsulin = await ResolveBolusInsulinAsync();
        var peak = bolusInsulin?.Peak;
        var curve = bolusInsulin?.Curve;

        try
        {
            var hasData = await _therapySettings.HasDataAsync(cancellationToken);
            if (hasData)
            {
                var dia = await _therapySettings.GetDIAAsync(nowMills, profileId, cancellationToken);
                var basal = await _basalRate.GetBasalRateAsync(nowMills, profileId, cancellationToken);
                var sens = await _sensitivity.GetSensitivityAsync(nowMills, profileId, cancellationToken);
                var carbs = await _carbRatio.GetCarbRatioAsync(nowMills, profileId, cancellationToken);
                var minBg = await _targetRange.GetLowBGTargetAsync(nowMills, profileId, cancellationToken);
                var maxBg = await _targetRange.GetHighBGTargetAsync(nowMills, profileId, cancellationToken);

                var orefProfile = new OrefModels.OrefProfile
                {
                    Dia = dia,
                    CurrentBasal = basal,
                    Sens = sens,
                    CarbRatio = carbs,
                    MinBg = minBg,
                    MaxBg = maxBg,
                    MaxIob = 10.0,
                    MaxBasal = 4.0,
                    MaxDailyBasal = 2.0
                };

                if (curve != null) orefProfile.Curve = curve;
                if (peak.HasValue) orefProfile.Peak = peak.Value;

                return orefProfile;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve profile from V4 resolvers, using defaults");
        }

        return new OrefModels.OrefProfile
        {
            Dia = await ResolveFallbackDiaAsync(profileId, nowMills, cancellationToken),
            CurrentBasal = 1.0,
            Sens = 50.0,
            CarbRatio = 10.0,
            MinBg = 100.0,
            MaxBg = 120.0,
            MaxIob = 10.0,
            MaxBasal = 4.0,
            MaxDailyBasal = 2.0
        };
    }

    private async Task<double> ResolveFallbackDiaAsync(string? profileId, long nowMills, CancellationToken cancellationToken)
    {
        try
        {
            return await _therapySettings.GetDIAAsync(nowMills, profileId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to resolve DIA, using the default");
            return TherapySnapshot.DefaultDia;
        }
    }

    private async Task<Core.Models.V4.PatientInsulin?> ResolveBolusInsulinAsync()
    {
        try
        {
            return await _insulins.GetPrimaryBolusInsulinAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve primary bolus insulin, falling back to defaults");
            return null;
        }
    }

    /// <summary>
    /// The loaded oref library's version, or null when it is not loaded. A seam over the static
    /// native calls, so tests hold whether or not the library is present.
    /// </summary>
    internal virtual string? OrefVersion() => OrefService.IsAvailable() ? OrefService.GetVersion() : null;

    internal virtual OrefModels.GlucoseStatus? CalculateGlucoseStatus(IEnumerable<OrefModels.GlucoseReading> readings) =>
        OrefService.CalculateGlucoseStatus(readings);
}

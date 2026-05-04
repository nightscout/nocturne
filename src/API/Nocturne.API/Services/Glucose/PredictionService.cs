using Nocturne.API.Controllers.V4;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Oref;
using OrefModels = Nocturne.Core.Oref.Models;

namespace Nocturne.API.Services.Glucose;

/// <summary>
/// <see cref="IPredictionService"/> implementation that calculates glucose predictions using oref
/// algorithms. Fetches current CGM readings, treatments, and profile data, then invokes the oref
/// IOB, COB, and determine-basal pipeline to produce prediction curves.
/// </summary>
/// <seealso cref="IPredictionService"/>
/// <seealso cref="IOrefService"/>
public class PredictionService : IPredictionService
{
    private readonly IEntryStore _store;
    private readonly ITreatmentService _treatments;
    private readonly IBasalRateResolver _basalRate;
    private readonly ISensitivityResolver _sensitivity;
    private readonly ICarbRatioResolver _carbRatio;
    private readonly ITargetRangeResolver _targetRange;
    private readonly ITherapySettingsResolver _therapySettings;
    private readonly IPatientInsulinRepository _insulins;
    private readonly ILogger<PredictionService> _logger;

    public PredictionService(
        IEntryStore store,
        ITreatmentService treatments,
        IBasalRateResolver basalRate,
        ISensitivityResolver sensitivity,
        ICarbRatioResolver carbRatio,
        ITargetRangeResolver targetRange,
        ITherapySettingsResolver therapySettings,
        IPatientInsulinRepository insulins,
        ILogger<PredictionService> logger)
    {
        _store = store;
        _treatments = treatments;
        _basalRate = basalRate;
        _sensitivity = sensitivity;
        _carbRatio = carbRatio;
        _targetRange = targetRange;
        _therapySettings = therapySettings;
        _insulins = insulins;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<GlucosePredictionResponse> GetPredictionsAsync(
        string? profileId = null,
        DateTimeOffset? asOf = null,
        CancellationToken cancellationToken = default)
    {
        var now = asOf ?? DateTimeOffset.UtcNow;
        var nowMills = now.ToUnixTimeMilliseconds();

        // Check if oref library is available
        var orefAvailable = OrefService.IsAvailable();
        _logger.LogInformation("[Predictions] Oref library available: {IsAvailable}, version: {Version}",
            orefAvailable, orefAvailable ? OrefService.GetVersion() : "N/A");

        if (!orefAvailable)
        {
            _logger.LogWarning("Oref library is not available - returning fallback prediction");
            return await GetFallbackPredictionsAsync(now, cancellationToken);
        }

        // Fetch recent glucose readings (last 10 entries at-or-before the anchor for delta
        // calculation). ToMills bounds the query at the anchor so an as-of replay sees only
        // readings the user would have had at that moment.
        var glucoseEntries = await _store.QueryAsync(
            new EntryQuery { Type = "sgv", Count = 10, ToMills = nowMills },
            cancellationToken);

        if (!glucoseEntries.Any())
        {
            throw new InvalidOperationException("No glucose readings available for predictions");
        }

        // Convert to oref glucose readings
        var orefGlucose = glucoseEntries
            .Where(e => e.Sgv.HasValue && e.Sgv > 0)
            .OrderByDescending(e => e.Mills)
            .Select(e => new OrefModels.GlucoseReading
            {
                Sgv = e.Sgv ?? 0,
                Date = e.Mills,
                Direction = e.Direction
            })
            .ToList();

        if (!orefGlucose.Any())
        {
            throw new InvalidOperationException("No valid glucose readings available");
        }

        // Calculate glucose status (delta, avgdelta)
        var glucoseStatus = OrefService.CalculateGlucoseStatus(orefGlucose);
        _logger.LogInformation("[Predictions] GlucoseStatus: glucose={Glucose}, delta={Delta}, status={HasStatus}",
            glucoseStatus?.Glucose ?? 0, glucoseStatus?.Delta ?? 0, glucoseStatus != null);
        if (glucoseStatus == null)
        {
            _logger.LogWarning("Failed to calculate glucose status - using fallback");
            return await GetFallbackPredictionsAsync(now, cancellationToken);
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
        var orefTreatments = treatments
            .Select(t => new OrefModels.OrefTreatment
            {
                EventType = t.EventType ?? "",
                Mills = t.Mills,
                Insulin = t.Insulin,
                Carbs = t.Carbs,
                Rate = t.Rate,
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

        // Return default profile (DIA falls back through resolver chain already, but we have no data)
        var defaultDia = bolusInsulin?.Dia ?? 3.0;
        return new OrefModels.OrefProfile
        {
            Dia = defaultDia,
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
    /// Get fallback predictions when oref is not available.
    /// Uses simple linear extrapolation based on current delta.
    /// Generates approximate curves for all prediction types for UI demonstration.
    /// </summary>
    private async Task<GlucosePredictionResponse> GetFallbackPredictionsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Get current entry
        var currentEntry = await _store.GetCurrentAsync(cancellationToken);

        if (currentEntry?.Sgv == null)
        {
            throw new InvalidOperationException("No glucose readings available");
        }

        var currentBg = currentEntry.Sgv.Value;
        var delta = currentEntry.Delta ?? 0;

        // Generate 48 points = 4 hours at 5-minute intervals
        var mainPredictions = new List<double>();
        var iobPredictions = new List<double>();
        var ztPredictions = new List<double>();
        var uamPredictions = new List<double>();
        var cobPredictions = new List<double>();

        for (int i = 0; i < 48; i++)
        {
            var minutes = i * 5;
            var decayFactor = Math.Exp(-minutes / 60.0); // Delta decays over time

            // Main prediction (weighted average approach)
            var mainPredicted = currentBg + (delta * i * decayFactor * 0.8);
            mainPredictions.Add(Math.Max(39, Math.Min(400, mainPredicted)));

            // IOB prediction (assumes insulin brings glucose down more)
            var iobPredicted = currentBg + (delta * i * decayFactor * 0.6) - (minutes * 0.3);
            iobPredictions.Add(Math.Max(39, Math.Min(400, iobPredicted)));

            // Zero Temp prediction (assumes no insulin, glucose rises more if rising)
            var ztPredicted = currentBg + (delta * i * decayFactor * 1.2) + (delta > 0 ? minutes * 0.2 : 0);
            ztPredictions.Add(Math.Max(39, Math.Min(400, ztPredicted)));

            // UAM prediction (aggressive rise detection)
            var uamPredicted = currentBg + (delta * i * decayFactor * 1.1);
            uamPredictions.Add(Math.Max(39, Math.Min(400, uamPredicted)));

            // COB prediction (carbs cause initial rise then insulin catches up)
            var carbEffect = Math.Sin(minutes * Math.PI / 180.0) * 20; // Peak around 60 minutes
            var cobPredicted = currentBg + (delta * i * decayFactor * 0.9) + carbEffect;
            cobPredictions.Add(Math.Max(39, Math.Min(400, cobPredicted)));
        }

        return new GlucosePredictionResponse
        {
            Timestamp = now,
            CurrentBg = currentBg,
            Delta = delta,
            EventualBg = mainPredictions.LastOrDefault(),
            Iob = 0,
            Cob = 0,
            IntervalMinutes = 5,
            Predictions = new PredictionCurves
            {
                Default = mainPredictions,
                IobOnly = iobPredictions,
                ZeroTemp = ztPredictions,
                Uam = uamPredictions,
                Cob = cobPredictions
            }
        };
    }
}

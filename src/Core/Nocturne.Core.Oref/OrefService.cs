using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Oref.Models;

namespace Nocturne.Core.Oref;

/// <summary>
/// High-level service for interacting with the oref algorithms.
/// Provides strongly-typed methods that serialize/deserialize JSON automatically.
/// </summary>
public class OrefService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Check if the native oref library is available.
    /// </summary>
    public static bool IsAvailable() => OrefInterop.IsAvailable();

    /// <summary>
    /// Get the oref library version.
    /// </summary>
    public static string GetVersion() => OrefInterop.GetVersion();

    /// <summary>
    /// Run a health check on the oref library.
    /// </summary>
    public static string HealthCheck() => OrefInterop.HealthCheck();

    /// <summary>
    /// Calculate glucose status from recent readings.
    /// </summary>
    /// <param name="readings">Recent glucose readings (most recent first)</param>
    /// <returns>Glucose status with delta calculations.</returns>
    public static GlucoseStatus? CalculateGlucoseStatus(IEnumerable<GlucoseReading> readings)
    {
        var json = JsonSerializer.Serialize(readings, JsonOptions);
        var result = OrefInterop.CalculateGlucoseStatus(json);

        if (string.IsNullOrEmpty(result) || result.Contains("\"error\""))
        {
            return null;
        }

        return JsonSerializer.Deserialize<GlucoseStatus>(result, JsonOptions);
    }

    /// <summary>
    /// Calculate Insulin on Board from treatment history.
    /// </summary>
    public static IobData? CalculateIob(
        OrefProfile profile,
        IEnumerable<OrefTreatment> treatments,
        DateTimeOffset currentTime)
    {
        var profileJson = JsonSerializer.Serialize(profile, JsonOptions);
        var treatmentsJson = JsonSerializer.Serialize(treatments, JsonOptions);
        var timeMillis = currentTime.ToUnixTimeMilliseconds();

        var result = OrefInterop.CalculateIob(profileJson, treatmentsJson, timeMillis, currentOnly: true);

        if (string.IsNullOrEmpty(result) || result.Contains("\"error\""))
        {
            return null;
        }

        // Result is an array, take first element for current IOB
        var iobArray = JsonSerializer.Deserialize<List<IobData>>(result, JsonOptions);
        return iobArray?.FirstOrDefault();
    }

    /// <summary>
    /// Calculate Carbs on Board from treatment history and glucose readings.
    /// </summary>
    public static CobResult? CalculateCob(
        OrefProfile profile,
        IEnumerable<GlucoseReading> glucose,
        IEnumerable<OrefTreatment> treatments,
        DateTimeOffset currentTime)
    {
        var profileJson = JsonSerializer.Serialize(profile, JsonOptions);
        var glucoseJson = JsonSerializer.Serialize(glucose, JsonOptions);
        var treatmentsJson = JsonSerializer.Serialize(treatments, JsonOptions);
        var timeMillis = currentTime.ToUnixTimeMilliseconds();

        var result = OrefInterop.CalculateCob(profileJson, glucoseJson, treatmentsJson, timeMillis);

        if (string.IsNullOrEmpty(result) || result.Contains("\"error\""))
        {
            return null;
        }

        return JsonSerializer.Deserialize<CobResult>(result, JsonOptions);
    }

    /// <summary>
    /// Run the determine-basal algorithm to get dosing recommendations and predictions.
    /// </summary>
    public static DetermineBasalResult? DetermineBasal(DetermineBasalInputs inputs, ILogger? logger = null)
    {
        var json = JsonSerializer.Serialize(inputs, JsonOptions);

        logger?.LogDebug("DetermineBasal: Input JSON (first 500 chars): {InputJson}", json[..Math.Min(json.Length, 500)]);
        logger?.LogDebug("DetermineBasal: Calling Rust with {InputLength} byte input", json.Length);

        var result = OrefInterop.DetermineBasal(json);

        logger?.LogDebug("DetermineBasal: Rust returned: {ResultPreview}", result?.Length > 0 ? result[..Math.Min(result.Length, 500)] : "(empty)");

        if (string.IsNullOrEmpty(result))
        {
            logger?.LogWarning("DetermineBasal: Result is empty");
            return null;
        }

        if (result.Contains("\"error\""))
        {
            logger?.LogError("DetermineBasal: Rust returned error: {ErrorResult}", result);
            return null;
        }

        return JsonSerializer.Deserialize<DetermineBasalResult>(result, JsonOptions);
    }

    /// <summary>
    /// Get glucose predictions for a given state.
    /// This is a convenience method that extracts just the prediction arrays from determine_basal.
    /// </summary>
    public static GlucosePredictions? GetPredictions(
        OrefProfile profile,
        GlucoseStatus glucoseStatus,
        IobData iobData,
        CurrentTemp currentTemp,
        DateTimeOffset currentTime,
        double autosensRatio = 1.0,
        double cob = 0.0)
    {
        var inputs = new DetermineBasalInputs
        {
            Profile = profile,
            GlucoseStatus = glucoseStatus,
            IobData = iobData,
            CurrentTemp = currentTemp,
            AutosensData = new AutosensData { Ratio = autosensRatio },
            MealData = new MealData { Cob = cob },
            MicroBolusAllowed = false,
            CurrentTimeMillis = currentTime.ToUnixTimeMilliseconds()
        };

        var result = DetermineBasal(inputs);

        if (result == null)
        {
            return null;
        }

        return new GlucosePredictions
        {
            CurrentBg = glucoseStatus.Glucose,
            EventualBg = result.EventualBg,
            Iob = result.Iob,
            Cob = result.Cob,
            PredictedBg = result.PredictedBg,
            PredBgsIob = result.PredBgsIob,
            PredBgsUam = result.PredBgsUam,
            PredBgsCob = result.PredBgsCob,
            PredBgsZt = result.PredBgsZt,
            SensitivityRatio = result.SensitivityRatio
        };
    }
}

/// <summary>
/// Glucose prediction results.
/// </summary>
public class GlucosePredictions
{
    /// <summary>Current blood glucose (mg/dL)</summary>
    public double CurrentBg { get; set; }

    /// <summary>Eventual BG if current trend continues (mg/dL)</summary>
    public double EventualBg { get; set; }

    /// <summary>Current IOB (U)</summary>
    public double Iob { get; set; }

    /// <summary>Current COB (g)</summary>
    public double Cob { get; set; }

    /// <summary>Main prediction curve (mg/dL, 5-min intervals)</summary>
    public List<double>? PredictedBg { get; set; }

    /// <summary>IOB-only prediction (mg/dL)</summary>
    public List<double>? PredBgsIob { get; set; }

    /// <summary>UAM prediction (mg/dL)</summary>
    public List<double>? PredBgsUam { get; set; }

    /// <summary>COB prediction (mg/dL)</summary>
    public List<double>? PredBgsCob { get; set; }

    /// <summary>Zero-temp prediction (mg/dL)</summary>
    public List<double>? PredBgsZt { get; set; }

    /// <summary>Sensitivity ratio used</summary>
    public double? SensitivityRatio { get; set; }
}

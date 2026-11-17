using System.Text.Json.Serialization;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Models.LoopAnalysis;

/// <summary>A prediction curve an AID system reports.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PredictionCurve>))]
public enum PredictionCurve
{
    /// <summary>The single curve of a system without oref's curves: Loop's prediction.</summary>
    Default,

    /// <summary>oref's insulin-only curve.</summary>
    Iob,

    /// <summary>oref's zero-temp curve: insulin on board with basal suspended from now on.</summary>
    ZeroTemp,

    /// <summary>oref's curve with the announced carbs absorbing.</summary>
    Cob,

    /// <summary>oref's unannounced-meal curve, extrapolating the deviation it observes.</summary>
    Uam,
}

/// <summary>
/// What was going on when a prediction was made. Each prediction is in exactly one context, the
/// first that applies in this order: adjusted, post-meal, post-correction, fasting. Carbs and
/// boluses count from three hours before the prediction up to it; one after it interrupts the
/// horizons it comes before (<see cref="PredictionHorizonAccuracy.Interrupted"/>).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PredictionContext>))]
public enum PredictionContext
{
    /// <summary>
    /// An override, temporary target or exercise span was active when the prediction was made: the
    /// loop was told to run differently. In oref a high temporary target is exercise mode and
    /// scales sensitivity.
    /// </summary>
    Adjusted,

    /// <summary>Carbs were entered.</summary>
    PostMeal,

    /// <summary>
    /// A manual bolus was given and no carbs entered. Automatic boluses are the loop's own dosing
    /// and do not count.
    /// </summary>
    PostCorrection,

    /// <summary>None of the above.</summary>
    Fasting,
}

/// <summary>The Prediction accuracy page: the primary range and, when asked for, a comparison range.</summary>
public class PredictionAccuracyResponse : LoopAnalysisResponse
{
    public PredictionAccuracyRange Primary { get; set; } = new();

    public PredictionAccuracyRange? Comparison { get; set; }

    /// <summary>How far before a prediction its context looks for carbs and manual boluses, ms.</summary>
    public long ContextLookbackMs { get; set; }
}

/// <summary>
/// How far each curve's predictions landed from the sensor readings that followed, over the
/// predictions made in one range.
/// </summary>
/// <remarks>
/// A prediction belongs to the range its start falls in, and is paired with readings past the
/// range's end. Snapshots are thinned as <c>IApsSnapshotRepository.GetPredictionsAsync</c> states.
/// </remarks>
public class PredictionAccuracyRange
{
    public DateTime From { get; set; }

    public DateTime To { get; set; }

    /// <summary>Snapshots whose predictions were analysed, after thinning and the filter's masks.</summary>
    public int Predictions { get; set; }

    /// <summary>The algorithms those snapshots came from.</summary>
    public List<AidAlgorithm> Algorithms { get; set; } = [];

    /// <summary>
    /// Each curve the snapshots carried, in <see cref="PredictionCurve"/> order. oref's default
    /// curve is a copy of its IOB curve, so a snapshot with oref's curves reports those alone.
    /// </summary>
    public List<PredictionCurveAccuracy> Curves { get; set; } = [];

    /// <summary>
    /// The curves left out of <see cref="Curves"/> because the caller's access withholds the
    /// contexts they are reported in, in <see cref="PredictionCurve"/> order. Empty where none were.
    /// </summary>
    public List<PredictionCurve> HiddenCurves { get; set; } = [];
}

public class PredictionCurveAccuracy
{
    public PredictionCurve Curve { get; set; }

    /// <summary>
    /// The contexts the curve is reported in: where the algorithm relies on it. The default curve
    /// is the one its system acts on everywhere, and oref acts on its UAM curve wherever it sees a
    /// deviation, announced meal or not, so both are reported in every context. oref's IOB and
    /// zero-temp curves are reported in fasting and post-correction time, and its COB curve in
    /// post-meal time.
    /// </summary>
    public List<PredictionContext> Contexts { get; set; } = [];

    /// <summary>Every prediction of the curve in its <see cref="Contexts"/>, by horizon.</summary>
    public List<PredictionHorizonAccuracy> Horizons { get; set; } = [];

    /// <summary>By the local hour of day the prediction was made in, all 24 hours.</summary>
    public List<PredictionHourAccuracy> ByHour { get; set; } = [];

    /// <summary>By context, one entry per member of <see cref="Contexts"/>.</summary>
    public List<PredictionContextAccuracy> ByContext { get; set; } = [];
}

public class PredictionHourAccuracy
{
    public int Hour { get; set; }

    public List<PredictionHorizonAccuracy> Horizons { get; set; } = [];
}

public class PredictionContextAccuracy
{
    public PredictionContext Context { get; set; }

    public List<PredictionHorizonAccuracy> Horizons { get; set; } = [];
}

/// <summary>
/// The error of the predicted value at one horizon against the sensor reading nearest the time it
/// predicted, within two and a half minutes. Error is predicted minus actual, mg/dL.
/// </summary>
/// <remarks>
/// Only the bias at <see cref="SettingsSignal"/> horizons, +30 and +60 minutes, is read as a sign of
/// the settings the prediction ran on. The reading at a longer horizon follows everything the loop
/// did after the prediction, which the prediction assumed would not happen, so its error measures
/// the loop's response as much as the prediction.
/// </remarks>
public class PredictionHorizonAccuracy
{
    /// <summary>Minutes after the prediction start; for Loop, after the start's five-minute grid point.</summary>
    public int Minutes { get; set; }

    /// <summary>
    /// The signed error over the matched predictions: its mean is the bias, positive where the
    /// prediction ran high. <see cref="LoopAnalysisAggregate.N"/> counts matched predictions and
    /// <see cref="LoopAnalysisAggregate.Sparse"/> counts the days they were made on. Null when none
    /// matched.
    /// </summary>
    public LoopAnalysisAggregate? Error { get; set; }

    /// <summary>Mean absolute error, mg/dL.</summary>
    public double? Mae { get; set; }

    /// <summary>Mean absolute relative difference, percent of the actual reading.</summary>
    public double? Mard { get; set; }

    /// <summary>
    /// Predictions that reached this horizon with no sensor reading within the tolerance of it.
    /// They are counted, not paired with an imputed value.
    /// </summary>
    public int Unmatched { get; set; }

    /// <summary>
    /// Predictions whose horizon fell in a data exclusion (a compression low, a sensor error), where
    /// the readings are known to be wrong. Neither paired nor unmatched.
    /// </summary>
    public int Excluded { get; set; }

    /// <summary>
    /// Predictions whose horizon a carb entry or manual bolus came before. The reading measures that
    /// action, which the prediction could not have known of, so these are kept out of every error
    /// figure. Null when the caller may not read treatments, which it is counted from.
    /// </summary>
    public int? Interrupted { get; set; }

    /// <summary>The horizon's bias is read as a settings signal; see the remarks.</summary>
    public bool SettingsSignal { get; set; }

    /// <summary>The size of a bias at which the report shows it at full strength, mg/dL.</summary>
    public const double FullScaleBiasMgdl = 30;

    /// <summary>
    /// The bias's size as a share of <see cref="FullScaleBiasMgdl"/>, at most 1; the report tints a
    /// cell by it. Null when no prediction matched.
    /// </summary>
    public double? BiasScale => Error?.Mean is { } bias ? Math.Min(1, Math.Abs(bias) / FullScaleBiasMgdl) : null;
}

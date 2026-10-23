namespace Nocturne.Core.Models.V4;

/// <summary>
/// Projection of an <see cref="ApsSnapshot"/> carrying only its prediction curves, without the
/// suggested, enacted and loop JSON blobs.
/// </summary>
/// <param name="PredictedStart">UTC origin of the curves; where each value falls is <c>PredictionAccuracyCalculator.TargetMills</c>.</param>
/// <param name="DefaultJson"><see cref="ApsSnapshot.PredictedDefaultJson"/>; null when the snapshot carries oref's curves.</param>
/// <param name="IobJson"><see cref="ApsSnapshot.PredictedIobJson"/>.</param>
/// <param name="ZtJson"><see cref="ApsSnapshot.PredictedZtJson"/>.</param>
/// <param name="CobJson"><see cref="ApsSnapshot.PredictedCobJson"/>.</param>
/// <param name="UamJson"><see cref="ApsSnapshot.PredictedUamJson"/>.</param>
public readonly record struct ApsPredictionPoint(
    DateTime PredictedStart,
    string? Device,
    bool Enacted,
    AidAlgorithm AidAlgorithm,
    string? DefaultJson,
    string? IobJson,
    string? ZtJson,
    string? CobJson,
    string? UamJson);

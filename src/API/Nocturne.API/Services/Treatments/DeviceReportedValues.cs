namespace Nocturne.API.Services.Treatments;

/// <summary>
/// Shared rules for preferring device-reported IOB/COB over locally-computed values.
/// </summary>
/// <remarks>
/// An AID system (Loop, OpenAPS, AAPS, Trio) uploads the IOB and COB it acted on. Nocturne's own
/// curve is an approximation of that, so where a recent device value exists it is the better answer
/// and every display path should agree on which one that is. <see cref="IobCalculator"/>,
/// <see cref="CobCalculator"/> and the chart pipeline all resolve recency through this constant so
/// the status pill, the chart and <c>/api/v4/summary</c> cannot disagree about whether a given
/// snapshot is still current.
/// </remarks>
public static class DeviceReportedValues
{
    /// <summary>
    /// How old a device snapshot may be and still be preferred over a locally-computed value.
    /// </summary>
    public const long RecencyThresholdMs = 30 * 60 * 1000;

    /// <summary>
    /// How far past the requested instant a snapshot may be timestamped and still be accepted,
    /// absorbing clock skew between the uploading device and the server.
    /// </summary>
    public const long FutureSkewToleranceMs = 5 * 60 * 1000;
}

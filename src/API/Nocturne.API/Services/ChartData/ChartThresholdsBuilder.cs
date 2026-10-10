using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.ChartData;

/// <summary>
/// Builds the <see cref="ChartThresholdsDto"/> shipped with chart data and with the current
/// therapy state.
/// </summary>
/// <remarks>
/// The coloring thresholds (very-low 54, low 70, high 180, very-high 250 mg/dL) are a fixed clinical
/// glycemic band, not the patient's personal target, so a narrow target (e.g. 95-95) does not
/// collapse the "In Range" band onto a single value. The personal target is read from the active
/// profile at the requested time and carried separately as <see cref="ChartThresholdsDto.TargetLow"/>/
/// <see cref="ChartThresholdsDto.TargetHigh"/> for a distinct reference line; it is null when there is
/// no profile. The axis ceiling is <see cref="GlucoseYMaxCap"/>; chart data replaces it with one
/// derived from its series.
/// </remarks>
internal static class ChartThresholdsBuilder
{
    private const double VeryLow = 54;
    private const double Low = GlucoseConstants.TargetBottomMgdl;
    private const double High = GlucoseConstants.TargetTopMgdl;
    private const double VeryHigh = 250;

    /// <summary>
    /// The glucose axis ceiling in mg/dL: the upper bound of the one derived from a series, and
    /// the ceiling carried where there is no series to derive it from.
    /// </summary>
    internal const double GlucoseYMaxCap = 400;

    public static async Task<ChartThresholdsDto> BuildAsync(
        ITargetRangeResolver targetRangeResolver,
        bool hasProfile,
        long atMills,
        CancellationToken cancellationToken)
    {
        var thresholds = new ChartThresholdsDto
        {
            VeryLow = VeryLow,
            Low = Low,
            High = High,
            VeryHigh = VeryHigh,
            GlucoseYMax = GlucoseYMaxCap,
        };

        if (!hasProfile)
            return thresholds;

        return thresholds with
        {
            TargetLow = await targetRangeResolver.GetLowBGTargetAsync(atMills, ct: cancellationToken),
            TargetHigh = await targetRangeResolver.GetHighBGTargetAsync(atMills, ct: cancellationToken),
        };
    }
}

using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.ChartData.Stages;

/// <summary>
/// Chart data pipeline stage that loads profile data and derives the configuration values
/// used by all subsequent stages: timezone, glucose thresholds, and default basal rate.
/// </summary>
/// <remarks>
/// <para>
/// The coloring thresholds (very-low 54, low 70, high 180, very-high 250 mg/dL) are a fixed clinical
/// glycemic band, not the patient's personal target — so a narrow target (e.g. 95-95) does not
/// collapse the "In Range" band onto a single value. The personal target is read from the active
/// profile at <see cref="ChartDataContext.EndTime"/> and carried separately as
/// <see cref="ChartThresholdsDto.TargetLow"/>/<see cref="ChartThresholdsDto.TargetHigh"/> for a
/// distinct reference line. When no profile is available there is no target and basal defaults to 1.0 U/hr.
/// </para>
/// </remarks>
/// <seealso cref="IChartDataStage"/>
/// <seealso cref="ChartDataContext"/>
internal sealed class ProfileLoadStage(
    ITherapySettingsResolver therapySettingsResolver,
    ITargetRangeResolver targetRangeResolver,
    IBasalRateResolver basalRateResolver,
    ILogger<ProfileLoadStage> logger
) : IChartDataStage
{
    private const double DefaultVeryLow = 54;
    private const double DefaultLow = 70;
    private const double DefaultHigh = 180;
    private const double DefaultVeryHigh = 250;

    public async Task<ChartDataContext> ExecuteAsync(ChartDataContext context, CancellationToken cancellationToken)
    {
        var hasData = await therapySettingsResolver.HasDataAsync(cancellationToken);

        string? timezone = null;
        ChartThresholdsDto thresholds;
        double defaultBasalRate;

        if (hasData)
        {
            timezone = await therapySettingsResolver.GetTimezoneAsync(ct: cancellationToken);

            thresholds = new ChartThresholdsDto
            {
                VeryLow = DefaultVeryLow,
                Low = DefaultLow,
                High = DefaultHigh,
                VeryHigh = DefaultVeryHigh,
                TargetLow = await targetRangeResolver.GetLowBGTargetAsync(context.EndTime, ct: cancellationToken),
                TargetHigh = await targetRangeResolver.GetHighBGTargetAsync(context.EndTime, ct: cancellationToken),
            };
            defaultBasalRate = await basalRateResolver.GetBasalRateAsync(context.EndTime, ct: cancellationToken);

            logger.LogDebug("Loaded profile data from V4 resolvers");
        }
        else
        {
            thresholds = new ChartThresholdsDto
            {
                VeryLow = DefaultVeryLow,
                Low = DefaultLow,
                High = DefaultHigh,
                VeryHigh = DefaultVeryHigh,
            };
            defaultBasalRate = 1.0;
        }

        return context with
        {
            Timezone = timezone,
            Thresholds = thresholds,
            DefaultBasalRate = defaultBasalRate,
        };
    }
}

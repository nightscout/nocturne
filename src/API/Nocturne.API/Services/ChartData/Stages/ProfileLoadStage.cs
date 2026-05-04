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
/// Hard-coded very-low (54 mg/dL) and very-high (250 mg/dL) thresholds are not configurable
/// per profile; low and high targets are read from the active profile at <see cref="ChartDataContext.EndTime"/>.
/// When no profile is available the fallback thresholds are 70/180 mg/dL and 1.0 U/hr basal.
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
                Low = await targetRangeResolver.GetLowBGTargetAsync(context.EndTime, ct: cancellationToken),
                High = await targetRangeResolver.GetHighBGTargetAsync(context.EndTime, ct: cancellationToken),
                VeryHigh = DefaultVeryHigh,
            };
            defaultBasalRate = await basalRateResolver.GetBasalRateAsync(context.EndTime, ct: cancellationToken);

            logger.LogDebug("Loaded profile data from V4 resolvers");
        }
        else
        {
            thresholds = new ChartThresholdsDto
            {
                VeryLow = DefaultVeryLow,
                Low = 70,
                High = 180,
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

using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.ChartData.Stages;

/// <summary>
/// Chart data pipeline stage that loads profile data and derives the configuration values
/// used by all subsequent stages: timezone, glucose thresholds, and default basal rate.
/// </summary>
/// <remarks>
/// Thresholds are built by <see cref="ChartThresholdsBuilder"/>. With no profile, basal defaults to 1.0 U/hr.
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
    public async Task<ChartDataContext> ExecuteAsync(ChartDataContext context, CancellationToken cancellationToken)
    {
        var hasData = await therapySettingsResolver.HasDataAsync(cancellationToken);

        string? timezone = null;
        ChartThresholdsDto thresholds;
        double defaultBasalRate;

        if (hasData)
        {
            timezone = await therapySettingsResolver.GetTimezoneAsync(ct: cancellationToken);

            thresholds = await ChartThresholdsBuilder.BuildAsync(
                targetRangeResolver, hasProfile: true, context.EndTime, cancellationToken);
            defaultBasalRate = await basalRateResolver.GetBasalRateAsync(context.EndTime, ct: cancellationToken);

            logger.LogDebug("Loaded profile data from V4 resolvers");
        }
        else
        {
            thresholds = await ChartThresholdsBuilder.BuildAsync(
                targetRangeResolver, hasProfile: false, context.EndTime, cancellationToken);
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

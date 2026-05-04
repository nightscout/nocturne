using Nocturne.Core.Models;

namespace Nocturne.API.Services.ChartData;

/// <summary>
/// Assembles a <see cref="DashboardChartData"/> DTO by copying fields from a fully-populated
/// <see cref="ChartDataContext"/> after all pipeline stages have run.
/// </summary>
/// <seealso cref="IChartDataAssembler"/>
internal sealed class DashboardChartDataAssembler : IChartDataAssembler
{
    public DashboardChartData Assemble(ChartDataContext context)
    {
        return new DashboardChartData
        {
            IobSeries = context.IobSeries,
            CobSeries = context.CobSeries,
            BasalSeries = context.BasalSeries,
            DefaultBasalRate = context.DefaultBasalRate,
            MaxBasalRate = context.MaxBasalRate,
            MaxIob = context.MaxIob,
            MaxCob = context.MaxCob,
            GlucoseData = context.GlucoseData,
            Thresholds = context.Thresholds with { GlucoseYMax = context.GlucoseYMax },
            BolusMarkers = context.BolusMarkers,
            CarbMarkers = context.CarbMarkers,
            DeviceEventMarkers = context.DeviceEventMarkers,
            BgCheckMarkers = context.BgCheckMarkers,
            PumpModeSpans = context.PumpModeSpans,
            ProfileSpans = context.ProfileSpans,
            OverrideSpans = context.OverrideSpans,
            ActivitySpans = context.ActivitySpans,
            TempBasalSpans = context.TempBasalSpans,
            BasalDeliverySpans = context.BasalDeliverySpans,
            SystemEventMarkers = context.SystemEventMarkers,
            TrackerMarkers = context.TrackerMarkers,
            HeartRateSeries = context.HeartRateSeries,
            StepSeries = context.StepSeries,
        };
    }
}

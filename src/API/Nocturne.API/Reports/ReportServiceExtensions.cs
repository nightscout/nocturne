using Nocturne.API.Reports.Datasets;

namespace Nocturne.API.Reports;

internal static class ReportServiceExtensions
{
    /// <summary>The custom report engine: the datasets, the validator and runner, the store, the catalogue and the flag fact evaluator.</summary>
    public static IServiceCollection AddCustomReports(this IServiceCollection services)
    {
        services.AddSingleton<IReportDataset, GlucoseSeriesDataset>();
        services.AddSingleton<IReportDataset, GlucoseStatisticsDataset>();
        services.AddSingleton<IReportDataset, EventResponseDataset>();
        services.AddSingleton<IReportDataset, EventOutcomesDataset>();
        services.AddSingleton<IReportDataset, EventOutcomeSpreadDataset>();
        services.AddSingleton<IReportDataset, EventOccurrenceDataset>();
        services.AddSingleton<IReportDataset, HeartRateDataset>();
        services.AddSingleton<IReportDataset, StepsDataset>();
        services.AddSingleton<IReportDataset, SleepDataset>();
        services.AddSingleton<IReportDataset, SettingsEvidenceDataset>();
        services.AddSingleton<IReportDataset, DailyProfileDataset>();
        services.AddSingleton<IReportDataset, GlucoseDistributionByHourDataset>();
        services.AddSingleton<IReportDataset, TreatmentsDataset>();
        services.AddSingleton<IReportDataset, BasalDeliveryDataset>();
        services.AddSingleton<IReportDataset, DeviceStatusDataset>();
        services.AddSingleton<IReportDataset, GlucoseDistributionDataset>();
        services.AddSingleton<IReportDataset, DailySummaryDataset>();
        services.AddSingleton<IReportDataset, PeriodComparisonDataset>();
        services.AddSingleton<IReportDataset, StateSpansDataset>();
        services.AddSingleton<IReportDataset, DeviceEventsDataset>();
        services.AddSingleton<IReportDataset, SystemEventsDataset>();
        services.AddSingleton<IReportDataset, TrackersDataset>();
        services.AddSingleton<IReportDataset, CurrentStatusDataset>();
        services.AddSingleton<IReportDataset, SleepStagesDataset>();
        services.AddSingleton<IReportDataset, SleepNightsDataset>();
        services.AddSingleton<IReportDataset, SleepBiometricsDataset>();
        services.AddSingleton<IReportDataset, LabHbA1cDataset>();
        services.AddSingleton<IReportDataset, BodyWeightDataset>();
        services.AddSingleton<IReportDataset, AlertExcursionsDataset>();
        services.AddSingleton<IReportDataset, AlertInstancesDataset>();
        services.AddSingleton<IReportDataset, DndWindowsDataset>();
        services.AddSingleton<IReportDataset, SensorDetailDataset>();
        services.AddSingleton<IReportDataset, MeterGlucoseDataset>();
        services.AddSingleton<IReportDataset, CompressionLowsDataset>();
        services.AddSingleton<IReportDataset, GlucoseEpisodesDataset>();
        services.AddSingleton<IReportDatasetRegistry, ReportDatasetRegistry>();
        services.AddSingleton<ReportSpecValidator>();
        services.AddSingleton<ReportSpecRunner>();
        services.AddScoped<ICustomReportCatalogue, ReportCatalogue>();
        services.AddScoped<ReportSourceRepositories>();
        services.AddScoped<ICustomReportSignalAvailability, ReportSignalAvailability>();
        services.AddScoped<ReportReadLog>();
        services.AddScoped<ReportRenderer>();
        services.AddScoped<IReportSpecStore, ReportSpecStore>();
        services.AddScoped<ICustomReportRunner, CustomReportRunner>();
        services.AddScoped<FlagFactEvaluator>();
        return services;
    }
}

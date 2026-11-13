using Nocturne.API.Reports.Datasets;

namespace Nocturne.API.Reports;

internal static class ReportServiceExtensions
{
    /// <summary>The custom report engine: the datasets, the validator and runner, the store and the catalogue.</summary>
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
        services.AddSingleton<IReportDatasetRegistry, ReportDatasetRegistry>();
        services.AddSingleton<ReportSpecValidator>();
        services.AddSingleton<ReportSpecRunner>();
        services.AddScoped<ICustomReportCatalogue, ReportCatalogue>();
        services.AddScoped<ReportReadLog>();
        services.AddScoped<ReportRenderer>();
        services.AddScoped<IReportSpecStore, ReportSpecStore>();
        services.AddScoped<ICustomReportRunner, CustomReportRunner>();
        return services;
    }
}

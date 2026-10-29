using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;
using Nocturne.API.Extensions;
using Nocturne.API.Mcp.Reports;
using Nocturne.API.Mcp.Reports.Datasets;
using Nocturne.API.Mcp.Tools;
using Nocturne.API.Services.Platform;

namespace Nocturne.API.Mcp;

internal static class McpServiceExtensions
{
    public static IServiceCollection AddNocturneMcp(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<McpOptions>(configuration.GetSection(McpOptions.SectionName));
        services.AddSingleton<ReportView>();
        services.AddScoped<McpToolReadLog>();
        AddReports(services);

        // Stateless: each POST is served by a server built on that request's services, so a tool
        // reads the request's tenant, credential and read context (see McpToolGuard).
        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "nocturne",
                    Title = "Nocturne",
                    Version = AppVersion.Current,
                };
            })
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools<StatusTools>()
            .WithTools<GlucoseTools>()
            .WithTools<DatasetTools>()
            .WithTools<FoodTools>()
            .WithResources<ReportResources>()
            .WithRequestFilters(filters =>
            {
                filters.AddCallToolFilter(McpToolGuard.Wrap);
                filters.AddListResourcesFilter(ReportResources.WithUiMeta);
            })
            .WithMcpApps();

        return services;
    }

    private static void AddReports(IServiceCollection services)
    {
        services.AddSingleton<IReportDataset, GlucoseSeriesDataset>();
        services.AddSingleton<IReportDataset, GlucoseStatisticsDataset>();
        services.AddSingleton<IReportDataset, EventResponseDataset>();
        services.AddSingleton<IReportDataset, SettingsEvidenceDataset>();
        services.AddSingleton<IReportDataset, DailyProfileDataset>();
        services.AddSingleton<IReportDataset, GlucoseDistributionByHourDataset>();
        services.AddSingleton<IReportDatasetRegistry, ReportDatasetRegistry>();
        services.AddSingleton<ReportSpecValidator>();
        services.AddSingleton<ReportSpecRunner>();
        services.AddScoped<ReportRenderer>();
        services.AddSingleton(ReportTools.Create);
        services.AddSingleton(ReportTools.CreateRefine);
    }

    public static void MapNocturneMcp(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<IOptions<McpOptions>>().Value.Enabled)
            return;

        app.MapMcp("/mcp")
            .WithMetadata(new McpEndpointMetadata())
            .RequireRateLimiting(ServiceRegistrationExtensions.McpRateLimitPolicy);
    }
}

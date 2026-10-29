using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;
using Nocturne.API.Extensions;
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
            .WithResources<ReportResources>()
            .WithRequestFilters(filters =>
            {
                filters.AddCallToolFilter(McpToolGuard.Wrap);
                filters.AddListResourcesFilter(ReportResources.WithUiMeta);
            })
            .WithMcpApps();

        return services;
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

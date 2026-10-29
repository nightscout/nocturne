using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace Nocturne.API.Mcp.Reports;

/// <summary>
/// Runs a normalised spec for the request's caller and shapes the tool result: the joined
/// summaries as text, the <see cref="ReportResult"/> as <c>structuredContent</c>.
/// </summary>
internal sealed class ReportRenderer(
    ReportSpecRunner runner,
    IHttpContextAccessor httpContextAccessor,
    IServiceProvider services,
    ITenantAppearanceReader appearance,
    ITenantAccessor tenant,
    ICategoryReadContext readContext,
    McpToolReadLog readLog,
    IOptions<MvcJsonOptions> jsonOptions)
{
    public async Task<(CallToolResult Result, ReportResult Report)> RenderAsync(ReportSpec spec, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("A report ran outside an HTTP request.");
        var useMmol = await UsesMmolAsync(cancellationToken);

        var report = await runner.RunAsync(
            spec,
            new ReportRunContext(useMmol, http.GetGrantedScopes(), services, readLog, readContext.IsHistoryClamped),
            cancellationToken);

        var text = new StringBuilder(report.Summary);
        foreach (var warning in report.Warnings)
            text.Append('\n').Append("Warning: ").Append(warning);

        return (new CallToolResult
        {
            Content = [new TextContentBlock { Text = text.ToString() }],
            StructuredContent = JsonSerializer.SerializeToElement(report, jsonOptions.Value.JsonSerializerOptions),
        }, report);
    }

    /// <summary>Whether the tenant shows glucose in mmol/L.</summary>
    public Task<bool> UsesMmolAsync(CancellationToken cancellationToken) =>
        GlucoseUnits.UsesMmolAsync(appearance, tenant, cancellationToken);
}

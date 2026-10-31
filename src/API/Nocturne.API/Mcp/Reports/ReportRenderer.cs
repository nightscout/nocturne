using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace Nocturne.API.Mcp.Reports;

/// <summary>
/// Runs a normalised spec for the request's caller: its scopes, its history clamp, its reader. The
/// MCP tools shape the result as a tool result (<see cref="RenderAsync"/>); the web app's custom
/// reports take the <see cref="ReportResult"/> itself (<see cref="RunAsync"/>).
/// </summary>
internal sealed class ReportRenderer(
    ReportSpecRunner runner,
    IHttpContextAccessor httpContextAccessor,
    IServiceProvider services,
    ITenantAppearanceReader appearance,
    ITenantAccessor tenant,
    McpToolReadLog readLog,
    TenantClockResolver tenantClock,
    IOptions<MvcJsonOptions> jsonOptions)
{
    /// <summary>The joined summaries as text, the <see cref="ReportResult"/> as <c>structuredContent</c>.</summary>
    /// <param name="shortened">The panels whose range the validator cut to their dataset's limit, by id.</param>
    public async Task<(CallToolResult Result, ReportResult Report)> RenderAsync(
        ReportSpec spec, CancellationToken cancellationToken, IReadOnlySet<string>? shortened = null)
    {
        var report = await RunAsync(spec, cancellationToken, shortened);

        var text = new StringBuilder(report.Summary);
        foreach (var warning in report.Warnings)
            text.Append('\n').Append("Warning: ").Append(warning);

        return (new CallToolResult
        {
            Content = [new TextContentBlock { Text = text.ToString() }],
            StructuredContent = JsonSerializer.SerializeToElement(report, jsonOptions.Value.JsonSerializerOptions),
        }, report);
    }

    /// <summary>
    /// The report, with each panel's web report link and the link that opens the whole spec in the
    /// web app (<see cref="ReportResult.OpenUrl"/>), both on the request's host.
    /// </summary>
    public async Task<ReportResult> RunAsync(ReportSpec spec, CancellationToken cancellationToken, IReadOnlySet<string>? shortened = null)
    {
        var http = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("A report ran outside an HTTP request.");
        var useMmol = await UsesMmolAsync(cancellationToken);

        var report = await runner.RunAsync(
            spec,
            new ReportRunContext(useMmol, http.GetGrantedScopes(), services, readLog, http.IsCallerHistoryClamped(), shortened),
            cancellationToken);

        var origin = $"{http.Request.PublicScheme()}://{http.Request.Host}";
        var zone = (await tenantClock.ResolveAsync(cancellationToken)).Zone?.Id;
        return report with
        {
            TimeZone = zone is null ? null
                : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone, out var iana) ? iana : zone,
            Panels = [.. report.Panels.Select((panel, i) => panel.Status == ReportPanelStatus.Ok
                ? panel with { Link = ReportLinks.For(origin, spec.Panels[i], panel, zone) }
                : panel)],
            OpenUrl = ReportSpecLink.NewReportUrl(origin, spec, jsonOptions.Value.JsonSerializerOptions),
        };
    }

    /// <summary>Whether the tenant shows glucose in mmol/L.</summary>
    public Task<bool> UsesMmolAsync(CancellationToken cancellationToken) =>
        GlucoseUnits.UsesMmolAsync(appearance, tenant, cancellationToken);
}

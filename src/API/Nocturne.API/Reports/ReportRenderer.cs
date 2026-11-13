using Nocturne.API.Extensions;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.API.Services.Identity;

namespace Nocturne.API.Reports;

/// <summary>
/// Runs a normalised spec for the request's caller: its scopes, its history clamp, its reader.
/// </summary>
internal sealed class ReportRenderer(
    ReportSpecRunner runner,
    IHttpContextAccessor httpContextAccessor,
    IServiceProvider services,
    ITenantAppearanceReader appearance,
    ITenantAccessor tenant,
    ITenantGlucoseUnits glucoseUnits,
    ReportReadLog readLog,
    TenantClockResolver tenantClock)
{
    /// <summary>The report, with each panel's built-in web report link on the request's host.</summary>
    public async Task<ReportResult> RunAsync(ReportSpec spec, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("A report ran outside an HTTP request.");
        var useMmol = await UsesMmolAsync(cancellationToken);
        var tenantZone = (await tenantClock.ResolveAsync(cancellationToken)).Zone;

        var report = await runner.RunAsync(
            spec,
            new ReportRunContext(useMmol, http.GetGrantedScopes(), services, readLog, http.IsCallerHistoryClamped(), tenantZone),
            cancellationToken);

        var origin = $"{http.Request.PublicScheme()}://{http.Request.Host}";
        var zone = tenantZone?.Id;
        var look = await appearance.GetTenantAppearanceAsync(tenant.TenantId, cancellationToken);
        return report with
        {
            TimeZone = zone is null ? null
                : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone, out var iana) ? iana : zone,
            Panels = [.. report.Panels.Select((panel, i) => panel.Status == ReportPanelStatus.Ok
                ? panel with { Link = ReportLinks.For(origin, spec.Panels[i], panel, zone) }
                : panel)],
            Locale = string.IsNullOrWhiteSpace(look.RegionFormat) ? null : look.RegionFormat,
            Hour12 = look.TimeFormat is null ? null : look.TimeFormat != "24",
        };
    }

    /// <summary>Whether the tenant shows glucose in mmol/L.</summary>
    public Task<bool> UsesMmolAsync(CancellationToken cancellationToken) =>
        glucoseUnits.UsesMmolAsync(cancellationToken);
}

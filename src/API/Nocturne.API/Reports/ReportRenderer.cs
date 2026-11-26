using Nocturne.API.Extensions;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Configuration;
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
    TenantClockResolver tenantClock,
    ISubjectRepository subjects)
{
    /// <summary>The report, with each panel's built-in web report link on the request's host.</summary>
    public async Task<ReportResult> RunAsync(ReportSpec spec, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("A report ran outside an HTTP request.");
        var useMmol = await UsesMmolAsync(cancellationToken);
        var tenantZone = (await tenantClock.ResolveAsync(cancellationToken)).Zone;
        var look = await appearance.GetTenantAppearanceAsync(tenant.TenantId, cancellationToken);
        var own = await CallerPreferencesAsync(http, cancellationToken);
        var timeFormat = own?.TimeFormat ?? look.TimeFormat;
        var regionFormat = string.IsNullOrWhiteSpace(own?.RegionFormat) ? look.RegionFormat : own.RegionFormat;
        bool? hour12 = timeFormat is null ? null : timeFormat != "24";

        var report = await runner.RunAsync(
            spec,
            new ReportRunContext(useMmol, http.GetGrantedScopes(), services, readLog, http.IsCallerHistoryClamped(), tenantZone, hour12,
                string.IsNullOrWhiteSpace(regionFormat) ? null : regionFormat, ReadsA1cInMmolMol(own)),
            cancellationToken);

        var origin = $"{http.Request.PublicScheme()}://{http.Request.Host}";
        var zone = tenantZone?.Id;
        return report with
        {
            TimeZone = zone is null ? null
                : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone, out var iana) ? iana : zone,
            Panels = [.. report.Panels.Select((panel, i) => panel.Status == ReportPanelStatus.Ok
                ? panel with { Link = ReportLinks.For(origin, spec.Panels[i], panel, zone) }
                : panel)],
            Locale = string.IsNullOrWhiteSpace(regionFormat) ? null : regionFormat,
            Hour12 = hour12,
        };
    }

    /// <summary>
    /// The caller's own display preferences, or null for a caller with no subject (a share, a legacy secret). The web
    /// draws with a member's own clock and region first and the tenant's where the member set none
    /// (<c>applyTenantPresentation</c>), so the times a report writes in words follow the same order.
    /// </summary>
    private async Task<UserDisplayPreferences?> CallerPreferencesAsync(HttpContext http, CancellationToken cancellationToken)
    {
        if (http.GetAuthContext()?.SubjectId is not { } subjectId)
            return null;
        var subject = await subjects.GetAsync(subjectId, cancellationToken);
        return subject is null ? null : UserDisplayPreferences.Deserialize(subject.Preferences);
    }

    /// <summary>Whether the caller reads HbA1c in mmol/mol: its own HbA1c unit, which has no tenant default.</summary>
    internal static bool ReadsA1cInMmolMol(UserDisplayPreferences? own) => own?.A1cUnits == "mmol/mol";

    /// <summary>Whether the caller reads HbA1c in mmol/mol.</summary>
    public async Task<bool> ReadsA1cInMmolMolAsync(CancellationToken cancellationToken) =>
        httpContextAccessor.HttpContext is { } http && ReadsA1cInMmolMol(await CallerPreferencesAsync(http, cancellationToken));

    /// <summary>Whether the tenant shows glucose in mmol/L.</summary>
    public Task<bool> UsesMmolAsync(CancellationToken cancellationToken) =>
        glucoseUnits.UsesMmolAsync(cancellationToken);
}

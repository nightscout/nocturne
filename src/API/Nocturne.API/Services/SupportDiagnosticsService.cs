using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services;

/// <summary>
/// The tenant configuration a support report may carry.
/// </summary>
/// <remarks>
/// Every field here is published in a GitHub issue the reporter cannot retract, so this type is an
/// allowlist and not a projection: fields are named one at a time, and nothing is copied wholesale
/// from a DTO that might later grow a credential. <see cref="SupportConnectorSummary"/> exists for
/// the same reason rather than reusing <c>ConnectorStatusDto</c>, which carries
/// <c>StateMessage</c> — a connector's last error, which can quote a tenant-configured URL.
/// </remarks>
public class SupportDiagnosticsResponse
{
    /// <summary>Tenant display units, as <c>features.display.units</c> holds them.</summary>
    public string? GlucoseUnits { get; set; }

    /// <summary>Tenant time format, either <c>12</c> or <c>24</c>.</summary>
    public string? TimeFormat { get; set; }

    /// <summary>IANA zone the patient's day is rendered in, when one is set.</summary>
    public string? PatientTimeZone { get; set; }

    /// <summary>Which reading wins when several sources cover the same moment.</summary>
    public string? DataSourcePriority { get; set; }

    /// <summary>Alert rules the tenant has defined, enabled or not.</summary>
    public int AlertRuleCount { get; set; }

    /// <summary>Configured connectors, or those that have ever stored a reading.</summary>
    public IReadOnlyList<SupportConnectorSummary> Connectors { get; set; } = [];
}

/// <summary>
/// One connector, reduced to what a support reader needs to see.
/// </summary>
/// <seealso cref="SupportDiagnosticsResponse"/>
public class SupportConnectorSummary
{
    /// <summary>Connector id, lowercased — <c>glooko</c>, <c>nightscout</c>.</summary>
    public required string Name { get; set; }

    public bool IsEnabled { get; set; }

    public bool IsHealthy { get; set; }

    public DateTime? LastSuccessfulSync { get; set; }
}

/// <summary>Assembles the snapshot a support report may attach.</summary>
public interface ISupportDiagnosticsService
{
    Task<SupportDiagnosticsResponse> GetAsync(CancellationToken ct = default);
}

/// <inheritdoc />
public class SupportDiagnosticsService(
    IConnectorHealthService connectorHealth,
    IUISettingsService uiSettings,
    ITherapySettingsResolver therapySettings,
    ITenantDbContextFactory contextFactory,
    ILogger<SupportDiagnosticsService> logger
) : ISupportDiagnosticsService
{
    /// <inheritdoc />
    public async Task<SupportDiagnosticsResponse> GetAsync(CancellationToken ct = default)
    {
        var settings = await uiSettings.GetSettingsAsync(ct);
        var statuses = await connectorHealth.GetConnectorStatusesAsync(ct);

        await using var db = await contextFactory.CreateAsync(ct);
        var alertRuleCount = await db.AlertRules.CountAsync(ct);

        // The timezone resolver reads the patient record and falls back to the legacy per-profile
        // value; a tenant that has set neither is not an error, so a failure here costs the field
        // rather than the report.
        string? timeZone = null;
        try
        {
            timeZone = await therapySettings.GetTimezoneAsync(ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Support diagnostics could not resolve the patient time zone");
        }

        return new SupportDiagnosticsResponse
        {
            GlucoseUnits = settings.Features.Display.Units,
            TimeFormat = settings.Features.Display.TimeFormat,
            PatientTimeZone = timeZone,
            DataSourcePriority = settings.Devices.CgmConfiguration.DataSourcePriority,
            AlertRuleCount = alertRuleCount,
            Connectors = statuses
                .Select(s => new SupportConnectorSummary
                {
                    Name = s.Name,
                    IsEnabled = s.IsEnabled,
                    IsHealthy = s.IsHealthy,
                    LastSuccessfulSync = s.LastSuccessfulSync,
                })
                .ToList(),
        };
    }
}

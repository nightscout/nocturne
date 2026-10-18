using System.Globalization;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.GoogleHealth.Configurations;

[ConnectorRegistration(
    "GoogleHealth", ServiceNames.GoogleHealthConnector, "GOOGLE_HEALTH",
    nameof(ConnectSource.GoogleHealth), DataSources.GoogleHealthConnector, "google-health",
    ConnectorCategory.Sync, "Import health and fitness data from Google Health", "Google Health",
    SupportsHistoricalSync = true,
    MaxHistoricalDays = 0,
    SupportsManualSync = true,
    SupportedDataTypes = [SyncDataType.Steps, SyncDataType.HeartRate, SyncDataType.BodyWeight, SyncDataType.Sleep],
    DefaultActiveThresholdMinutes = 30,
    DefaultStaleThresholdMinutes = 180)]
public sealed class GoogleHealthConnectorConfiguration : BaseConnectorConfiguration
{
    public GoogleHealthConnectorConfiguration()
    {
        ConnectSource = ConnectSource.GoogleHealth;
        SyncIntervalMinutes = 15;
    }

    [ConnectorProperty(ConnectorPropertyKey.RefreshToken, Secret = true, Hidden = true)]
    public string? RefreshToken { get; set; }

    /// <summary>
    /// The client <see cref="RefreshToken"/> was issued to, under the <c>clientId</c> key a former
    /// per-tenant client was stored under, so such a session is checked against the instance client
    /// like any other (<see cref="Services.GoogleHealthSessionStore.IssuedToAnotherClient"/>).
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.ClientId, Secret = true, Hidden = true)]
    public string? SessionClientId { get; set; }

    [ConnectorProperty(ConnectorPropertyKey.LookbackDays, DefaultValue = "7", MinValue = 1, MaxValue = 90)]
    public int HistoryDays { get; set; } = 7;

    [ConnectorProperty(ConnectorPropertyKey.ImportFrom, Hidden = true)]
    public string? ImportFrom { get; set; }

    [ConnectorProperty(ConnectorPropertyKey.PreviewOnly, Hidden = true)]
    public bool PreviewOnly { get; set; }

    [ConnectorProperty(ConnectorPropertyKey.GrantedScopes, Hidden = true)]
    public string? GrantedScopes { get; set; }

    public string[] ParsedGrantedScopes() => (GrantedScopes ?? string.Empty)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    [ConnectorProperty(ConnectorPropertyKey.SyncSteps, DefaultValue = "true")]
    public bool SyncSteps { get; set; } = true;

    [ConnectorProperty(ConnectorPropertyKey.SyncHeartRate, DefaultValue = "true")]
    public bool SyncHeartRate { get; set; } = true;

    [ConnectorProperty(ConnectorPropertyKey.SyncBodyWeight, DefaultValue = "true")]
    public bool SyncBodyWeight { get; set; } = true;

    [ConnectorProperty(ConnectorPropertyKey.SyncSleep, DefaultValue = "true")]
    public bool SyncSleep { get; set; } = true;

    protected override void ValidateSourceSpecificConfiguration()
    {
        if (HistoryDays is < 1 or > 90)
            throw new ArgumentException("Google Health LookbackDays must be between 1 and 90.");

        if (!string.IsNullOrWhiteSpace(ImportFrom) &&
            (!DateTimeOffset.TryParse(
                 ImportFrom,
                 CultureInfo.InvariantCulture,
                 DateTimeStyles.RoundtripKind,
                 out var importFrom) ||
             importFrom < new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero) ||
             importFrom > DateTimeOffset.UtcNow.AddDays(1)))
            throw new ArgumentException("Google Health ImportFrom is invalid.");
    }
}

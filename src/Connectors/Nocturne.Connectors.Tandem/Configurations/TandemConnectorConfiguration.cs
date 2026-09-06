using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.Tandem.Configurations;

/// <summary>
/// Configuration for the Tandem Source (t:connect) connector. Authenticates against the Tandem
/// Source cloud with an account email and password, then imports pump event history for a t:slim X2
/// or Mobi pump. Feature parity reference: the open-source <c>tconnectsync</c> project.
/// </summary>
[ConnectorRegistration(
    "Tandem",
    ServiceNames.TConnectSyncConnector,
    "TANDEM",
    "ConnectSource.Tandem",
    DataSources.TConnectSyncConnector,
    "tandem",
    ConnectorCategory.Pump,
    "Import t:slim X2 / Mobi pump data (boluses, basal, CGM, alarms, profiles) from Tandem Source",
    "Tandem Source",
    SupportsHistoricalSync = true,
    SupportsManualSync = true,
    DefaultActiveThresholdMinutes = 180,
    DefaultStaleThresholdMinutes = 360,
    SupportedDataTypes =
    [
        SyncDataType.Glucose,
        SyncDataType.Boluses,
        SyncDataType.CarbIntake,
        SyncDataType.BolusCalculations,
        SyncDataType.TempBasals,
        SyncDataType.DeviceEvents,
        SyncDataType.StateSpans,
        SyncDataType.DeviceStatus,
        SyncDataType.Profiles
    ]
)]
public class TandemConnectorConfiguration : BaseConnectorConfiguration
{
    public TandemConnectorConfiguration()
    {
        ConnectSource = ConnectSource.Tandem;
    }

    /// <summary>Tandem Source account email.</summary>
    [ConnectorProperty(ConnectorPropertyKey.Email, Required = true, Format = "email")]
    public string Email { get; set; } = string.Empty;

    /// <summary>Tandem Source account password.</summary>
    [ConnectorProperty(ConnectorPropertyKey.Password, Required = true, Secret = true)]
    public string Password { get; set; } = string.Empty;

    /// <summary>Account region. "US" (default) or "EU".</summary>
    [ConnectorProperty(ConnectorPropertyKey.Region, AllowedValues = ["US", "EU"], DefaultValue = "US")]
    public string Region { get; set; } = "US";

    /// <summary>
    /// Optional pump serial number to follow when more than one pump is on the account.
    /// When empty, the connector selects the pump with the most recent events.
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.PumpSerialNumber)]
    public string? PumpSerialNumber { get; set; }

    /// <summary>
    /// When true, fetch every event type from the pump history log rather than the default
    /// backend filter. Required to import device status (battery / IOB daily-basal events).
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.FetchAllEventTypes, DefaultValue = "false")]
    public bool FetchAllEventTypes { get; set; }

    /// <summary>
    /// When true, basal entries that resolve to a near-zero rate (&lt; 0.01 U/hr) are skipped,
    /// mirroring <c>tconnectsync</c>'s <c>IGNORE_ZERO_UNIT_BASAL</c>.
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.IgnoreZeroUnitBasal, DefaultValue = "false")]
    public bool IgnoreZeroUnitBasal { get; set; }

    protected override void ValidateSourceSpecificConfiguration()
    {
        if (!string.Equals(Region, "US", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Region, "EU", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Tandem: Region must be 'US' or 'EU' (was '{Region}')");
    }
}

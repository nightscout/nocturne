using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.Twiist.Configurations;

/// <summary>
/// Configuration for the Twiist Insight follower connector.
/// </summary>
[ConnectorRegistration(
    "Twiist",
    ServiceNames.TwiistConnector,
    "TWIIST",
    "ConnectSource.Twiist",
    "twiist-connector",
    "twiist",
    ConnectorCategory.Cgm,
    "Connect to Twiist Insight (Omnipod 5 / Tidepool Loop) via the follower API",
    "Twiist Insight",
    SupportsHistoricalSync = false,
    SupportsManualSync = true,
    SupportedDataTypes = [SyncDataType.Glucose, SyncDataType.Boluses, SyncDataType.CarbIntake]
)]
public class TwiistConnectorConfiguration : BaseConnectorConfiguration
{
    public TwiistConnectorConfiguration()
    {
        ConnectSource = ConnectSource.Twiist;
    }

    /// <summary>
    /// Twiist account username (email).
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.Username, Required = true)]
    public string Username { get; init; } = string.Empty;

    /// <summary>
    /// Twiist account password.
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.Password, Required = true, Secret = true)]
    public string Password { get; init; } = string.Empty;

    /// <summary>
    /// The PWD (person with diabetes) UUID v4 to follow, used as the path segment in
    /// /pwd/{id}/package. Auto-discovered from the follower overviews endpoint when left blank
    /// (the common single-follow case), so it is hidden from the UI and optional. A value can
    /// still be set as an advanced override when an account follows more than one person.
    /// Named to match its <see cref="ConnectorPropertyKey.PatientId"/> config key: the binder
    /// resolves JSON keys from the camel-cased property name, so the name must match the key
    /// or the persisted value never binds.
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.PatientId, Hidden = true)]
    public string PatientId { get; init; } = string.Empty;
}

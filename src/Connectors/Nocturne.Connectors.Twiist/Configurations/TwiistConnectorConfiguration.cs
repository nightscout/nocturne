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
    /// /pwd/{id}/package. Found via the follower overviews endpoint (the pwdId field).
    /// Named to match its <see cref="ConnectorPropertyKey.PatientId"/> config key: the binder
    /// resolves JSON keys from the camel-cased property name, so the name must match the key
    /// or the persisted value never binds.
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.PatientId, Required = true)]
    public string PatientId { get; init; } = string.Empty;
}

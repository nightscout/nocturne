using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.CareLink.Configurations;

[ConnectorRegistration(
    "CareLink", ServiceNames.CareLinkConnector, "CARELINK",
    nameof(ConnectSource.CareLink), DataSources.CareLinkConnector, "carelink",
    ConnectorCategory.Pump, "Connect to Medtronic CareLink",
    "Medtronic CareLink",
    SupportsHistoricalSync = false,
    SupportsManualSync = true,
    SupportedDataTypes = [SyncDataType.Glucose, SyncDataType.DeviceStatus],
    DefaultActiveThresholdMinutes = 10,
    DefaultStaleThresholdMinutes = 30
)]
public class CareLinkConnectorConfiguration : BaseConnectorConfiguration
{
    public CareLinkConnectorConfiguration()
    {
        ConnectSource = ConnectSource.CareLink;
        SyncIntervalMinutes = 5;
    }

    [ConnectorProperty(ConnectorPropertyKey.Username, Required = true)]
    public string Username { get; init; } = string.Empty;

    [ConnectorProperty(ConnectorPropertyKey.Password, Secret = true)]
    public string? Password { get; init; }

    [ConnectorProperty(ConnectorPropertyKey.RefreshToken, Secret = true)]
    public string? RefreshToken { get; init; }

    [ConnectorProperty(ConnectorPropertyKey.Server, AllowedValues = ["EU", "US"])]
    public string Server { get; init; } = "EU";

    [ConnectorProperty(ConnectorPropertyKey.CountryCode)]
    public string CountryCode { get; init; } = "gb";

    [ConnectorProperty(ConnectorPropertyKey.LanguageCode)]
    public string LanguageCode { get; init; } = "en";

    [ConnectorProperty(ConnectorPropertyKey.PatientId)]
    public string? PatientId { get; init; }

    protected override void ValidateSourceSpecificConfiguration()
    {
        if (string.IsNullOrWhiteSpace(Password) && string.IsNullOrWhiteSpace(RefreshToken))
            throw new ArgumentException(
                "At least one of Password or RefreshToken must be provided.");
    }
}

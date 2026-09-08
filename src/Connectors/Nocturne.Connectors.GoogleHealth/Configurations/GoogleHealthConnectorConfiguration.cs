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

    [ConnectorProperty(ConnectorPropertyKey.ClientId, Required = true)]
    public string ClientId { get; set; } = string.Empty;

    [ConnectorProperty(ConnectorPropertyKey.ClientSecret, Required = true, Secret = true)]
    public string? ClientSecret { get; set; }

    [ConnectorProperty(ConnectorPropertyKey.CallbackUrl, Required = true, Format = "uri")]
    public string CallbackUrl { get; set; } = string.Empty;

    public static bool IsValidClientId(string clientId) =>
        !string.IsNullOrWhiteSpace(clientId) &&
        clientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal);

    public static bool IsValidCallbackUrl(string callbackUrl) =>
        Uri.TryCreate(callbackUrl, UriKind.Absolute, out var callback) &&
        callback.Scheme == Uri.UriSchemeHttps &&
        callback.HostNameType == UriHostNameType.Dns &&
        callback.UserInfo == string.Empty &&
        callback.Query == string.Empty &&
        callback.Fragment == string.Empty &&
        callback.AbsolutePath == "/settings/connectors/google-health/callback";

    protected override void ValidateSourceSpecificConfiguration()
    {
        if (!IsValidClientId(ClientId))
            throw new ArgumentException("Google Health ClientId is invalid.");

        if (!IsValidCallbackUrl(CallbackUrl))
            throw new ArgumentException("Google Health CallbackUrl is invalid.");
    }
}

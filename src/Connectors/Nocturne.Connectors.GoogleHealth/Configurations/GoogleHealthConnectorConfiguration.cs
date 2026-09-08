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

    [ConnectorProperty(ConnectorPropertyKey.ClientId, Required = true)]
    public string ClientId { get; set; } = string.Empty;

    [ConnectorProperty(ConnectorPropertyKey.ClientSecret, Required = true, Secret = true)]
    public string? ClientSecret { get; set; }

    [ConnectorProperty(ConnectorPropertyKey.RefreshToken, Secret = true, Hidden = true)]
    public string? RefreshToken { get; set; }

    [ConnectorProperty(ConnectorPropertyKey.CallbackUrl, Required = true, Format = "uri")]
    public string CallbackUrl { get; set; } = string.Empty;

    [ConnectorProperty(ConnectorPropertyKey.LookbackDays, DefaultValue = "7", MinValue = 1, MaxValue = 90)]
    public int HistoryDays { get; set; } = 7;

    [ConnectorProperty(ConnectorPropertyKey.ImportFrom, Hidden = true)]
    public string? ImportFrom { get; set; }

    [ConnectorProperty(ConnectorPropertyKey.PreviewOnly, Hidden = true)]
    public bool PreviewOnly { get; set; }

    [ConnectorProperty(ConnectorPropertyKey.GrantedScopes, Secret = true, Hidden = true)]
    public string? GrantedScopes { get; set; }

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

using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.MyFitnessPal.Configurations;

[ConnectorRegistration(
    "MyFitnessPal",
    ServiceNames.MyFitnessPalConnector,
    "MYFITNESSPAL",
    "ConnectSource.MyFitnessPal",
    "myfitnesspal-connector",
    "myfitnesspal",
    ConnectorCategory.Nutrition,
    "Sync food diary entries from MyFitnessPal for meal matching",
    "MyFitnessPal",
    SupportsHistoricalSync = true,
    MaxHistoricalDays = 365,
    SupportsManualSync = true,
    DefaultActiveThresholdMinutes = 180,
    DefaultStaleThresholdMinutes = 360,
    SupportedDataTypes = [SyncDataType.Food]
)]
public class MyFitnessPalConnectorConfiguration : BaseConnectorConfiguration
{
    public MyFitnessPalConnectorConfiguration()
    {
        ConnectSource = ConnectSource.MyFitnessPal;
        SyncIntervalMinutes = 15;
    }

    [ConnectorProperty(ConnectorPropertyKey.Username, Required = true)]
    public string Username { get; set; } = string.Empty;

    [ConnectorProperty(ConnectorPropertyKey.Password, Secret = true)]
    public string? Password { get; set; }

    /// <summary>
    ///     Long-lived refresh token minted from the password grant. Once present it is used in
    ///     preference to the password, and is rewritten after each rotation.
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.RefreshToken, Secret = true)]
    public string? RefreshToken { get; set; }

    /// <summary>
    ///     MyFitnessPal numeric user id returned by the token endpoint, sent as the
    ///     <c>mfp-user-id</c> header. Derived automatically, so it is not a form field.
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.UserId, Hidden = true)]
    public string? UserId { get; set; }

    [ConnectorProperty(ConnectorPropertyKey.LookbackDays, DefaultValue = "7", MinValue = 1, MaxValue = 365)]
    public int LookbackDays { get; set; } = 7;

    /// <summary>
    ///     When the diary was last read all the way back to its first entry. Only such a read
    ///     establishes that an entry it never mentioned has been deleted; it runs on the schedule in
    ///     <see cref="MyFitnessPalConstants.FullWalkInterval"/>. Maintained by the connector, not a
    ///     form field.
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.LastFullWalkAt, Hidden = true)]
    public string? LastFullWalkAt { get; set; }

    protected override void ValidateSourceSpecificConfiguration()
    {
        if (string.IsNullOrWhiteSpace(Password) && string.IsNullOrWhiteSpace(RefreshToken))
            throw new ArgumentException(
                "At least one of Password or RefreshToken must be provided.");
    }
}

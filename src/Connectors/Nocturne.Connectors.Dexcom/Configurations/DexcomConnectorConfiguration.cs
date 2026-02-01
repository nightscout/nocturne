using System.ComponentModel.DataAnnotations;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.Dexcom.Configurations;

/// <summary>
///     Configuration specific to Dexcom Share connector
/// </summary>
[ConnectorRegistration(
    "Dexcom",
    "Nocturne_Connectors_Dexcom",
    ServiceNames.DexcomConnector,
    ServiceNames.ConnectorEnvironment.DexcomPrefix,
    "ConnectSource.Dexcom",
    "dexcom-connector",
    "dexcom",
    ConnectorCategory.Cgm,
    "Connect to Dexcom Share or Clarity",
    "Dexcom"
)]
public class DexcomConnectorConfiguration : BaseConnectorConfiguration
{
    public DexcomConnectorConfiguration()
    {
        ConnectSource = ConnectSource.Dexcom;
    }

    /// <summary>
    ///     Dexcom Share username
    /// </summary>
    [Required]
    [EnvironmentVariable("CONNECT_DEXCOM_USERNAME")]
    [AspireParameter("dexcom-username", "Username", false, "Dexcom account username")]
    [RuntimeConfigurable("Username", "Connection")]
    public string DexcomUsername { get; init; } = string.Empty;

    /// <summary>
    ///     Dexcom Share password
    /// </summary>
    [Required]
    [Secret]
    [EnvironmentVariable("CONNECT_DEXCOM_PASSWORD")]
    [AspireParameter("dexcom-password", "Password", true, "Dexcom account password")]
    public string DexcomPassword { get; init; } = string.Empty;

    /// <summary>
    ///     Dexcom server region (US or EU)
    /// </summary>
    [EnvironmentVariable("CONNECT_DEXCOM_SERVER")]
    [AspireParameter("dexcom-server", "Server", false, "Dexcom server (US or EU)", "US")]
    [RuntimeConfigurable("Server", "Connection")]
    [ConfigSchema(Enum = ["US", "EU"])]
    public string DexcomServer { get; init; } = "US";

    protected override void ValidateSourceSpecificConfiguration()
    {
        if (string.IsNullOrWhiteSpace(DexcomUsername))
            throw new ArgumentException(
                "CONNECT_DEXCOM_USERNAME is required when using Dexcom Share source"
            );

        if (string.IsNullOrWhiteSpace(DexcomPassword))
            throw new ArgumentException(
                "CONNECT_DEXCOM_PASSWORD is required when using Dexcom Share source"
            );
    }
}
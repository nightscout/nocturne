using System.ComponentModel.DataAnnotations;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.MyLife.Configurations;

[ConnectorRegistration(
    "MyLife",
    "Nocturne_Connectors_MyLife",
    ServiceNames.MyLifeConnector,
    ServiceNames.ConnectorEnvironment.MyLifePrefix,
    "ConnectSource.MyLife",
    "mylife-connector",
    "mylife",
    ConnectorCategory.Pump,
    "Connect to MyLife for pump data",
    "MyLife"
)]
public class MyLifeConnectorConfiguration : BaseConnectorConfiguration
{
    public MyLifeConnectorConfiguration()
    {
        ConnectSource = ConnectSource.MyLife;
    }

    [Required]
    [AspireParameter(
        "mylife-username",
        "Username",
        true,
        "MyLife account username"
    )]
    [EnvironmentVariable(ConnectorEnvironmentVariables.MyLifeUsername)]
    [RuntimeConfigurable("Username", "Connection")]
    public string MyLifeUsername { get; set; } = string.Empty;

    [Required]
    [Secret]
    [AspireParameter(
        "mylife-password",
        "Password",
        true,
        "MyLife account password"
    )]
    [EnvironmentVariable(ConnectorEnvironmentVariables.MyLifePassword)]
    public string MyLifePassword { get; set; } = string.Empty;

    [AspireParameter(
        "mylife-patient-id",
        "PatientId",
        false,
        "Patient id for MyLife",
        ""
    )]
    [EnvironmentVariable(ConnectorEnvironmentVariables.MyLifePatientId)]
    [RuntimeConfigurable("Patient ID", "Connection")]
    public string MyLifePatientId { get; set; } = string.Empty;

    [AspireParameter(
        "mylife-service-url",
        "ServiceUrl",
        false,
        "Override MyLife service url",
        ""
    )]
    [EnvironmentVariable(ConnectorEnvironmentVariables.MyLifeServiceUrl)]
    [RuntimeConfigurable("Service URL", "Connection")]
    [ConfigSchema(Format = "uri")]
    public string MyLifeServiceUrl { get; set; } = string.Empty;

    [AspireParameter(
        "mylife-enable-glucose-sync",
        "EnableGlucoseSync",
        false,
        "Enable CGM glucose sync",
        "true"
    )]
    [EnvironmentVariable(ConnectorEnvironmentVariables.MyLifeEnableGlucoseSync)]
    [RuntimeConfigurable("Enable Glucose Sync", "Sync")]
    public bool EnableGlucoseSync { get; set; } = true;

    [AspireParameter(
        "mylife-enable-manual-bg-sync",
        "EnableManualBgSync",
        false,
        "Enable manual BG sync",
        "true"
    )]
    [EnvironmentVariable(ConnectorEnvironmentVariables.MyLifeEnableManualBgSync)]
    [RuntimeConfigurable("Enable Manual BG Sync", "Sync")]
    public bool EnableManualBgSync { get; set; } = true;

    [AspireParameter(
        "mylife-enable-meal-carb-consolidation",
        "EnableMealCarbConsolidation",
        false,
        "Enable meal carb and bolus consolidation",
        "true"
    )]
    [EnvironmentVariable(ConnectorEnvironmentVariables.MyLifeEnableMealCarbConsolidation)]
    [RuntimeConfigurable("Meal Carb Consolidation", "Advanced")]
    public bool EnableMealCarbConsolidation { get; set; } = true;

    [AspireParameter(
        "mylife-enable-temp-basal-consolidation",
        "EnableTempBasalConsolidation",
        false,
        "Enable temp basal consolidation",
        "true"
    )]
    [EnvironmentVariable(ConnectorEnvironmentVariables.MyLifeEnableTempBasalConsolidation)]
    [RuntimeConfigurable("Temp Basal Consolidation", "Advanced")]
    public bool EnableTempBasalConsolidation { get; set; } = true;

    [AspireParameter(
        "mylife-temp-basal-consolidation-window-minutes",
        "TempBasalConsolidationWindowMinutes",
        false,
        "Temp basal consolidation window in minutes",
        "5"
    )]
    [EnvironmentVariable(ConnectorEnvironmentVariables.MyLifeTempBasalConsolidationWindowMinutes)]
    [RuntimeConfigurable("Consolidation Window (min)", "Advanced")]
    [ConfigSchema(Minimum = 1, Maximum = 30)]
    public int TempBasalConsolidationWindowMinutes { get; set; } = 5;

    [AspireParameter(
        "mylife-app-platform",
        "AppPlatform",
        false,
        "MyLife app platform",
        "2"
    )]
    [EnvironmentVariable(ConnectorEnvironmentVariables.MyLifeAppPlatform)]
    public int AppPlatform { get; set; } = 2;

    [AspireParameter(
        "mylife-app-version",
        "AppVersion",
        false,
        "MyLife app version",
        "20403"
    )]
    [EnvironmentVariable(ConnectorEnvironmentVariables.MyLifeAppVersion)]
    public int AppVersion { get; set; } = 20403;

    [AspireParameter(
        "mylife-sync-months",
        "SyncMonths",
        false,
        "Maximum months to sync",
        "6"
    )]
    [EnvironmentVariable(ConnectorEnvironmentVariables.MyLifeSyncMonths)]
    [RuntimeConfigurable("Sync Months", "Sync")]
    [ConfigSchema(Minimum = 1, Maximum = 24)]
    public int SyncMonths { get; set; } = 6;

    protected override void ValidateSourceSpecificConfiguration()
    {
        if (string.IsNullOrWhiteSpace(MyLifeUsername))
            throw new ArgumentException("CONNECT_MYLIFE_USERNAME is required");

        if (string.IsNullOrWhiteSpace(MyLifePassword))
            throw new ArgumentException("CONNECT_MYLIFE_PASSWORD is required");
    }
}
using System;
using System.ComponentModel.DataAnnotations;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.Configurations;

[ConnectorRegistration(
    connectorName: "MyLife",
    projectTypeName: "Nocturne_Connectors_MyLife",
    serviceName: ServiceNames.MyLifeConnector,
    environmentPrefix: ServiceNames.ConnectorEnvironment.MyLifePrefix,
    connectSourceName: "ConnectSource.MyLife",
    dataSourceId: "mylife-connector",
    icon: "mylife",
    category: ConnectorCategory.Pump,
    description: "Connect to MyLife for pump data",
    displayName: "MyLife"
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
        secret: true,
        description: "MyLife account username"
    )]
    [EnvironmentVariable(ConnectorEnvironmentVariables.MyLifeUsername)]
    public string MyLifeUsername { get; set; } = string.Empty;

    [Required]
    [AspireParameter(
        "mylife-password",
        "Password",
        secret: true,
        description: "MyLife account password"
    )]
    [EnvironmentVariable(ConnectorEnvironmentVariables.MyLifePassword)]
    public string MyLifePassword { get; set; } = string.Empty;

    [AspireParameter(
        "mylife-patient-id",
        "PatientId",
        secret: false,
        description: "Patient id for MyLife",
        defaultValue: ""
    )]
    [EnvironmentVariable(ConnectorEnvironmentVariables.MyLifePatientId)]
    public string MyLifePatientId { get; set; } = string.Empty;

    [AspireParameter(
        "mylife-service-url",
        "ServiceUrl",
        secret: false,
        description: "Override MyLife service url",
        defaultValue: ""
    )]
    [EnvironmentVariable(ConnectorEnvironmentVariables.MyLifeServiceUrl)]
    public string MyLifeServiceUrl { get; set; } = string.Empty;

    [AspireParameter(
        "mylife-enable-glucose-sync",
        "EnableGlucoseSync",
        secret: false,
        description: "Enable CGM glucose sync",
        defaultValue: "true"
    )]
    [EnvironmentVariable(ConnectorEnvironmentVariables.MyLifeEnableGlucoseSync)]
    public bool EnableGlucoseSync { get; set; } = true;

    [AspireParameter(
        "mylife-enable-manual-bg-sync",
        "EnableManualBgSync",
        secret: false,
        description: "Enable manual BG sync",
        defaultValue: "true"
    )]
    [EnvironmentVariable(ConnectorEnvironmentVariables.MyLifeEnableManualBgSync)]
    public bool EnableManualBgSync { get; set; } = true;

    [AspireParameter(
        "mylife-enable-meal-carb-consolidation",
        "EnableMealCarbConsolidation",
        secret: false,
        description: "Enable meal carb and bolus consolidation",
        defaultValue: "true"
    )]
    [EnvironmentVariable(ConnectorEnvironmentVariables.MyLifeEnableMealCarbConsolidation)]
    public bool EnableMealCarbConsolidation { get; set; } = true;

    [AspireParameter(
        "mylife-enable-temp-basal-consolidation",
        "EnableTempBasalConsolidation",
        secret: false,
        description: "Enable temp basal consolidation",
        defaultValue: "true"
    )]
    [EnvironmentVariable(ConnectorEnvironmentVariables.MyLifeEnableTempBasalConsolidation)]
    public bool EnableTempBasalConsolidation { get; set; } = true;

    [AspireParameter(
        "mylife-temp-basal-consolidation-window-minutes",
        "TempBasalConsolidationWindowMinutes",
        secret: false,
        description: "Temp basal consolidation window in minutes",
        defaultValue: "5"
    )]
    [EnvironmentVariable(ConnectorEnvironmentVariables.MyLifeTempBasalConsolidationWindowMinutes)]
    public int TempBasalConsolidationWindowMinutes { get; set; } = 5;

    [AspireParameter(
        "mylife-app-platform",
        "AppPlatform",
        secret: false,
        description: "MyLife app platform",
        defaultValue: "2"
    )]
    [EnvironmentVariable(ConnectorEnvironmentVariables.MyLifeAppPlatform)]
    public int AppPlatform { get; set; } = 2;

    [AspireParameter(
        "mylife-app-version",
        "AppVersion",
        secret: false,
        description: "MyLife app version",
        defaultValue: "20403"
    )]
    [EnvironmentVariable(ConnectorEnvironmentVariables.MyLifeAppVersion)]
    public int AppVersion { get; set; } = 20403;

    [AspireParameter(
        "mylife-sync-months",
        "SyncMonths",
        secret: false,
        description: "Maximum months to sync",
        defaultValue: "6"
    )]
    [EnvironmentVariable(ConnectorEnvironmentVariables.MyLifeSyncMonths)]
    public int SyncMonths { get; set; } = 6;

    protected override void ValidateSourceSpecificConfiguration()
    {
        if (string.IsNullOrWhiteSpace(MyLifeUsername))
            throw new ArgumentException("CONNECT_MYLIFE_USERNAME is required");

        if (string.IsNullOrWhiteSpace(MyLifePassword))
            throw new ArgumentException("CONNECT_MYLIFE_PASSWORD is required");
    }
}

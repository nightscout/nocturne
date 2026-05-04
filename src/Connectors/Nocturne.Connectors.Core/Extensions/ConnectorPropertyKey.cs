namespace Nocturne.Connectors.Core.Extensions;

/// <summary>
/// Defines all connector configuration property keys.
/// Used for type-safe property identification and frontend translation mapping.
/// </summary>
public enum ConnectorPropertyKey
{
    // Base configuration (BaseConnectorConfiguration)
    TimezoneOffset,
    Enabled,
    MaxRetryAttempts,
    BatchSize,
    SyncIntervalMinutes,

    // Glucose processing
    GlucoseProcessing,

    // Sync toggles
    SyncGlucose,
    SyncManualBG,
    SyncBoluses,
    SyncCarbIntake,
    SyncBolusCalculations,
    SyncNotes,
    SyncDeviceEvents,
    SyncStateSpans,
    SyncProfiles,
    SyncDeviceStatus,
    SyncActivity,
    SyncFood,

    // Common credentials
    Username,
    Password,
    Email,

    // Common server/region
    Server,
    Region,

    // Common connection
    PatientId,
    UserId,

    // Nightscout-specific
    Url,
    ApiSecret,
    MaxCount,

    // Glooko-specific
    UseV3Api,
    V3IncludeCgmBackfill,

    // MyLife-specific
    ServiceUrl,
    EnableMealCarbConsolidation,
    EnableTempBasalConsolidation,
    TempBasalConsolidationWindowMinutes,
    AppPlatform,
    AppVersion,

    // MyFitnessPal-specific
    LookbackDays,

    // Write-back
    WriteBackEnabled,
    WriteBackBatchSize,

    // Home Assistant-specific
    AccessToken,
    WebhookEnabled,
    WebhookSecret,

    // Eversense-specific
    PatientUsername
}

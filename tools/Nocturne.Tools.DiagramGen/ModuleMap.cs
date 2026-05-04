namespace Nocturne.Tools.DiagramGen;

/// <summary>
/// Maps entity CLR type names to logical domain modules for per-module ER diagrams.
/// </summary>
public static class ModuleMap
{
    public static readonly Dictionary<string, string[]> Modules = new()
    {
        ["multitenancy-identity"] = [
            "TenantEntity", "TenantMemberEntity", "TenantRoleEntity", "TenantMemberRoleEntity",
            "SubjectEntity", "SubjectRoleEntity", "RoleEntity", "MemberInviteEntity",
        ],
        ["authentication"] = [
            "PasskeyCredentialEntity", "TotpCredentialEntity", "RecoveryCodeEntity",
            "RefreshTokenEntity", "SubjectOidcIdentityEntity", "OidcProviderEntity",
            "AuthAuditLogEntity",
        ],
        ["oauth"] = [
            "OAuthClientEntity", "OAuthGrantEntity", "OAuthRefreshTokenEntity",
            "OAuthAuthorizationCodeEntity", "OAuthDeviceCodeEntity",
        ],
        ["glucose-readings"] = [
            "EntryEntity", "SensorGlucoseEntity", "MeterGlucoseEntity",
            "CalibrationEntity", "ApsSnapshotEntity", "PumpSnapshotEntity",
            "UploaderSnapshotEntity", "DeviceStatusExtrasEntity",
        ],
        ["treatments-events"] = [
            "TreatmentEntity", "TreatmentFoodEntity", "DecompositionBatchEntity",
            "BolusEntity", "TempBasalEntity", "CarbIntakeEntity", "BGCheckEntity",
            "BolusCalculationEntity", "NoteEntity", "DeviceEventEntity",
        ],
        ["therapy-profiles"] = [
            "ProfileEntity", "TherapySettingsEntity", "BasalScheduleEntity",
            "CarbRatioScheduleEntity", "SensitivityScheduleEntity", "TargetRangeScheduleEntity",
        ],
        ["patient-food-activity"] = [
            "PatientRecordEntity", "PatientDeviceEntity", "PatientInsulinEntity",
            "FoodEntity", "UserFoodFavoriteEntity", "ConnectorFoodEntryEntity",
            "StepCountEntity", "HeartRateEntity", "BodyWeightEntity",
        ],
        ["alerts-trackers"] = [
            "AlertRuleEntity", "AlertScheduleEntity", "AlertEscalationStepEntity",
            "AlertStepChannelEntity", "AlertTrackerStateEntity", "AlertExcursionEntity",
            "AlertInstanceEntity", "AlertDeliveryEntity", "AlertInviteEntity",
            "AlertCustomSoundEntity",
            "TrackerDefinitionEntity", "TrackerInstanceEntity", "TrackerPresetEntity",
            "TrackerNotificationThresholdEntity",
        ],
        ["system-connectors"] = [
            "SettingsEntity", "ClockFaceEntity", "CompressionLowSuggestionEntity",
            "InAppNotificationEntity", "StateSpanEntity", "SystemEventEntity",
            "DiscrepancyAnalysisEntity", "DiscrepancyDetailEntity",
            "ConnectorConfigurationEntity", "MigrationSourceEntity", "MigrationRunEntity",
            "DataSourceMetadataEntity", "LinkedRecordEntity",
            "ApsSnapshotEntity", "PumpSnapshotEntity", "UploaderSnapshotEntity", "DeviceEntity",
            "ChatIdentityDirectoryEntry", "ChatIdentityPendingLinkEntity",
        ],
    };

    public static readonly Dictionary<string, string> Titles = new()
    {
        ["multitenancy-identity"] = "Multitenancy and Identity",
        ["authentication"] = "Authentication",
        ["oauth"] = "OAuth",
        ["glucose-readings"] = "Glucose and Readings",
        ["treatments-events"] = "Treatments and Events",
        ["therapy-profiles"] = "Therapy and Profiles",
        ["patient-food-activity"] = "Patient, Food, and Activity",
        ["alerts-trackers"] = "Alerts and Trackers",
        ["system-connectors"] = "System and Connectors",
    };
}

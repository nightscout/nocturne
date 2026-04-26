using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.ValueGenerators;

namespace Nocturne.Infrastructure.Data;

/// <summary>
/// Entity Framework DbContext for PostgreSQL database operations
/// Multitenant architecture with per-tenant global query filters
/// </summary>
public class NocturneDbContext : DbContext
{
    /// <summary>
    /// Initializes a new instance of the NocturneDbContext class
    /// </summary>
    /// <param name="options">The options for this context</param>
    public NocturneDbContext(DbContextOptions<NocturneDbContext> options)
        : base(options) { }

    /// <summary>
    /// The current tenant ID. Set per-request by the DI factory.
    /// Referenced by global query filters for automatic tenant isolation.
    /// With context pooling, this property is set each time the context is checked out.
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Audit context for the current operation. Populated from HttpContext for HTTP
    /// requests (via <see cref="Interceptors.MutationAuditInterceptor"/>), or set
    /// directly by background services that have no HttpContext.
    /// </summary>
    public IAuditContext? AuditContext { get; set; }

    /// <summary>
    /// Gets or sets the Foods table for food database
    /// </summary>
    public DbSet<FoodEntity> Foods { get; set; }

    /// <summary>
    /// Gets or sets the ConnectorFoodEntries table for connector-imported foods
    /// </summary>
    public DbSet<ConnectorFoodEntryEntity> ConnectorFoodEntries { get; set; }

    /// <summary>
    /// Gets or sets the TreatmentFoods table for treatment food breakdowns
    /// </summary>
    public DbSet<TreatmentFoodEntity> TreatmentFoods { get; set; }

    /// <summary>
    /// Gets or sets the UserFoodFavorites table for user food favorites
    /// </summary>
    public DbSet<UserFoodFavoriteEntity> UserFoodFavorites { get; set; }

    /// <summary>
    /// Gets or sets the Settings table for application settings
    /// </summary>
    public DbSet<SettingsEntity> Settings { get; set; }

    /// <summary>
    /// Gets or sets the StepCounts table for xDrip step count / PebbleMovement records
    /// </summary>
    public DbSet<StepCountEntity> StepCounts { get; set; }

    /// <summary>
    /// Gets or sets the HeartRates table for xDrip heart rate records
    /// </summary>
    public DbSet<HeartRateEntity> HeartRates { get; set; }

    /// <summary>
    /// Gets or sets the BodyWeights table for body weight records
    /// </summary>
    public DbSet<BodyWeightEntity> BodyWeights { get; set; }

    /// <summary>
    /// Gets or sets the DiscrepancyAnalyses table for response comparison analysis
    /// </summary>
    public DbSet<DiscrepancyAnalysisEntity> DiscrepancyAnalyses { get; set; }

    /// <summary>
    /// Gets or sets the DiscrepancyDetails table for detailed discrepancy information
    /// </summary>
    public DbSet<DiscrepancyDetailEntity> DiscrepancyDetails { get; set; }


    // Authentication and Authorization entities

    /// <summary>
    /// Gets or sets the RefreshTokens table for refresh tokens (access tokens are stateless JWTs)
    /// </summary>
    public DbSet<RefreshTokenEntity> RefreshTokens { get; set; }

    /// <summary>
    /// Gets or sets the Subjects table for users and devices
    /// </summary>
    public DbSet<SubjectEntity> Subjects { get; set; }

    /// <summary>
    /// Gets or sets the SubjectAvatars table for avatar image storage
    /// </summary>
    public DbSet<SubjectAvatarEntity> SubjectAvatars { get; set; }

    /// <summary>
    /// Gets or sets the Roles table for authorization roles
    /// </summary>
    public DbSet<RoleEntity> Roles { get; set; }

    /// <summary>
    /// Gets or sets the SubjectRoles table for subject-role mappings
    /// </summary>
    public DbSet<SubjectRoleEntity> SubjectRoles { get; set; }

    /// <summary>
    /// Gets or sets the OidcProviders table for OIDC provider configurations
    /// </summary>
    public DbSet<OidcProviderEntity> OidcProviders { get; set; }

    /// <summary>
    /// Gets or sets the AuthAuditLog table for security event auditing
    /// </summary>
    public DbSet<AuthAuditLogEntity> AuthAuditLog { get; set; }

    /// <summary>
    /// Gets or sets the MutationAuditLog table for clinical data mutation auditing
    /// </summary>
    public DbSet<MutationAuditLogEntity> MutationAuditLog { get; set; }

    /// <summary>
    /// Gets or sets the PasskeyCredentials table for WebAuthn/passkey credentials
    /// </summary>
    public DbSet<PasskeyCredentialEntity> PasskeyCredentials { get; set; }

    /// <summary>
    /// Gets or sets the RecoveryCodes table for break-glass account recovery codes
    /// </summary>
    public DbSet<RecoveryCodeEntity> RecoveryCodes { get; set; }

    /// <summary>
    /// Gets or sets the TotpCredentials table for TOTP two-factor authentication
    /// </summary>
    public DbSet<TotpCredentialEntity> TotpCredentials { get; set; }

    /// <summary>
    /// Gets or sets the DataSourceMetadata table for user preferences about data sources
    /// </summary>
    public DbSet<DataSourceMetadataEntity> DataSourceMetadata { get; set; }

    // Tracker entities

    /// <summary>
    /// Gets or sets the TrackerDefinitions table for reusable tracker templates
    /// </summary>
    public DbSet<TrackerDefinitionEntity> TrackerDefinitions { get; set; }

    /// <summary>
    /// Gets or sets the TrackerInstances table for active/completed tracking sessions
    /// </summary>
    public DbSet<TrackerInstanceEntity> TrackerInstances { get; set; }

    /// <summary>
    /// Gets or sets the TrackerPresets table for quick-apply saved configurations
    /// </summary>
    public DbSet<TrackerPresetEntity> TrackerPresets { get; set; }

    /// <summary>
    /// Gets or sets the TrackerNotificationThresholds table for flexible notification thresholds
    /// </summary>
    public DbSet<TrackerNotificationThresholdEntity> TrackerNotificationThresholds { get; set; }

    // StateSpan entities

    /// <summary>
    /// Gets or sets the StateSpans table for time-ranged system states (pump modes, connectivity)
    /// </summary>
    public DbSet<StateSpanEntity> StateSpans { get; set; }

    /// <summary>
    /// Gets or sets the SystemEvents table for point-in-time system events (alarms, warnings)
    /// </summary>
    public DbSet<SystemEventEntity> SystemEvents { get; set; }

    // Migration tracking entities

    /// <summary>
    /// Gets or sets the MigrationSources table for tracking migration sources (Nightscout instances or MongoDB databases)
    /// </summary>
    public DbSet<MigrationSourceEntity> MigrationSources { get; set; }

    /// <summary>
    /// Gets or sets the MigrationRuns table for tracking individual migration job runs
    /// </summary>
    public DbSet<MigrationRunEntity> MigrationRuns { get; set; }

    /// <summary>
    /// Gets or sets the LinkedRecords table for deduplication linking
    /// </summary>
    public DbSet<LinkedRecordEntity> LinkedRecords { get; set; }

    // Connector Configuration entities

    /// <summary>
    /// Gets or sets the ConnectorConfigurations table for connector runtime configuration and encrypted secrets
    /// </summary>
    public DbSet<ConnectorConfigurationEntity> ConnectorConfigurations { get; set; }

    // In-App Notification entities

    /// <summary>
    /// Gets or sets the InAppNotifications table for unified in-app notifications
    /// </summary>
    public DbSet<InAppNotificationEntity> InAppNotifications { get; set; }

    /// <summary>
    /// Gets or sets the ClockFaces table for saved clock face configurations
    /// </summary>
    public DbSet<ClockFaceEntity> ClockFaces { get; set; }

    // OAuth 2.0 entities

    /// <summary>
    /// Gets or sets the OAuthClients table for registered/pinned OAuth client applications
    /// </summary>
    public DbSet<OAuthClientEntity> OAuthClients { get; set; }

    /// <summary>
    /// Gets or sets the OAuthGrants table for user-approved authorization grants
    /// </summary>
    public DbSet<OAuthGrantEntity> OAuthGrants { get; set; }

    /// <summary>
    /// Gets or sets the OAuthRefreshTokens table for OAuth refresh tokens (separate from legacy refresh tokens)
    /// </summary>
    public DbSet<OAuthRefreshTokenEntity> OAuthRefreshTokens { get; set; }

    /// <summary>
    /// Gets or sets the OAuthDeviceCodes table for Device Authorization Grant (RFC 8628)
    /// </summary>
    public DbSet<OAuthDeviceCodeEntity> OAuthDeviceCodes { get; set; }

    /// <summary>
    /// Gets or sets the OAuthAuthorizationCodes table for Authorization Code + PKCE flow (RFC 7636)
    /// </summary>
    public DbSet<OAuthAuthorizationCodeEntity> OAuthAuthorizationCodes { get; set; }

    /// <summary>
    /// Gets or sets the MemberInvites table for tenant membership invite links
    /// </summary>
    public DbSet<MemberInviteEntity> MemberInvites { get; set; } = null!;

    /// <summary>
    /// Gets or sets the CompressionLowSuggestions table for compression low detection
    /// </summary>
    public DbSet<CompressionLowSuggestionEntity> CompressionLowSuggestions { get; set; }

    // V4 Granular Models

    /// <summary>
    /// Gets or sets the SensorGlucose table for CGM readings (v4 granular model)
    /// </summary>
    public DbSet<SensorGlucoseEntity> SensorGlucose { get; set; }

    /// <summary>
    /// Gets or sets the MeterGlucose table for blood glucose meter readings (v4 granular model)
    /// </summary>
    public DbSet<MeterGlucoseEntity> MeterGlucose { get; set; }

    /// <summary>
    /// Gets or sets the Calibrations table for CGM sensor calibration records (v4 granular model)
    /// </summary>
    public DbSet<CalibrationEntity> Calibrations { get; set; }

    /// <summary>
    /// Gets or sets the Boluses table for insulin bolus delivery records (v4 granular model)
    /// </summary>
    public DbSet<BolusEntity> Boluses { get; set; }


    /// <summary>
    /// Gets or sets the CarbIntakes table for carbohydrate intake records (v4 granular model)
    /// </summary>
    public DbSet<CarbIntakeEntity> CarbIntakes { get; set; }

    /// <summary>
    /// Gets or sets the BGChecks table for blood glucose check records (v4 granular model)
    /// </summary>
    public DbSet<BGCheckEntity> BGChecks { get; set; }

    /// <summary>
    /// Gets or sets the Notes table for user note/annotation records (v4 granular model)
    /// </summary>
    public DbSet<NoteEntity> Notes { get; set; }

    /// <summary>
    /// Gets or sets the DeviceEvents table for device event records (v4 granular model)
    /// </summary>
    public DbSet<DeviceEventEntity> DeviceEvents { get; set; }

    /// <summary>
    /// Gets or sets the BolusCalculations table for bolus calculator/wizard records (v4 granular model)
    /// </summary>
    public DbSet<BolusCalculationEntity> BolusCalculations { get; set; }

    /// <summary>
    /// Gets or sets the ApsSnapshots table for APS algorithm snapshot records (v4 granular model)
    /// </summary>
    public DbSet<ApsSnapshotEntity> ApsSnapshots { get; set; }

    /// <summary>
    /// Gets or sets the PumpSnapshots table for pump status snapshot records (v4 granular model)
    /// </summary>
    public DbSet<PumpSnapshotEntity> PumpSnapshots { get; set; }

    /// <summary>
    /// Gets or sets the UploaderSnapshots table for uploader/phone status snapshot records (v4 granular model)
    /// </summary>
    public DbSet<UploaderSnapshotEntity> UploaderSnapshots { get; set; }

    /// <summary>
    /// Gets or sets the DeviceStatusExtras table for uncaptured devicestatus sub-objects (v4 diagnostic)
    /// </summary>
    public DbSet<DeviceStatusExtrasEntity> DeviceStatusExtras { get; set; }

    /// <summary>
    /// Gets or sets the Devices table for physical device records (v4 granular model)
    /// </summary>
    public DbSet<DeviceEntity> Devices { get; set; }

    /// <summary>
    /// Gets or sets the TempBasals table for temporary basal rate change records (v4 granular model)
    /// </summary>
    public DbSet<TempBasalEntity> TempBasals { get; set; }

    /// <summary>
    /// Gets or sets the DecompositionBatches table for grouping V4 records decomposed from the same source
    /// </summary>
    public DbSet<DecompositionBatchEntity> DecompositionBatches { get; set; }

    // V4 Profile Decomposition Models

    /// <summary>
    /// Gets or sets the TherapySettings table for therapy configuration records (v4 profile decomposition)
    /// </summary>
    public DbSet<TherapySettingsEntity> TherapySettings { get; set; }

    /// <summary>
    /// Gets or sets the BasalSchedules table for basal rate schedule records (v4 profile decomposition)
    /// </summary>
    public DbSet<BasalScheduleEntity> BasalSchedules { get; set; }

    /// <summary>
    /// Gets or sets the CarbRatioSchedules table for carb ratio schedule records (v4 profile decomposition)
    /// </summary>
    public DbSet<CarbRatioScheduleEntity> CarbRatioSchedules { get; set; }

    /// <summary>
    /// Gets or sets the SensitivitySchedules table for insulin sensitivity schedule records (v4 profile decomposition)
    /// </summary>
    public DbSet<SensitivityScheduleEntity> SensitivitySchedules { get; set; }

    /// <summary>
    /// Gets or sets the TargetRangeSchedules table for target range schedule records (v4 profile decomposition)
    /// </summary>
    public DbSet<TargetRangeScheduleEntity> TargetRangeSchedules { get; set; }

    // V4 Patient Profile Models

    /// <summary>
    /// Gets or sets the PatientRecords table for patient demographic and diabetes type records
    /// </summary>
    public DbSet<PatientRecordEntity> PatientRecords { get; set; }

    /// <summary>
    /// Gets or sets the PatientDevices table for patient device records (pumps, CGMs, pens, etc.)
    /// </summary>
    public DbSet<PatientDeviceEntity> PatientDevices { get; set; }

    /// <summary>
    /// Gets or sets the PatientInsulins table for patient insulin records (rapid-acting, long-acting, etc.)
    /// </summary>
    public DbSet<PatientInsulinEntity> PatientInsulins { get; set; }

    // Multitenancy entities

    /// <summary>
    /// Gets or sets the Tenants table for tenant isolation
    /// </summary>
    public DbSet<TenantEntity> Tenants { get; set; } = null!;

    /// <summary>
    /// Gets or sets the TenantMembers table for tenant membership
    /// </summary>
    public DbSet<TenantMemberEntity> TenantMembers { get; set; } = null!;

    /// <summary>
    /// Gets or sets the TenantRoles table for RBAC role definitions
    /// </summary>
    public DbSet<TenantRoleEntity> TenantRoles { get; set; } = null!;

    /// <summary>
    /// Gets or sets the TenantMemberRoles join table linking members to roles
    /// </summary>
    public DbSet<TenantMemberRoleEntity> TenantMemberRoles { get; set; } = null!;

    // Alert Engine entities

    /// <summary>
    /// Gets or sets the AlertRules table for composable alert condition definitions
    /// </summary>
    public DbSet<AlertRuleEntity> AlertRules { get; set; }

    /// <summary>
    /// Gets or sets the AlertSchedules table for time-of-day/day-of-week schedule windows
    /// </summary>
    public DbSet<AlertScheduleEntity> AlertSchedules { get; set; }

    /// <summary>
    /// Gets or sets the AlertEscalationSteps table for ordered escalation chain steps
    /// </summary>
    public DbSet<AlertEscalationStepEntity> AlertEscalationSteps { get; set; }

    /// <summary>
    /// Gets or sets the AlertStepChannels table for delivery channels per escalation step
    /// </summary>
    public DbSet<AlertStepChannelEntity> AlertStepChannels { get; set; }

    /// <summary>
    /// Gets or sets the AlertTrackerState table for per-rule state machine tracking
    /// </summary>
    public DbSet<AlertTrackerStateEntity> AlertTrackerState { get; set; }

    /// <summary>
    /// Gets or sets the AlertExcursions table for continuous out-of-range episodes
    /// </summary>
    public DbSet<AlertExcursionEntity> AlertExcursions { get; set; }

    /// <summary>
    /// Gets or sets the AlertInstances table for schedule-bound alert instances within excursions
    /// </summary>
    public DbSet<AlertInstanceEntity> AlertInstances { get; set; }

    /// <summary>
    /// Gets or sets the AlertDeliveries table for individual channel delivery attempts
    /// </summary>
    public DbSet<AlertDeliveryEntity> AlertDeliveries { get; set; }

    /// <summary>
    /// Gets or sets the AlertInvites table for shareable follower invite tokens
    /// </summary>
    public DbSet<AlertInviteEntity> AlertInvites { get; set; }

    /// <summary>
    /// Gets or sets the AlertCustomSounds table for user-uploaded alert sounds
    /// </summary>
    public DbSet<AlertCustomSoundEntity> AlertCustomSounds { get; set; }

    /// <summary>
    /// Gets or sets the ChatIdentityDirectory table — global routing for chat platform identities to tenant+user.
    /// </summary>
    public DbSet<ChatIdentityDirectoryEntry> ChatIdentityDirectory { get; set; }

    /// <summary>
    /// Gets or sets the ChatIdentityPendingLinks table — short-lived state tokens for the link flow.
    /// </summary>
    public DbSet<ChatIdentityPendingLinkEntity> ChatIdentityPendingLinks { get; set; }

    /// <summary>
    /// Gets or sets the SubjectOidcIdentities table — links subjects to OIDC provider identities.
    /// </summary>
    public DbSet<SubjectOidcIdentityEntity> SubjectOidcIdentities { get; set; }

    /// <summary>
    /// Gets or sets the CoachMarkStates table for per-user coach mark progression
    /// </summary>
    public DbSet<CoachMarkStateEntity> CoachMarkStates { get; set; }

    /// <summary>
    /// Gets or sets the ReadAccessLog table for HIPAA read-access audit logging
    /// </summary>
    public DbSet<ReadAccessLogEntity> ReadAccessLog { get; set; }

    /// <summary>
    /// Gets or sets the TenantAuditConfig table for per-tenant audit configuration
    /// </summary>
    public DbSet<TenantAuditConfigEntity> TenantAuditConfig { get; set; }

    /// <summary>
    /// Configure the database model and relationships
    /// </summary>
    /// <param name="modelBuilder">The model builder to configure</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure indexes for performance optimization
        ConfigureIndexes(modelBuilder);

        // Configure table-specific settings
        ConfigureEntities(modelBuilder);

        // Configure per-tenant global query filters
        ConfigureTenantFilters(modelBuilder);

        // Configure cascade deletes from tenant to all tenant-scoped entities
        ConfigureTenantCascadeDeletes(modelBuilder);

        // Normalize primary-key column naming. EF Core's default convention
        // emits the C# property name verbatim for the column, which produces
        // case-sensitive quoted "Id" columns in PostgreSQL. Some entities
        // explicitly mapped Id -> id but most did not, leaving the schema
        // inconsistent. Force every Id property to use snake_case "id" to
        // match the rest of the schema.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var idProperty = entityType.FindProperty("Id");
            if (idProperty is not null && idProperty.GetColumnName() != "id")
            {
                idProperty.SetColumnName("id");
            }
        }
    }

    private static void ConfigureIndexes(ModelBuilder modelBuilder)
    {
        // Food indexes - optimized for common queries
        modelBuilder.Entity<FoodEntity>().HasIndex(f => f.Name).HasDatabaseName("ix_foods_name");

        modelBuilder.Entity<FoodEntity>().HasIndex(f => f.Type).HasDatabaseName("ix_foods_type");

        modelBuilder
            .Entity<FoodEntity>()
            .HasIndex(f => f.Category)
            .HasDatabaseName("ix_foods_category");

        modelBuilder
            .Entity<FoodEntity>()
            .HasIndex(f => new { f.Type, f.Name })
            .HasDatabaseName("ix_foods_type_name");

        modelBuilder
            .Entity<FoodEntity>()
            .HasIndex(f => f.SysCreatedAt)
            .HasDatabaseName("ix_foods_sys_created_at");

        modelBuilder
            .Entity<FoodEntity>()
            .HasIndex(f => new { f.TenantId, f.ExternalSource, f.ExternalId })
            .HasDatabaseName("ix_foods_tenant_external")
            .HasFilter("external_source IS NOT NULL AND external_id IS NOT NULL")
            .IsUnique();

        // Connector food entry indexes
        modelBuilder
            .Entity<ConnectorFoodEntryEntity>()
            .HasIndex(e => e.ConnectorSource)
            .HasDatabaseName("ix_connector_food_entries_source");

        modelBuilder
            .Entity<ConnectorFoodEntryEntity>()
            .HasIndex(e => e.ExternalEntryId)
            .HasDatabaseName("ix_connector_food_entries_external_entry_id");

        modelBuilder
            .Entity<ConnectorFoodEntryEntity>()
            .HasIndex(e => new { e.TenantId, e.ConnectorSource, e.ExternalEntryId })
            .HasDatabaseName("ix_connector_food_entries_tenant_source_id")
            .IsUnique();

        modelBuilder
            .Entity<ConnectorFoodEntryEntity>()
            .HasIndex(e => e.Status)
            .HasDatabaseName("ix_connector_food_entries_status");

        modelBuilder
            .Entity<ConnectorFoodEntryEntity>()
            .HasIndex(e => e.ConsumedAt)
            .HasDatabaseName("ix_connector_food_entries_consumed_at");

        modelBuilder
            .Entity<ConnectorFoodEntryEntity>()
            .HasIndex(e => e.SysCreatedAt)
            .HasDatabaseName("ix_connector_food_entries_sys_created_at");

        // Treatment food breakdown indexes
        modelBuilder
            .Entity<TreatmentFoodEntity>()
            .HasIndex(tf => tf.CarbIntakeId)
            .HasDatabaseName("ix_treatment_foods_carb_intake_id");

        modelBuilder
            .Entity<TreatmentFoodEntity>()
            .HasIndex(tf => tf.FoodId)
            .HasDatabaseName("ix_treatment_foods_food_id");

        modelBuilder
            .Entity<TreatmentFoodEntity>()
            .HasIndex(tf => tf.SysCreatedAt)
            .HasDatabaseName("ix_treatment_foods_sys_created_at");

        // User food favorites indexes
        modelBuilder
            .Entity<UserFoodFavoriteEntity>()
            .HasIndex(f => f.UserId)
            .HasDatabaseName("ix_user_food_favorites_user_id");

        modelBuilder
            .Entity<UserFoodFavoriteEntity>()
            .HasIndex(f => f.FoodId)
            .HasDatabaseName("ix_user_food_favorites_food_id");

        modelBuilder
            .Entity<UserFoodFavoriteEntity>()
            .HasIndex(f => new { f.TenantId, f.UserId, f.FoodId })
            .HasDatabaseName("ix_user_food_favorites_tenant_user_food")
            .IsUnique();

        // Settings indexes - optimized for common queries
        modelBuilder
            .Entity<SettingsEntity>()
            .HasIndex(s => new { s.TenantId, s.Key })
            .HasDatabaseName("ix_settings_tenant_id_key")
            .IsUnique(); // Settings keys should be unique per tenant

        modelBuilder
            .Entity<SettingsEntity>()
            .HasIndex(s => s.Mills)
            .HasDatabaseName("ix_settings_mills")
            .IsDescending(); // Most recent first

        modelBuilder
            .Entity<SettingsEntity>()
            .HasIndex(s => s.IsActive)
            .HasDatabaseName("ix_settings_is_active");

        modelBuilder
            .Entity<SettingsEntity>()
            .HasIndex(s => s.SysCreatedAt)
            .HasDatabaseName("ix_settings_sys_created_at");

        // StepCount indexes - optimized for time-range graph queries
        modelBuilder
            .Entity<StepCountEntity>()
            .HasIndex(s => s.Timestamp)
            .HasDatabaseName("ix_step_counts_timestamp")
            .IsDescending();

        modelBuilder
            .Entity<StepCountEntity>()
            .HasIndex(s => s.SysCreatedAt)
            .HasDatabaseName("ix_step_counts_sys_created_at");

        // HeartRate indexes - optimized for time-range graph queries
        modelBuilder
            .Entity<HeartRateEntity>()
            .HasIndex(h => h.Timestamp)
            .HasDatabaseName("ix_heart_rates_timestamp")
            .IsDescending();

        modelBuilder
            .Entity<HeartRateEntity>()
            .HasIndex(h => h.SysCreatedAt)
            .HasDatabaseName("ix_heart_rates_sys_created_at");

        // BodyWeight indexes - optimized for time-range graph queries
        modelBuilder
            .Entity<BodyWeightEntity>()
            .HasIndex(b => b.Mills)
            .HasDatabaseName("ix_body_weights_mills")
            .IsDescending();

        modelBuilder
            .Entity<BodyWeightEntity>()
            .HasIndex(b => b.SysCreatedAt)
            .HasDatabaseName("ix_body_weights_sys_created_at");

        // Discrepancy analysis indexes - optimized for dashboard queries
        modelBuilder
            .Entity<DiscrepancyAnalysisEntity>()
            .HasIndex(d => d.AnalysisTimestamp)
            .HasDatabaseName("ix_discrepancy_analyses_timestamp")
            .IsDescending(); // Most recent first

        modelBuilder
            .Entity<DiscrepancyAnalysisEntity>()
            .HasIndex(d => d.CorrelationId)
            .HasDatabaseName("ix_discrepancy_analyses_correlation_id");

        modelBuilder
            .Entity<DiscrepancyAnalysisEntity>()
            .HasIndex(d => d.RequestPath)
            .HasDatabaseName("ix_discrepancy_analyses_request_path");

        modelBuilder
            .Entity<DiscrepancyAnalysisEntity>()
            .HasIndex(d => d.OverallMatch)
            .HasDatabaseName("ix_discrepancy_analyses_overall_match");

        modelBuilder
            .Entity<DiscrepancyAnalysisEntity>()
            .HasIndex(d => new { d.RequestPath, d.AnalysisTimestamp })
            .HasDatabaseName("ix_discrepancy_analyses_path_timestamp")
            .IsDescending(false, true); // Path asc, Timestamp desc

        // Discrepancy details indexes
        modelBuilder
            .Entity<DiscrepancyDetailEntity>()
            .HasIndex(d => d.AnalysisId)
            .HasDatabaseName("ix_discrepancy_details_analysis_id");

        modelBuilder
            .Entity<DiscrepancyDetailEntity>()
            .HasIndex(d => d.Severity)
            .HasDatabaseName("ix_discrepancy_details_severity");

        modelBuilder
            .Entity<DiscrepancyDetailEntity>()
            .HasIndex(d => d.DiscrepancyType)
            .HasDatabaseName("ix_discrepancy_details_type");


        // Refresh Token indexes - optimized for auth lookups
        modelBuilder
            .Entity<RefreshTokenEntity>()
            .HasIndex(t => t.TokenHash)
            .HasDatabaseName("ix_refresh_tokens_token_hash")
            .IsUnique();

        modelBuilder
            .Entity<RefreshTokenEntity>()
            .HasIndex(t => t.SubjectId)
            .HasDatabaseName("ix_refresh_tokens_subject_id");

        modelBuilder
            .Entity<RefreshTokenEntity>()
            .HasIndex(t => t.OidcSessionId)
            .HasDatabaseName("ix_refresh_tokens_oidc_session_id");

        modelBuilder
            .Entity<RefreshTokenEntity>()
            .HasIndex(t => t.ExpiresAt)
            .HasDatabaseName("ix_refresh_tokens_expires_at");

        modelBuilder
            .Entity<RefreshTokenEntity>()
            .HasIndex(t => t.RevokedAt)
            .HasDatabaseName("ix_refresh_tokens_revoked_at")
            .HasFilter("revoked_at IS NULL");

        // Subject indexes - optimized for auth lookups
        modelBuilder
            .Entity<SubjectEntity>()
            .HasIndex(s => s.Name)
            .HasDatabaseName("ix_subjects_name");

        modelBuilder
            .Entity<SubjectEntity>()
            .HasIndex(s => s.AccessTokenHash)
            .HasDatabaseName("ix_subjects_access_token_hash")
            .IsUnique();

        modelBuilder
            .Entity<SubjectEntity>()
            .HasIndex(s => s.Email)
            .HasDatabaseName("ix_subjects_email");

        // Role indexes
        modelBuilder
            .Entity<RoleEntity>()
            .HasIndex(r => r.Name)
            .HasDatabaseName("ix_roles_name")
            .IsUnique();

        // OIDC Provider indexes
        modelBuilder
            .Entity<OidcProviderEntity>()
            .HasIndex(o => o.IssuerUrl)
            .HasDatabaseName("ix_oidc_providers_issuer_url")
            .IsUnique();

        modelBuilder
            .Entity<OidcProviderEntity>()
            .HasIndex(o => o.IsEnabled)
            .HasDatabaseName("ix_oidc_providers_is_enabled");

        // Auth Audit Log indexes - optimized for security monitoring
        modelBuilder
            .Entity<AuthAuditLogEntity>()
            .HasIndex(a => a.SubjectId)
            .HasDatabaseName("ix_auth_audit_log_subject_id");

        modelBuilder
            .Entity<AuthAuditLogEntity>()
            .HasIndex(a => a.EventType)
            .HasDatabaseName("ix_auth_audit_log_event_type");

        modelBuilder
            .Entity<AuthAuditLogEntity>()
            .HasIndex(a => a.CreatedAt)
            .HasDatabaseName("ix_auth_audit_log_created_at")
            .IsDescending();

        modelBuilder
            .Entity<AuthAuditLogEntity>()
            .HasIndex(a => a.IpAddress)
            .HasDatabaseName("ix_auth_audit_log_ip_address");

        modelBuilder
            .Entity<AuthAuditLogEntity>()
            .HasIndex(a => new { a.SubjectId, a.CreatedAt })
            .HasDatabaseName("ix_auth_audit_log_subject_created")
            .IsDescending(false, true);

        // DataSourceMetadata indexes - optimized for device lookups
        modelBuilder
            .Entity<DataSourceMetadataEntity>()
            .HasIndex(d => new { d.TenantId, d.DeviceId })
            .HasDatabaseName("ix_data_source_metadata_tenant_device")
            .IsUnique();

        modelBuilder
            .Entity<DataSourceMetadataEntity>()
            .HasIndex(d => d.IsArchived)
            .HasDatabaseName("ix_data_source_metadata_is_archived");

        modelBuilder
            .Entity<DataSourceMetadataEntity>()
            .HasIndex(d => d.CreatedAt)
            .HasDatabaseName("ix_data_source_metadata_created_at");

        // Tracker Definitions indexes - optimized for user queries
        modelBuilder
            .Entity<TrackerDefinitionEntity>()
            .HasIndex(d => d.UserId)
            .HasDatabaseName("ix_tracker_definitions_user_id");

        modelBuilder
            .Entity<TrackerDefinitionEntity>()
            .HasIndex(d => new { d.UserId, d.Category })
            .HasDatabaseName("ix_tracker_definitions_user_category");

        modelBuilder
            .Entity<TrackerDefinitionEntity>()
            .HasIndex(d => d.IsFavorite)
            .HasDatabaseName("ix_tracker_definitions_is_favorite");

        modelBuilder
            .Entity<TrackerDefinitionEntity>()
            .HasIndex(d => d.CreatedAt)
            .HasDatabaseName("ix_tracker_definitions_created_at");

        // Tracker Instances indexes - optimized for active and history queries
        modelBuilder
            .Entity<TrackerInstanceEntity>()
            .HasIndex(i => i.UserId)
            .HasDatabaseName("ix_tracker_instances_user_id");

        modelBuilder
            .Entity<TrackerInstanceEntity>()
            .HasIndex(i => i.DefinitionId)
            .HasDatabaseName("ix_tracker_instances_definition_id");

        modelBuilder
            .Entity<TrackerInstanceEntity>()
            .HasIndex(i => i.CompletedAt)
            .HasDatabaseName("ix_tracker_instances_completed_at")
            .HasFilter("completed_at IS NULL"); // Partial index for active instances

        modelBuilder
            .Entity<TrackerInstanceEntity>()
            .HasIndex(i => new { i.UserId, i.CompletedAt })
            .HasDatabaseName("ix_tracker_instances_user_completed");

        modelBuilder
            .Entity<TrackerInstanceEntity>()
            .HasIndex(i => i.StartedAt)
            .HasDatabaseName("ix_tracker_instances_started_at")
            .IsDescending();

        // Tracker Presets indexes
        modelBuilder
            .Entity<TrackerPresetEntity>()
            .HasIndex(p => p.UserId)
            .HasDatabaseName("ix_tracker_presets_user_id");

        modelBuilder
            .Entity<TrackerPresetEntity>()
            .HasIndex(p => p.DefinitionId)
            .HasDatabaseName("ix_tracker_presets_definition_id");

        // Tracker Notification Thresholds - configure relationship to use TrackerDefinitionId
        modelBuilder
            .Entity<TrackerNotificationThresholdEntity>()
            .HasOne(t => t.Definition)
            .WithMany(d => d.NotificationThresholds)
            .HasForeignKey(t => t.TrackerDefinitionId);

        // Tracker Notification Thresholds indexes
        modelBuilder
            .Entity<TrackerNotificationThresholdEntity>()
            .HasIndex(t => t.TrackerDefinitionId)
            .HasDatabaseName("ix_tracker_notification_thresholds_definition_id");

        modelBuilder
            .Entity<TrackerNotificationThresholdEntity>()
            .HasIndex(t => new { t.TrackerDefinitionId, t.DisplayOrder })
            .HasDatabaseName("ix_tracker_notification_thresholds_def_order");

        // StateSpan indexes - optimized for time range and category queries
        modelBuilder
            .Entity<StateSpanEntity>()
            .HasIndex(s => s.StartTimestamp)
            .HasDatabaseName("ix_state_spans_start_timestamp")
            .IsDescending();

        modelBuilder
            .Entity<StateSpanEntity>()
            .HasIndex(s => s.Category)
            .HasDatabaseName("ix_state_spans_category");

        modelBuilder
            .Entity<StateSpanEntity>()
            .HasIndex(s => s.EndTimestamp)
            .HasDatabaseName("ix_state_spans_end_timestamp")
            .HasFilter("end_timestamp IS NULL"); // Partial index for active spans

        modelBuilder
            .Entity<StateSpanEntity>()
            .HasIndex(s => new { s.Category, s.StartTimestamp })
            .HasDatabaseName("ix_state_spans_category_start")
            .IsDescending(false, true);

        modelBuilder
            .Entity<StateSpanEntity>()
            .HasIndex(s => s.Source)
            .HasDatabaseName("ix_state_spans_source");

        modelBuilder
            .Entity<StateSpanEntity>()
            .HasIndex(s => s.OriginalId)
            .HasDatabaseName("ix_state_spans_original_id");

        modelBuilder
            .Entity<StateSpanEntity>()
            .HasIndex(s => s.SupersededById)
            .HasDatabaseName("ix_state_spans_superseded_by_id");

        modelBuilder
            .Entity<StateSpanEntity>()
            .HasOne<StateSpanEntity>()
            .WithMany()
            .HasForeignKey(s => s.SupersededById)
            .OnDelete(DeleteBehavior.SetNull);

        // SystemEvent indexes - optimized for time range and type queries
        modelBuilder
            .Entity<SystemEventEntity>()
            .HasIndex(e => e.Mills)
            .HasDatabaseName("ix_system_events_mills")
            .IsDescending();

        modelBuilder
            .Entity<SystemEventEntity>()
            .HasIndex(e => e.EventType)
            .HasDatabaseName("ix_system_events_event_type");

        modelBuilder
            .Entity<SystemEventEntity>()
            .HasIndex(e => e.Category)
            .HasDatabaseName("ix_system_events_category");

        modelBuilder
            .Entity<SystemEventEntity>()
            .HasIndex(e => new { e.Category, e.Mills })
            .HasDatabaseName("ix_system_events_category_timestamp")
            .IsDescending(false, true);

        modelBuilder
            .Entity<SystemEventEntity>()
            .HasIndex(e => e.Source)
            .HasDatabaseName("ix_system_events_source");

        modelBuilder
            .Entity<SystemEventEntity>()
            .HasIndex(e => e.OriginalId)
            .HasDatabaseName("ix_system_events_original_id");

        // Migration source indexes
        modelBuilder
            .Entity<MigrationSourceEntity>()
            .HasIndex(s => s.SourceIdentifier)
            .HasDatabaseName("ix_migration_sources_identifier")
            .IsUnique();

        modelBuilder
            .Entity<MigrationSourceEntity>()
            .HasIndex(s => s.LastMigrationAt)
            .HasDatabaseName("ix_migration_sources_last_migration");

        modelBuilder
            .Entity<MigrationSourceEntity>()
            .HasIndex(s => s.Mode)
            .HasDatabaseName("ix_migration_sources_mode");

        modelBuilder
            .Entity<MigrationSourceEntity>()
            .HasIndex(s => s.CreatedAt)
            .HasDatabaseName("ix_migration_sources_created_at")
            .IsDescending();

        // Migration run indexes
        modelBuilder
            .Entity<MigrationRunEntity>()
            .HasIndex(r => r.SourceId)
            .HasDatabaseName("ix_migration_runs_source_id");

        modelBuilder
            .Entity<MigrationRunEntity>()
            .HasIndex(r => r.State)
            .HasDatabaseName("ix_migration_runs_state");

        modelBuilder
            .Entity<MigrationRunEntity>()
            .HasIndex(r => r.StartedAt)
            .HasDatabaseName("ix_migration_runs_started_at")
            .IsDescending();

        modelBuilder
            .Entity<MigrationRunEntity>()
            .HasIndex(r => new { r.SourceId, r.State })
            .HasDatabaseName("ix_migration_runs_source_state");

        // LinkedRecords indexes - optimized for deduplication queries
        modelBuilder
            .Entity<LinkedRecordEntity>()
            .HasIndex(l => l.CanonicalId)
            .HasDatabaseName("ix_linked_records_canonical");

        modelBuilder
            .Entity<LinkedRecordEntity>()
            .HasIndex(l => new { l.RecordType, l.RecordId })
            .HasDatabaseName("ix_linked_records_record");

        modelBuilder
            .Entity<LinkedRecordEntity>()
            .HasIndex(l => new { l.TenantId, l.RecordType, l.RecordId })
            .IsUnique()
            .HasDatabaseName("ix_linked_records_tenant_type_id");

        modelBuilder
            .Entity<LinkedRecordEntity>()
            .HasIndex(l => new
            {
                l.RecordType,
                l.CanonicalId,
                l.IsPrimary,
            })
            .HasDatabaseName("ix_linked_records_type_canonical_primary");

        modelBuilder
            .Entity<LinkedRecordEntity>()
            .HasIndex(l => new { l.RecordType, l.SourceTimestamp })
            .HasDatabaseName("ix_linked_records_type_timestamp");

        // ConnectorConfiguration indexes - optimized for connector lookups
        modelBuilder
            .Entity<ConnectorConfigurationEntity>()
            .HasIndex(c => new { c.ConnectorName, c.TenantId })
            .HasDatabaseName("ix_connector_configurations_connector_name_tenant")
            .IsUnique();

        // InAppNotification indexes - optimized for user notification queries
        modelBuilder
            .Entity<InAppNotificationEntity>()
            .HasIndex(n => n.UserId)
            .HasDatabaseName("ix_in_app_notifications_user_id");

        modelBuilder
            .Entity<InAppNotificationEntity>()
            .HasIndex(n => n.Type)
            .HasDatabaseName("ix_in_app_notifications_type");

        modelBuilder
            .Entity<InAppNotificationEntity>()
            .HasIndex(n => n.IsArchived)
            .HasDatabaseName("ix_in_app_notifications_is_archived");

        modelBuilder
            .Entity<InAppNotificationEntity>()
            .HasIndex(n => n.CreatedAt)
            .HasDatabaseName("ix_in_app_notifications_created_at")
            .IsDescending();

        modelBuilder
            .Entity<InAppNotificationEntity>()
            .HasIndex(n => new { n.UserId, n.IsArchived })
            .HasDatabaseName("ix_in_app_notifications_user_archived");

        modelBuilder
            .Entity<InAppNotificationEntity>()
            .HasIndex(n => new
            {
                n.UserId,
                n.Type,
                n.SourceId,
                n.IsArchived,
            })
            .HasDatabaseName("ix_in_app_notifications_user_type_source_archived");

        modelBuilder
            .Entity<InAppNotificationEntity>()
            .HasIndex(n => n.SourceId)
            .HasDatabaseName("ix_in_app_notifications_source_id")
            .HasFilter("source_id IS NOT NULL");

        // OAuth Client indexes
        modelBuilder
            .Entity<OAuthClientEntity>()
            .HasIndex(c => new { c.TenantId, c.ClientId })
            .HasDatabaseName("ix_oauth_clients_tenant_client_id")
            .IsUnique();

        modelBuilder
            .Entity<OAuthClientEntity>()
            .HasIndex(c => new { c.TenantId, c.SoftwareId })
            .HasDatabaseName("ix_oauth_clients_tenant_software_id")
            .IsUnique()
            .HasFilter("\"software_id\" IS NOT NULL");

        // OAuth Grant indexes
        modelBuilder
            .Entity<OAuthGrantEntity>()
            .HasIndex(g => g.ClientEntityId)
            .HasDatabaseName("ix_oauth_grants_client_id");

        modelBuilder
            .Entity<OAuthGrantEntity>()
            .HasIndex(g => g.SubjectId)
            .HasDatabaseName("ix_oauth_grants_subject_id");

        modelBuilder
            .Entity<OAuthGrantEntity>()
            .HasIndex(g => new { g.ClientEntityId, g.SubjectId })
            .HasDatabaseName("ix_oauth_grants_client_subject");

        modelBuilder
            .Entity<OAuthGrantEntity>()
            .HasIndex(g => new { g.TenantId, g.SubjectId })
            .HasDatabaseName("ix_oauth_grants_tenant_subject");

        modelBuilder
            .Entity<OAuthGrantEntity>()
            .HasIndex(g => g.RevokedAt)
            .HasDatabaseName("ix_oauth_grants_revoked_at")
            .HasFilter("revoked_at IS NULL");

        // FollowerSubjectId indexes removed - follower sharing now uses TenantMembers

        // OAuth Refresh Token indexes
        modelBuilder
            .Entity<OAuthRefreshTokenEntity>()
            .HasIndex(t => t.TokenHash)
            .HasDatabaseName("ix_oauth_refresh_tokens_token_hash")
            .IsUnique();

        modelBuilder
            .Entity<OAuthRefreshTokenEntity>()
            .HasIndex(t => t.GrantId)
            .HasDatabaseName("ix_oauth_refresh_tokens_grant_id");

        modelBuilder
            .Entity<OAuthRefreshTokenEntity>()
            .HasIndex(t => t.ExpiresAt)
            .HasDatabaseName("ix_oauth_refresh_tokens_expires_at");

        modelBuilder
            .Entity<OAuthRefreshTokenEntity>()
            .HasIndex(t => t.RevokedAt)
            .HasDatabaseName("ix_oauth_refresh_tokens_revoked_at")
            .HasFilter("revoked_at IS NULL");

        // OAuth Device Code indexes
        modelBuilder
            .Entity<OAuthDeviceCodeEntity>()
            .HasIndex(d => d.DeviceCodeHash)
            .HasDatabaseName("ix_oauth_device_codes_device_code_hash")
            .IsUnique();

        modelBuilder
            .Entity<OAuthDeviceCodeEntity>()
            .HasIndex(d => d.UserCode)
            .HasDatabaseName("ix_oauth_device_codes_user_code")
            .IsUnique();

        modelBuilder
            .Entity<OAuthDeviceCodeEntity>()
            .HasIndex(d => d.ExpiresAt)
            .HasDatabaseName("ix_oauth_device_codes_expires_at");

        // OAuth Authorization Code indexes
        modelBuilder
            .Entity<OAuthAuthorizationCodeEntity>()
            .HasIndex(c => c.CodeHash)
            .HasDatabaseName("ix_oauth_authorization_codes_code_hash")
            .IsUnique();

        modelBuilder
            .Entity<OAuthAuthorizationCodeEntity>()
            .HasIndex(c => c.ExpiresAt)
            .HasDatabaseName("ix_oauth_authorization_codes_expires_at");

        modelBuilder
            .Entity<OAuthAuthorizationCodeEntity>()
            .HasIndex(c => c.SubjectId)
            .HasDatabaseName("ix_oauth_authorization_codes_subject_id");

        // ClockFaces indexes - optimized for user queries and public lookups
        modelBuilder
            .Entity<ClockFaceEntity>()
            .HasIndex(cf => cf.UserId)
            .HasDatabaseName("ix_clock_faces_user_id");

        modelBuilder
            .Entity<ClockFaceEntity>()
            .HasIndex(cf => cf.CreatedAt)
            .HasDatabaseName("ix_clock_faces_created_at")
            .IsDescending();

        modelBuilder
            .Entity<ClockFaceEntity>()
            .HasIndex(cf => new { cf.UserId, cf.CreatedAt })
            .HasDatabaseName("ix_clock_faces_user_created_at")
            .IsDescending(false, true);

        // CompressionLowSuggestions indexes
        modelBuilder
            .Entity<CompressionLowSuggestionEntity>()
            .HasIndex(e => e.NightOf)
            .HasDatabaseName("ix_compression_low_suggestions_night_of");

        modelBuilder
            .Entity<CompressionLowSuggestionEntity>()
            .HasIndex(e => e.Status)
            .HasDatabaseName("ix_compression_low_suggestions_status");

        // V4 Granular Model indexes

        // SensorGlucose indexes
        modelBuilder
            .Entity<SensorGlucoseEntity>()
            .HasIndex(e => e.Timestamp)
            .HasDatabaseName("ix_sensor_glucose_timestamp")
            .IsDescending();

        modelBuilder
            .Entity<SensorGlucoseEntity>()
            .HasIndex(e => new { e.TenantId, e.LegacyId })
            .HasDatabaseName("ix_sensor_glucose_tenant_legacy_id")
            .IsUnique()
            .HasFilter("legacy_id IS NOT NULL");

        modelBuilder
            .Entity<SensorGlucoseEntity>()
            .HasIndex(e => e.CorrelationId)
            .HasDatabaseName("ix_sensor_glucose_correlation_id");

        // MeterGlucose indexes
        modelBuilder
            .Entity<MeterGlucoseEntity>()
            .HasIndex(e => e.Timestamp)
            .HasDatabaseName("ix_meter_glucose_timestamp")
            .IsDescending();

        modelBuilder
            .Entity<MeterGlucoseEntity>()
            .HasIndex(e => e.LegacyId)
            .HasDatabaseName("ix_meter_glucose_legacy_id");

        modelBuilder
            .Entity<MeterGlucoseEntity>()
            .HasIndex(e => e.CorrelationId)
            .HasDatabaseName("ix_meter_glucose_correlation_id");

        // Calibrations indexes
        modelBuilder
            .Entity<CalibrationEntity>()
            .HasIndex(e => e.Timestamp)
            .HasDatabaseName("ix_calibrations_timestamp")
            .IsDescending();

        modelBuilder
            .Entity<CalibrationEntity>()
            .HasIndex(e => e.LegacyId)
            .HasDatabaseName("ix_calibrations_legacy_id");

        modelBuilder
            .Entity<CalibrationEntity>()
            .HasIndex(e => e.CorrelationId)
            .HasDatabaseName("ix_calibrations_correlation_id");

        // Boluses indexes
        modelBuilder
            .Entity<BolusEntity>()
            .HasIndex(e => e.Timestamp)
            .HasDatabaseName("ix_boluses_timestamp")
            .IsDescending();

        modelBuilder
            .Entity<BolusEntity>()
            .HasIndex(e => new { e.TenantId, e.LegacyId })
            .HasDatabaseName("ix_boluses_tenant_legacy_id")
            .IsUnique()
            .HasFilter("legacy_id IS NOT NULL");

        modelBuilder
            .Entity<BolusEntity>()
            .HasIndex(e => e.CorrelationId)
            .HasDatabaseName("ix_boluses_correlation_id");

        modelBuilder.Entity<BolusEntity>()
            .HasIndex(e => new { e.TenantId, e.DataSource, e.SyncIdentifier })
            .HasDatabaseName("ix_boluses_tenant_source_sync_id")
            .IsUnique()
            .HasFilter("sync_identifier IS NOT NULL");

        // CarbIntakes indexes
        modelBuilder
            .Entity<CarbIntakeEntity>()
            .HasIndex(e => e.Timestamp)
            .HasDatabaseName("ix_carb_intakes_timestamp")
            .IsDescending();

        modelBuilder
            .Entity<CarbIntakeEntity>()
            .HasIndex(e => new { e.TenantId, e.LegacyId })
            .HasDatabaseName("ix_carb_intakes_tenant_legacy_id")
            .IsUnique()
            .HasFilter("legacy_id IS NOT NULL");

        modelBuilder
            .Entity<CarbIntakeEntity>()
            .HasIndex(e => e.CorrelationId)
            .HasDatabaseName("ix_carb_intakes_correlation_id");

        modelBuilder.Entity<CarbIntakeEntity>()
            .HasIndex(e => new { e.TenantId, e.DataSource, e.SyncIdentifier })
            .HasDatabaseName("ix_carb_intakes_tenant_source_sync_id")
            .IsUnique()
            .HasFilter("sync_identifier IS NOT NULL");

        // BGChecks indexes
        modelBuilder
            .Entity<BGCheckEntity>()
            .HasIndex(e => e.Timestamp)
            .HasDatabaseName("ix_bg_checks_timestamp")
            .IsDescending();

        modelBuilder
            .Entity<BGCheckEntity>()
            .HasIndex(e => new { e.TenantId, e.LegacyId })
            .HasDatabaseName("ix_bg_checks_tenant_legacy_id")
            .IsUnique()
            .HasFilter("legacy_id IS NOT NULL");

        modelBuilder
            .Entity<BGCheckEntity>()
            .HasIndex(e => e.CorrelationId)
            .HasDatabaseName("ix_bg_checks_correlation_id");

        // Notes indexes
        modelBuilder
            .Entity<NoteEntity>()
            .HasIndex(e => e.Timestamp)
            .HasDatabaseName("ix_notes_timestamp")
            .IsDescending();

        modelBuilder
            .Entity<NoteEntity>()
            .HasIndex(e => new { e.TenantId, e.LegacyId })
            .HasDatabaseName("ix_notes_tenant_legacy_id")
            .IsUnique()
            .HasFilter("legacy_id IS NOT NULL");

        modelBuilder
            .Entity<NoteEntity>()
            .HasIndex(e => e.CorrelationId)
            .HasDatabaseName("ix_notes_correlation_id");

        // DeviceEvents indexes
        modelBuilder
            .Entity<DeviceEventEntity>()
            .HasIndex(e => e.Timestamp)
            .HasDatabaseName("ix_device_events_timestamp")
            .IsDescending();

        modelBuilder
            .Entity<DeviceEventEntity>()
            .HasIndex(e => new { e.TenantId, e.LegacyId })
            .HasDatabaseName("ix_device_events_tenant_legacy_id")
            .IsUnique()
            .HasFilter("legacy_id IS NOT NULL");

        modelBuilder
            .Entity<DeviceEventEntity>()
            .HasIndex(e => e.CorrelationId)
            .HasDatabaseName("ix_device_events_correlation_id");

        // BolusCalculations indexes
        modelBuilder
            .Entity<BolusCalculationEntity>()
            .HasIndex(e => e.Timestamp)
            .HasDatabaseName("ix_bolus_calculations_timestamp")
            .IsDescending();

        modelBuilder
            .Entity<BolusCalculationEntity>()
            .HasIndex(e => new { e.TenantId, e.LegacyId })
            .HasDatabaseName("ix_bolus_calculations_tenant_legacy_id")
            .IsUnique()
            .HasFilter("legacy_id IS NOT NULL");

        modelBuilder
            .Entity<BolusCalculationEntity>()
            .HasIndex(e => e.CorrelationId)
            .HasDatabaseName("ix_bolus_calculations_correlation_id");

        // ApsSnapshot indexes
        modelBuilder
            .Entity<ApsSnapshotEntity>()
            .HasIndex(e => e.Timestamp)
            .HasDatabaseName("ix_aps_snapshots_timestamp")
            .IsDescending();

        modelBuilder
            .Entity<ApsSnapshotEntity>()
            .HasIndex(e => e.LegacyId)
            .HasDatabaseName("ix_aps_snapshots_legacy_id");

        // PumpSnapshot indexes
        modelBuilder
            .Entity<PumpSnapshotEntity>()
            .HasIndex(e => e.Timestamp)
            .HasDatabaseName("ix_pump_snapshots_timestamp")
            .IsDescending();

        modelBuilder
            .Entity<PumpSnapshotEntity>()
            .HasIndex(e => e.LegacyId)
            .HasDatabaseName("ix_pump_snapshots_legacy_id");

        // UploaderSnapshot indexes
        modelBuilder
            .Entity<UploaderSnapshotEntity>()
            .HasIndex(e => e.Timestamp)
            .HasDatabaseName("ix_uploader_snapshots_timestamp")
            .IsDescending();

        modelBuilder
            .Entity<UploaderSnapshotEntity>()
            .HasIndex(e => e.LegacyId)
            .HasDatabaseName("ix_uploader_snapshots_legacy_id");


        // DeviceStatusExtras indexes
        modelBuilder
            .Entity<DeviceStatusExtrasEntity>()
            .HasIndex(e => e.CorrelationId)
            .HasDatabaseName("ix_device_status_extras_correlation_id");

        // TempBasals indexes
        modelBuilder
            .Entity<TempBasalEntity>()
            .HasIndex(e => e.StartTimestamp)
            .HasDatabaseName("ix_temp_basals_start_timestamp")
            .IsDescending();

        modelBuilder
            .Entity<TempBasalEntity>()
            .HasIndex(e => e.EndTimestamp)
            .HasDatabaseName("ix_temp_basals_end_timestamp");

        modelBuilder
            .Entity<TempBasalEntity>()
            .HasIndex(e => new { e.TenantId, e.LegacyId })
            .HasDatabaseName("ix_temp_basals_tenant_legacy_id")
            .IsUnique()
            .HasFilter("legacy_id IS NOT NULL");

        modelBuilder
            .Entity<TempBasalEntity>()
            .HasIndex(e => e.CorrelationId)
            .HasDatabaseName("ix_temp_basals_correlation_id");

        // Devices unique index is handled by [Index] attribute on entity

        // V4 Profile Decomposition indexes

        // TherapySettings indexes
        modelBuilder
            .Entity<TherapySettingsEntity>()
            .HasIndex(e => e.Timestamp)
            .HasDatabaseName("ix_therapy_settings_timestamp")
            .IsDescending();

        modelBuilder
            .Entity<TherapySettingsEntity>()
            .HasIndex(e => new { e.TenantId, e.LegacyId })
            .HasDatabaseName("ix_therapy_settings_tenant_legacy_id")
            .IsUnique()
            .HasFilter("legacy_id IS NOT NULL");

        modelBuilder
            .Entity<TherapySettingsEntity>()
            .HasIndex(e => e.CorrelationId)
            .HasDatabaseName("ix_therapy_settings_correlation_id");

        modelBuilder
            .Entity<TherapySettingsEntity>()
            .HasIndex(e => e.ProfileName)
            .HasDatabaseName("ix_therapy_settings_profile_name");

        // BasalSchedule indexes
        modelBuilder
            .Entity<BasalScheduleEntity>()
            .HasIndex(e => e.Timestamp)
            .HasDatabaseName("ix_basal_schedules_timestamp")
            .IsDescending();

        modelBuilder
            .Entity<BasalScheduleEntity>()
            .HasIndex(e => new { e.TenantId, e.LegacyId })
            .HasDatabaseName("ix_basal_schedules_tenant_legacy_id")
            .IsUnique()
            .HasFilter("legacy_id IS NOT NULL");

        modelBuilder
            .Entity<BasalScheduleEntity>()
            .HasIndex(e => e.CorrelationId)
            .HasDatabaseName("ix_basal_schedules_correlation_id");

        modelBuilder
            .Entity<BasalScheduleEntity>()
            .HasIndex(e => e.ProfileName)
            .HasDatabaseName("ix_basal_schedules_profile_name");

        // CarbRatioSchedule indexes
        modelBuilder
            .Entity<CarbRatioScheduleEntity>()
            .HasIndex(e => e.Timestamp)
            .HasDatabaseName("ix_carb_ratio_schedules_timestamp")
            .IsDescending();

        modelBuilder
            .Entity<CarbRatioScheduleEntity>()
            .HasIndex(e => new { e.TenantId, e.LegacyId })
            .HasDatabaseName("ix_carb_ratio_schedules_tenant_legacy_id")
            .IsUnique()
            .HasFilter("legacy_id IS NOT NULL");

        modelBuilder
            .Entity<CarbRatioScheduleEntity>()
            .HasIndex(e => e.CorrelationId)
            .HasDatabaseName("ix_carb_ratio_schedules_correlation_id");

        modelBuilder
            .Entity<CarbRatioScheduleEntity>()
            .HasIndex(e => e.ProfileName)
            .HasDatabaseName("ix_carb_ratio_schedules_profile_name");

        // SensitivitySchedule indexes
        modelBuilder
            .Entity<SensitivityScheduleEntity>()
            .HasIndex(e => e.Timestamp)
            .HasDatabaseName("ix_sensitivity_schedules_timestamp")
            .IsDescending();

        modelBuilder
            .Entity<SensitivityScheduleEntity>()
            .HasIndex(e => new { e.TenantId, e.LegacyId })
            .HasDatabaseName("ix_sensitivity_schedules_tenant_legacy_id")
            .IsUnique()
            .HasFilter("legacy_id IS NOT NULL");

        modelBuilder
            .Entity<SensitivityScheduleEntity>()
            .HasIndex(e => e.CorrelationId)
            .HasDatabaseName("ix_sensitivity_schedules_correlation_id");

        modelBuilder
            .Entity<SensitivityScheduleEntity>()
            .HasIndex(e => e.ProfileName)
            .HasDatabaseName("ix_sensitivity_schedules_profile_name");

        // TargetRangeSchedule indexes
        modelBuilder
            .Entity<TargetRangeScheduleEntity>()
            .HasIndex(e => e.Timestamp)
            .HasDatabaseName("ix_target_range_schedules_timestamp")
            .IsDescending();

        modelBuilder
            .Entity<TargetRangeScheduleEntity>()
            .HasIndex(e => new { e.TenantId, e.LegacyId })
            .HasDatabaseName("ix_target_range_schedules_tenant_legacy_id")
            .IsUnique()
            .HasFilter("legacy_id IS NOT NULL");

        modelBuilder
            .Entity<TargetRangeScheduleEntity>()
            .HasIndex(e => e.CorrelationId)
            .HasDatabaseName("ix_target_range_schedules_correlation_id");

        modelBuilder
            .Entity<TargetRangeScheduleEntity>()
            .HasIndex(e => e.ProfileName)
            .HasDatabaseName("ix_target_range_schedules_profile_name");

        // Tenant indexes
        modelBuilder.Entity<TenantEntity>()
            .HasIndex(t => t.Slug)
            .HasDatabaseName("ix_tenants_slug")
            .IsUnique();

        modelBuilder.Entity<TenantMemberEntity>()
            .HasIndex(tm => tm.SubjectId)
            .HasDatabaseName("ix_tenant_members_subject_id");

        // PatientRecord: unique constraint — one record per tenant
        modelBuilder.Entity<PatientRecordEntity>()
            .HasIndex(e => e.TenantId)
            .HasDatabaseName("ix_patient_records_tenant_id")
            .IsUnique();

        // PatientDevice: query by tenant + current status
        modelBuilder.Entity<PatientDeviceEntity>()
            .HasIndex(e => new { e.TenantId, e.IsCurrent })
            .HasDatabaseName("ix_patient_devices_tenant_is_current");

        // PatientInsulin: query by tenant + current status
        modelBuilder.Entity<PatientInsulinEntity>()
            .HasIndex(e => new { e.TenantId, e.IsCurrent })
            .HasDatabaseName("ix_patient_insulins_tenant_is_current");

        // Alert Engine indexes

        // Sweep query: find instances that need escalation
        modelBuilder.Entity<AlertInstanceEntity>()
            .HasIndex(e => new { e.Status, e.NextEscalationAt })
            .HasDatabaseName("ix_alert_instances_status_next_escalation");

        // Active excursion lookup by tenant
        modelBuilder.Entity<AlertExcursionEntity>()
            .HasIndex(e => new { e.TenantId, e.EndedAt })
            .HasDatabaseName("ix_alert_excursions_tenant_ended_at");

        // Per-rule excursion lookup
        modelBuilder.Entity<AlertExcursionEntity>()
            .HasIndex(e => new { e.AlertRuleId, e.EndedAt })
            .HasDatabaseName("ix_alert_excursions_rule_ended_at");

        // Pending delivery sweep
        modelBuilder.Entity<AlertDeliveryEntity>()
            .HasIndex(e => new { e.Status, e.CreatedAt })
            .HasDatabaseName("ix_alert_deliveries_status_created_at");

        // Unique invite token lookup
        modelBuilder.Entity<AlertInviteEntity>()
            .HasIndex(e => e.Token)
            .HasDatabaseName("ix_alert_invites_token")
            .IsUnique();

        // Signal loss sweep: find tenants that haven't reported recently
        modelBuilder.Entity<TenantEntity>()
            .HasIndex(t => t.LastReadingAt)
            .HasDatabaseName("ix_tenants_last_reading_at");

        // Chat identity directory — global (NOT tenant-scoped) routing table.
        modelBuilder.Entity<ChatIdentityDirectoryEntry>(b =>
        {
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasValueGenerator<GuidV7ValueGenerator>();

            b.HasIndex(e => new { e.Platform, e.PlatformUserId, e.TenantId })
                .IsUnique()
                .HasDatabaseName("ux_directory_user_tenant");

            // TODO(Task 1.5): ChatIdentityDirectoryService.CreateLinkAsync must
            // auto-suffix label collisions within a (platform, platform_user_id)
            // set before insert — this unique index will throw otherwise.
            b.HasIndex(e => new { e.Platform, e.PlatformUserId, e.Label })
                .IsUnique()
                .HasDatabaseName("ux_directory_user_label");

            // At most one default per Discord user — partial unique index.
            b.HasIndex(e => new { e.Platform, e.PlatformUserId })
                .IsUnique()
                .HasFilter("is_default = true")
                .HasDatabaseName("ux_directory_user_one_default");

            b.HasIndex(e => e.TenantId).HasDatabaseName("ix_directory_tenant_id");
        });

        modelBuilder.Entity<ChatIdentityPendingLinkEntity>(b =>
        {
            b.HasKey(e => e.Token);
            b.HasIndex(e => e.ExpiresAt).HasDatabaseName("ix_pending_links_expires_at");
        });

        // Subject OIDC identities — join table for multi-provider OIDC linking
        modelBuilder.Entity<SubjectOidcIdentityEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasValueGenerator<GuidV7ValueGenerator>();

            e.HasIndex(x => new { x.OidcSubjectId, x.Issuer }).IsUnique()
                .HasDatabaseName("ix_subject_oidc_identities_external");
            e.HasIndex(x => x.SubjectId)
                .HasDatabaseName("ix_subject_oidc_identities_subject_id");

            e.HasOne(x => x.Subject)
                .WithMany(s => s.OidcIdentities)
                .HasForeignKey(x => x.SubjectId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Provider)
                .WithMany()
                .HasForeignKey(x => x.ProviderId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureEntities(ModelBuilder modelBuilder)
    {
        // Configure UUID Version 7 value generators for all entity primary keys
        modelBuilder
            .Entity<FoodEntity>()
            .Property(f => f.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<ConnectorFoodEntryEntity>()
            .Property(e => e.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<TreatmentFoodEntity>()
            .Property(tf => tf.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<UserFoodFavoriteEntity>()
            .Property(f => f.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<SettingsEntity>()
            .Property(s => s.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<StepCountEntity>()
            .Property(s => s.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<HeartRateEntity>()
            .Property(h => h.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<DiscrepancyAnalysisEntity>()
            .Property(d => d.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<DiscrepancyDetailEntity>()
            .Property(d => d.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
// Auth entity UUID generators
        modelBuilder
            .Entity<RefreshTokenEntity>()
            .Property(t => t.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<SubjectEntity>()
            .Property(s => s.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<RoleEntity>()
            .Property(r => r.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<OidcProviderEntity>()
            .Property(o => o.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<AuthAuditLogEntity>()
            .Property(a => a.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<MutationAuditLogEntity>()
            .Property(a => a.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<ReadAccessLogEntity>()
            .Property(a => a.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<TenantAuditConfigEntity>()
            .Property(a => a.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();

        // Tracker entity UUID generators
        modelBuilder
            .Entity<TrackerDefinitionEntity>()
            .Property(d => d.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<TrackerInstanceEntity>()
            .Property(i => i.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<TrackerPresetEntity>()
            .Property(p => p.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<TrackerNotificationThresholdEntity>()
            .Property(t => t.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();

        modelBuilder
            .Entity<StateSpanEntity>()
            .Property(s => s.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();

        modelBuilder
            .Entity<SystemEventEntity>()
            .Property(e => e.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();

        modelBuilder
            .Entity<LinkedRecordEntity>()
            .Property(l => l.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();

        modelBuilder
            .Entity<ConnectorConfigurationEntity>()
            .Property(c => c.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();

        modelBuilder
            .Entity<CompressionLowSuggestionEntity>()
            .Property(c => c.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();

        // V4 Granular Model UUID generators
        modelBuilder
            .Entity<SensorGlucoseEntity>()
            .Property(e => e.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<MeterGlucoseEntity>()
            .Property(e => e.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<CalibrationEntity>()
            .Property(e => e.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<BolusEntity>()
            .Property(e => e.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<CarbIntakeEntity>()
            .Property(e => e.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<BGCheckEntity>()
            .Property(e => e.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<NoteEntity>()
            .Property(e => e.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<DeviceEventEntity>()
            .Property(e => e.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<BolusCalculationEntity>()
            .Property(e => e.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<ApsSnapshotEntity>()
            .Property(e => e.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<PumpSnapshotEntity>()
            .Property(e => e.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<UploaderSnapshotEntity>()
            .Property(e => e.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<DecompositionBatchEntity>()
            .Property(e => e.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();

        // V4 Profile Decomposition UUID generators
        modelBuilder
            .Entity<TherapySettingsEntity>()
            .Property(e => e.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<BasalScheduleEntity>()
            .Property(e => e.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<CarbRatioScheduleEntity>()
            .Property(e => e.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<SensitivityScheduleEntity>()
            .Property(e => e.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<TargetRangeScheduleEntity>()
            .Property(e => e.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();

        // Tenant entity UUID generators
        modelBuilder
            .Entity<TenantEntity>()
            .Property(t => t.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<TenantMemberEntity>()
            .Property(tm => tm.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();

        modelBuilder
            .Entity<ConnectorFoodEntryEntity>()
            .HasOne(e => e.Food)
            .WithMany()
            .HasForeignKey(e => e.FoodId)
            .OnDelete(DeleteBehavior.SetNull);

        // V4 entity foreign key relationships
        modelBuilder
            .Entity<BolusEntity>()
            .HasOne<DeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder
            .Entity<BolusEntity>()
            .HasOne<BolusCalculationEntity>()
            .WithMany()
            .HasForeignKey(e => e.BolusCalculationId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder
            .Entity<BolusEntity>()
            .HasOne<ApsSnapshotEntity>()
            .WithMany()
            .HasForeignKey(e => e.ApsSnapshotId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder
            .Entity<TempBasalEntity>()
            .HasOne<DeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder
            .Entity<TempBasalEntity>()
            .HasOne<ApsSnapshotEntity>()
            .WithMany()
            .HasForeignKey(e => e.ApsSnapshotId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder
            .Entity<PumpSnapshotEntity>()
            .HasOne<DeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder
            .Entity<UploaderSnapshotEntity>()
            .HasOne<DeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder
            .Entity<PatientDeviceEntity>()
            .HasOne<DeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.SetNull);

        // DecompositionBatch → V4 entity cascade relationships (CorrelationId = batch PK)
        modelBuilder.Entity<BolusEntity>()
            .HasOne<DecompositionBatchEntity>()
            .WithMany()
            .HasForeignKey(e => e.CorrelationId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<CarbIntakeEntity>()
            .HasOne<DecompositionBatchEntity>()
            .WithMany()
            .HasForeignKey(e => e.CorrelationId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<BGCheckEntity>()
            .HasOne<DecompositionBatchEntity>()
            .WithMany()
            .HasForeignKey(e => e.CorrelationId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<NoteEntity>()
            .HasOne<DecompositionBatchEntity>()
            .WithMany()
            .HasForeignKey(e => e.CorrelationId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DeviceEventEntity>()
            .HasOne<DecompositionBatchEntity>()
            .WithMany()
            .HasForeignKey(e => e.CorrelationId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<BolusCalculationEntity>()
            .HasOne<DecompositionBatchEntity>()
            .WithMany()
            .HasForeignKey(e => e.CorrelationId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TempBasalEntity>()
            .HasOne<DecompositionBatchEntity>()
            .WithMany()
            .HasForeignKey(e => e.CorrelationId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SensorGlucoseEntity>()
            .HasOne<DecompositionBatchEntity>()
            .WithMany()
            .HasForeignKey(e => e.CorrelationId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MeterGlucoseEntity>()
            .HasOne<DecompositionBatchEntity>()
            .WithMany()
            .HasForeignKey(e => e.CorrelationId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<CalibrationEntity>()
            .HasOne<DecompositionBatchEntity>()
            .WithMany()
            .HasForeignKey(e => e.CorrelationId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TherapySettingsEntity>()
            .HasOne<DecompositionBatchEntity>()
            .WithMany()
            .HasForeignKey(e => e.CorrelationId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<BasalScheduleEntity>()
            .HasOne<DecompositionBatchEntity>()
            .WithMany()
            .HasForeignKey(e => e.CorrelationId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<CarbRatioScheduleEntity>()
            .HasOne<DecompositionBatchEntity>()
            .WithMany()
            .HasForeignKey(e => e.CorrelationId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SensitivityScheduleEntity>()
            .HasOne<DecompositionBatchEntity>()
            .WithMany()
            .HasForeignKey(e => e.CorrelationId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TargetRangeScheduleEntity>()
            .HasOne<DecompositionBatchEntity>()
            .WithMany()
            .HasForeignKey(e => e.CorrelationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Configure automatic timestamp updates
        modelBuilder
            .Entity<FoodEntity>()
            .Property(f => f.SysUpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAddOrUpdate();

        modelBuilder
            .Entity<ConnectorFoodEntryEntity>()
            .Property(e => e.Status)
            .HasConversion<string>();

        modelBuilder
            .Entity<ConnectorFoodEntryEntity>()
            .Property(e => e.SysUpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAddOrUpdate();

        modelBuilder
            .Entity<ConnectorFoodEntryEntity>()
            .Property(e => e.Status)
            .HasDefaultValue(ConnectorFoodEntryStatus.Pending);

        modelBuilder
            .Entity<TreatmentFoodEntity>()
            .Property(tf => tf.SysUpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAddOrUpdate();

        modelBuilder
            .Entity<UserFoodFavoriteEntity>()
            .Property(f => f.SysCreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        modelBuilder
            .Entity<SettingsEntity>()
            .Property(s => s.SysUpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAddOrUpdate();

        modelBuilder
            .Entity<StepCountEntity>()
            .Property(s => s.SysUpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAddOrUpdate();

        modelBuilder
            .Entity<HeartRateEntity>()
            .Property(h => h.SysUpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAddOrUpdate();

        // Configure required fields and defaults
        modelBuilder.Entity<FoodEntity>().Property(f => f.Type).HasDefaultValue("food");

        modelBuilder
            .Entity<FoodEntity>()
            .Property(f => f.Gi)
            .HasDefaultValue(GlycemicIndex.Medium)
            .HasSentinel((GlycemicIndex)0); // CLR default (0) is not a valid enum value, use it as sentinel

        modelBuilder
            .Entity<TreatmentFoodEntity>()
            .Property(tf => tf.TimeOffsetMinutes)
            .HasDefaultValue(0);

        modelBuilder.Entity<TreatmentFoodEntity>(entity =>
        {
            entity
                .HasOne(tf => tf.CarbIntake)
                .WithMany()
                .HasForeignKey(tf => tf.CarbIntakeId)
                .OnDelete(DeleteBehavior.Cascade);

            entity
                .HasOne(tf => tf.Food)
                .WithMany()
                .HasForeignKey(tf => tf.FoodId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<UserFoodFavoriteEntity>(entity =>
        {
            entity
                .HasOne(f => f.Food)
                .WithMany()
                .HasForeignKey(f => f.FoodId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FoodEntity>().Property(f => f.Unit).HasDefaultValue("g");

        modelBuilder.Entity<FoodEntity>().Property(f => f.Position).HasDefaultValue(99999);

        // Settings defaults
        modelBuilder.Entity<SettingsEntity>().Property(s => s.IsActive).HasDefaultValue(true);

        // Configure RefreshToken entity relationships and defaults
        modelBuilder.Entity<RefreshTokenEntity>(entity =>
        {
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity
                .Property(e => e.UpdatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .ValueGeneratedOnAddOrUpdate();

            entity
                .HasOne(e => e.Subject)
                .WithMany(s => s.RefreshTokens)
                .HasForeignKey(e => e.SubjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure Subject entity relationships and defaults
        modelBuilder.Entity<SubjectEntity>(entity =>
        {
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity
                .Property(e => e.UpdatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .ValueGeneratedOnAddOrUpdate();

        });

        modelBuilder.Entity<SubjectAvatarEntity>(entity =>
        {
            entity.ToTable("subject_avatars");
            entity.Property(e => e.Id).HasValueGenerator<GuidV7ValueGenerator>();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(e => e.SubjectId).IsUnique().HasDatabaseName("ix_subject_avatars_subject_id");
            entity.HasOne(e => e.Subject).WithMany().HasForeignKey(e => e.SubjectId).OnDelete(DeleteBehavior.Cascade);
        });

        // Configure Role entity defaults
        modelBuilder.Entity<RoleEntity>(entity =>
        {
            entity.Property(e => e.IsSystemRole).HasDefaultValue(false);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity
                .Property(e => e.UpdatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .ValueGeneratedOnAddOrUpdate();
        });

        // Configure SubjectRole (many-to-many) relationships
        modelBuilder.Entity<SubjectRoleEntity>(entity =>
        {
            entity.HasKey(e => new { e.SubjectId, e.RoleId });

            entity.Property(e => e.AssignedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity
                .HasOne(e => e.Subject)
                .WithMany(s => s.SubjectRoles)
                .HasForeignKey(e => e.SubjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity
                .HasOne(e => e.Role)
                .WithMany(r => r.SubjectRoles)
                .HasForeignKey(e => e.RoleId)
                .OnDelete(DeleteBehavior.Cascade);

            entity
                .HasOne(e => e.AssignedBy)
                .WithMany()
                .HasForeignKey(e => e.AssignedById)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Configure OIDC Provider entity defaults
        modelBuilder.Entity<OidcProviderEntity>(entity =>
        {
            entity.Property(e => e.ClaimMappingsJson).HasDefaultValue("{}");
            entity.Property(e => e.IsEnabled).HasDefaultValue(true);
            entity.Property(e => e.DisplayOrder).HasDefaultValue(0);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity
                .Property(e => e.UpdatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .ValueGeneratedOnAddOrUpdate();
        });

        // Configure Auth Audit Log entity relationships and defaults
        modelBuilder.Entity<AuthAuditLogEntity>(entity =>
        {
            entity.Property(e => e.Success).HasDefaultValue(true);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity
                .HasOne(e => e.Subject)
                .WithMany()
                .HasForeignKey(e => e.SubjectId)
                .OnDelete(DeleteBehavior.SetNull);

            entity
                .HasOne(e => e.RefreshToken)
                .WithMany()
                .HasForeignKey(e => e.RefreshTokenId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Configure Mutation Audit Log entity defaults and indexes
        modelBuilder.Entity<MutationAuditLogEntity>(entity =>
        {
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasIndex(e => new { e.TenantId, e.EntityType, e.EntityId })
                .HasDatabaseName("ix_mutation_audit_log_entity");

            entity.HasIndex(e => new { e.TenantId, e.SubjectId, e.CreatedAt })
                .HasDatabaseName("ix_mutation_audit_log_subject");

            entity.HasIndex(e => e.CorrelationId)
                .HasDatabaseName("ix_mutation_audit_log_correlation")
                .HasFilter("correlation_id IS NOT NULL");

            entity.HasIndex(e => new { e.TenantId, e.CreatedAt })
                .HasDatabaseName("ix_mutation_audit_log_created");
        });

        // Configure Read Access Log entity defaults and indexes
        modelBuilder.Entity<ReadAccessLogEntity>(entity =>
        {
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasIndex(e => new { e.TenantId, e.SubjectId, e.CreatedAt })
                .HasDatabaseName("ix_read_access_log_subject");

            entity.HasIndex(e => new { e.TenantId, e.EntityType, e.CreatedAt })
                .HasDatabaseName("ix_read_access_log_entity_type");

            entity.HasIndex(e => new { e.TenantId, e.CreatedAt })
                .HasDatabaseName("ix_read_access_log_created");

            entity.HasIndex(e => e.CorrelationId)
                .HasDatabaseName("ix_read_access_log_correlation")
                .HasFilter("correlation_id IS NOT NULL");
        });

        // Configure Tenant Audit Config entity defaults and indexes
        modelBuilder.Entity<TenantAuditConfigEntity>(entity =>
        {
            entity.Property(e => e.ReadAuditEnabled).HasDefaultValue(false);
            entity.Property(e => e.SysCreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.SysUpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasIndex(e => e.TenantId)
                .IsUnique()
                .HasDatabaseName("ix_tenant_audit_config_tenant_id");
        });

        // Configure LinkedRecordEntity defaults
        modelBuilder.Entity<LinkedRecordEntity>(entity =>
        {
            entity.Property(e => e.IsPrimary).HasDefaultValue(false);
            entity.Property(e => e.SysCreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        // Configure InAppNotification entity
        modelBuilder.Entity<InAppNotificationEntity>(entity =>
        {
            entity.Property(e => e.Id).HasValueGenerator<GuidV7ValueGenerator>();
            entity.Property(e => e.IsArchived).HasDefaultValue(false);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            // Store enums as strings in the database
            entity.Property(e => e.Category).HasConversion<string>();
            entity.Property(e => e.Urgency).HasConversion<string>();
            entity.Property(e => e.ArchiveReason).HasConversion<string>();
        });

        // Configure OAuth Client entity
        modelBuilder.Entity<OAuthClientEntity>(entity =>
        {
            entity.Property(e => e.Id).HasValueGenerator<GuidV7ValueGenerator>();
            entity.Property(e => e.RedirectUris).HasDefaultValue("[]");
            entity.Property(e => e.IsKnown).HasDefaultValue(false);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity
                .Property(e => e.UpdatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .ValueGeneratedOnAddOrUpdate();
        });

        // Configure OAuth Grant entity
        modelBuilder.Entity<OAuthGrantEntity>(entity =>
        {
            entity.Property(e => e.Id).HasValueGenerator<GuidV7ValueGenerator>();
            entity.Property(e => e.GrantType).HasDefaultValue(OAuthGrantTypes.App);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity
                .HasOne(e => e.Client)
                .WithMany(c => c.Grants)
                .HasForeignKey(e => e.ClientEntityId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired(false);

            entity
                .HasOne(e => e.Subject)
                .WithMany()
                .HasForeignKey(e => e.SubjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity
                .HasOne(e => e.CreatedBy)
                .WithMany()
                .HasForeignKey(e => e.CreatedBySubjectId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);

        });

        // Configure OAuth Refresh Token entity
        modelBuilder.Entity<OAuthRefreshTokenEntity>(entity =>
        {
            entity.Property(e => e.Id).HasValueGenerator<GuidV7ValueGenerator>();
            entity.Property(e => e.IssuedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity
                .HasOne(e => e.Grant)
                .WithMany(g => g.RefreshTokens)
                .HasForeignKey(e => e.GrantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity
                .HasOne(e => e.ReplacedBy)
                .WithMany()
                .HasForeignKey(e => e.ReplacedById)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Configure OAuth Device Code entity
        modelBuilder.Entity<OAuthDeviceCodeEntity>(entity =>
        {
            entity.Property(e => e.Id).HasValueGenerator<GuidV7ValueGenerator>();
            entity.Property(e => e.Interval).HasDefaultValue(5);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity
                .HasOne(e => e.Grant)
                .WithMany()
                .HasForeignKey(e => e.GrantId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Configure OAuth Authorization Code entity
        modelBuilder.Entity<OAuthAuthorizationCodeEntity>(entity =>
        {
            entity.Property(e => e.Id).HasValueGenerator<GuidV7ValueGenerator>();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity
                .HasOne(e => e.Client)
                .WithMany()
                .HasForeignKey(e => e.ClientEntityId)
                .OnDelete(DeleteBehavior.Cascade);

            entity
                .HasOne(e => e.Subject)
                .WithMany()
                .HasForeignKey(e => e.SubjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure Member Invite entity
        modelBuilder.Entity<MemberInviteEntity>(entity =>
        {
            entity.Property(e => e.Id).HasValueGenerator<GuidV7ValueGenerator>();

            entity.HasOne(e => e.Tenant)
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.CreatedBy)
                .WithMany()
                .HasForeignKey(e => e.CreatedBySubjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => e.TenantId);
        });

        // Configure ClockFace entity
        modelBuilder.Entity<ClockFaceEntity>(entity =>
        {
            entity.Property(e => e.Id).HasValueGenerator<GuidV7ValueGenerator>();
            entity.Property(e => e.ConfigJson).HasDefaultValue("{}");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity
                .Property(e => e.UpdatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .ValueGeneratedOnAddOrUpdate();
            entity.Property(e => e.SysCreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity
                .Property(e => e.SysUpdatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .ValueGeneratedOnAddOrUpdate();
        });

        // Configure TenantMember relationships
        modelBuilder.Entity<TenantMemberEntity>()
            .HasOne(tm => tm.Tenant)
            .WithMany(t => t.Members)
            .HasForeignKey(tm => tm.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TenantMemberEntity>()
            .HasOne(tm => tm.Subject)
            .WithMany()
            .HasForeignKey(tm => tm.SubjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TenantMemberEntity>()
            .HasOne(e => e.CreatedFromInvite)
            .WithMany(i => i.CreatedMembers)
            .HasForeignKey(e => e.CreatedFromInviteId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<TenantMemberEntity>()
            .HasIndex(e => new { e.TenantId, e.SubjectId })
            .HasDatabaseName("ix_tenant_members_tenant_subject")
            .IsUnique()
            .HasFilter("revoked_at IS NULL");

        modelBuilder.Entity<TenantMemberEntity>()
            .HasIndex(e => new { e.TenantId, e.Username })
            .HasDatabaseName("ix_tenant_members_tenant_username")
            .IsUnique()
            .HasFilter("username IS NOT NULL AND revoked_at IS NULL");

        // Configure TenantRole entity
        modelBuilder.Entity<TenantRoleEntity>(entity =>
        {
            entity.HasIndex(e => new { e.TenantId, e.Slug }).IsUnique();
            entity.Property(e => e.SysCreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.SysUpdatedAt).HasDefaultValueSql("now()");
        });

        // Configure TenantMemberRole join entity
        modelBuilder.Entity<TenantMemberRoleEntity>(entity =>
        {
            entity.HasIndex(e => new { e.TenantMemberId, e.TenantRoleId }).IsUnique();
            entity.Property(e => e.SysCreatedAt).HasDefaultValueSql("now()");
        });

        // ───────────────────────────────────────────────
        // Alert Engine entity configuration
        // ───────────────────────────────────────────────

        // AlertRuleEntity
        modelBuilder.Entity<AlertRuleEntity>(entity =>
        {
            entity.ToTable("alert_rules");
            entity.Property(e => e.Id).HasValueGenerator<GuidV7ValueGenerator>();
            entity.Property(e => e.ConditionType).HasConversion(
                new Converters.EnumMemberValueConverter<Core.Models.Alerts.AlertConditionType>());
            entity.Property(e => e.ConditionParams).HasColumnType("jsonb").HasDefaultValue("{}");
            entity.Property(e => e.ConfirmationReadings).HasDefaultValue(1);
            entity.Property(e => e.Severity).HasConversion(
                new Converters.EnumMemberValueConverter<Core.Models.Alerts.AlertRuleSeverity>());
            entity.Property(e => e.ClientConfiguration).HasColumnType("jsonb").HasDefaultValue("{}");
            entity.Property(e => e.IsEnabled).HasDefaultValue(true);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        // AlertScheduleEntity
        modelBuilder.Entity<AlertScheduleEntity>(entity =>
        {
            entity.ToTable("alert_schedules");
            entity.Property(e => e.Id).HasValueGenerator<GuidV7ValueGenerator>();
            entity.Property(e => e.DaysOfWeek).HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.AlertRule)
                .WithMany(r => r.Schedules)
                .HasForeignKey(e => e.AlertRuleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AlertEscalationStepEntity
        modelBuilder.Entity<AlertEscalationStepEntity>(entity =>
        {
            entity.ToTable("alert_escalation_steps");
            entity.Property(e => e.Id).HasValueGenerator<GuidV7ValueGenerator>();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.AlertSchedule)
                .WithMany(s => s.EscalationSteps)
                .HasForeignKey(e => e.AlertScheduleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AlertStepChannelEntity
        modelBuilder.Entity<AlertStepChannelEntity>(entity =>
        {
            entity.ToTable("alert_step_channels");
            entity.Property(e => e.Id).HasValueGenerator<GuidV7ValueGenerator>();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.ChannelType).HasConversion(
                new Converters.EnumMemberValueConverter<Core.Models.Alerts.ChannelType>());

            entity.HasOne(e => e.EscalationStep)
                .WithMany(s => s.Channels)
                .HasForeignKey(e => e.EscalationStepId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AlertTrackerStateEntity (1:1 with AlertRule, PK = AlertRuleId)
        modelBuilder.Entity<AlertTrackerStateEntity>(entity =>
        {
            entity.ToTable("alert_tracker_state");
            entity.HasKey(e => e.AlertRuleId);
            entity.Property(e => e.AlertRuleId).ValueGeneratedNever();
            entity.Property(e => e.State).HasDefaultValue("idle");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.AlertRule)
                .WithOne(r => r.TrackerState)
                .HasForeignKey<AlertTrackerStateEntity>(e => e.AlertRuleId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.ActiveExcursion)
                .WithMany()
                .HasForeignKey(e => e.ActiveExcursionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // AlertExcursionEntity
        modelBuilder.Entity<AlertExcursionEntity>(entity =>
        {
            entity.ToTable("alert_excursions");
            entity.Property(e => e.Id).HasValueGenerator<GuidV7ValueGenerator>();

            entity.HasOne(e => e.AlertRule)
                .WithMany()
                .HasForeignKey(e => e.AlertRuleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AlertInstanceEntity
        modelBuilder.Entity<AlertInstanceEntity>(entity =>
        {
            entity.ToTable("alert_instances");
            entity.Property(e => e.Id).HasValueGenerator<GuidV7ValueGenerator>();
            entity.Property(e => e.Status).HasDefaultValue("triggered");

            entity.HasOne(e => e.AlertExcursion)
                .WithMany(ex => ex.Instances)
                .HasForeignKey(e => e.AlertExcursionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.AlertSchedule)
                .WithMany()
                .HasForeignKey(e => e.AlertScheduleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AlertDeliveryEntity
        modelBuilder.Entity<AlertDeliveryEntity>(entity =>
        {
            entity.ToTable("alert_deliveries");
            entity.Property(e => e.Id).HasValueGenerator<GuidV7ValueGenerator>();
            entity.Property(e => e.Payload).HasColumnType("jsonb").HasDefaultValue("{}");
            entity.Property(e => e.Status).HasDefaultValue("pending");
            entity.Property(e => e.ChannelType).HasConversion(
                new Converters.EnumMemberValueConverter<Core.Models.Alerts.ChannelType>());
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.RetryCount).HasDefaultValue(0);

            entity.HasOne(e => e.AlertInstance)
                .WithMany()
                .HasForeignKey(e => e.AlertInstanceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.EscalationStep)
                .WithMany()
                .HasForeignKey(e => e.EscalationStepId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // AlertInviteEntity
        modelBuilder.Entity<AlertInviteEntity>(entity =>
        {
            entity.ToTable("alert_invites");
            entity.Property(e => e.Id).HasValueGenerator<GuidV7ValueGenerator>();
            entity.Property(e => e.PermissionScope).HasDefaultValue("view_acknowledge");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.EscalationStep)
                .WithMany()
                .HasForeignKey(e => e.EscalationStepId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AlertCustomSoundEntity
        modelBuilder.Entity<AlertCustomSoundEntity>(entity =>
        {
            entity.ToTable("alert_custom_sounds");
            entity.Property(e => e.Id).HasValueGenerator<GuidV7ValueGenerator>();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        // PasskeyCredentialEntity
        modelBuilder.Entity<PasskeyCredentialEntity>(entity =>
        {
            entity.HasIndex(e => e.CredentialId).IsUnique();
            entity.HasOne(e => e.Subject).WithMany(s => s.PasskeyCredentials).HasForeignKey(e => e.SubjectId);
        });

        // RecoveryCodeEntity
        modelBuilder.Entity<RecoveryCodeEntity>(entity =>
        {
            entity.HasIndex(e => e.SubjectId);
            entity.HasOne(e => e.Subject).WithMany().HasForeignKey(e => e.SubjectId);
        });

        // CoachMarkStateEntity
        modelBuilder
            .Entity<CoachMarkStateEntity>()
            .HasIndex(e => new { e.SubjectId, e.MarkKey })
            .IsUnique();

    }

    /// <summary>
    /// Saves all changes made in this context to the database
    /// </summary>
    /// <returns>The number of state entries written to the database</returns>
    public override int SaveChanges()
    {
        UpdateTimestamps();
        return base.SaveChanges();
    }

    /// <summary>
    /// Asynchronously saves all changes made in this context to the database
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete</param>
    /// <returns>A task that represents the asynchronous save operation. The task result contains the number of state entries written to the database</returns>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        UpdateTimestamps();
        return await base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Update system tracking timestamps before saving
    /// </summary>
    private void UpdateTimestamps()
    {
        var utcNow = DateTime.UtcNow;

        foreach (var entry in ChangeTracker.Entries())
        {
            // Enforce tenant ID on all new ITenantScoped entities
            if (entry.State == EntityState.Added && entry.Entity is ITenantScoped tenantScoped)
            {
                if (tenantScoped.TenantId == Guid.Empty && TenantId != Guid.Empty)
                {
                    tenantScoped.TenantId = TenantId;
                }
                else if (tenantScoped.TenantId == Guid.Empty)
                {
                    throw new InvalidOperationException(
                        $"Cannot save {entry.Entity.GetType().Name} without a TenantId. " +
                        "Ensure tenant context is resolved before writing data.");
                }
            }

            // Prevent cross-tenant writes
            if (entry.State == EntityState.Modified && entry.Entity is ITenantScoped modifiedTenant)
            {
                if (TenantId != Guid.Empty && modifiedTenant.TenantId != TenantId)
                {
                    throw new InvalidOperationException(
                        $"Cannot modify {entry.Entity.GetType().Name} belonging to tenant " +
                        $"{modifiedTenant.TenantId} from tenant context {TenantId}.");
                }
            }

            if (entry.Entity is FoodEntity foodEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    foodEntity.SysCreatedAt = utcNow;
                }
                foodEntity.SysUpdatedAt = utcNow;
            }
            else if (entry.Entity is ConnectorFoodEntryEntity connectorFoodEntryEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    connectorFoodEntryEntity.SysCreatedAt = utcNow;
                }
                connectorFoodEntryEntity.SysUpdatedAt = utcNow;
            }
            else if (entry.Entity is TreatmentFoodEntity treatmentFoodEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    treatmentFoodEntity.SysCreatedAt = utcNow;
                }
                treatmentFoodEntity.SysUpdatedAt = utcNow;
            }
            else if (entry.Entity is UserFoodFavoriteEntity userFoodFavoriteEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    userFoodFavoriteEntity.SysCreatedAt = utcNow;
                }
            }
            else if (entry.Entity is SettingsEntity settingsEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    settingsEntity.SysCreatedAt = utcNow;
                }
                settingsEntity.SysUpdatedAt = utcNow;
            }
            else if (entry.Entity is StepCountEntity stepCountEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    stepCountEntity.SysCreatedAt = utcNow;
                }
                stepCountEntity.SysUpdatedAt = utcNow;
            }
            else if (entry.Entity is HeartRateEntity heartRateEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    heartRateEntity.SysCreatedAt = utcNow;
                }
                heartRateEntity.SysUpdatedAt = utcNow;
            }
// Auth entities
            else if (entry.Entity is RefreshTokenEntity refreshTokenEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    refreshTokenEntity.CreatedAt = utcNow;
                }
                refreshTokenEntity.UpdatedAt = utcNow;
            }
            else if (entry.Entity is SubjectEntity subjectEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    subjectEntity.CreatedAt = utcNow;
                }
                subjectEntity.UpdatedAt = utcNow;
            }
            else if (entry.Entity is RoleEntity roleEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    roleEntity.CreatedAt = utcNow;
                }
                roleEntity.UpdatedAt = utcNow;
            }
            else if (entry.Entity is OidcProviderEntity oidcProviderEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    oidcProviderEntity.CreatedAt = utcNow;
                }
                oidcProviderEntity.UpdatedAt = utcNow;
            }
            else if (entry.Entity is AuthAuditLogEntity authAuditLogEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    authAuditLogEntity.CreatedAt = utcNow;
                }
            }
            else if (entry.Entity is LinkedRecordEntity linkedRecordEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    linkedRecordEntity.SysCreatedAt = utcNow;
                }
            }
            else if (entry.Entity is ConnectorConfigurationEntity connectorConfigEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    connectorConfigEntity.SysCreatedAt = utcNow;
                    connectorConfigEntity.LastModified = DateTimeOffset.UtcNow;
                }
                connectorConfigEntity.SysUpdatedAt = utcNow;
            }
            // OAuth entities
            else if (entry.Entity is OAuthClientEntity oauthClientEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    oauthClientEntity.CreatedAt = utcNow;
                }
                oauthClientEntity.UpdatedAt = utcNow;
            }
            else if (entry.Entity is OAuthGrantEntity oauthGrantEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    oauthGrantEntity.CreatedAt = utcNow;
                }
            }
            else if (entry.Entity is OAuthRefreshTokenEntity oauthRefreshTokenEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    oauthRefreshTokenEntity.IssuedAt = utcNow;
                }
            }
            else if (entry.Entity is OAuthDeviceCodeEntity oauthDeviceCodeEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    oauthDeviceCodeEntity.CreatedAt = utcNow;
                }
            }
            else if (entry.Entity is OAuthAuthorizationCodeEntity oauthAuthCodeEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    oauthAuthCodeEntity.CreatedAt = utcNow;
                }
            }
            else if (entry.Entity is ClockFaceEntity clockFaceEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    clockFaceEntity.CreatedAt = utcNow;
                    clockFaceEntity.SysCreatedAt = utcNow;
                }
                clockFaceEntity.UpdatedAt = utcNow;
                clockFaceEntity.SysUpdatedAt = utcNow;
            }
            // V4 Granular Model entities
            else if (entry.Entity is SensorGlucoseEntity sensorGlucoseEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    sensorGlucoseEntity.SysCreatedAt = utcNow;
                }
                sensorGlucoseEntity.SysUpdatedAt = utcNow;
            }
            else if (entry.Entity is MeterGlucoseEntity meterGlucoseEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    meterGlucoseEntity.SysCreatedAt = utcNow;
                }
                meterGlucoseEntity.SysUpdatedAt = utcNow;
            }
            else if (entry.Entity is CalibrationEntity calibrationEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    calibrationEntity.SysCreatedAt = utcNow;
                }
                calibrationEntity.SysUpdatedAt = utcNow;
            }
            else if (entry.Entity is BolusEntity bolusEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    bolusEntity.SysCreatedAt = utcNow;
                }
                bolusEntity.SysUpdatedAt = utcNow;
            }
            else if (entry.Entity is CarbIntakeEntity carbIntakeEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    carbIntakeEntity.SysCreatedAt = utcNow;
                }
                carbIntakeEntity.SysUpdatedAt = utcNow;
            }
            else if (entry.Entity is BGCheckEntity bgCheckEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    bgCheckEntity.SysCreatedAt = utcNow;
                }
                bgCheckEntity.SysUpdatedAt = utcNow;
            }
            else if (entry.Entity is NoteEntity noteEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    noteEntity.SysCreatedAt = utcNow;
                }
                noteEntity.SysUpdatedAt = utcNow;
            }
            else if (entry.Entity is DeviceEventEntity deviceEventEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    deviceEventEntity.SysCreatedAt = utcNow;
                }
                deviceEventEntity.SysUpdatedAt = utcNow;
            }
            else if (entry.Entity is BolusCalculationEntity bolusCalculationEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    bolusCalculationEntity.SysCreatedAt = utcNow;
                }
                bolusCalculationEntity.SysUpdatedAt = utcNow;
            }
            else if (entry.Entity is ApsSnapshotEntity apsSnapshotEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    apsSnapshotEntity.SysCreatedAt = utcNow;
                }
                apsSnapshotEntity.SysUpdatedAt = utcNow;
            }
            else if (entry.Entity is PumpSnapshotEntity pumpSnapshotEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    pumpSnapshotEntity.SysCreatedAt = utcNow;
                }
                pumpSnapshotEntity.SysUpdatedAt = utcNow;
            }
            else if (entry.Entity is UploaderSnapshotEntity uploaderSnapshotEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    uploaderSnapshotEntity.SysCreatedAt = utcNow;
                }
                uploaderSnapshotEntity.SysUpdatedAt = utcNow;
            }
            // V4 Profile Decomposition entities
            else if (entry.Entity is TherapySettingsEntity therapySettingsEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    therapySettingsEntity.SysCreatedAt = utcNow;
                }
                therapySettingsEntity.SysUpdatedAt = utcNow;
            }
            else if (entry.Entity is BasalScheduleEntity basalScheduleEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    basalScheduleEntity.SysCreatedAt = utcNow;
                }
                basalScheduleEntity.SysUpdatedAt = utcNow;
            }
            else if (entry.Entity is CarbRatioScheduleEntity carbRatioScheduleEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    carbRatioScheduleEntity.SysCreatedAt = utcNow;
                }
                carbRatioScheduleEntity.SysUpdatedAt = utcNow;
            }
            else if (entry.Entity is SensitivityScheduleEntity sensitivityScheduleEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    sensitivityScheduleEntity.SysCreatedAt = utcNow;
                }
                sensitivityScheduleEntity.SysUpdatedAt = utcNow;
            }
            else if (entry.Entity is TargetRangeScheduleEntity targetRangeScheduleEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    targetRangeScheduleEntity.SysCreatedAt = utcNow;
                }
                targetRangeScheduleEntity.SysUpdatedAt = utcNow;
            }
            else if (entry.Entity is TenantEntity tenantEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    tenantEntity.SysCreatedAt = utcNow;
                }
                tenantEntity.SysUpdatedAt = utcNow;
            }
            else if (entry.Entity is TenantMemberEntity tenantMemberEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    tenantMemberEntity.SysCreatedAt = utcNow;
                }
                tenantMemberEntity.SysUpdatedAt = utcNow;
            }
        }
    }

    /// <summary>
    /// Applies global query filters for tenant isolation on all ITenantScoped entities.
    /// Filters reference this.TenantId which is set per-request.
    /// EF Core parameterizes the value, so pooled contexts work correctly.
    /// </summary>
    private void ConfigureTenantFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType))
                continue;

            var parameter = Expression.Parameter(entityType.ClrType, "e");
            var tenantIdProperty = Expression.Property(parameter, nameof(ITenantScoped.TenantId));
            var currentTenantId = Expression.Property(Expression.Constant(this), nameof(TenantId));
            Expression body = Expression.Equal(tenantIdProperty, currentTenantId);

            if (typeof(ISoftDeletable).IsAssignableFrom(entityType.ClrType))
            {
                var deletedAtProperty = Expression.Property(parameter, nameof(ISoftDeletable.DeletedAt));
                var nullValue = Expression.Constant(null, typeof(DateTime?));
                var isNotDeleted = Expression.Equal(deletedAtProperty, nullValue);
                body = Expression.AndAlso(body, isNotDeleted);
            }

            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(Expression.Lambda(body, parameter));
        }
    }

    /// <summary>
    /// Adds FK constraints with ON DELETE CASCADE from every ITenantScoped entity's
    /// TenantId column to the tenants table. This ensures tenant deletion cascades to
    /// all tenant-scoped data instead of silently orphaning rows.
    /// </summary>
    private static void ConfigureTenantCascadeDeletes(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType))
                continue;

            modelBuilder.Entity(entityType.ClrType)
                .HasOne(typeof(TenantEntity))
                .WithMany()
                .HasForeignKey(nameof(ITenantScoped.TenantId))
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}

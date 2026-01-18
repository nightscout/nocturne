using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.ValueGenerators;

namespace Nocturne.Infrastructure.Data;

/// <summary>
/// Entity Framework DbContext for PostgreSQL database operations
/// Single-tenant architecture for main Nocturne application
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
    /// Gets or sets the Entries table for glucose entries
    /// </summary>
    public DbSet<EntryEntity> Entries { get; set; }

    /// <summary>
    /// Gets or sets the Treatments table for diabetes treatments
    /// </summary>
    public DbSet<TreatmentEntity> Treatments { get; set; }

    /// <summary>
    /// Gets or sets the DeviceStatuses table for device status information
    /// </summary>
    public DbSet<DeviceStatusEntity> DeviceStatuses { get; set; }

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
    /// Gets or sets the Profiles table for user profiles
    /// </summary>
    public DbSet<ProfileEntity> Profiles { get; set; }

    /// <summary>
    /// Gets or sets the Activities table for user activities
    /// </summary>
    public DbSet<ActivityEntity> Activities { get; set; }

    /// <summary>
    /// Gets or sets the DiscrepancyAnalyses table for response comparison analysis
    /// </summary>
    public DbSet<DiscrepancyAnalysisEntity> DiscrepancyAnalyses { get; set; }

    /// <summary>
    /// Gets or sets the DiscrepancyDetails table for detailed discrepancy information
    /// </summary>
    public DbSet<DiscrepancyDetailEntity> DiscrepancyDetails { get; set; }

    /// <summary>
    /// Gets or sets the AlertRules table for notification alert rules
    /// </summary>
    public DbSet<AlertRuleEntity> AlertRules { get; set; }

    /// <summary>
    /// Gets or sets the AlertHistory table for notification alert history
    /// </summary>
    public DbSet<AlertHistoryEntity> AlertHistory { get; set; }

    /// <summary>
    /// Gets or sets the NotificationPreferences table for user notification preferences
    /// </summary>
    public DbSet<NotificationPreferencesEntity> NotificationPreferences { get; set; }

    /// <summary>
    /// Gets or sets the EmergencyContacts table for escalation contact management
    /// </summary>
    public DbSet<EmergencyContactEntity> EmergencyContacts { get; set; }

    /// <summary>
    /// Gets or sets the DeviceHealth table for device health monitoring and maintenance alerts
    /// </summary>
    public DbSet<DeviceHealthEntity> DeviceHealth { get; set; }

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

    // Local Identity Provider entities

    /// <summary>
    /// Gets or sets the LocalUsers table for built-in authentication users
    /// </summary>
    public DbSet<LocalUserEntity> LocalUsers { get; set; }

    /// <summary>
    /// Gets or sets the PasswordResetRequests table for pending password reset requests
    /// </summary>
    public DbSet<PasswordResetRequestEntity> PasswordResetRequests { get; set; }

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
    }

    private static void ConfigureIndexes(ModelBuilder modelBuilder)
    {
        // Entries indexes - optimized for common queries
        modelBuilder
            .Entity<EntryEntity>()
            .HasIndex(e => e.Mills)
            .HasDatabaseName("ix_entries_mills")
            .IsDescending(); // Most recent first

        modelBuilder.Entity<EntryEntity>().HasIndex(e => e.Type).HasDatabaseName("ix_entries_type");

        modelBuilder
            .Entity<EntryEntity>()
            .HasIndex(e => new { e.Type, e.Mills })
            .HasDatabaseName("ix_entries_type_mills")
            .IsDescending(false, true); // Type asc, Mills desc

        // Composite index for duplicate detection
        modelBuilder
            .Entity<EntryEntity>()
            .HasIndex(e => new
            {
                e.Device,
                e.Type,
                e.Sgv,
                e.Mills,
            })
            .HasDatabaseName("ix_entries_duplicate_detection");

        // Treatments indexes - optimized for common queries
        modelBuilder
            .Entity<TreatmentEntity>()
            .HasIndex(t => t.Mills)
            .HasDatabaseName("ix_treatments_mills")
            .IsDescending(); // Most recent first

        modelBuilder
            .Entity<TreatmentEntity>()
            .HasIndex(t => t.EventType)
            .HasDatabaseName("ix_treatments_event_type");

        modelBuilder
            .Entity<TreatmentEntity>()
            .HasIndex(t => new { t.EventType, t.Mills })
            .HasDatabaseName("ix_treatments_event_type_mills")
            .IsDescending(false, true); // EventType asc, Mills desc

        // DeviceStatus indexes
        modelBuilder
            .Entity<DeviceStatusEntity>()
            .HasIndex(d => d.Mills)
            .HasDatabaseName("ix_devicestatus_mills")
            .IsDescending(); // Most recent first

        modelBuilder
            .Entity<DeviceStatusEntity>()
            .HasIndex(d => d.Device)
            .HasDatabaseName("ix_devicestatus_device");

        modelBuilder
            .Entity<DeviceStatusEntity>()
            .HasIndex(d => new { d.Device, d.Mills })
            .HasDatabaseName("ix_devicestatus_device_mills")
            .IsDescending(false, true); // Device asc, Mills desc

        // System tracking indexes for maintenance operations
        modelBuilder
            .Entity<EntryEntity>()
            .HasIndex(e => e.SysCreatedAt)
            .HasDatabaseName("ix_entries_sys_created_at");

        modelBuilder
            .Entity<TreatmentEntity>()
            .HasIndex(t => t.SysCreatedAt)
            .HasDatabaseName("ix_treatments_sys_created_at");

        modelBuilder
            .Entity<DeviceStatusEntity>()
            .HasIndex(d => d.SysCreatedAt)
            .HasDatabaseName("ix_devicestatus_sys_created_at");

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
            .HasIndex(f => new { f.ExternalSource, f.ExternalId })
            .HasDatabaseName("ix_foods_external_source_id")
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
            .HasIndex(e => new { e.ConnectorSource, e.ExternalEntryId })
            .HasDatabaseName("ix_connector_food_entries_source_entry")
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
            .HasIndex(tf => tf.TreatmentId)
            .HasDatabaseName("ix_treatment_foods_treatment_id");

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
            .HasIndex(f => new { f.UserId, f.FoodId })
            .HasDatabaseName("ix_user_food_favorites_user_food")
            .IsUnique();

        // Settings indexes - optimized for common queries
        modelBuilder
            .Entity<SettingsEntity>()
            .HasIndex(s => s.Key)
            .HasDatabaseName("ix_settings_key")
            .IsUnique(); // Settings keys should be unique

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

        // Profile indexes - optimized for common queries
        modelBuilder
            .Entity<ProfileEntity>()
            .HasIndex(p => p.Mills)
            .HasDatabaseName("ix_profiles_mills")
            .IsDescending(); // Most recent first

        modelBuilder
            .Entity<ProfileEntity>()
            .HasIndex(p => p.DefaultProfile)
            .HasDatabaseName("ix_profiles_default_profile");

        modelBuilder
            .Entity<ProfileEntity>()
            .HasIndex(p => p.Units)
            .HasDatabaseName("ix_profiles_units");

        modelBuilder
            .Entity<ProfileEntity>()
            .HasIndex(p => p.CreatedAtPg)
            .HasDatabaseName("ix_profiles_sys_created_at");

        // Activity indexes - optimized for common queries
        modelBuilder
            .Entity<ActivityEntity>()
            .HasIndex(a => a.Mills)
            .HasDatabaseName("ix_activities_mills")
            .IsDescending(); // Most recent first

        modelBuilder
            .Entity<ActivityEntity>()
            .HasIndex(a => a.Type)
            .HasDatabaseName("ix_activities_type");

        modelBuilder
            .Entity<ActivityEntity>()
            .HasIndex(a => new { a.Type, a.Mills })
            .HasDatabaseName("ix_activities_type_mills")
            .IsDescending(false, true); // Type asc, Mills desc

        modelBuilder
            .Entity<ActivityEntity>()
            .HasIndex(a => a.SysCreatedAt)
            .HasDatabaseName("ix_activities_sys_created_at");

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

        // Alert Rules indexes - optimized for user queries
        modelBuilder
            .Entity<AlertRuleEntity>()
            .HasIndex(a => a.UserId)
            .HasDatabaseName("ix_alert_rules_user_id");

        modelBuilder
            .Entity<AlertRuleEntity>()
            .HasIndex(a => a.IsEnabled)
            .HasDatabaseName("ix_alert_rules_is_enabled");

        modelBuilder
            .Entity<AlertRuleEntity>()
            .HasIndex(a => new { a.UserId, a.IsEnabled })
            .HasDatabaseName("ix_alert_rules_user_enabled");

        modelBuilder
            .Entity<AlertRuleEntity>()
            .HasIndex(a => a.CreatedAt)
            .HasDatabaseName("ix_alert_rules_created_at");

        // Alert History indexes - optimized for monitoring and dashboard queries
        modelBuilder
            .Entity<AlertHistoryEntity>()
            .HasIndex(h => h.UserId)
            .HasDatabaseName("ix_alert_history_user_id");

        modelBuilder
            .Entity<AlertHistoryEntity>()
            .HasIndex(h => h.Status)
            .HasDatabaseName("ix_alert_history_status");

        modelBuilder
            .Entity<AlertHistoryEntity>()
            .HasIndex(h => h.AlertType)
            .HasDatabaseName("ix_alert_history_alert_type");

        modelBuilder
            .Entity<AlertHistoryEntity>()
            .HasIndex(h => h.TriggerTime)
            .HasDatabaseName("ix_alert_history_trigger_time")
            .IsDescending(); // Most recent first

        modelBuilder
            .Entity<AlertHistoryEntity>()
            .HasIndex(h => new { h.UserId, h.Status })
            .HasDatabaseName("ix_alert_history_user_status");

        modelBuilder
            .Entity<AlertHistoryEntity>()
            .HasIndex(h => new { h.UserId, h.TriggerTime })
            .HasDatabaseName("ix_alert_history_user_trigger_time")
            .IsDescending(false, true); // UserId asc, TriggerTime desc

        modelBuilder
            .Entity<AlertHistoryEntity>()
            .HasIndex(h => h.AlertRuleId)
            .HasDatabaseName("ix_alert_history_alert_rule_id");

        // Notification Preferences indexes - optimized for user lookups
        modelBuilder
            .Entity<NotificationPreferencesEntity>()
            .HasIndex(p => p.UserId)
            .HasDatabaseName("ix_notification_preferences_user_id")
            .IsUnique(); // One preference set per user

        modelBuilder
            .Entity<NotificationPreferencesEntity>()
            .HasIndex(p => p.EmailEnabled)
            .HasDatabaseName("ix_notification_preferences_email_enabled");

        modelBuilder
            .Entity<NotificationPreferencesEntity>()
            .HasIndex(p => p.PushoverEnabled)
            .HasDatabaseName("ix_notification_preferences_pushover_enabled");

        modelBuilder
            .Entity<NotificationPreferencesEntity>()
            .HasIndex(p => p.SmsEnabled)
            .HasDatabaseName("ix_notification_preferences_sms_enabled");

        // Emergency Contacts indexes - optimized for escalation queries
        modelBuilder
            .Entity<EmergencyContactEntity>()
            .HasIndex(c => c.UserId)
            .HasDatabaseName("ix_emergency_contacts_user_id");

        modelBuilder
            .Entity<EmergencyContactEntity>()
            .HasIndex(c => c.IsActive)
            .HasDatabaseName("ix_emergency_contacts_is_active");

        modelBuilder
            .Entity<EmergencyContactEntity>()
            .HasIndex(c => c.Priority)
            .HasDatabaseName("ix_emergency_contacts_priority");

        modelBuilder
            .Entity<EmergencyContactEntity>()
            .HasIndex(c => c.ContactType)
            .HasDatabaseName("ix_emergency_contacts_contact_type");

        // Device Health indexes - optimized for device monitoring and maintenance queries
        modelBuilder
            .Entity<DeviceHealthEntity>()
            .HasIndex(d => d.UserId)
            .HasDatabaseName("ix_device_health_user_id");

        modelBuilder
            .Entity<DeviceHealthEntity>()
            .HasIndex(d => d.DeviceId)
            .HasDatabaseName("ix_device_health_device_id")
            .IsUnique(); // One health record per device

        modelBuilder
            .Entity<DeviceHealthEntity>()
            .HasIndex(d => d.DeviceType)
            .HasDatabaseName("ix_device_health_device_type");

        modelBuilder
            .Entity<DeviceHealthEntity>()
            .HasIndex(d => d.Status)
            .HasDatabaseName("ix_device_health_status");

        modelBuilder
            .Entity<DeviceHealthEntity>()
            .HasIndex(d => new { d.UserId, d.DeviceType })
            .HasDatabaseName("ix_device_health_user_device_type");

        modelBuilder
            .Entity<DeviceHealthEntity>()
            .HasIndex(d => new { d.UserId, d.Status })
            .HasDatabaseName("ix_device_health_user_status");

        modelBuilder
            .Entity<DeviceHealthEntity>()
            .HasIndex(d => d.LastDataReceived)
            .HasDatabaseName("ix_device_health_last_data_received")
            .IsDescending(); // Most recent first

        modelBuilder
            .Entity<DeviceHealthEntity>()
            .HasIndex(d => d.SensorExpiration)
            .HasDatabaseName("ix_device_health_sensor_expiration");

        modelBuilder
            .Entity<DeviceHealthEntity>()
            .HasIndex(d => d.LastMaintenanceAlert)
            .HasDatabaseName("ix_device_health_last_maintenance_alert");

        modelBuilder
            .Entity<DeviceHealthEntity>()
            .HasIndex(d => d.CreatedAt)
            .HasDatabaseName("ix_device_health_created_at");

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
            .HasIndex(s => new { s.OidcSubjectId, s.OidcIssuer })
            .HasDatabaseName("ix_subjects_oidc_identity")
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
            .HasIndex(d => d.DeviceId)
            .HasDatabaseName("ix_data_source_metadata_device_id")
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
            .HasIndex(s => s.StartMills)
            .HasDatabaseName("ix_state_spans_start_mills")
            .IsDescending();

        modelBuilder
            .Entity<StateSpanEntity>()
            .HasIndex(s => s.Category)
            .HasDatabaseName("ix_state_spans_category");

        modelBuilder
            .Entity<StateSpanEntity>()
            .HasIndex(s => s.EndMills)
            .HasDatabaseName("ix_state_spans_end_mills")
            .HasFilter("end_mills IS NULL"); // Partial index for active spans

        modelBuilder
            .Entity<StateSpanEntity>()
            .HasIndex(s => new { s.Category, s.StartMills })
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
            .HasDatabaseName("ix_system_events_category_mills")
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
            .HasIndex(l => new { l.RecordType, l.RecordId })
            .IsUnique()
            .HasDatabaseName("ix_linked_records_unique");

        modelBuilder
            .Entity<LinkedRecordEntity>()
            .HasIndex(l => new { l.RecordType, l.CanonicalId, l.IsPrimary })
            .HasDatabaseName("ix_linked_records_type_canonical_primary");

        modelBuilder
            .Entity<LinkedRecordEntity>()
            .HasIndex(l => new { l.RecordType, l.SourceTimestamp })
            .HasDatabaseName("ix_linked_records_type_timestamp");

    }

    private static void ConfigureEntities(ModelBuilder modelBuilder)
    {
        // Configure UUID Version 7 value generators for all entity primary keys
        modelBuilder
            .Entity<EntryEntity>()
            .Property(e => e.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<TreatmentEntity>()
            .Property(t => t.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<DeviceStatusEntity>()
            .Property(d => d.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
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
            .Entity<ProfileEntity>()
            .Property(p => p.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<ActivityEntity>()
            .Property(a => a.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<DiscrepancyAnalysisEntity>()
            .Property(d => d.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<DiscrepancyDetailEntity>()
            .Property(d => d.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<AlertRuleEntity>()
            .Property(a => a.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<AlertHistoryEntity>()
            .Property(a => a.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<NotificationPreferencesEntity>()
            .Property(n => n.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<EmergencyContactEntity>()
            .Property(e => e.Id)
            .HasValueGenerator<GuidV7ValueGenerator>();
        modelBuilder
            .Entity<DeviceHealthEntity>()
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
            .Entity<ConnectorFoodEntryEntity>()
            .HasOne(e => e.Food)
            .WithMany()
            .HasForeignKey(e => e.FoodId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder
            .Entity<ConnectorFoodEntryEntity>()
            .HasOne(e => e.MatchedTreatment)
            .WithMany()
            .HasForeignKey(e => e.MatchedTreatmentId)
            .OnDelete(DeleteBehavior.SetNull);


        // Configure automatic timestamp updates
        modelBuilder
            .Entity<EntryEntity>()
            .Property(e => e.SysUpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAddOrUpdate();

        modelBuilder
            .Entity<TreatmentEntity>()
            .Property(t => t.SysUpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAddOrUpdate();

        modelBuilder
            .Entity<DeviceStatusEntity>()
            .Property(d => d.SysUpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAddOrUpdate();

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
            .Entity<ActivityEntity>()
            .Property(a => a.SysUpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAddOrUpdate();

        // Configure JSON column defaults and constraints
        modelBuilder.Entity<EntryEntity>().Property(e => e.ScaledJson).HasDefaultValue("null");

        modelBuilder.Entity<EntryEntity>().Property(e => e.MetaJson).HasDefaultValue("{}");

        modelBuilder.Entity<TreatmentEntity>().Property(t => t.BolusCalcJson).HasDefaultValue("{}");

        modelBuilder.Entity<TreatmentEntity>().Property(t => t.ProfileJson).HasDefaultValue("null");

        // Configure required fields and defaults
        modelBuilder.Entity<EntryEntity>().Property(e => e.Type).HasDefaultValue("sgv");

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
                .HasOne(tf => tf.Treatment)
                .WithMany()
                .HasForeignKey(tf => tf.TreatmentId)
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

        // Profile defaults
        modelBuilder
            .Entity<ProfileEntity>()
            .Property(p => p.DefaultProfile)
            .HasDefaultValue("Default");
        modelBuilder.Entity<ProfileEntity>().Property(p => p.Units).HasDefaultValue("mg/dl");
        modelBuilder.Entity<ProfileEntity>().Property(p => p.StoreJson).HasDefaultValue("{}");

        // Profile automatic timestamps
        modelBuilder
            .Entity<ProfileEntity>()
            .Property(p => p.CreatedAtPg)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        modelBuilder
            .Entity<ProfileEntity>()
            .Property(p => p.UpdatedAtPg)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAddOrUpdate();

        // Configure DeviceStatus JSON fields with default empty objects
        foreach (
            var jsonProperty in new[]
            {
                nameof(DeviceStatusEntity.UploaderJson),
                nameof(DeviceStatusEntity.PumpJson),
                nameof(DeviceStatusEntity.OpenApsJson),
                nameof(DeviceStatusEntity.LoopJson),
                nameof(DeviceStatusEntity.XDripJsJson),
                nameof(DeviceStatusEntity.RadioAdapterJson),
                nameof(DeviceStatusEntity.ConnectJson),
                nameof(DeviceStatusEntity.OverrideJson),
                nameof(DeviceStatusEntity.CgmJson),
                nameof(DeviceStatusEntity.MeterJson),
                nameof(DeviceStatusEntity.InsulinPenJson),
            }
        )
        {
            modelBuilder
                .Entity<DeviceStatusEntity>()
                .Property(jsonProperty)
                .HasDefaultValue("null");
        }

        // Configure AlertRule defaults and constraints
        modelBuilder.Entity<AlertRuleEntity>().Property(a => a.IsEnabled).HasDefaultValue(true);
        modelBuilder
            .Entity<AlertRuleEntity>()
            .Property(a => a.EscalationDelayMinutes)
            .HasDefaultValue(15);
        modelBuilder.Entity<AlertRuleEntity>().Property(a => a.MaxEscalations).HasDefaultValue(3);
        modelBuilder
            .Entity<AlertRuleEntity>()
            .Property(a => a.DefaultSnoozeMinutes)
            .HasDefaultValue(30);
        modelBuilder
            .Entity<AlertRuleEntity>()
            .Property(a => a.MaxSnoozeMinutes)
            .HasDefaultValue(120);
        modelBuilder
            .Entity<AlertRuleEntity>()
            .Property(a => a.NotificationChannels)
            .HasDefaultValue("[]");
        modelBuilder
            .Entity<AlertRuleEntity>()
            .Property(a => a.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");
        modelBuilder
            .Entity<AlertRuleEntity>()
            .Property(a => a.UpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAddOrUpdate();

        // Configure AlertHistory defaults and constraints
        modelBuilder
            .Entity<AlertHistoryEntity>()
            .Property(h => h.EscalationLevel)
            .HasDefaultValue(0);
        modelBuilder
            .Entity<AlertHistoryEntity>()
            .Property(h => h.NotificationsSent)
            .HasDefaultValue("[]");
        modelBuilder
            .Entity<AlertHistoryEntity>()
            .Property(h => h.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");
        modelBuilder
            .Entity<AlertHistoryEntity>()
            .Property(h => h.UpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAddOrUpdate();

        // Configure NotificationPreferences defaults
        modelBuilder
            .Entity<NotificationPreferencesEntity>()
            .Property(p => p.EmailEnabled)
            .HasDefaultValue(true);
        modelBuilder
            .Entity<NotificationPreferencesEntity>()
            .Property(p => p.PushoverEnabled)
            .HasDefaultValue(false);
        modelBuilder
            .Entity<NotificationPreferencesEntity>()
            .Property(p => p.SmsEnabled)
            .HasDefaultValue(false);
        modelBuilder
            .Entity<NotificationPreferencesEntity>()
            .Property(p => p.WebhookEnabled)
            .HasDefaultValue(false);
        modelBuilder
            .Entity<NotificationPreferencesEntity>()
            .Property(p => p.QuietHoursEnabled)
            .HasDefaultValue(false);
        modelBuilder
            .Entity<NotificationPreferencesEntity>()
            .Property(p => p.EmergencyOverrideQuietHours)
            .HasDefaultValue(true);
        modelBuilder
            .Entity<NotificationPreferencesEntity>()
            .Property(p => p.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");
        modelBuilder
            .Entity<NotificationPreferencesEntity>()
            .Property(p => p.UpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAddOrUpdate();

        // Configure EmergencyContacts defaults and constraints
        modelBuilder
            .Entity<EmergencyContactEntity>()
            .Property(c => c.IsActive)
            .HasDefaultValue(true);
        modelBuilder.Entity<EmergencyContactEntity>().Property(c => c.Priority).HasDefaultValue(1);
        modelBuilder
            .Entity<EmergencyContactEntity>()
            .Property(c => c.AlertTypes)
            .HasDefaultValue("[]");
        modelBuilder
            .Entity<EmergencyContactEntity>()
            .Property(c => c.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");
        modelBuilder
            .Entity<EmergencyContactEntity>()
            .Property(c => c.UpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAddOrUpdate();

        // Configure DeviceHealth defaults and constraints
        modelBuilder
            .Entity<DeviceHealthEntity>()
            .Property(d => d.DeviceType)
            .HasDefaultValue(DeviceType.Unknown)
            .HasSentinel(DeviceType.CGM); // CGM is CLR default (0), use it as sentinel
        modelBuilder
            .Entity<DeviceHealthEntity>()
            .Property(d => d.Status)
            .HasDefaultValue(DeviceStatusType.Active);
        modelBuilder
            .Entity<DeviceHealthEntity>()
            .Property(d => d.BatteryWarningThreshold)
            .HasDefaultValue(20.0m);
        modelBuilder
            .Entity<DeviceHealthEntity>()
            .Property(d => d.SensorExpirationWarningHours)
            .HasDefaultValue(24);
        modelBuilder
            .Entity<DeviceHealthEntity>()
            .Property(d => d.DataGapWarningMinutes)
            .HasDefaultValue(30);
        modelBuilder
            .Entity<DeviceHealthEntity>()
            .Property(d => d.CalibrationReminderHours)
            .HasDefaultValue(12);
        modelBuilder
            .Entity<DeviceHealthEntity>()
            .Property(d => d.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");
        modelBuilder
            .Entity<DeviceHealthEntity>()
            .Property(d => d.UpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .ValueGeneratedOnAddOrUpdate();

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

        // Configure Role entity defaults
        modelBuilder.Entity<RoleEntity>(entity =>
        {
            entity.Property(e => e.Permissions).HasDefaultValue(new List<string>());
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
            entity
                .Property(e => e.Scopes)
                .HasDefaultValue(new List<string> { "openid", "profile", "email" });
            entity.Property(e => e.DefaultRoles).HasDefaultValue(new List<string> { "readable" });
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

        // Configure LocalUser entity for built-in identity provider
        modelBuilder.Entity<LocalUserEntity>(entity =>
        {
            entity.Property(e => e.Id).HasValueGenerator<GuidV7ValueGenerator>();
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.EmailVerified).HasDefaultValue(false);
            entity.Property(e => e.PendingApproval).HasDefaultValue(false);
            entity.Property(e => e.FailedLoginAttempts).HasDefaultValue(0);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity
                .Property(e => e.UpdatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .ValueGeneratedOnAddOrUpdate();

            // Unique index on normalized email for fast lookups
            entity
                .HasIndex(e => e.NormalizedEmail)
                .IsUnique()
                .HasDatabaseName("ix_local_users_normalized_email");

            // Relationship to Subject for permissions
            entity
                .HasOne(e => e.Subject)
                .WithMany()
                .HasForeignKey(e => e.SubjectId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Configure LinkedRecordEntity defaults
        modelBuilder.Entity<LinkedRecordEntity>(entity =>
        {
            entity.Property(e => e.IsPrimary).HasDefaultValue(false);
            entity.Property(e => e.SysCreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        // Configure PasswordResetRequest entity
        modelBuilder.Entity<PasswordResetRequestEntity>(entity =>
        {
            entity.Property(e => e.Id).HasValueGenerator<GuidV7ValueGenerator>();
            entity.Property(e => e.AdminNotified).HasDefaultValue(false);
            entity.Property(e => e.Handled).HasDefaultValue(false);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            // Index for finding pending requests
            entity
                .HasIndex(e => e.Handled)
                .HasFilter("handled = false")
                .HasDatabaseName("ix_password_reset_requests_pending");

            // Relationships
            entity
                .HasOne(e => e.LocalUser)
                .WithMany()
                .HasForeignKey(e => e.LocalUserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity
                .HasOne(e => e.HandledBy)
                .WithMany()
                .HasForeignKey(e => e.HandledById)
                .OnDelete(DeleteBehavior.SetNull);
        });

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
            if (entry.Entity is EntryEntity entryEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    entryEntity.SysCreatedAt = utcNow;
                }
                entryEntity.SysUpdatedAt = utcNow;
            }
            else if (entry.Entity is TreatmentEntity treatmentEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    treatmentEntity.SysCreatedAt = utcNow;
                }
                treatmentEntity.SysUpdatedAt = utcNow;
            }
            else if (entry.Entity is DeviceStatusEntity deviceStatusEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    deviceStatusEntity.SysCreatedAt = utcNow;
                }
                deviceStatusEntity.SysUpdatedAt = utcNow;
            }
            else if (entry.Entity is FoodEntity foodEntity)
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
            else if (entry.Entity is ActivityEntity activityEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    activityEntity.SysCreatedAt = utcNow;
                }
                activityEntity.SysUpdatedAt = utcNow;
            }
            else if (entry.Entity is ProfileEntity profileEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    profileEntity.CreatedAtPg = utcNow;
                }
                profileEntity.UpdatedAtPg = utcNow;
            }
            else if (entry.Entity is AlertRuleEntity alertRuleEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    alertRuleEntity.CreatedAt = utcNow;
                }
                alertRuleEntity.UpdatedAt = utcNow;
            }
            else if (entry.Entity is AlertHistoryEntity alertHistoryEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    alertHistoryEntity.CreatedAt = utcNow;
                }
                alertHistoryEntity.UpdatedAt = utcNow;
            }
            else if (entry.Entity is NotificationPreferencesEntity notificationPreferencesEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    notificationPreferencesEntity.CreatedAt = utcNow;
                }
                notificationPreferencesEntity.UpdatedAt = utcNow;
            }
            else if (entry.Entity is EmergencyContactEntity emergencyContactEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    emergencyContactEntity.CreatedAt = utcNow;
                }
                emergencyContactEntity.UpdatedAt = utcNow;
            }
            else if (entry.Entity is DeviceHealthEntity deviceHealthEntity)
            {
                if (entry.State == EntityState.Added)
                {
                    deviceHealthEntity.CreatedAt = utcNow;
                }
                deviceHealthEntity.UpdatedAt = utcNow;
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
        }
    }
}

using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
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
public class NocturneDbContext : DbContext, IDataProtectionKeyContext
{
    private readonly DbContextOptions<NocturneDbContext> _options;

    /// <summary>
    /// Initializes a new instance of the NocturneDbContext class
    /// </summary>
    /// <param name="options">The options for this context</param>
    public NocturneDbContext(DbContextOptions<NocturneDbContext> options)
        : base(options)
    {
        _options = options;
    }

    /// <summary>
    /// The application service provider these options were built with, or null when the context was
    /// constructed from a bare <see cref="DbContextOptionsBuilder{TContext}"/> (design-time, tests).
    /// Read during <see cref="OnModelCreating"/> to resolve services the model itself depends on.
    /// </summary>
    private IServiceProvider? ApplicationServices =>
        _options.FindExtension<Microsoft.EntityFrameworkCore.Infrastructure.CoreOptionsExtension>()
            ?.ApplicationServiceProvider;

    /// <summary>
    /// The current tenant ID. Set per-request by the DI factory.
    /// Referenced by global query filters for automatic tenant isolation.
    /// With context pooling, this property is set each time the context is checked out.
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// The subject whose own rows a subject-scoped cross-tenant read may reach. Set per-lease by
    /// the few callers that legitimately read one subject's rows across tenants (the tenant
    /// switcher, the caregiver overview, membership enumeration). The
    /// <see cref="Interceptors.TenantConnectionInterceptor"/> carries it to the
    /// <c>app.current_subject_id</c> GUC. <see cref="Guid.Empty"/> leaves the GUC unset, so a
    /// policy arm reading it matches no row (fail-closed).
    /// </summary>
    public Guid SubjectId { get; set; }

    /// <summary>
    /// Audit context for the current operation. Populated from HttpContext for HTTP
    /// requests (via <see cref="Interceptors.MutationAuditInterceptor"/>), or set
    /// directly by background services that have no HttpContext.
    /// </summary>
    public IAuditContext? AuditContext { get; set; }

    /// <summary>
    /// True when this context serves an anonymous public share request. Set per-lease
    /// wherever <see cref="TenantId"/> is set (known pre-auth at tenant resolution). The
    /// <see cref="Interceptors.TenantConnectionInterceptor"/> carries it to the
    /// <c>app.is_share</c> GUC, which gates the per-category public-share RLS policies.
    /// </summary>
    public bool IsShareContext { get; set; }

    /// <summary>
    /// Comma-separated governing read scopes a public share may see, or <c>null</c> for
    /// non-shares. Set only on the factory-created context (post-auth, once the share's
    /// categories are resolved); carried to the <c>app.visible_categories</c> GUC. A share
    /// context with no CSV denies all categorized data (fail-closed).
    /// </summary>
    public string? VisibleCategories { get; set; }

    /// <summary>
    /// True when a public share may see full history instead of the last 24 hours. Set only
    /// on the factory-created context (post-auth, alongside <see cref="VisibleCategories"/>);
    /// carried to the <c>app.share_full_history</c> GUC. A share context that never sets it
    /// is clamped to 24 hours (fail-closed). Meaningless for non-shares — the clamp only
    /// applies when <c>app.is_share</c> is 'true'.
    /// </summary>
    public bool ShareFullHistory { get; set; }

    public DbSet<FoodEntity> Foods { get; set; }

    public DbSet<ConnectorFoodEntryEntity> ConnectorFoodEntries { get; set; }

    public DbSet<TreatmentFoodEntity> TreatmentFoods { get; set; }

    public DbSet<UserFoodFavoriteEntity> UserFoodFavorites { get; set; }

    public DbSet<SettingsEntity> Settings { get; set; }

    public DbSet<StepCountEntity> StepCounts { get; set; }

    public DbSet<HeartRateEntity> HeartRates { get; set; }

    public DbSet<BodyWeightEntity> BodyWeights { get; set; }

    public DbSet<DiscrepancyAnalysisEntity> DiscrepancyAnalyses { get; set; }

    public DbSet<DiscrepancyDetailEntity> DiscrepancyDetails { get; set; }

    // Authentication and Authorization entities

    public DbSet<RefreshTokenEntity> RefreshTokens { get; set; }

    public DbSet<SubjectEntity> Subjects { get; set; }

    public DbSet<SubjectAvatarEntity> SubjectAvatars { get; set; }

    public DbSet<RoleEntity> Roles { get; set; }

    public DbSet<SubjectRoleEntity> SubjectRoles { get; set; }

    public DbSet<OidcProviderEntity> OidcProviders { get; set; }

    public DbSet<AuthAuditLogEntity> AuthAuditLog { get; set; }

    public DbSet<MutationAuditLogEntity> MutationAuditLog { get; set; }

    public DbSet<PasskeyCredentialEntity> PasskeyCredentials { get; set; }

    public DbSet<RecoveryCodeEntity> RecoveryCodes { get; set; }

    public DbSet<TotpCredentialEntity> TotpCredentials { get; set; }

    public DbSet<TotpStepUpTokenEntity> TotpStepUpTokens { get; set; }

    /// <summary>
    /// ASP.NET Core Data Protection key ring — persisted so keys survive container restarts.
    /// Not tenant-scoped; no RLS.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; }

    public DbSet<DataSourceMetadataEntity> DataSourceMetadata { get; set; }

    // Tracker entities

    public DbSet<TrackerDefinitionEntity> TrackerDefinitions { get; set; }

    public DbSet<TrackerInstanceEntity> TrackerInstances { get; set; }

    public DbSet<TrackerPresetEntity> TrackerPresets { get; set; }

    public DbSet<TrackerNotificationThresholdEntity> TrackerNotificationThresholds { get; set; }

    // StateSpan entities

    public DbSet<StateSpanEntity> StateSpans { get; set; }

    public DbSet<SystemEventEntity> SystemEvents { get; set; }

    // Sleep entities

    public DbSet<SleepSessionEntity> SleepSessions { get; set; }

    public DbSet<SleepStageEntity> SleepStages { get; set; }

    public DbSet<SleepBiometricSampleEntity> SleepBiometricSamples { get; set; }

    // Migration tracking entities

    public DbSet<MigrationSourceEntity> MigrationSources { get; set; }

    public DbSet<MigrationRunEntity> MigrationRuns { get; set; }

    public DbSet<ConnectorResetJobEntity> ConnectorResetJobs { get; set; }

    public DbSet<LinkedRecordEntity> LinkedRecords { get; set; }

    public DbSet<DedupReconcileStateEntity> DedupReconcileState { get; set; }

    // Connector Configuration entities

    public DbSet<ConnectorConfigurationEntity> ConnectorConfigurations { get; set; }

    /// <summary>
    /// Instance-wide platform configuration (not tenant-scoped): encrypted bot-platform credentials
    /// (Discord, Slack, Telegram, WhatsApp) and platform-level config.
    /// </summary>
    public DbSet<PlatformSettingsEntity> PlatformSettings { get; set; }

    // In-App Notification entities

    public DbSet<InAppNotificationEntity> InAppNotifications { get; set; }

    public DbSet<ClockFaceEntity> ClockFaces { get; set; }

    // OAuth 2.0 entities

    public DbSet<OAuthClientEntity> OAuthClients { get; set; }

    public DbSet<OAuthGrantEntity> OAuthGrants { get; set; }

    public DbSet<OAuthRefreshTokenEntity> OAuthRefreshTokens { get; set; }

    public DbSet<OAuthDeviceCodeEntity> OAuthDeviceCodes { get; set; }

    public DbSet<OAuthAuthorizationCodeEntity> OAuthAuthorizationCodes { get; set; }

    public DbSet<MemberInviteEntity> MemberInvites { get; set; } = null!;

    public DbSet<MembershipRequestEntity> MembershipRequests { get; set; } = null!;

    public DbSet<CompressionLowSuggestionEntity> CompressionLowSuggestions { get; set; }

    // V4 Granular Models

    public DbSet<SensorGlucoseEntity> SensorGlucose { get; set; }

    public DbSet<MeterGlucoseEntity> MeterGlucose { get; set; }

    /// <summary>
    /// The tenant's ordered record of which IANA zone the person was in over time, used to convert
    /// fake-UTC connector data (e.g. Glooko) to true UTC.
    /// </summary>
    public DbSet<TimezoneTimelineEntity> TimezoneTimeline { get; set; }

    public DbSet<CalibrationEntity> Calibrations { get; set; }

    public DbSet<BolusEntity> Boluses { get; set; }

    /// <summary>
    /// Discrete long-acting basal insulin injection records (MDI; v4 granular model).
    /// </summary>
    public DbSet<BasalInjectionEntity> BasalInjections { get; set; }

    public DbSet<CarbIntakeEntity> CarbIntakes { get; set; }

    public DbSet<BGCheckEntity> BGChecks { get; set; }

    public DbSet<NoteEntity> Notes { get; set; }

    public DbSet<DeviceEventEntity> DeviceEvents { get; set; }

    public DbSet<BolusCalculationEntity> BolusCalculations { get; set; }

    public DbSet<ApsSnapshotEntity> ApsSnapshots { get; set; }

    public DbSet<PumpSnapshotEntity> PumpSnapshots { get; set; }

    public DbSet<UploaderSnapshotEntity> UploaderSnapshots { get; set; }

    public DbSet<DeviceStatusExtrasEntity> DeviceStatusExtras { get; set; }

    public DbSet<DeviceEntity> Devices { get; set; }

    public DbSet<TempBasalEntity> TempBasals { get; set; }

    // V4 Profile Decomposition Models

    public DbSet<TherapySettingsEntity> TherapySettings { get; set; }

    public DbSet<BasalScheduleEntity> BasalSchedules { get; set; }

    public DbSet<CarbRatioScheduleEntity> CarbRatioSchedules { get; set; }

    public DbSet<SensitivityScheduleEntity> SensitivitySchedules { get; set; }

    public DbSet<TargetRangeScheduleEntity> TargetRangeSchedules { get; set; }

    // V4 Patient Profile Models

    public DbSet<PatientRecordEntity> PatientRecords { get; set; }

    public DbSet<PatientDeviceEntity> PatientDevices { get; set; }

    public DbSet<PatientInsulinEntity> PatientInsulins { get; set; }

    // Multitenancy entities

    public DbSet<TenantEntity> Tenants { get; set; } = null!;

    public DbSet<TenantMemberEntity> TenantMembers { get; set; } = null!;

    public DbSet<TenantRoleEntity> TenantRoles { get; set; } = null!;

    public DbSet<TenantMemberRoleEntity> TenantMemberRoles { get; set; } = null!;

    // Alert Engine entities

    public DbSet<AlertRuleEntity> AlertRules { get; set; }

    public DbSet<AlertConditionTimerEntity> AlertConditionTimers { get; set; }

    public DbSet<AlertTrackerStateEntity> AlertTrackerState { get; set; }

    public DbSet<AlertExcursionEntity> AlertExcursions { get; set; }

    public DbSet<AlertInstanceEntity> AlertInstances { get; set; }

    public DbSet<AlertDeliveryEntity> AlertDeliveries { get; set; }

    public DbSet<AlertInviteEntity> AlertInvites { get; set; }

    public DbSet<AlertCustomSoundEntity> AlertCustomSounds { get; set; }

    /// <summary>
    /// The flat per-rule delivery channel list, in place of a schedule/escalation-step/step-channel chain.
    /// </summary>
    public DbSet<AlertRuleChannelEntity> AlertRuleChannels { get; set; }

    /// <summary>
    /// One row per tenant: the Do Not Disturb manual toggle, scheduled DND window, and timezone.
    /// </summary>
    public DbSet<TenantAlertSettingsEntity> TenantAlertSettings { get; set; }

    /// <summary>
    /// Scoped Do Not Disturb windows (ADR 0004): independent per-scope mutes with client-supplied ids.
    /// </summary>
    public DbSet<DndWindowEntity> DndWindows { get; set; }

    /// <summary>
    /// Registered app installs (Prelude, Companion) that can be alert-engine actuation targets, with
    /// the capabilities each advertises.
    /// </summary>
    public DbSet<ClientDeviceEntity> ClientDevices { get; set; }

    public DbSet<ChatIdentityDirectoryEntry> ChatIdentityDirectory { get; set; }

    public DbSet<ChatIdentityPendingLinkEntity> ChatIdentityPendingLinks { get; set; }

    public DbSet<SubjectOidcIdentityEntity> SubjectOidcIdentities { get; set; }

    public DbSet<CoachMarkStateEntity> CoachMarkStates { get; set; }

    public DbSet<ReadAccessLogEntity> ReadAccessLog { get; set; }

    public DbSet<TenantAuditConfigEntity> TenantAuditConfig { get; set; }

    public DbSet<TenantDataRetentionConfigEntity> TenantDataRetentionConfig { get; set; }

    public DbSet<TenantDemoConfigEntity> TenantDemoConfigs => Set<TenantDemoConfigEntity>();

    /// <summary>
    /// Configure the database model and relationships
    /// </summary>
    /// <param name="modelBuilder">The model builder to configure</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureIndexes(modelBuilder);

        ConfigureEntities(modelBuilder);

        // The TOTP shared secret is a permanent second factor, so the column holds a Data
        // Protection payload rather than the seed. Configured here rather than in the static
        // ConfigureEntities because the converter closes over a runtime-resolved protector.
        modelBuilder
            .Entity<TotpCredentialEntity>()
            .Property(e => e.SecretKey)
            .HasConversion(
                Security.TotpSecretProtection.CreateConverter(
                    Security.TotpSecretProtection.CreateProtector(ApplicationServices)));

        ConfigureTenantFilters(modelBuilder);

        // Tenant membership is "active" only while not revoked. Enforcing this once here
        // keeps every membership query (auth gates, setup detection, admin listings) from
        // having to repeat `RevokedAt == null`. The matching partial unique index
        // (ix_tenant_members_tenant_subject, filtered on revoked_at IS NULL) lets a revoked
        // membership coexist with a fresh active one, so re-adds remain valid.
        modelBuilder.Entity<TenantMemberEntity>().HasQueryFilter(tm => tm.RevokedAt == null);

        ConfigureTenantCascadeDeletes(modelBuilder);

        // EF Core's default convention emits the C# property name verbatim for the column, which
        // would leave case-sensitive quoted "Id" columns in an otherwise snake_case schema. The
        // generator is a backstop for construction sites that leave Id unset; an Id EF does not
        // generate (one that is also a foreign key) is left alone.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.FindProperty("Id") is not { } idProperty)
            {
                continue;
            }

            if (idProperty.GetColumnName() != "id")
            {
                idProperty.SetColumnName("id");
            }

            if (idProperty.ClrType == typeof(Guid)
                && idProperty.IsPrimaryKey()
                && idProperty.ValueGenerated == ValueGenerated.OnAdd)
            {
                idProperty.SetValueGeneratorFactory((_, _) => new GuidV7ValueGenerator());
            }
        }

        // Postgres normalizes jsonb on write (key order, whitespace), so a jsonb-backed string
        // read back never equals the app's compact serialization byte-for-byte. Compare these
        // columns semantically so an unchanged round-trip is not flagged as a modification.
        // Guarded on relational: the InMemory test provider has no column types (and no jsonb
        // normalization to compensate for).
        if (Database.IsRelational())
        {
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    if (property.ClrType == typeof(string) && property.GetColumnType() == "jsonb")
                    {
                        property.SetValueComparer(JsonbStringComparer.Instance);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The device-status snapshot tables, whose dedup key is the <see cref="ISyncDedupable"/> one
    /// rather than their legacy id.
    /// </summary>
    internal static readonly Type[] V4SnapshotEntities =
    [
        typeof(ApsSnapshotEntity),
        typeof(PumpSnapshotEntity),
        typeof(UploaderSnapshotEntity),
    ];

    /// <summary>
    /// V4 record tables keyed on <see cref="IV4TimeSeriesEntity.Timestamp"/>.
    /// </summary>
    internal static readonly Type[] V4TimeSeriesRecordEntities =
    [
        typeof(SensorGlucoseEntity),
        typeof(MeterGlucoseEntity),
        typeof(CalibrationEntity),
        typeof(BolusEntity),
        typeof(BasalInjectionEntity),
        typeof(CarbIntakeEntity),
        typeof(BGCheckEntity),
        typeof(NoteEntity),
        typeof(DeviceEventEntity),
        typeof(BolusCalculationEntity),
        typeof(TherapySettingsEntity),
        typeof(BasalScheduleEntity),
        typeof(CarbRatioScheduleEntity),
        typeof(SensitivityScheduleEntity),
        typeof(TargetRangeScheduleEntity),
        .. V4SnapshotEntities,
    ];

    /// <summary>
    /// V4 record tables whose legacy id is the dedup key, adding the span-shaped
    /// <see cref="TempBasalEntity"/>. The snapshot tables drop out: their legacy id carries a plain
    /// lookup index instead of the tenant-scoped unique one.
    /// </summary>
    internal static readonly Type[] V4LegacyIdRecordEntities =
        [.. V4TimeSeriesRecordEntities.Except(V4SnapshotEntities), typeof(TempBasalEntity)];

    /// <summary>
    /// Profile-decomposition schedule tables, read as (tenant, profile, newest-first).
    /// </summary>
    internal static readonly Type[] V4ProfileScheduleEntities =
    [
        typeof(BasalScheduleEntity),
        typeof(CarbRatioScheduleEntity),
        typeof(SensitivityScheduleEntity),
        typeof(TargetRangeScheduleEntity),
    ];

    /// <summary>
    /// Tables looked up by profile name, adding <see cref="TherapySettingsEntity"/> — one row per
    /// profile, so it needs the lookup without the composite ordering index.
    /// </summary>
    internal static readonly Type[] V4ProfileNamedEntities =
        [.. V4ProfileScheduleEntities, typeof(TherapySettingsEntity)];

    /// <summary>
    /// Tables carrying the <see cref="ISyncDedupable"/> upsert key. Listed rather than discovered
    /// from the interface, which only three of them implement.
    /// </summary>
    internal static readonly Type[] SyncDedupedEntities =
    [
        typeof(StepCountEntity),
        typeof(HeartRateEntity),
        typeof(BodyWeightEntity),
        typeof(SensorGlucoseEntity),
        typeof(BolusEntity),
        typeof(BasalInjectionEntity),
        typeof(CarbIntakeEntity),
        typeof(TempBasalEntity),
        .. V4SnapshotEntities,
    ];

    /// <summary>
    /// The index shapes the record tables share, where only the table-name stem differs.
    /// </summary>
    private static void ConfigureSharedRecordIndexes(ModelBuilder modelBuilder)
    {
        foreach (var entity in V4TimeSeriesRecordEntities.Select(t => modelBuilder.Entity(t)))
        {
            entity.HasIndex(nameof(IV4TimeSeriesEntity.Timestamp))
                .HasDatabaseName($"ix_{entity.Metadata.GetTableName()}_timestamp")
                .IsDescending();
        }

        // The legacy-id uniqueness must drop soft-deleted rows, or the next resync of a
        // system-swept legacy id is a 23505 — see SoftDeleteDedupExtensions.GetBlockingLegacyIdsAsync.
        foreach (var entity in V4LegacyIdRecordEntities.Select(t => modelBuilder.Entity(t)))
        {
            var table = entity.Metadata.GetTableName();

            entity.HasIndex(nameof(ITenantScoped.TenantId), nameof(IV4Entity.LegacyId))
                .HasDatabaseName($"ix_{table}_tenant_legacy_id")
                .IsUnique()
                .HasFilter("legacy_id IS NOT NULL AND deleted_at IS NULL");

            entity.HasIndex(nameof(IV4Entity.CorrelationId))
                .HasDatabaseName($"ix_{table}_correlation_id");
        }

        foreach (var entity in V4SnapshotEntities.Select(t => modelBuilder.Entity(t)))
        {
            var table = entity.Metadata.GetTableName();

            entity.HasIndex(nameof(IV4Entity.LegacyId))
                .HasDatabaseName($"ix_{table}_legacy_id");

            // The partial sync-id index leads with tenant_id, which makes EF drop the auto-created
            // tenant index as redundant, but a filtered index can't serve general tenant-scoped
            // scans (all pre-existing rows have NULL sync_identifier).
            entity.HasIndex(nameof(ITenantScoped.TenantId))
                .HasDatabaseName($"IX_{table}_tenant_id");
        }

        foreach (var entity in V4ProfileNamedEntities.Select(t => modelBuilder.Entity(t)))
        {
            entity.HasIndex(nameof(BasalScheduleEntity.ProfileName))
                .HasDatabaseName($"ix_{entity.Metadata.GetTableName()}_profile_name");
        }

        foreach (var entity in V4ProfileScheduleEntities.Select(t => modelBuilder.Entity(t)))
        {
            entity.HasIndex(
                    nameof(ITenantScoped.TenantId),
                    nameof(BasalScheduleEntity.ProfileName),
                    nameof(IV4TimeSeriesEntity.Timestamp))
                .HasDatabaseName($"ix_{entity.Metadata.GetTableName()}_tenant_profile_timestamp")
                .IsDescending(false, false, true);
        }

        foreach (var entity in SyncDedupedEntities.Select(t => modelBuilder.Entity(t)))
        {
            entity.HasIndex(
                    nameof(ITenantScoped.TenantId),
                    nameof(ISyncDedupable.DataSource),
                    nameof(ISyncDedupable.SyncIdentifier))
                .HasDatabaseName($"ix_{entity.Metadata.GetTableName()}_tenant_source_sync_id")
                .IsUnique()
                .HasFilter("sync_identifier IS NOT NULL AND deleted_at IS NULL");
        }
    }

    private static void ConfigureIndexes(ModelBuilder modelBuilder)
    {
        // Unique per install within a tenant — the upsert key.
        modelBuilder
            .Entity<ClientDeviceEntity>()
            .HasIndex(e => new { e.TenantId, e.InstallId })
            .HasDatabaseName("ix_client_devices_tenant_install")
            .IsUnique();

        // Fan-out resolution: "all devices of this kind in the tenant".
        modelBuilder
            .Entity<ClientDeviceEntity>()
            .HasIndex(e => new { e.TenantId, e.Kind })
            .HasDatabaseName("ix_client_devices_tenant_kind");

        // Subject-scoped intent delivery and listing a user's own devices.
        modelBuilder
            .Entity<ClientDeviceEntity>()
            .HasIndex(e => new { e.TenantId, e.SubjectId })
            .HasDatabaseName("ix_client_devices_tenant_subject");

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

        modelBuilder
            .Entity<StepCountEntity>()
            .HasIndex(s => s.Timestamp)
            .HasDatabaseName("ix_step_counts_timestamp")
            .IsDescending();

        modelBuilder
            .Entity<StepCountEntity>()
            .HasIndex(s => s.SysCreatedAt)
            .HasDatabaseName("ix_step_counts_sys_created_at");

        // Non-filtered tenant+time index — covers the tenant FK (the filtered sync-id unique
        // index cannot) and serves tenant-scoped range reads.
        modelBuilder
            .Entity<StepCountEntity>()
            .HasIndex(s => new { s.TenantId, s.Timestamp })
            .HasDatabaseName("ix_step_counts_tenant_timestamp");

        // Connector resume watermark: MAX(timestamp) for one data source, every sync cycle. A
        // source with no rows yet would otherwise scan the tenant's whole table.
        modelBuilder
            .Entity<StepCountEntity>()
            .HasIndex(s => new { s.TenantId, s.DataSource, s.Timestamp })
            .HasDatabaseName("ix_step_counts_tenant_source_timestamp")
            .IsDescending(false, false, true);

        modelBuilder
            .Entity<HeartRateEntity>()
            .HasIndex(h => h.Timestamp)
            .HasDatabaseName("ix_heart_rates_timestamp")
            .IsDescending();

        modelBuilder
            .Entity<HeartRateEntity>()
            .HasIndex(h => h.SysCreatedAt)
            .HasDatabaseName("ix_heart_rates_sys_created_at");

        modelBuilder
            .Entity<HeartRateEntity>()
            .HasIndex(h => new { h.TenantId, h.Timestamp })
            .HasDatabaseName("ix_heart_rates_tenant_timestamp");

        // Connector resume watermark, as on step_counts. Heart rate arrives at up to 1 Hz, so this
        // is the largest table an unindexed source filter would scan.
        modelBuilder
            .Entity<HeartRateEntity>()
            .HasIndex(h => new { h.TenantId, h.DataSource, h.Timestamp })
            .HasDatabaseName("ix_heart_rates_tenant_source_timestamp")
            .IsDescending(false, false, true);

        modelBuilder
            .Entity<BodyWeightEntity>()
            .HasIndex(b => b.Mills)
            .HasDatabaseName("ix_body_weights_mills")
            .IsDescending();

        modelBuilder
            .Entity<BodyWeightEntity>()
            .HasIndex(b => b.SysCreatedAt)
            .HasDatabaseName("ix_body_weights_sys_created_at");

        modelBuilder
            .Entity<BodyWeightEntity>()
            .HasIndex(b => new { b.TenantId, b.Mills })
            .HasDatabaseName("ix_body_weights_tenant_mills");

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

        modelBuilder
            .Entity<SubjectEntity>()
            .HasIndex(s => s.Name)
            .HasDatabaseName("ix_subjects_name");

        modelBuilder
            .Entity<SubjectEntity>()
            .HasIndex(s => s.AccessTokenHash)
            .HasDatabaseName("ix_subjects_access_token_hash")
            .IsUnique();

        // Legacy Nightscout digest is prefix-matched (not equality), so this index only
        // narrows the candidate set; it is filtered to the small migrated-subject population.
        modelBuilder
            .Entity<SubjectEntity>()
            .HasIndex(s => s.LegacyTokenDigest)
            .HasDatabaseName("ix_subjects_legacy_token_digest")
            .HasFilter("legacy_token_digest IS NOT NULL");

        modelBuilder
            .Entity<SubjectEntity>()
            .HasIndex(s => s.Email)
            .HasDatabaseName("ix_subjects_email");

        modelBuilder
            .Entity<RoleEntity>()
            .HasIndex(r => r.Name)
            .HasDatabaseName("ix_roles_name")
            .IsUnique();

        modelBuilder
            .Entity<OidcProviderEntity>()
            .HasIndex(o => o.IssuerUrl)
            .HasDatabaseName("ix_oidc_providers_issuer_url")
            .IsUnique();

        modelBuilder
            .Entity<OidcProviderEntity>()
            .HasIndex(o => o.IsEnabled)
            .HasDatabaseName("ix_oidc_providers_is_enabled");

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

        // Managed alert rule synthesised from the threshold: SET NULL on rule deletion so
        // the startup backfill re-synthesises rather than leaving a dangling reference.
        modelBuilder
            .Entity<TrackerNotificationThresholdEntity>()
            .HasOne<AlertRuleEntity>()
            .WithMany()
            .HasForeignKey(t => t.AlertRuleId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder
            .Entity<AlertRuleEntity>()
            .HasIndex(r => r.ManagedBy)
            .HasDatabaseName("ix_alert_rules_managed_by")
            .HasFilter("managed_by IS NOT NULL");

        modelBuilder
            .Entity<TrackerNotificationThresholdEntity>()
            .HasIndex(t => t.TrackerDefinitionId)
            .HasDatabaseName("ix_tracker_notification_thresholds_definition_id");

        modelBuilder
            .Entity<TrackerNotificationThresholdEntity>()
            .HasIndex(t => new { t.TrackerDefinitionId, t.DisplayOrder })
            .HasDatabaseName("ix_tracker_notification_thresholds_def_order");

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

        // Connector resume watermark: MAX(start_timestamp) over the activity categories for one
        // data source. Tenant leads because a source id is the same string installation-wide, so
        // ix_state_spans_source would walk every tenant's spans for that source.
        modelBuilder
            .Entity<StateSpanEntity>()
            .HasIndex(s => new { s.TenantId, s.Source, s.Category, s.StartTimestamp })
            .HasDatabaseName("ix_state_spans_tenant_source_category_start")
            .IsDescending(false, false, false, true);

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

        modelBuilder
            .Entity<SleepSessionEntity>()
            .HasIndex(s => new { s.TenantId, s.Source, s.OriginalId })
            .IsUnique()
            .HasFilter("original_id IS NOT NULL")
            .HasDatabaseName("ux_sleep_sessions_tenant_source_original");

        modelBuilder
            .Entity<SleepSessionEntity>()
            .HasIndex(s => new { s.TenantId, s.StartTime })
            .IsDescending(false, true)
            .HasDatabaseName("ix_sleep_sessions_tenant_start_time");

        modelBuilder
            .Entity<SleepStageEntity>()
            .HasOne(s => s.SleepSession)
            .WithMany(ss => ss.Stages)
            .HasForeignKey(s => s.SleepSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder
            .Entity<SleepBiometricSampleEntity>()
            .HasOne(b => b.SleepSession)
            .WithMany(ss => ss.BiometricSamples)
            .HasForeignKey(b => b.SleepSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder
            .Entity<SleepStageEntity>()
            .HasIndex(s => s.SleepSessionId)
            .HasDatabaseName("ix_sleep_stages_sleep_session_id");

        modelBuilder
            .Entity<SleepBiometricSampleEntity>()
            .HasIndex(b => b.SleepSessionId)
            .HasDatabaseName("ix_sleep_biometric_samples_sleep_session_id");

        // Sources dedupe per tenant: the identifier alone must not be unique, or tenant B
        // migrating from the same URL as tenant A would collide with (and read) A's source row.
        modelBuilder
            .Entity<MigrationSourceEntity>()
            .HasIndex(s => new { s.TenantId, s.SourceIdentifier })
            .HasDatabaseName("ix_migration_sources_tenant_identifier")
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

        modelBuilder
            .Entity<MigrationRunEntity>()
            .HasIndex(r => r.TenantId)
            .HasDatabaseName("ix_migration_runs_tenant");

        modelBuilder
            .Entity<ConnectorResetJobEntity>()
            .HasIndex(j => j.TenantId)
            .HasDatabaseName("ix_connector_reset_jobs_tenant");

        modelBuilder
            .Entity<ConnectorResetJobEntity>()
            .HasIndex(j => j.State)
            .HasDatabaseName("ix_connector_reset_jobs_state");

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

        // Partial index for the NOT EXISTS anti-join in read queries —
        // only non-primary rows enter the index, keeping it small.
        modelBuilder
            .Entity<LinkedRecordEntity>()
            .HasIndex(l => new { l.RecordType, l.RecordId })
            .HasDatabaseName("ix_linked_records_non_primary_record")
            .HasFilter("NOT is_primary");

        modelBuilder
            .Entity<ConnectorConfigurationEntity>()
            .HasIndex(c => new { c.ConnectorName, c.TenantId })
            .HasDatabaseName("ix_connector_configurations_connector_name_tenant")
            .IsUnique();

        modelBuilder.Entity<PlatformSettingsEntity>()
            .HasIndex(ps => ps.Category)
            .HasDatabaseName("ix_platform_settings_category")
            .IsUnique();

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

        modelBuilder
            .Entity<CompressionLowSuggestionEntity>()
            .HasIndex(e => e.NightOf)
            .HasDatabaseName("ix_compression_low_suggestions_night_of");

        modelBuilder
            .Entity<CompressionLowSuggestionEntity>()
            .HasIndex(e => e.Status)
            .HasDatabaseName("ix_compression_low_suggestions_status");

        ConfigureSharedRecordIndexes(modelBuilder);

        modelBuilder
            .Entity<SensorGlucoseEntity>()
            .HasOne<PatientDeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.PatientDeviceId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder
            .Entity<SensorGlucoseEntity>()
            .HasIndex(e => e.PatientDeviceId)
            .HasDatabaseName("ix_sensor_glucose_patient_device_id")
            .HasFilter("patient_device_id IS NOT NULL");

        modelBuilder
            .Entity<SensorGlucoseEntity>()
            .HasIndex(e => new { e.TenantId, e.Timestamp })
            .HasDatabaseName("ix_sensor_glucose_tenant_timestamp")
            .IsDescending(false, true);

        // Keep the conventional TenantId index (see ApsSnapshot note).
        modelBuilder
            .Entity<MeterGlucoseEntity>()
            .HasIndex(e => e.TenantId)
            .HasDatabaseName("IX_meter_glucose_tenant_id");

        // Keep the conventional TenantId index (see ApsSnapshot note).
        modelBuilder
            .Entity<CalibrationEntity>()
            .HasIndex(e => e.TenantId)
            .HasDatabaseName("IX_calibrations_tenant_id");

        modelBuilder
            .Entity<BolusEntity>()
            .HasIndex(e => new { e.TenantId, e.Timestamp })
            .HasDatabaseName("ix_boluses_tenant_timestamp")
            .IsDescending(false, true);

        // TimezoneTimeline: one zone-change boundary per tenant per instant (the ordered list is
        // inherently non-overlapping, so a duplicate effective_from is an authoring error).
        modelBuilder.Entity<TimezoneTimelineEntity>()
            .HasIndex(e => new { e.TenantId, e.EffectiveFrom })
            .HasDatabaseName("ix_timezone_timeline_tenant_effective_from")
            .IsUnique();

        modelBuilder
            .Entity<CarbIntakeEntity>()
            .HasIndex(e => new { e.TenantId, e.Timestamp })
            .HasDatabaseName("ix_carb_intakes_tenant_timestamp")
            .IsDescending(false, true);

        // Latest-of-a-kind lookups (device age, alert enrichment) filter on tenant + event type
        // and take the newest row. Without the event_type column the plan walks the whole
        // tenant's history backwards and never terminates early for a type that was never logged.
        modelBuilder
            .Entity<DeviceEventEntity>()
            .HasIndex(e => new { e.TenantId, e.EventType, e.Timestamp })
            .HasDatabaseName("ix_device_events_tenant_event_type_timestamp")
            .IsDescending(false, false, true);

        modelBuilder
            .Entity<DeviceStatusExtrasEntity>()
            .HasIndex(e => e.CorrelationId)
            .HasDatabaseName("ix_device_status_extras_correlation_id");

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
            .HasIndex(e => new { e.TenantId, e.StartTimestamp })
            .HasDatabaseName("ix_temp_basals_tenant_start_timestamp")
            .IsDescending(false, true);

        // Devices unique index (scoped to live records, per tenant).
        // TenantId must be part of the key: devices are tenant-owned (FindByCategoryTypeAndSerialAsync
        // is RLS-scoped to the current tenant) and the type/serial often carry shared, non-unique
        // values — e.g. a pump's manufacturer/model ("Insulet"/"Omnipod 5") or the generic Loop
        // uploader identity ("iPhone"/"unknown"). Without TenantId the constraint is global, so the
        // first tenant to register such a device permanently blocks every other tenant's insert,
        // surfacing as a 500 on devicestatus ingestion (and a network error in Loop).
        modelBuilder
            .Entity<DeviceEntity>()
            .HasIndex(e => new { e.TenantId, e.Category, e.Type, e.Serial })
            .HasDatabaseName("ix_devices_category_type_serial")
            .IsUnique()
            .HasFilter("deleted_at IS NULL");

        modelBuilder
            .Entity<TherapySettingsEntity>()
            .HasIndex(e => new { e.TenantId, e.Timestamp })
            .HasDatabaseName("ix_therapy_settings_tenant_timestamp")
            .IsDescending(false, true);

        modelBuilder.Entity<TenantEntity>()
            .HasIndex(t => t.Slug)
            .HasDatabaseName("ix_tenants_slug")
            .IsUnique();

        // Unique share token for public dashboard resolution. Postgres allows multiple
        // NULLs in a unique index, so tenants without sharing enabled don't collide.
        modelBuilder.Entity<TenantEntity>()
            .HasIndex(t => t.ShareToken)
            .HasDatabaseName("ix_tenants_share_token")
            .IsUnique();

        modelBuilder.Entity<TenantMemberEntity>()
            .HasIndex(tm => tm.SubjectId)
            .HasDatabaseName("ix_tenant_members_subject_id");

        // PatientRecord: unique constraint — one record per tenant (scoped to live records)
        modelBuilder.Entity<PatientRecordEntity>()
            .HasIndex(e => e.TenantId)
            .HasDatabaseName("ix_patient_records_tenant_id")
            .IsUnique()
            .HasFilter("deleted_at IS NULL");

        // PatientDevice: query by tenant + current status
        modelBuilder.Entity<PatientDeviceEntity>()
            .HasIndex(e => new { e.TenantId, e.IsCurrent })
            .HasDatabaseName("ix_patient_devices_tenant_is_current");

        // PatientInsulin: query by tenant + current status
        modelBuilder.Entity<PatientInsulinEntity>()
            .HasIndex(e => new { e.TenantId, e.IsCurrent })
            .HasDatabaseName("ix_patient_insulins_tenant_is_current");

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

            b.HasIndex(e => new { e.Platform, e.PlatformUserId, e.TenantId })
                .IsUnique()
                .HasDatabaseName("ux_directory_user_tenant");

            // Labels route bot commands, so they must be unambiguous within a platform user's set
            // of links. ChatIdentityDirectoryService.CreateLinkAsync auto-suffixes a colliding
            // label before insert and retries against this index when it loses a race.
            b.HasIndex(e => new { e.Platform, e.PlatformUserId, e.Label })
                .IsUnique()
                .HasDatabaseName("ux_directory_user_label");

            // At most one default per Discord user — partial unique index.
            b.HasIndex(e => new { e.Platform, e.PlatformUserId })
                .IsUnique()
                .HasFilter("is_default = true")
                .HasDatabaseName("ux_directory_user_one_default");

            b.HasIndex(e => e.TenantId).HasDatabaseName("ix_directory_tenant_id");

            // Not tenant-scoped (no RLS — the bot resolves across tenants), but the tenant
            // reference is still a real FK so deleting a tenant takes its directory rows with it.
            // Without it these rows outlive the tenant, and each one holds a chat-platform user id
            // plus the tenant's slug and display name in Label/DisplayName — so a "delete
            // everything" would leave a person's Discord/Telegram id still associated with the
            // instance they had. No navigation property: nothing should traverse tenant -> chat
            // links, this exists purely for the cascade.
            b.HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

            // The directory row is what binds a chat account to a tenant, so it must not outlive
            // the subject it was issued for. Subjects are global, so this only covers deleting the
            // person entirely — losing one tenant's membership is handled where that happens.
            b.HasOne<SubjectEntity>()
                .WithMany()
                .HasForeignKey(e => e.NocturneUserId)
                .OnDelete(DeleteBehavior.Cascade);
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
        modelBuilder
            .Entity<ConnectorFoodEntryEntity>()
            .HasOne(e => e.Food)
            .WithMany()
            .HasForeignKey(e => e.FoodId)
            .OnDelete(DeleteBehavior.SetNull);

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
            .Entity<ApsSnapshotEntity>()
            .HasOne<DeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder
            .Entity<DeviceEventEntity>()
            .HasOne<DeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder
            .Entity<ApsSnapshotEntity>()
            .HasOne<PatientDeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.PatientDeviceId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder
            .Entity<DeviceEventEntity>()
            .HasOne<PatientDeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.PatientDeviceId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder
            .Entity<TempBasalEntity>()
            .HasOne<PatientDeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.PatientDeviceId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder
            .Entity<PumpSnapshotEntity>()
            .HasOne<PatientDeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.PatientDeviceId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder
            .Entity<BolusEntity>()
            .HasOne<PatientDeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.PatientDeviceId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder
            .Entity<BasalInjectionEntity>()
            .HasOne<PatientDeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.PatientDeviceId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder
            .Entity<BasalInjectionEntity>()
            .HasIndex(e => e.PatientDeviceId)
            .HasDatabaseName("ix_basal_injections_patient_device_id")
            .HasFilter("patient_device_id IS NOT NULL");

        modelBuilder
            .Entity<MeterGlucoseEntity>()
            .HasOne<PatientDeviceEntity>()
            .WithMany()
            .HasForeignKey(e => e.PatientDeviceId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder
            .Entity<MeterGlucoseEntity>()
            .HasIndex(e => e.PatientDeviceId)
            .HasDatabaseName("ix_meter_glucose_patient_device_id")
            .HasFilter("patient_device_id IS NOT NULL");

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

        modelBuilder
            .Entity<FoodEntity>()
            .Property(f => f.SysUpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        modelBuilder
            .Entity<ConnectorFoodEntryEntity>()
            .Property(e => e.Status)
            .HasConversion<string>();

        modelBuilder
            .Entity<ConnectorFoodEntryEntity>()
            .Property(e => e.SysUpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        modelBuilder
            .Entity<ConnectorFoodEntryEntity>()
            .Property(e => e.Status)
            .HasDefaultValue(ConnectorFoodEntryStatus.Pending);

        modelBuilder
            .Entity<TreatmentFoodEntity>()
            .Property(tf => tf.SysUpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        modelBuilder
            .Entity<UserFoodFavoriteEntity>()
            .Property(f => f.SysCreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        modelBuilder
            .Entity<SettingsEntity>()
            .Property(s => s.SysUpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        modelBuilder
            .Entity<StepCountEntity>()
            .Property(s => s.SysUpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        modelBuilder
            .Entity<HeartRateEntity>()
            .Property(h => h.SysUpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

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

        modelBuilder.Entity<SettingsEntity>().Property(s => s.IsActive).HasDefaultValue(true);

        modelBuilder.Entity<RefreshTokenEntity>(entity =>
        {
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity
                .HasOne(e => e.Subject)
                .WithMany(s => s.RefreshTokens)
                .HasForeignKey(e => e.SubjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SubjectEntity>(entity =>
        {
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            // Per-user display preferences stored as a JSONB blob (semantic comparison is
            // applied by the relational jsonb-string value-comparer configured above).
            entity.Property(e => e.Preferences).HasColumnType("jsonb");
        });

        modelBuilder.Entity<SubjectAvatarEntity>(entity =>
        {
            entity.ToTable("subject_avatars");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(e => e.SubjectId).IsUnique().HasDatabaseName("ix_subject_avatars_subject_id");
            entity.HasOne(e => e.Subject).WithMany().HasForeignKey(e => e.SubjectId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RoleEntity>(entity =>
        {
            entity.Property(e => e.IsSystemRole).HasDefaultValue(false);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

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

        modelBuilder.Entity<OidcProviderEntity>(entity =>
        {
            entity.Property(e => e.ClaimMappingsJson).HasDefaultValue("{}");
            entity.Property(e => e.IsEnabled).HasDefaultValue(true);
            entity.Property(e => e.DisplayOrder).HasDefaultValue(0);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

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

        modelBuilder.Entity<TenantAuditConfigEntity>(entity =>
        {
            entity.Property(e => e.ReadAuditEnabled).HasDefaultValue(false);
            entity.Property(e => e.SysCreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.SysUpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasIndex(e => e.TenantId)
                .IsUnique()
                .HasDatabaseName("ix_tenant_audit_config_tenant_id");
        });

        modelBuilder.Entity<TenantDataRetentionConfigEntity>(entity =>
        {
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasIndex(e => e.TenantId)
                .IsUnique()
                .HasDatabaseName("ix_tenant_data_retention_config_tenant_id");
        });

        modelBuilder.Entity<LinkedRecordEntity>(entity =>
        {
            entity.Property(e => e.IsPrimary).HasDefaultValue(false);
            entity.Property(e => e.SysCreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        // One row per tenant, keyed on the tenant id rather than an Id of its own.
        modelBuilder.Entity<DedupReconcileStateEntity>(entity =>
        {
            entity.HasKey(e => e.TenantId);
        });

        modelBuilder.Entity<InAppNotificationEntity>(entity =>
        {
            entity.Property(e => e.IsArchived).HasDefaultValue(false);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.Property(e => e.Category).HasConversion<string>();
            entity.Property(e => e.Urgency).HasConversion<string>();
            entity.Property(e => e.ArchiveReason).HasConversion<string>();
        });

        modelBuilder.Entity<OAuthClientEntity>(entity =>
        {
            entity.Property(e => e.RedirectUris).HasDefaultValue("[]");
            entity.Property(e => e.IsKnown).HasDefaultValue(false);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<OAuthGrantEntity>(entity =>
        {
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

        modelBuilder.Entity<OAuthRefreshTokenEntity>(entity =>
        {
            entity.Property(e => e.IssuedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity
                .HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(e => e.TenantId)
                .OnDelete(DeleteBehavior.Cascade);

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

        modelBuilder.Entity<OAuthDeviceCodeEntity>(entity =>
        {
            entity.Property(e => e.Interval).HasDefaultValue(5);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity
                .HasOne(e => e.Grant)
                .WithMany()
                .HasForeignKey(e => e.GrantId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<OAuthAuthorizationCodeEntity>(entity =>
        {
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

        modelBuilder.Entity<MemberInviteEntity>(entity =>
        {

            // The tenant relationship is configured centrally for every ITenantScoped entity by
            // ConfigureTenantCascadeDeletes, which binds the Tenant navigation.
            entity.HasOne(e => e.CreatedBy)
                .WithMany()
                .HasForeignKey(e => e.CreatedBySubjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => e.TenantId);
        });

        modelBuilder.Entity<MembershipRequestEntity>(entity =>
        {

            entity.HasIndex(e => new { e.TenantId, e.SubjectId })
                .HasFilter("status = 'pending'")
                .IsUnique();
        });

        modelBuilder.Entity<ClockFaceEntity>(entity =>
        {
            entity.Property(e => e.ConfigJson).HasDefaultValue("{}");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.SysCreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.SysUpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

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

        modelBuilder.Entity<TenantRoleEntity>(entity =>
        {
            entity.HasIndex(e => new { e.TenantId, e.Slug }).IsUnique();
            entity.Property(e => e.SysCreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.SysUpdatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<TenantMemberRoleEntity>(entity =>
        {
            entity.HasIndex(e => new { e.TenantMemberId, e.TenantRoleId }).IsUnique();
            entity.Property(e => e.SysCreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<AlertRuleEntity>(entity =>
        {
            entity.ToTable("alert_rules");
            entity.Property(e => e.ConditionType).HasConversion(
                new Converters.EnumMemberValueConverter<Core.Models.Alerts.AlertConditionType>());
            entity.Property(e => e.ConditionParams).HasColumnType("jsonb").HasDefaultValue("{}");
            entity.Property(e => e.Severity).HasConversion(
                new Converters.EnumMemberValueConverter<Core.Models.Alerts.AlertRuleSeverity>());
            entity.Property(e => e.ScopeClass).HasConversion(
                new Converters.EnumMemberValueConverter<Core.Models.Alerts.RuleScopeClass>());
            entity.Property(e => e.ClientConfiguration).HasColumnType("jsonb").HasDefaultValue("{}");
            entity.Property(e => e.IsEnabled).HasDefaultValue(true);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<ClientDeviceEntity>(entity =>
        {
            entity.ToTable("client_devices");
            entity.Property(e => e.Capabilities).HasColumnType("text[]");
            entity.Property(e => e.LastSeenAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            // Revoke-cascade: removing the OAuth grant removes the device. The FK is nullable and
            // unpopulated until the device-management flow resolves the grant, so existing rows are
            // unaffected.
            entity.HasOne<OAuthGrantEntity>()
                .WithMany()
                .HasForeignKey(e => e.GrantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AlertConditionTimerEntity>(entity =>
        {
            entity.ToTable("alert_condition_timers");
            entity.HasKey(e => new { e.AlertRuleId, e.ConditionPath });

            entity.HasOne(e => e.AlertRule)
                .WithMany()
                .HasForeignKey(e => e.AlertRuleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

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

        modelBuilder.Entity<AlertExcursionEntity>(entity =>
        {
            entity.ToTable("alert_excursions");

            entity.HasOne(e => e.AlertRule)
                .WithMany()
                .HasForeignKey(e => e.AlertRuleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AlertInstanceEntity>(entity =>
        {
            entity.ToTable("alert_instances");
            entity.Property(e => e.Status).HasDefaultValue("triggered");

            entity.HasOne(e => e.AlertExcursion)
                .WithMany(ex => ex.Instances)
                .HasForeignKey(e => e.AlertExcursionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AlertDeliveryEntity>(entity =>
        {
            entity.ToTable("alert_deliveries");
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

            entity.HasOne(e => e.AlertRuleChannel)
                .WithMany()
                .HasForeignKey(e => e.AlertRuleChannelId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AlertInviteEntity>(entity =>
        {
            entity.ToTable("alert_invites");
            entity.Property(e => e.PermissionScope).HasDefaultValue("view_acknowledge");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(e => e.AlertRuleChannel)
                .WithMany()
                .HasForeignKey(e => e.AlertRuleChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AlertCustomSoundEntity>(entity =>
        {
            entity.ToTable("alert_custom_sounds");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<AlertRuleChannelEntity>(entity =>
        {
            entity.ToTable("alert_rule_channels");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.ChannelType).HasConversion(
                new Converters.EnumMemberValueConverter<Core.Models.Alerts.ChannelType>());

            entity.HasOne(e => e.AlertRule)
                .WithMany(r => r.Channels)
                .HasForeignKey(e => e.AlertRuleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TenantAlertSettingsEntity>(entity =>
        {
            entity.ToTable("tenant_alert_settings");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            // Unique on TenantId enforces the one-row-per-tenant invariant. Named explicitly
            // so it isn't merged with the FK-driven auto-index on tenant_id.
            entity.HasIndex(e => e.TenantId)
                .IsUnique()
                .HasDatabaseName("IX_tenant_alert_settings_tenant_id_unique");
        });

        modelBuilder.Entity<DndWindowEntity>(entity =>
        {
            entity.ToTable("dnd_windows");
            // Id is client-supplied so an offline-authored window re-syncs idempotently; the
            // conventional v7 default only applies if a caller omits one.
            entity.Property(e => e.Scope).HasConversion(
                new Converters.EnumMemberValueConverter<Core.Models.Alerts.DndScope>());
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            // Scope-keyed lookups for the gate/supersede only ever read uncleared windows,
            // so a partial index over active windows (WHERE cleared_at IS NULL) keeps the
            // cleared/expired audit history out of the hot path (ADR 0004 D5).
            entity.HasIndex(e => new { e.TenantId, e.Scope })
                .HasFilter("cleared_at IS NULL");
        });

        modelBuilder.Entity<PasskeyCredentialEntity>(entity =>
        {
            entity.HasIndex(e => e.CredentialId).IsUnique();
            entity.HasOne(e => e.Subject).WithMany(s => s.PasskeyCredentials).HasForeignKey(e => e.SubjectId);
        });

        modelBuilder.Entity<RecoveryCodeEntity>(entity =>
        {
            entity.HasIndex(e => e.SubjectId);
            entity.HasOne(e => e.Subject).WithMany().HasForeignKey(e => e.SubjectId);
        });

        modelBuilder.Entity<TotpStepUpTokenEntity>(entity =>
        {
            entity.HasIndex(e => e.SubjectId);
            // The cleanup sweep deletes by expiry.
            entity.HasIndex(e => e.ExpiresAt);
            entity.HasOne(e => e.Subject).WithMany().HasForeignKey(e => e.SubjectId);
        });

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
    /// Update system tracking timestamps before saving, and enforce tenant ownership.
    /// </summary>
    private void UpdateTimestamps()
    {
        var utcNow = DateTime.UtcNow;

        foreach (var entry in ChangeTracker.Entries())
        {
            var isAdded = entry.State == EntityState.Added;

            EnforceTenantOwnership(entry, isAdded);

            // Update timestamps are stamped on insert and on real modifications only. An
            // unchanged tracked row is left alone rather than rewritten on every save, and a row
            // whose only modified properties are these bookkeeping timestamps (a deliberate
            // "touch") keeps the value the caller assigned.
            var stampUpdated = isAdded || HasNonTimestampModification(entry);

            // System tracking columns (sys_created_at / sys_updated_at) on tenant data.
            if (isAdded && entry.Entity is ISystemCreated systemCreated)
            {
                systemCreated.SysCreatedAt = utcNow;
            }
            if (stampUpdated && entry.Entity is ISystemTimestamped systemTimestamped)
            {
                systemTimestamped.SysUpdatedAt = utcNow;
            }

            // Auth/identity tables use the created_at / updated_at convention instead.
            if (isAdded && entry.Entity is IEntityCreated entityCreated)
            {
                entityCreated.CreatedAt = utcNow;
            }
            if (stampUpdated && entry.Entity is IEntityTimestamped entityTimestamped)
            {
                entityTimestamped.UpdatedAt = utcNow;
            }

            ApplyEntitySpecificTimestamps(entry.Entity, isAdded, stampUpdated, utcNow);
        }
    }

    /// <summary>
    /// True if the entry has a modified property other than the update-timestamp bookkeeping
    /// columns managed by <see cref="UpdateTimestamps"/>.
    /// </summary>
    private static bool HasNonTimestampModification(EntityEntry entry)
        => entry.State == EntityState.Modified
            && entry.Properties.Any(p =>
                p.IsModified
                && p.Metadata.Name != nameof(ISystemTimestamped.SysUpdatedAt)
                && p.Metadata.Name != nameof(IEntityTimestamped.UpdatedAt));

    /// <summary>
    /// Enforces tenant ownership on a tracked entity: stamps the resolved tenant on new
    /// rows and blocks cross-tenant modifications.
    /// </summary>
    private void EnforceTenantOwnership(EntityEntry entry, bool isAdded)
    {
        if (entry.Entity is not ITenantScoped tenantScoped)
        {
            return;
        }

        if (isAdded)
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
        else if (entry.State == EntityState.Modified
            && TenantId != Guid.Empty
            && tenantScoped.TenantId != TenantId)
        {
            throw new InvalidOperationException(
                $"Cannot modify {entry.Entity.GetType().Name} belonging to tenant " +
                $"{tenantScoped.TenantId} from tenant context {TenantId}.");
        }
    }

    /// <summary>
    /// Applies timestamps for the few entities whose columns do not follow either the
    /// sys_* or created_at/updated_at conventions covered by the marker interfaces.
    /// </summary>
    private static void ApplyEntitySpecificTimestamps(object entity, bool isAdded, bool stampUpdated, DateTime utcNow)
    {
        switch (entity)
        {
            // Nullable updated_at, set alongside its ISystemTimestamped stamps.
            case ClockFaceEntity clockFace when stampUpdated:
                clockFace.UpdatedAt = utcNow;
                break;
            // Mirror of sys_created_at on a DateTimeOffset column, set on insert only.
            case ConnectorConfigurationEntity connectorConfig when isAdded:
                connectorConfig.LastModified = utcNow;
                break;
            // Creation timestamp stored as issued_at, set on insert only.
            case OAuthRefreshTokenEntity oauthRefreshToken when isAdded:
                oauthRefreshToken.IssuedAt = utcNow;
                break;
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

                // Records whether the latest soft-delete was user-initiated. The soft-delete
                // dedup discriminator (SoftDeleteDedupExtensions) blocks connector resync from
                // re-creating a user-deleted row, while a system-sweep delete stays re-creatable.
                // A shadow property so it lands on every soft-deletable table without a per-entity edit.
                modelBuilder.Entity(entityType.ClrType)
                    .Property<bool>("DeletedByUser")
                    .HasColumnName("deleted_by_user")
                    .HasDefaultValue(false);
            }

            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(Expression.Lambda(body, parameter));
        }
    }

    /// <summary>
    /// Adds FK constraints with ON DELETE CASCADE from every ITenantScoped entity's
    /// TenantId column to the tenants table. This ensures tenant deletion cascades to
    /// all tenant-scoped data instead of silently orphaning rows.
    /// An entity that also exposes a <c>Tenant</c> reference has that navigation bound to this
    /// relationship; configured without it, EF treats the navigation as a second relationship and
    /// gives it a shadow foreign key of its own.
    /// </summary>
    private static void ConfigureTenantCascadeDeletes(ModelBuilder modelBuilder)
    {
        // The conventional name of the tenant reference on the entities that expose one.
        const string TenantNavigation = "Tenant";

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType))
                continue;

            var navigation = entityType.ClrType.GetProperty(TenantNavigation)?.PropertyType == typeof(TenantEntity)
                ? TenantNavigation
                : null;

            modelBuilder.Entity(entityType.ClrType)
                .HasOne(typeof(TenantEntity), navigation)
                .WithMany()
                .HasForeignKey(nameof(ITenantScoped.TenantId))
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}

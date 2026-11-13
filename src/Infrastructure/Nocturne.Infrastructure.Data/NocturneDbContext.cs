using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.ValueGenerators;
using Nocturne.Infrastructure.Data.Logging;

namespace Nocturne.Infrastructure.Data;

/// <summary>
/// Entity Framework DbContext for PostgreSQL database operations
/// Multitenant architecture with per-tenant global query filters
/// </summary>
public class NocturneDbContext : DbContext, IDataProtectionKeyContext
{
    private readonly DbContextOptions<NocturneDbContext> _options;

    /// <summary>
    /// Key of the global query filter restricting every <see cref="ITenantScoped"/> entity to
    /// <see cref="TenantId"/>.
    /// </summary>
    public const string TenantFilterKey = "tenant_isolation";

    /// <summary>
    /// Key of the global query filter hiding soft-deleted rows of every <see cref="ISoftDeletable"/>
    /// entity. Named separately from <see cref="TenantFilterKey"/> so a purge can lift it alone —
    /// see <see cref="Extensions.PurgeExtensions"/>.
    /// </summary>
    public const string SoftDeleteFilterKey = "soft_delete";

    /// <summary>
    /// Name of the shadow property flagging a user-initiated soft delete.
    /// </summary>
    public const string DeletedByUserProperty = "DeletedByUser";

    /// <summary>
    /// The most rows of one entity type a single save will stamp with the same
    /// <see cref="ISystemTimestamped.SysUpdatedAt"/> millisecond. A bulk import of one type wider
    /// than this spreads onto successive milliseconds, so one tie group can never drag a
    /// <see cref="HistoryPage"/> page past roughly twice its limit.
    /// </summary>
    public const int SystemTimestampGroupSize = 1000;

    /// <summary>
    /// The current UTC time truncated to the microsecond a PostgreSQL <c>timestamptz</c> stores, so
    /// that a stamp held in memory equals the one read back from its row.
    /// </summary>
    public static DateTime UtcNowAtStoredPrecision()
    {
        var now = DateTime.UtcNow;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
    }

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

    private Guid? _tenantPin;

    /// <summary>
    /// The scope this context reads its tenant, share, clamp and audit state from. Left unset, the
    /// context reads <see cref="TenantReadContext.FailClosed"/>.
    /// </summary>
    internal TenantReadContext ReadContext { get; set; } = TenantReadContext.FailClosed;

    /// <summary>
    /// The tenant whose rows this context reaches: a pin when one is set, otherwise the scope's
    /// resolved tenant, read live. The tenant query filter and the <c>app.current_tenant_id</c> GUC
    /// both read it. Only <see cref="INocturneDbContextFactory"/> and
    /// <see cref="Extensions.RlsPinningExtensions.PinTenantAsync"/> pin it.
    /// </summary>
    public Guid TenantId
    {
        get => _tenantPin ?? ReadContext.TenantId;
        internal set => _tenantPin = value;
    }

    /// <summary>
    /// <see cref="TenantId"/> as an optional, for the non-tenant-scoped columns that record which
    /// tenant an action targeted and must stay null rather than empty on an unpinned context.
    /// </summary>
    public Guid? TenantIdOrNull => TenantId == Guid.Empty ? null : TenantId;

    /// <summary>
    /// The subject whose own rows a subject-scoped cross-tenant read may reach (the tenant switcher,
    /// the caregiver overview, membership enumeration), carried to the <c>app.current_subject_id</c>
    /// GUC. <see cref="Guid.Empty"/> writes it empty, which the <c>subject_pin</c> policy reads as unset,
    /// admitting every row.
    /// </summary>
    public Guid SubjectId { get; internal set; }

    /// <summary>
    /// The audit actor <see cref="Interceptors.MutationAuditInterceptor"/> attributes this context's
    /// writes to: the scope's <see cref="IAuditContext"/>, or none for a context with no scope.
    /// </summary>
    public IAuditContext? AuditContext => ReadContext.AuditContext;

    /// <summary>The scope's <see cref="TenantReadContext.IsShare"/>, carried to <c>app.is_share</c>.</summary>
    public bool IsShareContext => ReadContext.IsShare;

    /// <summary>
    /// The scope's <see cref="TenantReadContext.VisibleCategories"/>, carried to
    /// <c>app.visible_categories</c>.
    /// </summary>
    public string? VisibleCategories => ReadContext.VisibleCategories;

    /// <summary>
    /// The scope's <see cref="TenantReadContext.ShareFullHistory"/>, carried to
    /// <c>app.share_full_history</c>.
    /// </summary>
    public bool ShareFullHistory => ReadContext.ShareFullHistory;

    /// <summary>
    /// The scope's <see cref="TenantReadContext.HistoryClamped"/>, carried to <c>app.history_clamped</c>.
    /// </summary>
    public bool HistoryClamped => ReadContext.HistoryClamped;

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

    public DbSet<JobLedgerEntity> JobLedger { get; set; }

    public DbSet<LinkedRecordEntity> LinkedRecords { get; set; }

    public DbSet<DedupReconcileStateEntity> DedupReconcileState { get; set; }

    // Connector Configuration entities

    public DbSet<ConnectorConfigurationEntity> ConnectorConfigurations { get; set; }

    /// <summary>
    /// Instance-wide platform configuration (not tenant-scoped): encrypted bot-platform credentials
    /// (Discord, Slack, Telegram, WhatsApp) and platform-level config.
    /// </summary>
    public DbSet<PlatformSettingsEntity> PlatformSettings { get; set; }

    /// <summary>
    /// One-time notices sent to tenant owners (not tenant-scoped), one per kind, version and owner.
    /// </summary>
    public DbSet<OwnerNoticeEntity> OwnerNotices { get; set; }

    public DbSet<InstanceKeyCheckEntity> InstanceKeyChecks { get; set; }

    // In-App Notification entities

    public DbSet<InAppNotificationEntity> InAppNotifications { get; set; }

    public DbSet<ClockFaceEntity> ClockFaces { get; set; }

    // OAuth 2.0 entities

    public DbSet<OAuthClientEntity> OAuthClients { get; set; }

    public DbSet<OAuthGrantEntity> OAuthGrants { get; set; }

    public DbSet<OAuthRefreshTokenEntity> OAuthRefreshTokens { get; set; }

    public DbSet<OAuthDeviceCodeEntity> OAuthDeviceCodes { get; set; }

    public DbSet<OAuthAuthorizationCodeEntity> OAuthAuthorizationCodes { get; set; }

    public DbSet<LoginCodeEntity> LoginCodes { get; set; } = null!;

    public DbSet<MemberInviteEntity> MemberInvites { get; set; } = null!;

    public DbSet<TenantCreationInviteEntity> TenantCreationInvites { get; set; } = null!;

    public DbSet<TenantCreationInviteUseEntity> TenantCreationInviteUses { get; set; } = null!;

    public DbSet<PlatformPolicyEntity> PlatformPolicy { get; set; } = null!;

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

    /// <summary>
    /// Device-clock offset evidence gathered by connectors — stored separately from
    /// <see cref="TimezoneTimeline"/> so derived knowledge never clobbers a user assertion.
    /// </summary>
    public DbSet<DeviceClockObservationEntity> DeviceClockObservations { get; set; }

    /// <summary>Submitted records answered but not stored; see <see cref="RejectedUploadEntity"/>.</summary>
    public DbSet<RejectedUploadEntity> RejectedUploads { get; set; }

    public DbSet<CalibrationEntity> Calibrations { get; set; }

    public DbSet<BolusEntity> Boluses { get; set; }

    /// <summary>
    /// Discrete long-acting basal insulin injection records (MDI; v4 granular model).
    /// </summary>
    public DbSet<BasalInjectionEntity> BasalInjections { get; set; }

    public DbSet<CarbIntakeEntity> CarbIntakes { get; set; }

    public DbSet<BGCheckEntity> BGChecks { get; set; }

    public DbSet<NoteEntity> Notes { get; set; }

    /// <summary>Manually-entered lab HbA1c results, compared against the computed eHbA1c estimate.</summary>
    public DbSet<LabHbA1cResultEntity> LabHbA1cResults { get; set; }

    public DbSet<DeviceEventEntity> DeviceEvents { get; set; }

    public DbSet<BolusCalculationEntity> BolusCalculations { get; set; }

    public DbSet<ApsSnapshotEntity> ApsSnapshots { get; set; }

    public DbSet<PumpSnapshotEntity> PumpSnapshots { get; set; }

    public DbSet<UploaderSnapshotEntity> UploaderSnapshots { get; set; }

    public DbSet<DeviceStatusExtrasEntity> DeviceStatusExtras { get; set; }

    public DbSet<DeviceEntity> Devices { get; set; }

    public DbSet<TempBasalEntity> TempBasals { get; set; }

    public DbSet<ProfileActivationEntity> ProfileActivations { get; set; }

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

    public DbSet<AlertExcursionMuteEntity> AlertExcursionMutes { get; set; }

    public DbSet<AlertExcursionReceiptEntity> AlertExcursionReceipts { get; set; }

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

    public DbSet<SetupHubItemEntity> SetupHubItems { get; set; }

    public DbSet<SetupHubAdditionEntity> SetupHubAdditions { get; set; }

    public DbSet<TranslationDraftEntity> TranslationDrafts { get; set; }

    public DbSet<ReadAccessLogEntity> ReadAccessLog { get; set; }

    public DbSet<TenantAuditConfigEntity> TenantAuditConfig { get; set; }

    public DbSet<TenantDataRetentionConfigEntity> TenantDataRetentionConfig { get; set; }

    public DbSet<TenantDemoConfigEntity> TenantDemoConfigs => Set<TenantDemoConfigEntity>();

    public DbSet<GoogleHealthGrantHolderEntity> GoogleHealthGrantHolders => Set<GoogleHealthGrantHolderEntity>();

    /// <summary>
    /// Configure the database model and relationships
    /// </summary>
    /// <param name="modelBuilder">The model builder to configure</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NocturneDbContext).Assembly);

        ModelConventions.ConfigureSharedRecordIndexes(modelBuilder);

        ModelConventions.ConfigureDuplicateFlags(modelBuilder);

        ModelConventions.ConfigureCurrentTimestampDefaults(modelBuilder);

        // The TOTP shared secret is a permanent second factor, so the column holds a Data
        // Protection payload rather than the seed. Configured here rather than in its entity
        // configuration because the converter closes over a runtime-resolved protector.
        modelBuilder
            .Entity<TotpCredentialEntity>()
            .Property(e => e.SecretKey)
            .HasConversion(
                Security.TotpSecretProtection.CreateConverter(
                    Security.TotpSecretProtection.CreateProtector(ApplicationServices)));

        ConfigureTenantFilters(modelBuilder);

        ModelConventions.ConfigureTenantCascadeDeletes(modelBuilder);

        Jsonb.Register(modelBuilder);

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

    /// <summary>The entity types the context declares a <see cref="DbSet{TEntity}"/> for.</summary>
    private static readonly Type[] MappedEntityTypes =
    [
        .. typeof(NocturneDbContext).GetProperties()
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(p => p.PropertyType.GetGenericArguments()[0]),
    ];

    private static Type[] Implementing<TMarker>() => [.. MappedEntityTypes.Where(typeof(TMarker).IsAssignableFrom)];

    /// <summary>
    /// The device-status snapshot tables, upserted on the <see cref="ISyncDedupable"/> key.
    /// </summary>
    internal static readonly Type[] V4SnapshotEntities =
        [.. V4RecordTables.OfKind(V4RecordKind.DeviceStatus).Select(t => t.Entity)];

    /// <summary>
    /// The <see cref="IV4Entity"/> tables, whose legacy id is an insert-only dedup key. The snapshots
    /// belong here too: their sync-identifier uniqueness is filtered on a non-null identifier, which a
    /// legacy import never carries.
    /// </summary>
    internal static readonly Type[] V4LegacyIdRecordEntities = Implementing<IV4Entity>();

    /// <summary>
    /// V4 record tables keyed on <see cref="IObservationTimestamped.Timestamp"/>: the
    /// <see cref="V4LegacyIdRecordEntities"/> but the span-shaped <see cref="TempBasalEntity"/> and
    /// <see cref="ProfileActivationEntity"/>, whose span-start indexes are named for the start.
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
    /// The tables a connector watermarks on their newest timestamp, adding the span-shaped
    /// <see cref="TempBasalEntity"/>, whose <see cref="IObservationTimestamped.Timestamp"/> is the
    /// span start.
    /// </summary>
    internal static readonly Type[] V4WatermarkedEntities =
        [.. V4TimeSeriesRecordEntities, typeof(TempBasalEntity)];

    /// <summary>
    /// V4 record tables whose lists read one tenant newest-first by timestamp, then id on the tables
    /// that tie-break on it. The tables deduplication links are listed apart, in
    /// <see cref="V4VisibleSortedListEntities"/>.
    /// </summary>
    internal static readonly Type[] V4SortedListEntities =
    [
        typeof(MeterGlucoseEntity),
        typeof(CalibrationEntity),
        .. V4SnapshotEntities,
    ];

    /// <summary>
    /// The <see cref="IDuplicateFlagged"/> <see cref="IV4TimeSeriesEntity"/> tables, whose lists read
    /// the same order as <see cref="V4SortedListEntities"/> over the rows
    /// <see cref="Extensions.ReadVisibilityFilter"/> shows. <see cref="TempBasalEntity"/>'s timestamp is
    /// its span start.
    /// </summary>
    internal static readonly Type[] V4VisibleSortedListEntities =
        [.. Implementing<IDuplicateFlagged>().Where(typeof(IV4TimeSeriesEntity).IsAssignableFrom)];

    /// <summary>
    /// The <see cref="ILegacyIdentified"/> tables, each with the unique legacy-id index and its
    /// user-tombstone companion: <see cref="V4LegacyIdRecordEntities"/> and
    /// <see cref="StateSpanEntity"/>, which carries no decomposition correlation.
    /// </summary>
    internal static readonly Type[] LegacyIdKeyedEntities = Implementing<ILegacyIdentified>();

    /// <summary>
    /// Tables looked up by the decomposition correlation, adding
    /// <see cref="DeviceStatusExtrasEntity"/> — which carries the correlation but no legacy id
    /// (see its <see cref="DeviceStatusExtrasEntity.CorrelationId"/>), so it cannot ride the
    /// <see cref="V4LegacyIdRecordEntities"/> list.
    /// </summary>
    internal static readonly Type[] V4CorrelationIndexedEntities =
        [.. V4LegacyIdRecordEntities, typeof(DeviceStatusExtrasEntity)];

    /// <summary>
    /// Tables a v3 history endpoint pages through <see cref="HistoryPage"/> on <c>sys_updated_at</c>:
    /// the V4 record tables the legacy treatment projection reads (its state
    /// spans page on <c>ix_state_spans_tenant_category_updated_at</c>), the four tables a device
    /// status is decomposed into, the three glucose types the entry projection reads and the five
    /// profile-decomposition tables.
    /// </summary>
    internal static readonly Type[] V4HistoryPagedEntities =
    [
        typeof(BolusEntity),
        typeof(CarbIntakeEntity),
        typeof(BGCheckEntity),
        typeof(NoteEntity),
        typeof(DeviceEventEntity),
        typeof(TempBasalEntity),
        typeof(ProfileActivationEntity),
        typeof(BolusCalculationEntity),
        typeof(ApsSnapshotEntity),
        typeof(PumpSnapshotEntity),
        typeof(UploaderSnapshotEntity),
        typeof(DeviceStatusExtrasEntity),
        typeof(SensorGlucoseEntity),
        typeof(MeterGlucoseEntity),
        typeof(CalibrationEntity),
        typeof(TherapySettingsEntity),
        typeof(BasalScheduleEntity),
        typeof(CarbRatioScheduleEntity),
        typeof(SensitivityScheduleEntity),
        typeof(TargetRangeScheduleEntity),
    ];

    /// <summary>
    /// <c>SET NULL</c> foreign keys on the high-volume record tables that most rows leave empty.
    /// The index only serves the referential action when a device, bolus calculation or APS
    /// snapshot is deleted, which never looks for a null, so it is filtered to the non-null rows.
    /// On production the columns are 67-100% null.
    /// </summary>
    internal static readonly (Type Entity, string Property)[] SparseForeignKeys =
    [
        (typeof(ApsSnapshotEntity), nameof(ApsSnapshotEntity.DeviceId)),
        (typeof(ApsSnapshotEntity), nameof(ApsSnapshotEntity.PatientDeviceId)),
        (typeof(PumpSnapshotEntity), nameof(PumpSnapshotEntity.DeviceId)),
        (typeof(PumpSnapshotEntity), nameof(PumpSnapshotEntity.PatientDeviceId)),
        (typeof(UploaderSnapshotEntity), nameof(UploaderSnapshotEntity.DeviceId)),
        (typeof(TempBasalEntity), nameof(TempBasalEntity.DeviceId)),
        (typeof(TempBasalEntity), nameof(TempBasalEntity.PatientDeviceId)),
        (typeof(TempBasalEntity), nameof(TempBasalEntity.ApsSnapshotId)),
        (typeof(BolusEntity), nameof(BolusEntity.DeviceId)),
        (typeof(BolusEntity), nameof(BolusEntity.PatientDeviceId)),
        (typeof(BolusEntity), nameof(BolusEntity.ApsSnapshotId)),
        (typeof(BolusEntity), nameof(BolusEntity.BolusCalculationId)),
    ];

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
    /// The <see cref="IProfileScopedEntity"/> tables, looked up by profile name. Beside
    /// <see cref="V4ProfileScheduleEntities"/> that is <see cref="TherapySettingsEntity"/>: one row per
    /// profile, so it needs the lookup without the composite ordering index.
    /// </summary>
    internal static readonly Type[] V4ProfileNamedEntities = Implementing<IProfileScopedEntity>();

    /// <summary>
    /// The <see cref="ISyncDedupable"/> tables, each with the partial unique upsert-key index, so
    /// declaring the interface on a table is a migration. Every entity a
    /// <see cref="Repositories.V4.SyncKeyedRepositoryBase{TModel,TEntity}"/> serves must be here, so
    /// its keyed delete names at most one live row. A table carrying the two columns without the
    /// interface gets no such index.
    /// </summary>
    internal static readonly Type[] SyncDedupedEntities = Implementing<ISyncDedupable>();

    /// <summary>
    /// The marker-declared timestamp columns, by the marker that declares them. Every column here
    /// gets a <c>CURRENT_TIMESTAMP</c> default, so a write that bypasses <see cref="UpdateTimestamps"/>
    /// — raw SQL, <c>ExecuteUpdate</c>-style bulk paths, a manual fix in psql — still lands a real
    /// time rather than <c>0001-01-01</c> in a non-nullable column.
    /// </summary>
    internal static readonly (Type Marker, string Property)[] MarkerTimestampColumns =
    [
        (typeof(ISystemCreated), nameof(ISystemCreated.SysCreatedAt)),
        (typeof(ISystemTimestamped), nameof(ISystemTimestamped.SysUpdatedAt)),
        (typeof(IEntityCreated), nameof(IEntityCreated.CreatedAt)),
        (typeof(IEntityTimestamped), nameof(IEntityTimestamped.UpdatedAt)),
    ];

    /// <summary>
    /// The timestamp columns outside <see cref="MarkerTimestampColumns"/> whose database default is
    /// also <c>CURRENT_TIMESTAMP</c>, grouped by the column the default lands on: tables that carry
    /// the default without declaring the marker for it, and three off-convention column names that
    /// each govern a single table. Adding a table here is a migration.
    /// </summary>
    internal static readonly (string Property, Type[] Entities)[] CurrentTimestampDefaults =
    [
        (nameof(IEntityCreated.CreatedAt),
        [
            typeof(AlertCustomSoundEntity),
            typeof(AlertDeliveryEntity),
            typeof(AlertInviteEntity),
            typeof(AlertRuleChannelEntity),
            typeof(AlertRuleEntity),
            typeof(ClientDeviceEntity),
            typeof(DndWindowEntity),
            typeof(InAppNotificationEntity),
            typeof(MutationAuditLogEntity),
            typeof(ReadAccessLogEntity),
            typeof(SubjectAvatarEntity),
            typeof(TenantAlertSettingsEntity),
            typeof(TenantDataRetentionConfigEntity),
        ]),
        (nameof(IEntityTimestamped.UpdatedAt),
        [
            typeof(AlertRuleEntity),
            typeof(AlertTrackerStateEntity),
            typeof(ClientDeviceEntity),
            typeof(ClockFaceEntity),
            typeof(TenantAlertSettingsEntity),
            typeof(TenantDataRetentionConfigEntity),
        ]),
        (nameof(SubjectRoleEntity.AssignedAt), [typeof(SubjectRoleEntity)]),
        (nameof(OAuthRefreshTokenEntity.IssuedAt), [typeof(OAuthRefreshTokenEntity)]),
        (nameof(ClientDeviceEntity.LastSeenAt), [typeof(ClientDeviceEntity)]),
    ];

    /// <summary>
    /// Keys <paramref name="entity"/> on <c>(TenantId, Id)</c>. The writer derives these ids from the
    /// client's record id, and two tenants fed from one upstream send the same one. Every lookup by id
    /// is tenant-filtered, so a global key would reject the second tenant's row without either tenant
    /// seeing why. EF generates no value for a member of a composite key by convention, so <c>Id</c>
    /// is marked generated to keep the UUID v7 backstop in <see cref="OnModelCreating"/>, and
    /// <c>TenantId</c> takes <see cref="ResolvedTenantValueGenerator"/>.
    /// </summary>
    internal static void ScopeKeyToTenant<TEntity>(EntityTypeBuilder<TEntity> entity)
        where TEntity : class, ITenantScoped
    {
        entity.HasKey(nameof(ITenantScoped.TenantId), "Id");
        entity.Property<Guid>(nameof(ITenantScoped.TenantId)).HasValueGenerator<ResolvedTenantValueGenerator>();
        entity.Property<Guid>("Id").ValueGeneratedOnAdd();
    }

    /// <summary>
    /// Saves all changes made in this context to the database
    /// </summary>
    /// <returns>The number of state entries written to the database</returns>
    public override int SaveChanges()
    {
        var autoDetectChanges = DetectChangesOnceForSave();
        var opened = false;
        try
        {
            if (MustOpenBeforeStamping())
            {
                Database.OpenConnection();
                opened = true;
            }

            UpdateTimestamps();
            return base.SaveChanges();
        }
        finally
        {
            ChangeTracker.AutoDetectChangesEnabled = autoDetectChanges;
            if (opened)
                Database.CloseConnection();
        }
    }

    /// <summary>
    /// Asynchronously saves all changes made in this context to the database
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete</param>
    /// <returns>A task that represents the asynchronous save operation. The task result contains the number of state entries written to the database</returns>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var autoDetectChanges = DetectChangesOnceForSave();
        var opened = false;
        try
        {
            if (MustOpenBeforeStamping())
            {
                await Database.OpenConnectionAsync(cancellationToken);
                opened = true;
            }

            UpdateTimestamps();
            return HistoryClamped && InsertsOnly()
                ? await this.WriteWithHistoryClampLiftedAsync(token => base.SaveChangesAsync(token), cancellationToken)
                : await base.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            DetachPendingAuditRows();
            throw;
        }
        finally
        {
            ChangeTracker.AutoDetectChangesEnabled = autoDetectChanges;
            if (opened)
                await Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Whether the save only inserts, which <see cref="SaveChangesAsync(CancellationToken)"/> runs with
    /// the member history clamp lifted (<see cref="HistoryClampLiftExtensions"/>): an insert reaches no
    /// existing row, so the lift only lets a clamped member record a row dated before the window. A
    /// save that also updates or deletes keeps the clamp, since an entity attached by key rather than
    /// read could name a row the clamp hides.
    /// </summary>
    private bool InsertsOnly()
    {
        var added = false;
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                return false;
            added |= entry.State == EntityState.Added;
        }

        return added;
    }

    /// <summary>
    /// Whether a save must take its connection before it stamps: a stamp taken while it waits for a
    /// pooled connection would fall behind rows other saves commit in the meantime, which a v3 history
    /// client could be moved past (<see cref="HistoryHorizon"/>). A connection the caller already
    /// opened, or a save with nothing to write, needs none; a connection opened here is closed here,
    /// since EF leaves one it did not open to whoever opened it.
    /// </summary>
    private bool MustOpenBeforeStamping() =>
        Database.IsNpgsql()
        && ChangeTracker.HasChanges()
        && Database.GetDbConnection().State != System.Data.ConnectionState.Open;

    /// <summary>
    /// Drops the audit rows of a save that did not complete, so a retry on the same context does
    /// not write them twice. It runs from <see cref="SaveChangesAsync(CancellationToken)"/> rather
    /// than an interceptor hook because EF reports a <see cref="DbUpdateConcurrencyException"/>
    /// to no interceptor failure hook. Every <see cref="MutationAuditLogEntity"/>
    /// still <c>Added</c> belongs to the failed save: <see cref="Interceptors.MutationAuditInterceptor"/>
    /// adds its rows during it, and <see cref="Extensions.AuditedBulkDeleteExtensions"/> adds its own
    /// immediately before it, inside the same transaction as the delete they describe.
    /// </summary>
    private void DetachPendingAuditRows()
    {
        var pending = ChangeTracker.Entries<MutationAuditLogEntity>()
            .Where(e => e.State == EntityState.Added)
            .ToList();

        foreach (var entry in pending)
            entry.State = EntityState.Detached;
    }

    /// <summary>
    /// Runs the single change-detection pass a save needs, then disables auto-detection for the
    /// rest of it. Enabled, detection repeats for every <c>ChangeTracker.Entries()</c> call in the
    /// pipeline — <see cref="UpdateTimestamps"/>, then
    /// <see cref="Interceptors.MutationAuditInterceptor"/>, then EF's own pass — and every pass
    /// re-parses two <c>JsonDocument</c>s per jsonb column of every entity whose value keeps
    /// comparing equal, through <see cref="JsonbStringComparer"/>. The cost of detecting once is
    /// that a modification made after this point must be recorded through the change tracker;
    /// see <see cref="Stamp"/>.
    /// A caller that has already disabled detection keeps its own contract: nothing is detected on
    /// its behalf and its value is what gets restored.
    /// </summary>
    /// <returns>The value <c>AutoDetectChangesEnabled</c> must be restored to once the save ends.</returns>
    private bool DetectChangesOnceForSave()
    {
        var wasEnabled = ChangeTracker.AutoDetectChangesEnabled;
        if (wasEnabled)
        {
            ChangeTracker.DetectChanges();
            ChangeTracker.AutoDetectChangesEnabled = false;
        }

        return wasEnabled;
    }

    /// <summary>
    /// Update system tracking timestamps before saving, and enforce tenant ownership.
    /// </summary>
    private void UpdateTimestamps()
    {
        var utcNow = UtcNowAtStoredPrecision();
        // Column types are a relational concept: asking the InMemory provider for one throws.
        var isRelational = Database.IsRelational();
        var stampedUpdated = new Dictionary<Type, int>();

        foreach (var entry in ChangeTracker.Entries())
        {
            var isAdded = entry.State == EntityState.Added;

            EnforceTenantOwnership(entry, isAdded);
            StripNulCharacters(entry, isAdded, isRelational, ReportTruncation);
            ApplyUpstreamFingerprint(entry);

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
            if (stampUpdated && entry.Entity is ISystemTimestamped)
            {
                var entityType = entry.Metadata.ClrType;
                var index = stampedUpdated.GetValueOrDefault(entityType);
                stampedUpdated[entityType] = index + 1;

                Stamp(
                    entry,
                    nameof(ISystemTimestamped.SysUpdatedAt),
                    utcNow.AddMilliseconds(index / SystemTimestampGroupSize));
            }

            // Auth/identity tables use the created_at / updated_at convention instead.
            if (isAdded && entry.Entity is IEntityCreated entityCreated)
            {
                entityCreated.CreatedAt = utcNow;
            }
            if (stampUpdated && entry.Entity is IEntityTimestamped)
            {
                Stamp(entry, nameof(IEntityTimestamped.UpdatedAt), utcNow);
            }

            ApplyEntitySpecificTimestamps(entry, isAdded, stampUpdated, utcNow);
        }
    }

    /// <summary>
    /// Writes the fingerprint <see cref="UpstreamFingerprintScope"/> names for a tracked row. It goes
    /// through the change tracker, so a row the write otherwise left unchanged still gets it.
    /// </summary>
    private static void ApplyUpstreamFingerprint(EntityEntry entry)
    {
        if (entry.State is EntityState.Deleted or EntityState.Detached
            || entry.Entity is not IUpstreamFingerprinted { LegacyId: { } legacyId } row
            || !UpstreamFingerprintScope.TryGet(row.DataSource, legacyId, out var fingerprint)
            || row.UpstreamFingerprint == fingerprint)
            return;

        entry.Property(nameof(IUpstreamFingerprinted.UpstreamFingerprint)).CurrentValue = fingerprint;
    }

    /// <summary>
    /// Writes a timestamp through the change tracker, which is how a modified row's stamp reaches
    /// the UPDATE now that <see cref="DetectChangesOnceForSave"/> has turned auto-detection off.
    /// Only the modify path needs it: an added row is inserted from its CLR values whatever the
    /// modified flags say, so those stamps are plain assignments.
    /// </summary>
    private static void Stamp(EntityEntry entry, string propertyName, object value)
        => entry.Property(propertyName).CurrentValue = value;

    /// <summary>
    /// True if the entry has a modified property other than the update-timestamp bookkeeping
    /// columns managed by <see cref="UpdateTimestamps"/>. An upstream fingerprint is bookkeeping
    /// too: v3 history clients page on the update stamp, and a fingerprint-only write must not send
    /// them every row again.
    /// </summary>
    private static bool HasNonTimestampModification(EntityEntry entry)
        => entry.State == EntityState.Modified && HasModificationBesideStamps(entry, countFingerprint: false);

    /// <summary>
    /// Whether a save would write nothing to the row of <paramref name="entry"/>, a tracked entity
    /// just assigned to: no property is modified but the update stamps the save itself manages. The
    /// fingerprint the open <see cref="UpstreamFingerprintScope"/> names is applied first, as the save
    /// applies it, so a row whose fingerprint alone differs is still written.
    /// </summary>
    internal static bool WritesNothing(EntityEntry entry)
    {
        ApplyUpstreamFingerprint(entry);
        return !HasModificationBesideStamps(entry, countFingerprint: true);
    }

    private static bool HasModificationBesideStamps(EntityEntry entry, bool countFingerprint)
        => entry.Properties.Any(p =>
            p.IsModified
            && p.Metadata.Name != nameof(ISystemTimestamped.SysUpdatedAt)
            && p.Metadata.Name != nameof(IEntityTimestamped.UpdatedAt)
            && (countFingerprint || p.Metadata.Name != nameof(IUpstreamFingerprinted.UpstreamFingerprint)));

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

    private void ReportTruncation(EntityEntry entry, string property, int length) =>
        (_truncationLogger ??= this.GetService<ILoggerFactory>().CreateLogger<NocturneDbContext>())
            .LogTruncated(entry.Metadata.GetTableName() ?? entry.Metadata.ClrType.Name, property, length);

    private ILogger? _truncationLogger;

    /// <summary>
    /// The string-mapped properties of an entity type, each flagged with whether its column is
    /// jsonb and, for a <see cref="FitToColumnAttribute"/> one, the length it is cut to, so the
    /// model metadata is read once per type rather than once per row.
    /// </summary>
    private static readonly ConcurrentDictionary<IEntityType, (string Name, bool IsJsonb, int? Fit)[]> StringColumns = new();

    /// <summary>
    /// The six-character JSON escape for U+0000, matched only where the backslash opening it is
    /// preceded by an even number of backslashes. An odd count is an escaped backslash followed by
    /// the literal text u0000, which is a valid jsonb value and must survive.
    /// </summary>
    private static readonly Regex JsonNulEscape = new(@"(?<!\\)((?:\\\\)*)\\u0000", RegexOptions.Compiled);

    /// <summary>
    /// Strips NUL characters out of the string columns of a row about to be written. Legacy
    /// Nightscout records carry an embedded U+0000 in string fields — a device name from some old
    /// uploaders — which Mongo and JSON both accept; Postgres rejects the raw byte in a text column
    /// (22021) and the escape sequence in a jsonb one (22P05), failing the whole insert batch the
    /// row happens to land in. Runs in the SaveChanges walk, before any interceptor sees the entry,
    /// so audit snapshots record what is actually persisted. Unpaired surrogates become U+FFFD in
    /// the same pass (<see cref="StorableText"/>), and a <see cref="FitToColumnAttribute"/> column is
    /// cut to its length.
    /// </summary>
    private static void StripNulCharacters(
        EntityEntry entry, bool isAdded, bool isRelational, Action<EntityEntry, string, int> onTruncated)
    {
        if (!isAdded && entry.State != EntityState.Modified)
        {
            return;
        }

        var columns = StringColumns.GetOrAdd(
            entry.Metadata,
            static (entityType, relational) =>
                [.. entityType.GetProperties()
                    .Where(p => p.ClrType == typeof(string))
                    .Select(p => (
                        p.Name,
                        IsJsonb: relational && p.GetColumnType() == "jsonb",
                        Fit: p.PropertyInfo?.IsDefined(typeof(FitToColumnAttribute), inherit: true) == true
                            ? p.GetMaxLength()
                            : null))],
            isRelational);

        foreach (var (name, isJsonb, fit) in columns)
        {
            var property = entry.Property(name);
            if (property.CurrentValue is not string value)
            {
                continue;
            }

            var stripped = StorableText.ReplaceUnpairedSurrogates(
                value.Contains('\0') ? value.Replace("\0", string.Empty) : value);
            if (isJsonb && stripped.Contains(@"\u0000", StringComparison.Ordinal))
            {
                stripped = JsonNulEscape.Replace(stripped, "$1");
            }

            if (fit is { } length && stripped.Length > length)
            {
                stripped = stripped[..(char.IsHighSurrogate(stripped[length - 1]) ? length - 1 : length)];
                onTruncated(entry, name, length);
            }

            if (!string.Equals(stripped, value, StringComparison.Ordinal))
            {
                property.CurrentValue = stripped;
            }
        }
    }

    /// <summary>
    /// Applies timestamps for the few entities whose columns do not follow either the
    /// sys_* or created_at/updated_at conventions covered by the marker interfaces.
    /// </summary>
    private static void ApplyEntitySpecificTimestamps(EntityEntry entry, bool isAdded, bool stampUpdated, DateTime utcNow)
    {
        switch (entry.Entity)
        {
            // Nullable updated_at, set alongside its ISystemTimestamped stamps.
            case ClockFaceEntity when stampUpdated:
                Stamp(entry, nameof(ClockFaceEntity.UpdatedAt), utcNow);
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
    /// Applies the global query filters carried by every ITenantScoped entity: tenant isolation,
    /// plus the soft-delete predicate on the ISoftDeletable ones.
    /// Filters reference this.TenantId, which EF Core evaluates per query against the executing
    /// context.
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
            var entityBuilder = modelBuilder.Entity(entityType.ClrType);

            // Keyed rather than anonymous filters: a hard purge has to lift the soft-delete
            // predicate without lifting tenant isolation with it (PurgeExtensions).
            entityBuilder.HasQueryFilter(
                TenantFilterKey,
                Expression.Lambda(Expression.Equal(tenantIdProperty, currentTenantId), parameter));

            if (!typeof(ISoftDeletable).IsAssignableFrom(entityType.ClrType))
                continue;

            var deletedAtProperty = Expression.Property(parameter, nameof(ISoftDeletable.DeletedAt));
            var nullValue = Expression.Constant(null, typeof(DateTime?));
            entityBuilder.HasQueryFilter(
                SoftDeleteFilterKey,
                Expression.Lambda(Expression.Equal(deletedAtProperty, nullValue), parameter));

            // Records whether the latest soft-delete was user-initiated. The soft-delete
            // dedup discriminator (SoftDeleteDedupExtensions) blocks connector resync from
            // re-creating a user-deleted row, while a system-sweep delete stays re-creatable.
            // A shadow property so it lands on every soft-deletable table without a per-entity edit.
            entityBuilder
                .Property<bool>(DeletedByUserProperty)
                .HasColumnName("deleted_by_user")
                .HasDefaultValue(false);
        }
    }
}

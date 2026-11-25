using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Contracts.Storage;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Configuration;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Jobs;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Contracts.Uploads;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Reports;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Repositories.Health;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Security;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Services.Deduplication;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// Service collection extensions for PostgreSQL data infrastructure
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add PostgreSQL data services with explicit connection string
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="connectionString">PostgreSQL connection string</param>
    /// <param name="configuration">
    /// Application configuration whose <see cref="PostgreSqlConfiguration.SectionName"/> section is
    /// bound over the defaults, so every setting on <see cref="PostgreSqlConfiguration"/> is
    /// reachable from appsettings or the environment. Deliberately has no default: passing
    /// <see langword="null"/> is a decision to run on compiled-in defaults, and omitting it by
    /// accident should not compile.
    /// </param>
    /// <param name="configure">
    /// Optional overrides applied after the section, for values the host derives at startup.
    /// </param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddPostgreSqlInfrastructure(
        this IServiceCollection services,
        string connectionString,
        IConfiguration? configuration,
        Action<PostgreSqlConfiguration>? configure = null
    )
        => services.AddPostgreSqlInfrastructure(
            PostgreSqlConfiguration.Resolve(connectionString, configuration, configure));

    /// <summary>
    /// Register the resolved options so the host's resolution is observable from DI. Separate from
    /// <see cref="AddPostgreSqlInfrastructure(IServiceCollection, PostgreSqlConfiguration)"/>
    /// because a host that skips the pool (the test host runs as Testing) still exposes them.
    /// </summary>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddPostgreSqlConfiguration(
        this IServiceCollection services,
        PostgreSqlConfiguration config
    )
    {
        services.AddSingleton(config);
        return services;
    }

    /// <summary>
    /// Add PostgreSQL data services from already-resolved options. The single registration path:
    /// every other overload resolves its options through
    /// <see cref="PostgreSqlConfiguration.Resolve"/> and lands here.
    /// </summary>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddPostgreSqlInfrastructure(
        this IServiceCollection services,
        PostgreSqlConfiguration config
    )
    {
        if (string.IsNullOrEmpty(config.ConnectionString))
        {
            throw new ArgumentException(
                "Connection string cannot be null or empty",
                nameof(config)
            );
        }

        // Register interceptors as singletons so caches are shared across all DbContext instances.
        services.TryAddSingleton(new HistoryCommitMargin(TimeSpan.FromMilliseconds(config.HistoryCommitMarginMilliseconds)));
        services.TryAddSingleton<TenantConnectionInterceptor>();
        services.TryAddSingleton<MutationAuditInterceptor>();
        services.TryAddSingleton<PreparedStatementRepairInterceptor>();

        // Audit config cache (singleton — uses IDbContextFactory internally)
        services.TryAddSingleton<ITenantAuditConfigCache, TenantAuditConfigCache>();

        // Built once here so a bad pool setting fails at registration. Each provider builds its own
        // pools through a factory, because a provider disposes only what it created: a pool
        // registered as an instance keeps its connections open after its provider is disposed.
        PostgresRuntimeOptions.BuildRuntimeDataSource(config).Dispose();
        services.AddSingleton(_ => new ContextDataSource(PostgresRuntimeOptions.BuildRuntimeDataSource(config)));
        services.AddSingleton(_ => PostgresRuntimeOptions.BuildRawDataSource(config));

        // Non-pooled: each acquisition is a fresh context, discarded after use. A faulted context
        // is never returned to a pool and reused, so a fault cannot poison later callers. Database
        // connections are pooled by the NpgsqlDataSource singleton.
        services.AddDbContextFactory<NocturneDbContext>(
            (sp, options) =>
            {
                options.UseNpgsql(
                    sp.GetRequiredService<ContextDataSource>().Source,
                    npgsqlOptions =>
                    {
                        npgsqlOptions.EnableRetryOnFailure(
                            maxRetryCount: config.MaxRetryCount,
                            maxRetryDelay: TimeSpan.FromSeconds(config.MaxRetryDelaySeconds),
                            errorCodesToAdd: null
                        );

                        npgsqlOptions.CommandTimeout(config.CommandTimeoutSeconds);
                    }
                );

                if (config.EnableSensitiveDataLogging)
                {
                    options.EnableSensitiveDataLogging();
                }

                if (config.EnableDetailedErrors)
                {
                    options.EnableDetailedErrors();
                }

                options.EnableServiceProviderCaching();
                options.AddInterceptors(
                    sp.GetRequiredService<TenantConnectionInterceptor>(),
                    sp.GetRequiredService<MutationAuditInterceptor>(),
                    sp.GetRequiredService<PreparedStatementRepairInterceptor>());
            }
        );

        // AddDbContextFactory also registers the context itself as scoped. Every context comes from
        // INocturneDbContextFactory instead, so that registration goes.
        services.RemoveAll<NocturneDbContext>();

        // Register the context factory, repositories, and shared services.
        AddDataServices(services);

        return services;
    }

    /// <summary>
    /// Register <see cref="INocturneDbContextFactory"/>, repository
    /// interfaces and deduplication. Called by AddPostgreSqlInfrastructure; also usable independently
    /// by test factories that provide their own IDbContextFactory without creating an NpgsqlDataSource.
    /// </summary>
    public static IServiceCollection AddDataServices(this IServiceCollection services)
    {
        services.AddScoped<ICategoryReadContext>(_ => new CategoryReadContext());
        services.AddScoped(sp => new TenantReadContext(
            sp.GetService<ITenantAccessor>(),
            sp.GetService<ICategoryReadContext>(),
            sp.GetService<IAuditContext>()));
        services.TryAddSingleton(sp => new RootServiceProvider(sp));
        services.TryAddTransient<NocturneDbContextFactory>();
        services.TryAddTransient<INocturneDbContextFactory>(sp => sp.GetRequiredService<NocturneDbContextFactory>());
        services.AddScoped<IReaderCachePartition, ReaderCachePartition>();

        services.AddScoped<IDuplicateLinker, DuplicateLinker>();
        services.AddScoped<IRecordChangePublisher, Repositories.V4.RecordChangePublisher>();
        services.AddScoped<DuplicateReconciler>();
        services.AddScoped<IDuplicateReconciler>(sp => sp.GetRequiredService<DuplicateReconciler>());
        services.AddScoped<IDeduplicationAdmin, DeduplicationAdmin>();

        // Register all repositories via their port interfaces
        services.AddScoped<IFoodRepository, FoodRepository>();

        services.AddScoped<ISettingsRepository, SettingsRepository>();
        services.AddTransient<IOAuthTokenRepository, OAuthTokenRepository>();
        services.AddTransient<IOAuthGrantRepository, OAuthGrantRepository>();
        services.AddTransient<IOAuthClientRepository, OAuthClientRepository>();
        services.AddTransient<IMemberInviteRepository, MemberInviteRepository>();
        services.AddTransient<ITenantCreationInviteRepository, TenantCreationInviteRepository>();
        services.AddTransient<IPlatformPolicyRepository, PlatformPolicyRepository>();
        services.AddTransient<ITeamRepository, TeamRepository>();
        services.AddTransient<ISubjectRepository, SubjectRepository>();
        services.AddTransient<ITenantRepository, TenantRepository>();
        services.AddTransient<ITenantMembershipRepository, TenantMembershipRepository>();
        services.AddTransient<IMembershipReasonLapses, MembershipReasonLapses>();
        services.AddTransient<IOidcProviderRepository, OidcProviderRepository>();
        services.AddTransient<IRoleRepository, RoleRepository>();
        services.AddTransient<IPasskeyCredentialRepository, PasskeyCredentialRepository>();
        services.AddTransient<ISpentPasskeyChallengeRepository, SpentPasskeyChallengeRepository>();
        services.AddTransient<ITotpCredentialRepository, TotpCredentialRepository>();
        services.AddTransient<ITrustedDeviceRepository, TrustedDeviceRepository>();
        services.AddTransient<IRecoveryCodeRepository, RecoveryCodeRepository>();
        services.AddTransient<IConnectorConfigurationRepository, ConnectorConfigurationRepository>();
        services.AddTransient<IGoogleHealthGrantHolderRepository, GoogleHealthGrantHolderRepository>();
        services.AddTransient<IGoogleHealthWorkStore, GoogleHealthWorkRepository>();
        services.AddTransient<IConnectorResetJobRepository, ConnectorResetJobRepository>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddTransient<IJobLedger, JobLedgerRepository>();
        services.AddScoped<SourceOwnedDataRepository>();
        services.AddScoped<ISourceOwnedDataReader>(sp => sp.GetRequiredService<SourceOwnedDataRepository>());
        services.AddScoped<ISourceOwnedDataPurge>(sp => sp.GetRequiredService<SourceOwnedDataRepository>());
        services.AddScoped<IDemoDataPurge, DemoDataPurgeRepository>();
        services.AddScoped<ICollectionLastModifiedReader, CollectionLastModifiedReader>();

        // Register avatar storage
        services.AddScoped<IAvatarStore, DatabaseAvatarStore>();

        return services;
    }

    /// <summary>
    /// Add discrepancy analysis repository services
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddDiscrepancyAnalysisRepository(
        this IServiceCollection services
    )
    {
        services.AddScoped<IDiscrepancyAnalysisRepository, DiscrepancyAnalysisRepository>();
        return services;
    }

    /// <summary>
    /// Add the V4 record repositories: glucose, treatment, device, snapshot, profile, patient record,
    /// lab HbA1c and report spec.
    /// </summary>
    public static IServiceCollection AddV4Repositories(this IServiceCollection services)
    {
        services.AddScoped<ISensorGlucoseRepository, SensorGlucoseRepository>();
        services.AddScoped<IMeterGlucoseRepository, MeterGlucoseRepository>();
        services.AddScoped<ICalibrationRepository, CalibrationRepository>();
        services.AddScoped<IBolusRepository, BolusRepository>();
        services.AddScoped<IBasalInjectionRepository, BasalInjectionRepository>();
        services.AddScoped<ITempBasalRepository, TempBasalRepository>();
        services.AddScoped<IProfileActivationRepository, ProfileActivationRepository>();
        services.AddScoped<ICarbIntakeRepository, CarbIntakeRepository>();
        services.AddScoped<IBGCheckRepository, BGCheckRepository>();
        services.AddScoped<INoteRepository, NoteRepository>();
        services.AddScoped<IDeviceEventRepository, DeviceEventRepository>();
        services.AddScoped<IBolusCalculationRepository, BolusCalculationRepository>();
        services.AddScoped<IDecomposedTreatmentKeys, DecomposedTreatmentKeys>();
        services.AddScoped<IWriteBackSentRecords>(sp => sp.GetRequiredService<IDecomposedTreatmentKeys>());
        services.AddScoped<IDeviceRepository, DeviceRepository>();

        services.AddScoped<ILabHbA1cResultRepository, LabHbA1cResultRepository>();
        services.AddScoped<IReportSpecRepository, ReportSpecRepository>();

        services.AddScoped<IApsSnapshotRepository, ApsSnapshotRepository>();
        services.AddScoped<IPumpSnapshotRepository, PumpSnapshotRepository>();
        services.AddScoped<IUploaderSnapshotRepository, UploaderSnapshotRepository>();
        services.AddScoped<IDeviceStatusExtrasRepository, DeviceStatusExtrasRepository>();
        services.AddScoped<ISensorStateReportRepository, SensorStateReportRepository>();
        services.AddScoped<IDeviceStatusGroupRepository, DeviceStatusGroupRepository>();

        services.AddScoped<ITherapySettingsRepository, TherapySettingsRepository>();
        services.AddScoped<IBasalScheduleRepository, BasalScheduleRepository>();
        services.AddScoped<ICarbRatioScheduleRepository, CarbRatioScheduleRepository>();
        services.AddScoped<ISensitivityScheduleRepository, SensitivityScheduleRepository>();
        services.AddScoped<ITargetRangeScheduleRepository, TargetRangeScheduleRepository>();

        services.AddScoped<IPatientRecordRepository, PatientRecordRepository>();
        services.AddScoped<IPatientDeviceRepository, PatientDeviceRepository>();
        services.AddScoped<IPatientInsulinRepository, PatientInsulinRepository>();
        return services;
    }

    /// <summary>
    /// Add the alert and tracker repositories.
    /// </summary>
    public static IServiceCollection AddAlertRepositories(this IServiceCollection services)
    {
        services.AddScoped<IAlertRepository, AlertRepository>();
        services.AddScoped<IAlertTrackerRepository, AlertTrackerRepository>();
        services.AddScoped<IEvaluatedReadingStore, AlertEvaluatedReadingRepository>();
        services.AddScoped<IAlertRuleRepository, AlertRuleRepository>();
        services.AddScoped<IDndWindowRepository, DndWindowRepository>();
        services.AddScoped<ITenantAlertSettingsRepository, TenantAlertSettingsRepository>();
        services.AddScoped<ITrackerRepository, TrackerRepository>();
        services.AddScoped<ITrackerConsumableLock, TrackerRepository>();
        return services;
    }

    /// <summary>
    /// Add the repositories of health and activity data: state spans, sleep, activity, heart rate,
    /// steps and body weight.
    /// </summary>
    public static IServiceCollection AddHealthRepositories(this IServiceCollection services)
    {
        services.AddScoped<IStateSpanRepository, StateSpanRepository>();
        services.AddScoped<ISleepSessionRepository, SleepSessionRepository>();
        services.AddScoped<IActivityDedupRepository, ActivityDedupRepository>();
        services.AddScoped<IActivityRecordRepository, ActivityRecordRepository>();
        services.AddScoped<IHeartRateRepository, HeartRateRepository>();
        services.AddScoped<IObservedHealthSeriesRepository<StepCount>, StepCountRepository>();
        services.AddScoped<IBodyWeightRepository, BodyWeightRepository>();
        services.AddScoped<IHealthSeriesRepository<BodyWeight>>(sp => sp.GetRequiredService<IBodyWeightRepository>());
        services.AddScoped<IHealthRecordTransactions, HealthRecordTransactions>();
        return services;
    }

    /// <summary>
    /// Add the food repositories that <see cref="AddDataServices"/> does not: user favorites,
    /// treatment foods and connector food entries.
    /// </summary>
    public static IServiceCollection AddFoodRepositories(this IServiceCollection services)
    {
        services.AddScoped<IUserFoodFavoriteRepository, UserFoodFavoriteRepository>();
        services.AddScoped<ITreatmentFoodRepository, TreatmentFoodRepository>();
        services.AddScoped<IConnectorFoodEntryRepository, ConnectorFoodEntryRepository>();
        return services;
    }

    /// <summary>
    /// Add the repositories for first-party tokens, in-app notifications, compression low
    /// suggestions and system events.
    /// </summary>
    public static IServiceCollection AddNotificationAndEventRepositories(this IServiceCollection services)
    {
        services.AddScoped<IFirstPartyTokenRepository, EfFirstPartyTokenRepository>();
        services.AddScoped<IInAppNotificationRepository, InAppNotificationRepository>();
        services.AddScoped<ICompressionLowRepository, CompressionLowRepository>();
        services.AddScoped<ISystemEventRepository, SystemEventRepository>();
        return services;
    }

    /// <summary>
    /// Add the audit repositories: rejected uploads, audit configuration, the audit log and the
    /// member history reader.
    /// </summary>
    public static IServiceCollection AddAuditRepositories(this IServiceCollection services)
    {
        services.AddScoped<IRejectedUploadStore, RejectedUploadRepository>();
        services.AddScoped<IAuditConfigStore, AuditConfigRepository>();
        services.AddScoped<IAuditLogReader, AuditLogReader>();
        services.AddScoped<IMemberHistoryReader, MemberHistoryReader>();
        return services;
    }

    /// <summary>
    /// Persists Data Protection keys to <see cref="NocturneDbContext"/> so the key ring
    /// survives container restarts (<see cref="NocturneDbXmlRepository"/>). Call this on the builder
    /// returned by <c>services.AddDataProtection()</c>.
    /// </summary>
    public static IDataProtectionBuilder PersistKeysToNocturneDb(
        this IDataProtectionBuilder builder)
    {
        builder.Services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(services =>
            new ConfigureOptions<KeyManagementOptions>(options =>
                options.XmlRepository = new NocturneDbXmlRepository(
                    services.GetRequiredService<IDbContextFactory<NocturneDbContext>>(),
                    services.GetRequiredService<ILogger<NocturneDbXmlRepository>>())));
        return builder;
    }
}

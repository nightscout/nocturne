using System.Threading.RateLimiting;
using Fido2NetLib;
using Nocturne.API.Authorization;
using Nocturne.API.Configuration;
using Nocturne.API.Services;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.AidDetection;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.API.Services.Alerts.Webhooks;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Services.CoachMarks;
using Nocturne.API.Services.Timezones;
using Nocturne.API.Services.ChartData;
using Nocturne.API.Services.ChartData.Stages;
using Nocturne.API.Services.ConnectorPublishing;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.Effects;
using Nocturne.API.Services.Entries;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Sleep;
using Nocturne.API.Services.Health;
using Nocturne.API.Services.Identity;
using Nocturne.API.Services.Legacy;
using Nocturne.API.Services.Monitoring;
using Nocturne.API.Services.NotificationActionHandlers;
using Nocturne.API.Services.Notifications;
using Nocturne.API.Services.NotificationTemplates;
using Nocturne.API.Services.Platform;
using Nocturne.API.Services.Profiles;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.API.Services.Realtime;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Services.V4;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Nightscout.Services.WriteBack;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.CoachMarks;
using Nocturne.Core.Contracts.Timezones;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Monitoring;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Configuration;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Shared.Services;
using JwtOptions = Nocturne.Core.Models.Configuration.JwtOptions;
using OidcOptions = Nocturne.Core.Models.Configuration.OidcOptions;

namespace Nocturne.API.Extensions;

/// <summary>
/// Extension methods that organize DI registrations into logical groups,
/// keeping Program.cs scannable.
/// </summary>
public static class ServiceRegistrationExtensions
{
    /// <summary>
    /// Core API utility and calculation services (status, versioning, time queries,
    /// IOB/COB, predictions, statistics, etc.)
    /// </summary>
    public static IServiceCollection AddApiCoreServices(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.AddScoped<IStatusService, StatusService>();
        services.AddScoped<IVersionService, VersionService>();
        services.AddSingleton<IXmlDocumentationService, XmlDocumentationService>();
        services.AddScoped<IDocumentProcessingService, DocumentProcessingService>();

        services.AddScoped<IBraceExpansionService, BraceExpansionService>();
        services.AddScoped<ITimeQueryService, TimeQueryService>();

        services.AddScoped<IDDataService, DDataService>();
        services.AddScoped<IPropertiesService, PropertiesService>();
        services.AddScoped<ISummaryService, SummaryService>();
        // Prediction service — configurable via Predictions:Source (None, DeviceStatus, OrefWasm)
        var predictionSource = PredictionOptions.ResolveSource(configuration);
        switch (predictionSource)
        {
            case PredictionSource.DeviceStatus:
                services.AddScoped<IPredictionService, DeviceStatusPredictionService>();
                break;
            case PredictionSource.OrefWasm:
                services.AddScoped<IPredictionService, PredictionService>();
                services.AddOrefService(options =>
                {
                    options.WasmPath = "oref.wasm";
                    options.Enabled = true;
                });
                break;
            case PredictionSource.None:
            default:
                break;
        }

        services.AddScoped<IIobCalculator, IobCalculator>();
        services.AddScoped<ICobCalculator, CobCalculator>();
        services.AddScoped<IAr2Service, Ar2Service>();
        services.AddScoped<IBolusWizardService, BolusWizardService>();

        services.AddScoped<IAuthorizationService, AuthorizationService>();
        services.AddScoped<IHubTokenAuthorizer, HubTokenAuthorizer>();
        services.AddScoped<IAlexaService, AlexaService>();

        services.AddScoped<IStatisticsService, StatisticsService>();
        services.AddScoped<ISensorIntegrityService, SensorIntegrityService>();

        // Analytics
        services.Configure<AnalyticsConfiguration>(
            configuration.GetSection(AnalyticsConfiguration.SectionName)
        );
        services.AddScoped<IAnalyticsService, AnalyticsService>();
        services.AddScoped<IConnectorHealthService, ConnectorHealthService>();

        // GitHub issue creation
        services.Configure<GitHubIssueOptions>(configuration.GetSection("GitHub"));
        services.AddSingleton<GitHubIssueService>();

        return services;
    }

    /// <summary>
    /// Authentication, authorization, identity providers, multitenancy,
    /// and auth middleware handlers.
    /// </summary>
    public static IServiceCollection AddAuthenticationAndIdentity(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        // Options
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.PostConfigure<JwtOptions>(options =>
        {
            if (string.IsNullOrEmpty(options.SecretKey))
            {
                options.SecretKey =
                    configuration[$"Parameters:{ServiceNames.Parameters.InstanceKey}"]
                    ?? configuration[ServiceNames.ConfigKeys.InstanceKey]
                    ?? throw new InvalidOperationException(
                        "JWT signing key could not be derived: instance key is not configured.");
            }
        });
        services.Configure<OidcOptions>(configuration.GetSection(OidcOptions.SectionName));
        services.Configure<PlatformOptions>(configuration.GetSection(PlatformOptions.SectionName));
        // Auth services
        services.AddScoped<IAuthAuditService, AuthAuditService>();
        services.AddScoped<IJwtService, JwtService>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();
        services.AddSingleton<IRotationSuccessorCache, RotationSuccessorCache>();
        services.AddScoped<IFirstPartyTokenRepository, EfFirstPartyTokenRepository>();
        services.AddScoped<ISubjectService, SubjectService>();
        services.AddScoped<ISessionService, SessionService>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<IOidcProviderService, OidcProviderService>();
        services.AddScoped<IOidcAuthService, OidcAuthService>();

        // OAuth services
        services.AddScoped<IOAuthClientService, OAuthClientService>();
        services.AddSingleton<RedirectUriValidator>();
        services.AddScoped<IOAuthGrantService, OAuthGrantService>();
        services.AddScoped<IOAuthTokenService, OAuthTokenService>();
        services.AddScoped<IOAuthDeviceCodeService, OAuthDeviceCodeService>();
        services.AddScoped<IMemberInviteService, MemberInviteService>();
        services.AddScoped<IMembershipRequestService, MembershipRequestService>();
        services.AddScoped<IGuestLinkService, GuestLinkService>();
        services.AddSingleton<IOAuthTokenRevocationCache, OAuthTokenRevocationCache>();
        services.AddHostedService<OAuthCodeCleanupService>();

        services.AddHostedService<AuthorizationSeedService>();

        services.AddSingleton<GuestSessionCacheService>();
        services.AddSingleton<PublicAccessCacheService>();
        services.AddSingleton<ShareTokenCacheService>();
        services.AddSingleton<IShareTokenGenerator, ShareTokenGenerator>();
        services.AddScoped<IShareLinkService, ShareLinkService>();
        // Singleton because its consumer runs at startup outside any request scope; it creates its
        // own scope per notification.
        services.AddSingleton<IShareLinkRotatedNotifier, ShareLinkRotatedNotifier>();

        // Passkey (WebAuthn/FIDO2) services
        services.AddScoped<IPasskeyService, PasskeyService>();
        services.AddScoped<IRecoveryCodeService, RecoveryCodeService>();
        services.AddScoped<ITotpService, TotpService>();
        // Derive WebAuthn RP config from the base domain (single source of truth)
        var baseDomain = configuration[BaseDomainOptions.ConfigKey] ?? "localhost:1612";
        var rpId = baseDomain.Split(':')[0]; // hostname without port
        var origin = $"https://{baseDomain}";
        services.AddFido2(options =>
        {
            options.ServerDomain = rpId;
            options.ServerName = "Nocturne";
            options.Origins = new HashSet<string> { origin };
        });

        // Base domain (used by tenant resolution, OIDC redirects, etc.)
        services.Configure<BaseDomainOptions>(opts =>
            opts.BaseDomain = configuration[BaseDomainOptions.ConfigKey] ?? ""
        );

        // Operator (SaaS platform policy)
        services.AddOptions<OperatorConfiguration>()
            .Bind(configuration.GetSection(OperatorConfiguration.SectionName))
            .Validate(config =>
            {
                if (config.Support.AccountBilling is { } ab)
                    return !string.IsNullOrWhiteSpace(ab.Url);
                return true;
            }, "Operator:Support:AccountBilling:Url is required when AccountBilling is configured");

        services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
        services.AddScoped<ITenantOwnerResolver, TenantOwnerResolver>();
        services.AddScoped<ITenantMemberService, TenantMemberService>();
        services.AddScoped<ITenantRoleService, TenantRoleService>();
        services.AddScoped<ITenantService, TenantService>();
        services.AddScoped<ITenantOverviewService, TenantOverviewService>();

        // Shared by InstanceKeyHandler (authentication) and TenantSetupMiddleware
        // (setup-gate bypass) so instance-key validation rules live in one place.
        services.AddSingleton<IInstanceKeyValidator, InstanceKeyValidator>();

        // Auth handlers (executed in priority order, lowest first)
        services.AddSingleton<IAuthHandler, PlatformAccessCookieHandler>(); // Priority 40
        services.AddSingleton<IAuthHandler, SessionCookieHandler>(); // Priority 50
        services.AddSingleton<IAuthHandler, GuestSessionHandler>(); // Priority 52
        services.AddSingleton<GuestSessionHandler>(); // For direct cookie-setting use
        services.AddSingleton<IAuthHandler, InstanceKeyHandler>(); // Priority 55
        services.AddSingleton<IAuthHandler, OidcTokenHandler>(); // Priority 100
        services.AddSingleton<IAuthHandler, OAuthAccessTokenHandler>(); // Priority 150
        services.AddSingleton<IAuthHandler, DirectGrantTokenHandler>(); // Priority 150
        services.AddSingleton<IAuthHandler, LegacyJwtHandler>(); // Priority 200
        services.AddSingleton<IAuthHandler, AccessTokenHandler>(); // Priority 300
        services.AddSingleton<IAuthHandler, ApiKeyHandler>(); // Priority 400

        // OIDC provider discovery HTTP client
        services.AddHttpClient(
            "OidcProvider",
            client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            }
        );

        // Rate limiting for OAuth endpoints
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(
                "oauth-token",
                context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 30,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                        }
                    )
            );

            options.AddPolicy(
                "oauth-device",
                context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 10,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                        }
                    )
            );

            // RFC 7591 Dynamic Client Registration: 10 registrations per IP per hour.
            options.AddPolicy(
                "oauth-register",
                context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 10,
                            Window = TimeSpan.FromHours(1),
                            QueueLimit = 0,
                        }
                    )
            );

            options.AddPolicy(
                "oauth-device-approve",
                context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 20,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                        }
                    )
            );

            options.AddPolicy(
                "totp-login",
                context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 10,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                        }
                    )
            );

            // Guest link activation: 5 attempts per IP per 10 minutes.
            options.AddPolicy(
                "guest-activate",
                context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 5,
                            Window = TimeSpan.FromMinutes(10),
                            QueueLimit = 0,
                        }
                    )
            );

            // Support issue creation: 5 issues per IP per hour.
            options.AddPolicy(
                "support-issues",
                context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 5,
                            Window = TimeSpan.FromHours(1),
                            QueueLimit = 0,
                        }
                    )
            );

            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new
                    {
                        error = "rate_limit_exceeded",
                        error_description = "Too many requests. Please try again later.",
                    },
                    ct
                );
            };
        });

        return services;
    }

    /// <summary>
    /// Domain CRUD services for entries, treatments, device status, profiles,
    /// food, activities, trackers, and all other data-owning services.
    /// </summary>
    public static IServiceCollection AddDomainServices(this IServiceCollection services)
    {
        // Demo mode
        services.AddSingleton<IDemoModeService, DemoModeService>();

        // V4 projection (must be registered before EntryService/TreatmentService)
        services.AddScoped<IV4ToLegacyProjectionService, V4ToLegacyProjectionService>();

        // Collection effect descriptors (resolved by WriteSideEffectsService)
        services.AddSingleton<ICollectionEffectDescriptor, ProfileEffectDescriptor>();
        services.AddSingleton<ICollectionEffectDescriptor, DeviceStatusEffectDescriptor>();
        services.AddSingleton<ICollectionEffectDescriptor, FoodEffectDescriptor>();

        // Core domain services
        services.AddScoped<ITreatmentService, TreatmentService>();
        services.AddScoped<ITreatmentStore, Nocturne.API.Services.Treatments.TreatmentReadService>();
        services.AddScoped<ITreatmentCache, Nocturne.API.Services.Treatments.TreatmentCacheAdapter>();
        services.AddScoped<SignalRTreatmentEventSink>();
        services.AddScoped<IDataEventSink<Treatment>>(sp =>
        {
            var sinks = new List<IDataEventSink<Treatment>>
            {
                sp.GetRequiredService<SignalRTreatmentEventSink>(),
            };
            var writeBack = sp.GetService<NightscoutTreatmentWriteBackSink>();
            if (writeBack is not null) sinks.Add(writeBack);

            return new CompositeDataEventSink<Treatment>(
                sinks,
                sp.GetService<ILogger<CompositeDataEventSink<Treatment>>>());
        });
        services.AddScoped<IWriteSideEffects, WriteSideEffectsService>();
        services.AddScoped<IEntryService, EntryService>();
        services.AddScoped<IEntryStore, Nocturne.API.Services.Entries.EntryReadService>();
        services.AddScoped<IEntryCache, Nocturne.API.Services.Entries.EntryCacheAdapter>();
        services.AddScoped<SignalREntryEventSink>();
        services.AddScoped<IDataEventSink<Entry>>(sp =>
        {
            var sinks = new List<IDataEventSink<Entry>>
            {
                sp.GetRequiredService<SignalREntryEventSink>(),
            };
            var writeBack = sp.GetService<NightscoutEntryWriteBackSink>();
            if (writeBack is not null) sinks.Add(writeBack);

            return new CompositeDataEventSink<Entry>(
                sinks,
                sp.GetService<ILogger<CompositeDataEventSink<Entry>>>());
        });
        services.AddScoped<IStateSpanService, StateSpanService>();
        services.AddScoped<ISleepService, SleepService>();
        services.AddScoped<ISleepReportService, SleepReportService>();
        services.AddScoped<DeviceStatusProjectionService>();
        services.AddScoped<IDataEventSink<DeviceStatus>>(sp =>
        {
            var sinks = new List<IDataEventSink<DeviceStatus>>();
            var writeBack = sp.GetService<NightscoutDeviceStatusWriteBackSink>();
            if (writeBack is not null) sinks.Add(writeBack);
            return new CompositeDataEventSink<DeviceStatus>(
                sinks,
                sp.GetService<ILogger<CompositeDataEventSink<DeviceStatus>>>());
        });
        services.AddScoped<IBatteryService, BatteryService>();
        services.AddScoped<IProfileWriteService, ProfileWriteService>();
        services.AddScoped<IActiveProfileResolver, ActiveProfileResolver>();
        services.AddScoped<IBasalRateResolver, BasalRateResolver>();
        services.AddScoped<IBasalSegmentService, BasalSegmentService>();
        services.AddScoped<ISensitivityResolver, SensitivityResolver>();
        services.AddScoped<ICarbRatioResolver, CarbRatioResolver>();
        services.AddScoped<ITargetRangeResolver, TargetRangeResolver>();
        services.AddScoped<ITherapySettingsResolver, TherapySettingsResolver>();
        services.AddScoped<ITherapyTimelineResolver, TherapyTimelineResolver>();
        services.AddScoped<Services.Glucose.IProfileSnapshotService, Services.Glucose.ProfileSnapshotService>();
        services.AddScoped<ITempBasalResolver, TempBasalResolver>();
        services.AddScoped<IProfileProjectionService, ProfileProjectionService>();
        services.AddScoped<IDataEventSink<Profile>>(sp =>
        {
            var sinks = new List<IDataEventSink<Profile>>();
            var writeBack = sp.GetService<NightscoutProfileWriteBackSink>();
            if (writeBack is not null) sinks.Add(writeBack);
            return new CompositeDataEventSink<Profile>(
                sinks,
                sp.GetService<ILogger<CompositeDataEventSink<Profile>>>());
        });

        // Food services
        services.AddScoped<IFoodService, FoodService>();
        services.AddScoped<IDataEventSink<Food>>(sp =>
        {
            var sinks = new List<IDataEventSink<Food>>();
            var writeBack = sp.GetService<NightscoutFoodWriteBackSink>();
            if (writeBack is not null) sinks.Add(writeBack);
            return new CompositeDataEventSink<Food>(
                sinks,
                sp.GetService<ILogger<CompositeDataEventSink<Food>>>());
        });
        services.AddScoped<IConnectorFoodEntryService, ConnectorFoodEntryService>();
        services.AddScoped<ITreatmentFoodService, TreatmentFoodService>();
        services.AddScoped<IUserFoodFavoriteService, UserFoodFavoriteService>();
        services.AddScoped<IConnectorFoodEntryRepository, ConnectorFoodEntryRepository>();
        services.AddScoped<IMealMatchingService, MealMatchingService>();

        // Activity and health metric services
        services.AddScoped<IActivityService, ActivityService>();
        services.AddScoped<IDataEventSink<Activity>>(sp =>
        {
            var sinks = new List<IDataEventSink<Activity>>();
            var writeBack = sp.GetService<NightscoutActivityWriteBackSink>();
            if (writeBack is not null) sinks.Add(writeBack);
            return new CompositeDataEventSink<Activity>(
                sinks,
                sp.GetService<ILogger<CompositeDataEventSink<Activity>>>());
        });
        services.AddScoped<IHeartRateService, HeartRateService>();
        services.AddScoped<IBodyWeightService, BodyWeightService>();
        services.AddScoped<IStepCountService, StepCountService>();

        // Tracker services
        services.AddScoped<ITrackerTriggerService, TrackerTriggerService>();
        // Tracker notifications ride the alert engine: thresholds are synthesised into
        // managed tracker_age alert rules, backfilled once at startup for pre-existing
        // definitions (and self-healing if a managed rule is ever lost).
        services.AddScoped<ITrackerAlertRuleSyncService, TrackerAlertRuleSyncService>();
        services.AddHostedService<TrackerAlertRuleBackfillService>();
        services.AddScoped<ITrackerSuggestionService, TrackerSuggestionService>();
        services.AddScoped<IDeviceAgeService, DeviceAgeService>();

        // Device resolution
        services.AddScoped<IDeviceService, DeviceService>();
        services.AddScoped<IPatientDeviceStamper, PatientDeviceStamper>();
        services.AddScoped<IDeviceReattributionService, DeviceReattributionService>();

        // Canonical glucose stream (single-stream view for v1/v3, alarms, unfiltered analytics)
        services.AddScoped<ICanonicalGlucoseService, CanonicalGlucoseService>();
        services.AddScoped<ICanonicalAlertEvaluator, CanonicalAlertEvaluator>();

        // Coach marks
        services.AddScoped<ICoachMarkService, CoachMarkService>();

        // Timezone timeline (fake-UTC connector conversion + travel/relocation)
        services.AddScoped<ITimezoneTimelineService, TimezoneTimelineService>();

        // UI and display
        services.AddScoped<IUISettingsService, UISettingsService>();
        services.AddScoped<
            IMyFitnessPalMatchingSettingsService,
            MyFitnessPalMatchingSettingsService
        >();
        services.AddScoped<IClockFaceService, ClockFaceService>();
        services.AddScoped<IWidgetSummaryService, WidgetSummaryService>();
        // Basal series builder (used by chart data pipeline and reports endpoint)
        services.AddScoped<IBasalSeriesBuilder, BasalSeriesBuilder>();

        // Chart data pipeline stages (order matters!)
        services.AddScoped<ProfileLoadStage>();
        services.AddScoped<DataFetchStage>();
        services.AddScoped<IobCobComputeStage>();
        services.AddScoped<DtoMappingStage>();

        services.AddScoped<IEnumerable<IChartDataStage>>(sp => new IChartDataStage[]
        {
            sp.GetRequiredService<ProfileLoadStage>(),
            sp.GetRequiredService<DataFetchStage>(),
            sp.GetRequiredService<IobCobComputeStage>(),
            sp.GetRequiredService<DtoMappingStage>(),
        });

        services.AddScoped<IChartDataAssembler, DashboardChartDataAssembler>();
        services.AddScoped<IChartDataService, ChartDataService>();
        services.AddScoped<IActogramReportService, ActogramReportService>();
        services.AddScoped<IDataOverviewService, DataOverviewService>();

        return services;
    }

    /// <summary>
    /// V4 repositories, snapshot repositories, profile repositories,
    /// patient record repositories, AID detection, and decomposition pipeline.
    /// </summary>
    public static IServiceCollection AddV4Infrastructure(this IServiceCollection services)
    {
        // V4 Repositories
        services.AddScoped<ISensorGlucoseRepository, SensorGlucoseRepository>();
        services.AddScoped<IMeterGlucoseRepository, MeterGlucoseRepository>();
        services.AddScoped<ICalibrationRepository, CalibrationRepository>();
        services.AddScoped<IBolusRepository, BolusRepository>();
        services.AddScoped<IBasalInjectionRepository, BasalInjectionRepository>();
        services.AddScoped<ITempBasalRepository, TempBasalRepository>();
        services.AddScoped<ICarbIntakeRepository, CarbIntakeRepository>();
        services.AddScoped<IBGCheckRepository, BGCheckRepository>();
        services.AddScoped<INoteRepository, NoteRepository>();
        services.AddScoped<IDeviceEventRepository, DeviceEventRepository>();
        services.AddScoped<IBolusCalculationRepository, BolusCalculationRepository>();
        services.AddScoped<IDeviceRepository, DeviceRepository>();

        // V4 Snapshot Repositories
        services.AddScoped<IApsSnapshotRepository, ApsSnapshotRepository>();
        services.AddScoped<IPumpSnapshotRepository, PumpSnapshotRepository>();
        services.AddScoped<IUploaderSnapshotRepository, UploaderSnapshotRepository>();
        services.AddScoped<IDeviceStatusExtrasRepository, DeviceStatusExtrasRepository>();

        // V4 Profile Repositories
        services.AddScoped<ITherapySettingsRepository, TherapySettingsRepository>();
        services.AddScoped<IBasalScheduleRepository, BasalScheduleRepository>();
        services.AddScoped<ICarbRatioScheduleRepository, CarbRatioScheduleRepository>();
        services.AddScoped<ISensitivityScheduleRepository, SensitivityScheduleRepository>();
        services.AddScoped<ITargetRangeScheduleRepository, TargetRangeScheduleRepository>();

        // V4 Patient Record Repositories
        services.AddScoped<IPatientRecordRepository, PatientRecordRepository>();
        services.AddScoped<IPatientDeviceRepository, PatientDeviceRepository>();
        services.AddScoped<IPatientInsulinRepository, PatientInsulinRepository>();

        // Glucose processing
        services.AddScoped<IGlucoseProcessingConfigProvider, GlucoseProcessingConfigProvider>();
        services.AddScoped<IGlucoseProcessingResolver, GlucoseProcessingResolver>();

        // AID Detection Strategies and Metrics Service
        services.AddSingleton<IAidDetectionStrategy, ApsSnapshotStrategy>();
        services.AddSingleton<IAidDetectionStrategy, TbrBasedStrategy>();
        services.AddSingleton<IAidDetectionStrategy, NoAidStrategy>();
        services.AddScoped<IAidMetricsService, AidMetricsService>();

        // V4 Decomposers
        services.AddScoped<IEntryDecomposer, EntryDecomposer>();
        services.AddScoped<ITreatmentDecomposer, TreatmentDecomposer>();
        services.AddScoped<IDeviceStatusDecomposer, DeviceStatusDecomposer>();
        services.AddScoped<IActivityDecomposer, ActivityDecomposer>();
        services.AddScoped<IProfileDecomposer, ProfileDecomposer>();

        // Unified generic decomposer registrations
        services.AddScoped<IDecomposer<Entry>>(sp =>
            (IDecomposer<Entry>)sp.GetRequiredService<IEntryDecomposer>()
        );
        services.AddScoped<IDecomposer<Treatment>>(sp =>
            (IDecomposer<Treatment>)sp.GetRequiredService<ITreatmentDecomposer>()
        );
        services.AddScoped<IDecomposer<DeviceStatus>>(sp =>
            (IDecomposer<DeviceStatus>)sp.GetRequiredService<IDeviceStatusDecomposer>()
        );
        services.AddScoped<IDecomposer<Activity>>(sp =>
            (IDecomposer<Activity>)sp.GetRequiredService<IActivityDecomposer>()
        );
        services.AddScoped<IDecomposer<Profile>>(sp =>
            (IDecomposer<Profile>)sp.GetRequiredService<IProfileDecomposer>()
        );
        services.AddScoped<IDecompositionPipeline, DecompositionPipeline>();

        return services;
    }

    /// <summary>
    /// Real-time communication (SignalR), notifications (in-app, push, Loop/OpenAPS),
    /// and the notification resolution background service.
    /// </summary>
    public static IServiceCollection AddRealTimeAndNotifications(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        // SignalR
        // Global hub filters have to be added to HubOptions — SignalR reads HubOptions.HubFilters
        // and never resolves IHubFilter from the container, so a filter registered only in DI never
        // runs. TenantHubFilter is outermost so the invocation scope's ITenantAccessor is populated
        // before anything inside resolves a tenant-scoped service; HubAuthorizationFilter needs none
        // of that itself, since it reads the credential out of HttpContext.Items.
        services.AddSignalR(options =>
        {
            Microsoft.AspNetCore.SignalR.HubOptionsExtensions
                .AddFilter<Nocturne.API.Hubs.TenantHubFilter>(options);
            Microsoft.AspNetCore.SignalR.HubOptionsExtensions
                .AddFilter<Nocturne.API.Hubs.HubAuthorizationFilter>(options);
        });
        services.AddSingleton<Nocturne.API.Hubs.TenantHubFilter>();
        services.AddSingleton<Nocturne.API.Hubs.HubAuthorizationFilter>();
        services.AddScoped<ISignalRBroadcastService, SignalRBroadcastService>();
        services.AddScoped<ISyncProgressReporter, SignalRSyncProgressReporter>();

        // Native V4 record broadcasting (companion + Prelude) over the glucose/care/device/therapy
        // categories. Open generic so every V4 model type resolves; the repository chokepoint fires it
        // for live writes only. Additive to the legacy v1 IDataEventSink<T> projections above.
        services.AddScoped(typeof(IV4RecordBroadcaster<>), typeof(SignalRV4RecordBroadcaster<>));

        // Push notifications
        services.AddScoped<INotificationV2Service, NotificationV2Service>();
        services.AddScoped<INotificationV1Service, NotificationV1Service>();
        services.AddScoped<IApnsClientFactory, ApnsClientFactory>();
        services.AddHttpClient(
            "dotAPNS",
            client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            }
        );

        // Loop/OpenAPS integration
        services.Configure<LoopConfiguration>(configuration.GetSection("Loop"));
        services.AddScoped<ILoopService, LoopService>();
        services.AddScoped<IOpenApsService, OpenApsService>();
        services.AddScoped<IPumpAlertService, PumpAlertService>();

        // In-app notifications
        services.AddScoped<IInAppNotificationRepository, InAppNotificationRepository>();
        services.AddScoped<IInAppNotificationService, InAppNotificationService>();
        services.AddHostedService<NotificationResolutionService>();
        services.AddHostedService<NotificationCleanupService>();

        // Notification template registry (singleton -- templates are immutable after startup)
        var templateRegistry = new NotificationTemplateRegistry().AddBuiltInTemplates();
        services.AddSingleton<INotificationTemplateRegistry>(templateRegistry);

        // Client device registry (Prelude/Companion actuation targets)
        services.AddScoped<
            Nocturne.Core.Contracts.ClientDevices.IClientDeviceService,
            Nocturne.API.Services.ClientDevices.ClientDeviceService>();

        // Notification action handlers (scoped -- they may depend on scoped services)
        services.AddScoped<INotificationActionHandler, MealMatchActionHandler>();
        services.AddScoped<INotificationActionHandler, TrackerSuggestionActionHandler>();
        services.AddScoped<INotificationActionHandler, AlertActionHandler>();

        return services;
    }

    /// <summary>
    /// Alert engines, device health monitoring, compression low detection,
    /// and all notifier implementations (SignalR, webhook, Pushover).
    /// </summary>
    public static IServiceCollection AddAlertingAndMonitoring(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        // Compression low detection
        services.AddScoped<ICompressionLowRepository, CompressionLowRepository>();
        services.AddScoped<ICompressionLowService, CompressionLowService>();
        services.AddSingleton<CompressionLowDetectionService>();
        services.AddSingleton<ICompressionLowDetectionService>(sp =>
            sp.GetRequiredService<CompressionLowDetectionService>()
        );
        services.AddHostedService(sp => sp.GetRequiredService<CompressionLowDetectionService>());

        // Webhook infrastructure (reused by new alert engine)
        services.AddScoped<WebhookRequestSender>();

        // Condition evaluators. Scoped because SustainedEvaluator depends on the scoped
        // IConditionTimerStore (DbContext-backed); the registry is also scoped because it captures
        // IEnumerable<IConditionEvaluator>.
        services.AddAlertEvaluators();
        services.AddScoped<ConditionEvaluatorRegistry>();

        // Sustained-condition timer store
        services.AddScoped<IConditionTimerStore, ConditionTimerRepository>();

        // Excursion tracker
        services.AddScoped<IExcursionTracker, ExcursionTracker>();

        // Alert evaluation engine seam (Alerts:Engine = managed | shadow | rust)
        services.AddAlertEvaluationEngine(configuration);

        // Alert engine core
        services.AddScoped<IAlertRepository, AlertRepository>();
        services.Configure<AlertEvaluationOptions>(
            configuration.GetSection(AlertEvaluationOptions.SectionName));
        services.AddScoped<IReservoirEstimationService, ReservoirEstimationService>();
        // Bundles the enricher's data-source dependencies; resolved positionally from DI.
        services.AddScoped<SensorContextEnricherDependencies>();
        services.AddScoped<ISensorContextEnricher, SensorContextEnricher>();
        services.AddScoped<IAlertOrchestrator, AlertOrchestrator>();
        services.AddScoped<IAlertDeliveryService, AlertDeliveryService>();
        services.AddScoped<IAlertAcknowledgementService, AlertAcknowledgementService>();
        services.AddScoped<IExcursionResolutionHandler, ExcursionResolutionHandler>();
        services.AddScoped<IAlertReferenceService, AlertReferenceService>();
        services.AddScoped<IAlertReplayService, AlertReplayService>();
        // Scope-class classification (scoped Do Not Disturb, ADR 0004): stateless over
        // the static native engine, so a singleton. Backfilled once at startup.
        services.AddSingleton<IRuleScopeClassifier, RuleScopeClassifier>();
        services.AddHostedService<RuleScopeClassBackfillService>();

        // Delivery providers
        services.AddScoped<Nocturne.API.Services.Alerts.Providers.WebPushProvider>();
        services.AddScoped<Nocturne.API.Services.Alerts.Providers.InAppProvider>();
        services.AddScoped<Nocturne.API.Services.Alerts.Providers.WebhookProvider>();
        services.AddScoped<Nocturne.API.Services.Alerts.Providers.ChatBotProvider>();
        services.AddScoped<Nocturne.API.Services.Alerts.Providers.HomeAssistantProvider>();
        services.AddScoped<Nocturne.API.Services.Alerts.Providers.DeviceActionProvider>();
        services.AddHttpClient("ChatBot");

        // Chat identity
        services.AddScoped<Nocturne.API.Services.Chat.ChatIdentityService>();
        services.AddScoped<Nocturne.API.Services.Chat.ChatIdentityDirectoryService>();
        services.AddScoped<Nocturne.API.Services.Chat.ChatIdentityPendingLinkService>();

        // Bot health tracking
        services.AddSingleton<BotHealthService>();

        // Background sweep
        services.AddHostedService<AlertSweepService>();

        // Periodic watermark-driven deduplication reconciliation across active tenants
        services.AddHostedService<Nocturne.API.Services.BackgroundServices.DeduplicationReconciliationBackgroundService>();

        return services;
    }

    /// <summary>
    /// Data source connectors, deduplication, secret encryption,
    /// connector sync, and demo service health monitoring.
    /// </summary>
    public static IServiceCollection AddConnectorInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.AddScoped<IDataSourceService, DataSourceService>();
        services.AddScoped<IDeduplicationService, DeduplicationService>();
        services.AddSingleton<ISecretEncryptionService, SecretEncryptionService>();
        services.AddScoped<IConnectorConfigurationService, ConnectorConfigurationService>();
        services.AddScoped<PlatformSettingsService>();
        services.AddScoped<IConnectorSyncService, ConnectorSyncService>();
        services.AddScoped<IConnectorCursorResetService, ConnectorCursorResetService>();
        // Singleton: holds the in-memory job registry so a reset started by one request can be
        // polled by later requests. Creates its own DI scopes for the scoped reset engine.
        services.AddSingleton<IConnectorCursorResetJobService, ConnectorCursorResetJobService>();

        // Connector runtime
        services.AddBaseConnectorServices();
        services.AddScoped<IGlucosePublisher, GlucosePublisher>();
        services.AddScoped<ITreatmentPublisher, TreatmentPublisher>();
        services.AddScoped<IDevicePublisher, DevicePublisher>();
        services.AddScoped<IMetadataPublisher, MetadataPublisher>();
        services.AddScoped<IConnectorPublisher, InProcessConnectorPublisher>();
        services.AddConnectors(
            configuration,
            backgroundServiceAssembly: typeof(Program).Assembly
        );

        // Demo service health monitor
        services.AddHttpClient("DemoServiceHealth");
        services.AddHostedService<DemoServiceHealthMonitor>();

        return services;
    }

    /// <summary>
    /// Registers the <see cref="Nocturne.Core.Contracts.Alerts.IAlertEvaluationEngine"/>
    /// seam: all three engine implementations plus the singleton
    /// <see cref="Nocturne.API.Services.Alerts.Engines.AlertEngineSelection"/> resolved
    /// from the <c>Alerts:Engine</c> flag (<c>managed</c> | <c>shadow</c> | <c>rust</c>,
    /// default <c>managed</c>). The native-library probe runs once, on first resolution;
    /// rust/shadow degrade gracefully to managed with a logged warning when the
    /// nocturne_alerts library can't load.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration carrying the <c>Alerts:Engine</c> flag.</param>
    /// <param name="nativeProbe">
    /// Native-library availability probe override for tests; defaults to
    /// <see cref="Nocturne.Core.Alerts.Native.AlertsInterop.IsAvailable"/> (the version export).
    /// </param>
    public static IServiceCollection AddAlertEvaluationEngine(
        this IServiceCollection services,
        IConfiguration configuration,
        Func<bool>? nativeProbe = null)
    {
        services.AddScoped<Nocturne.API.Services.Alerts.Engines.ManagedAlertEngine>();
        services.AddScoped<Nocturne.API.Services.Alerts.Engines.RustBackedAlertEngine>();
        services.AddScoped<
            Nocturne.API.Services.Alerts.Engines.IShadowRuleEvaluator,
            Nocturne.API.Services.Alerts.Engines.RustShadowRuleEvaluator>();
        services.AddScoped<Nocturne.API.Services.Alerts.Engines.ShadowAlertEngine>();

        // Singleton so the configuration parse + native probe + selection log happen once
        // (lazily, on the first scope that evaluates alerts).
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(Nocturne.API.Services.Alerts.Engines.AlertEngineSelector).FullName!);
            return Nocturne.API.Services.Alerts.Engines.AlertEngineSelector.Select(
                configuration[Nocturne.API.Services.Alerts.Engines.AlertEngineSelector.ConfigurationKey],
                nativeProbe ?? Nocturne.Core.Alerts.Native.AlertsInterop.IsAvailable,
                logger);
        });

        services.AddScoped<Nocturne.Core.Contracts.Alerts.IAlertEvaluationEngine>(sp =>
            sp.GetRequiredService<Nocturne.API.Services.Alerts.Engines.AlertEngineSelection>().Mode switch
            {
                Nocturne.API.Services.Alerts.Engines.AlertEngineMode.Rust =>
                    sp.GetRequiredService<Nocturne.API.Services.Alerts.Engines.RustBackedAlertEngine>(),
                Nocturne.API.Services.Alerts.Engines.AlertEngineMode.Shadow =>
                    sp.GetRequiredService<Nocturne.API.Services.Alerts.Engines.ShadowAlertEngine>(),
                _ => sp.GetRequiredService<Nocturne.API.Services.Alerts.Engines.ManagedAlertEngine>(),
            });

        return services;
    }

    /// <summary>
    /// Registers every <see cref="IConditionEvaluator"/> implementation that the
    /// <see cref="ConditionEvaluatorRegistry"/> resolves at runtime. Extracted from
    /// <see cref="AddAlertingAndMonitoring"/> so production wiring and the registry
    /// coverage tests can share the same single source of truth — adding a new
    /// evaluator here automatically updates both.
    /// </summary>
    public static IServiceCollection AddAlertEvaluators(this IServiceCollection services)
    {
        services.AddScoped<IConditionEvaluator, ThresholdEvaluator>();
        services.AddScoped<IConditionEvaluator, RateOfChangeEvaluator>();
        services.AddScoped<IConditionEvaluator, StalenessEvaluator>();
        services.AddScoped<IConditionEvaluator, CompositeEvaluator>();
        services.AddScoped<IConditionEvaluator, NotEvaluator>();
        services.AddScoped<IConditionEvaluator, SustainedEvaluator>();
        services.AddScoped<IConditionEvaluator, PredictedEvaluator>();
        services.AddScoped<IConditionEvaluator, TrendEvaluator>();
        services.AddScoped<IConditionEvaluator, TimeOfDayEvaluator>();
        services.AddScoped<IConditionEvaluator, IobEvaluator>();
        services.AddScoped<IConditionEvaluator, CobEvaluator>();
        services.AddScoped<IConditionEvaluator, ReservoirEvaluator>();
        services.AddScoped<IConditionEvaluator, SiteAgeEvaluator>();
        services.AddScoped<IConditionEvaluator, SensorAgeEvaluator>();
        services.AddScoped<IConditionEvaluator, AlertStateEvaluator>();
        services.AddScoped<IConditionEvaluator, LoopStaleEvaluator>();
        services.AddScoped<IConditionEvaluator, LoopEnactionStaleEvaluator>();
        services.AddScoped<IConditionEvaluator, PumpSuspendedEvaluator>();
        services.AddScoped<IConditionEvaluator, PumpBatteryEvaluator>();
        services.AddScoped<IConditionEvaluator, TempBasalEvaluator>();
        services.AddScoped<IConditionEvaluator, UploaderBatteryEvaluator>();
        services.AddScoped<IConditionEvaluator, OverrideActiveEvaluator>();
        services.AddScoped<IConditionEvaluator, SensitivityRatioEvaluator>();
        services.AddScoped<IConditionEvaluator, DoNotDisturbEvaluator>();
        services.AddScoped<IConditionEvaluator, GlucoseBucketEvaluator>();
        services.AddScoped<IConditionEvaluator, TimeSinceLastCarbEvaluator>();
        services.AddScoped<IConditionEvaluator, TimeSinceLastBolusEvaluator>();
        services.AddScoped<IConditionEvaluator, DayOfWeekEvaluator>();
        services.AddScoped<IConditionEvaluator, PumpStateEvaluator>();
        services.AddScoped<IConditionEvaluator, StateSpanActiveEvaluator>();
        services.AddScoped<IConditionEvaluator, SleepSessionActiveEvaluator>();
        services.AddScoped<IConditionEvaluator, TrackerAgeEvaluator>();
        return services;
    }

    /// <summary>
    /// Migration job service and startup migration check.
    /// </summary>
    public static IServiceCollection AddMigrationServices(this IServiceCollection services)
    {
        services.AddSingleton<
            Nocturne.API.Services.Migration.IMigrationJobService,
            Nocturne.API.Services.Migration.MigrationJobService
        >();
        services.AddHostedService<Nocturne.API.Services.Migration.MigrationStartupService>();

        return services;
    }
}

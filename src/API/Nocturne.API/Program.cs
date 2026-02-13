using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Nocturne.API.Configuration;
using Nocturne.API.Extensions;
using Nocturne.API.Hubs;
using Nocturne.API.Middleware;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.OpenApi;
using Nocturne.API.Services;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Notifiers;
using Nocturne.API.Services.Alerts.Webhooks;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Services.ConnectorPublishing;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Dexcom;
using Nocturne.Connectors.FreeStyle;
using Nocturne.Connectors.Glooko;
using Nocturne.Connectors.MyLife;
using Nocturne.Connectors.Tidepool;
using Nocturne.Connectors.MyFitnessPal;
using Nocturne.Connectors.Nightscout;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Cache.Extensions;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Shared.Services;
using OpenTelemetry.Logs;
using Scalar.AspNetCore;
using EmailOptions = Nocturne.Core.Models.Configuration.EmailOptions;
using JwtOptions = Nocturne.Core.Models.Configuration.JwtOptions;
using LocalIdentityOptions = Nocturne.Core.Models.Configuration.LocalIdentityOptions;
using OidcOptions = Nocturne.Core.Models.Configuration.OidcOptions;

var builder = WebApplication.CreateBuilder(args);

// Try to find appsettings.json in solution root first, fallback to current directory
var configPath = Directory.GetCurrentDirectory();
var solutionRoot = Path.GetFullPath(Path.Combine(configPath, "..", "..", ".."));

if (File.Exists(Path.Combine(solutionRoot, "appsettings.json")))
{
    // Local development - use solution root
    builder.Environment.ContentRootPath = solutionRoot;
    configPath = solutionRoot;
}

// else: Docker or other deployment - use current directory (where files are copied)

builder.Configuration.SetBasePath(configPath);

// Add additional configuration
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
builder.Configuration.AddJsonFile(
    $"appsettings.{builder.Environment.EnvironmentName}.json",
    optional: true,
    reloadOnChange: true
);

// Ensure environment variables (injected by Aspire) take precedence over appsettings.json
builder.Configuration.AddEnvironmentVariables();

if (string.IsNullOrEmpty(builder.Configuration["NocturneApiUrl"]))
{
    var baseUrl = builder.Configuration["BaseUrl"];
    if (!string.IsNullOrEmpty(baseUrl))
    {
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["NocturneApiUrl"] = baseUrl }
        );
    }
}

// Configure Kestrel to allow larger request bodies for analytics endpoints
// 90 days of demo data can exceed the 30MB default limit
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 100 * 1024 * 1024; // 100 MB
});

builder.AddServiceDefaults();

builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = builder.Environment.IsDevelopment();
    options.ValidateOnBuild = builder.Environment.IsDevelopment();
});

// Configure PostgreSQL database
var aspirePostgreSqlConnection = builder.Configuration.GetConnectionString(ServiceNames.PostgreSql);

if (string.IsNullOrWhiteSpace(aspirePostgreSqlConnection))
{
    throw new InvalidOperationException(
        $"PostgreSQL connection string '{ServiceNames.PostgreSql}' not found. Ensure Aspire is properly configured."
    );
}

builder.Services.AddPostgreSqlInfrastructure(
    aspirePostgreSqlConnection,
    config =>
    {
        config.EnableDetailedErrors = builder.Environment.IsDevelopment();
        config.EnableSensitiveDataLogging = builder.Environment.IsDevelopment();
    }
);

builder.Services.AddDiscrepancyAnalysisRepository();
builder.Services.AddAlertRepositories();

builder.Services.AddDataProtection();

// Add compatibility proxy services
builder.Services.AddCompatibilityProxyServices(builder.Configuration);

// Use in-memory cache for single-user deployments
builder.Services.AddNocturneMemoryCache();

builder.Logging.ClearProviders();
builder.Logging.AddOpenTelemetry(logging => logging.AddConsoleExporter());

// Configure Loop settings
builder.Services.Configure<LoopConfiguration>(options =>
{
    // Read from environment variables (matches legacy env.extendedSettings.loop)
    options.ApnsKey = Environment.GetEnvironmentVariable("LOOP_APNS_KEY");
    options.ApnsKeyId = Environment.GetEnvironmentVariable("LOOP_APNS_KEY_ID");
    options.DeveloperTeamId = Environment.GetEnvironmentVariable("LOOP_DEVELOPER_TEAM_ID");
    options.PushServerEnvironment =
        Environment.GetEnvironmentVariable("LOOP_PUSH_SERVER_ENVIRONMENT") ?? "development";
});

var loopApnsKeyId = Environment.GetEnvironmentVariable("LOOP_APNS_KEY_ID");
Console.WriteLine(
    $"Loop configuration loaded - APNS Key ID: {(string.IsNullOrEmpty(loopApnsKeyId) ? "Not configured" : $"{loopApnsKeyId[..Math.Min(4, loopApnsKeyId.Length)]}****")}"
);

// Add services

// Add native API services for strangler pattern
// Note: NightscoutJsonFilter is added globally to apply null-omission and
// NocturneOnly field exclusion to v1-v3 API responses only
builder.Services.AddControllers(options =>
{
    options.Filters.Add<NightscoutJsonFilter>();
});
builder.Services.AddEndpointsApiExplorer();

// Note: Using NSwag instead of Microsoft.AspNetCore.OpenApi for better compatibility
builder.Services.AddOpenApi();

// Add OpenAPI document generation with NSwag
builder.Services.AddOpenApiDocument(config =>
{
    // Add remote function metadata processor
    config.OperationProcessors.Add(new RemoteFunctionOperationProcessor());

    config.PostProcess = document =>
    {
        document.Info.Version = "v1";
        document.Info.Title = "Nocturne API";
        document.Info.Description = "Modern C# rewrite of Nightscout API with 1:1 compatibility";
        document.Info.Contact = new NSwag.OpenApiContact
        {
            Name = "Nocturne API",
            Url = "https://github.com/ryceg/nocturne",
        };
        document.Info.License = new NSwag.OpenApiLicense
        {
            Name = "Use under LICX",
            Url = "https://example.com/license",
        };
    };
});

// Register native API services with proper dependency injection
builder.Services.AddScoped<IStatusService, StatusService>();
builder.Services.AddScoped<IVersionService, VersionService>();
builder.Services.AddScoped<IDataFormatService, DataFormatService>();
builder.Services.AddSingleton<IXmlDocumentationService, XmlDocumentationService>();
builder.Services.AddScoped<IDocumentProcessingService, DocumentProcessingService>();
builder.Services.AddScoped<ITreatmentProcessingService, TreatmentProcessingService>();

builder.Services.AddScoped<IBraceExpansionService, BraceExpansionService>();
builder.Services.AddScoped<ITimeQueryService, TimeQueryService>();

builder.Services.AddScoped<IDDataService, DDataService>();
builder.Services.AddScoped<IPropertiesService, PropertiesService>();
builder.Services.AddScoped<ISummaryService, SummaryService>();
builder.Services.AddScoped<IIobService, IobService>();
builder.Services.AddScoped<IPredictionService, PredictionService>();
builder.Services.AddScoped<ICobService, CobService>();
builder.Services.AddScoped<IProfileService, ProfileService>();
builder.Services.AddScoped<IAr2Service, Ar2Service>();
builder.Services.AddScoped<IBolusWizardService, BolusWizardService>();

// oref Rust WASM service - provides high-performance IOB/COB/determine-basal calculations
builder.Services.AddOrefService(options =>
{
    // Look for WASM file in standard locations
    options.WasmPath = "oref.wasm";
    options.Enabled = true;
});

builder.Services.AddScoped<IDirectionService, DirectionService>();
builder.Services.AddScoped<INotificationV2Service, NotificationV2Service>();
builder.Services.AddScoped<INotificationV1Service, NotificationV1Service>();
builder.Services.AddScoped<IApnsClientFactory, ApnsClientFactory>();
builder.Services.AddScoped<ILoopService, LoopService>();
builder.Services.AddScoped<IOpenApsService, OpenApsService>();
builder.Services.AddScoped<IPumpAlertService, PumpAlertService>();
builder.Services.AddScoped<IAuthorizationService, AuthorizationService>();
builder.Services.AddScoped<IAlexaService, AlexaService>();

// Authentication and authorization services
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<OidcOptions>(builder.Configuration.GetSection(OidcOptions.SectionName));
builder.Services.Configure<LocalIdentityOptions>(
    builder.Configuration.GetSection(LocalIdentityOptions.SectionName)
);
builder.Services.Configure<EmailOptions>(
    builder.Configuration.GetSection(EmailOptions.SectionName)
);
builder.Services.AddScoped<IJwtService, JwtService>();
builder.Services.AddScoped<IRefreshTokenService, RefreshTokenService>();
builder.Services.AddScoped<ISubjectService, SubjectService>();
builder.Services.AddScoped<IRoleService, RoleService>();
builder.Services.AddScoped<IOidcProviderService, OidcProviderService>();
builder.Services.AddScoped<IOidcAuthService, OidcAuthService>();

// Local identity provider services (built-in authentication without external OIDC)
builder.Services.AddScoped<ILocalIdentityService, LocalIdentityService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddHostedService<UserSeedService>();
builder.Services.AddHostedService<AuthorizationSeedService>();

// Register authentication handlers for the middleware pipeline
// Handlers are executed in priority order (lowest first)
builder.Services.AddSingleton<IAuthHandler, SessionCookieHandler>(); // Priority 50 (web sessions)
builder.Services.AddSingleton<IAuthHandler, OidcTokenHandler>(); // Priority 100
builder.Services.AddSingleton<IAuthHandler, LegacyJwtHandler>(); // Priority 200
builder.Services.AddSingleton<IAuthHandler, AccessTokenHandler>(); // Priority 300
builder.Services.AddSingleton<IAuthHandler, ApiSecretHandler>(); // Priority 400

// Add HTTP client for OIDC provider discovery
builder.Services.AddHttpClient(
    "OidcProvider",
    client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
    }
);

// Statistics service for analytics and calculations
builder.Services.AddScoped<IStatisticsService, StatisticsService>();

// Compression low detection services
builder.Services.AddScoped<ICompressionLowRepository, CompressionLowRepository>();
builder.Services.AddScoped<ICompressionLowService, CompressionLowService>();
builder.Services.AddSingleton<CompressionLowDetectionService>();
builder.Services.AddSingleton<ICompressionLowDetectionService>(sp =>
    sp.GetRequiredService<CompressionLowDetectionService>()
);
builder.Services.AddHostedService(sp => sp.GetRequiredService<CompressionLowDetectionService>());

// Data source service for services/connectors management
builder.Services.AddScoped<IDataSourceService, DataSourceService>();

// Deduplication service for linking records from multiple data sources
builder.Services.AddScoped<IDeduplicationService, DeduplicationService>();

// Secret encryption service - singleton since key derivation should happen once
builder.Services.AddSingleton<ISecretEncryptionService, SecretEncryptionService>();

// Connector configuration service for runtime config and secrets management
builder.Services.AddScoped<IConnectorConfigurationService, ConnectorConfigurationService>();
builder.Services.AddScoped<IConnectorSyncService, ConnectorSyncService>();

// Connector runtime services (single executable)
builder.Services.AddBaseConnectorServices();
builder.Services.AddScoped<IConnectorPublisher, InProcessConnectorPublisher>();
builder.Services.AddDexcomConnector(builder.Configuration);
builder.Services.AddGlookoConnector(builder.Configuration);
builder.Services.AddLibreLinkUpConnector(builder.Configuration);
builder.Services.AddMyLifeConnector(builder.Configuration);
builder.Services.AddTidepoolConnector(builder.Configuration);
builder.Services.AddMyFitnessPalConnector(builder.Configuration);
builder.Services.AddNightscoutConnector(builder.Configuration);

static bool IsConnectorEnabled(IConfiguration configuration, string connectorName)
{
    var section = configuration.GetSection($"Parameters:Connectors:{connectorName}");
    if (!section.Exists())
        section = configuration.GetSection($"Connectors:{connectorName}");

    return section.GetValue<bool>("Enabled");
}

if (IsConnectorEnabled(builder.Configuration, "Dexcom"))
    builder.Services.AddHostedService<DexcomConnectorBackgroundService>();
if (IsConnectorEnabled(builder.Configuration, "Glooko"))
    builder.Services.AddHostedService<GlookoConnectorBackgroundService>();
if (IsConnectorEnabled(builder.Configuration, "LibreLinkUp"))
    builder.Services.AddHostedService<FreeStyleConnectorBackgroundService>();
if (IsConnectorEnabled(builder.Configuration, "MyLife"))
    builder.Services.AddHostedService<MyLifeConnectorBackgroundService>();
if (IsConnectorEnabled(builder.Configuration, "Tidepool"))
    builder.Services.AddHostedService<TidepoolConnectorBackgroundService>();
if (IsConnectorEnabled(builder.Configuration, "MyFitnessPal"))
    builder.Services.AddHostedService<MyFitnessPalConnectorBackgroundService>();
if (IsConnectorEnabled(builder.Configuration, "Nightscout"))
    builder.Services.AddHostedService<NightscoutConnectorBackgroundService>();

// Configure JWT authentication
var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName);
var secretKey = jwtOptions["SecretKey"] ?? "DefaultSecretKeyForNocturneWhichShouldBeChanged";
var key = Encoding.ASCII.GetBytes(secretKey);

builder
    .Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = false;
        options.SaveToken = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(key),
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
        };
    });

builder.Services.AddAuthorization();

// Configure CORS for frontend with credentials support
// Note: AllowAnyOrigin() cannot be combined with AllowCredentials() per CORS spec
// Using SetIsOriginAllowed to dynamically allow origins while supporting cookies
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .SetIsOriginAllowed(_ => true) // Allow any origin (development-friendly, restrict in production)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials(); // Required for cookies/auth to work cross-origin
    });
});

// Add HTTP client for dotAPNS (Apple Push Notifications)
builder.Services.AddHttpClient(
    "dotAPNS",
    client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30); // APNS-specific timeout
    }
);

// Add SignalR for real-time communication
builder.Services.AddSignalR();

// Register SignalR broadcast service
builder.Services.AddScoped<ISignalRBroadcastService, SignalRBroadcastService>();

// Register tracker trigger service for auto-starting trackers from treatments
builder.Services.AddScoped<ITrackerTriggerService, TrackerTriggerService>();

// Register tracker alert service for evaluating tracker thresholds and generating alerts
builder.Services.AddScoped<ITrackerAlertService, TrackerAlertService>();

// Register tracker suggestion service for suggesting tracker resets based on treatments/sensor gaps
builder.Services.AddScoped<ITrackerSuggestionService, TrackerSuggestionService>();

// Register in-app notification repository and service
builder.Services.AddScoped<InAppNotificationRepository>();
builder.Services.AddScoped<IInAppNotificationService, InAppNotificationService>();

// Register meal matching repository and service
builder.Services.AddScoped<IConnectorFoodEntryRepository, ConnectorFoodEntryRepository>();
builder.Services.AddScoped<IMealMatchingService, MealMatchingService>();

// Register legacy device age service (bridges Tracker system to legacy deviceage endpoints)
builder.Services.AddScoped<ILegacyDeviceAgeService, LegacyDeviceAgeService>();

// Register demo mode service for querying demo mode status
// This service is used by EntryService, TreatmentService, and StatusService to filter data
builder.Services.AddSingleton<IDemoModeService, DemoModeService>();

// Register UI settings service for frontend configuration persistence
builder.Services.AddScoped<IUISettingsService, UISettingsService>();

// Register domain services for WebSocket broadcasting
builder.Services.AddScoped<ITreatmentService, TreatmentService>();
builder.Services.AddScoped<IEntryService, EntryService>();
builder.Services.AddScoped<IStateSpanService, StateSpanService>();
builder.Services.AddScoped<IDeviceStatusService, DeviceStatusService>();
builder.Services.AddScoped<IBatteryService, BatteryService>();
builder.Services.AddScoped<IProfileDataService, ProfileDataService>();
builder.Services.AddScoped<IFoodService, FoodService>();
builder.Services.AddScoped<IConnectorFoodEntryService, ConnectorFoodEntryService>();
builder.Services.AddScoped<ITreatmentFoodService, TreatmentFoodService>();
builder.Services.AddScoped<IUserFoodFavoriteService, UserFoodFavoriteService>();
builder.Services.AddScoped<IActivityService, ActivityService>();
builder.Services.AddScoped<
    IMyFitnessPalMatchingSettingsService,
    MyFitnessPalMatchingSettingsService
>();
builder.Services.AddScoped<IClockFaceService, ClockFaceService>();
builder.Services.AddScoped<IChartDataService, ChartDataService>();

// Note: Processing status service is registered by AddNocturneMemoryCache

// Register demo service health monitor
// Demo data generation is handled by the separate Nocturne.Services.Demo service
// The API only monitors the demo service health and cleans up demo data when the service stops
builder.Services.AddHttpClient("DemoServiceHealth");
builder.Services.AddHostedService<DemoServiceHealthMonitor>();

// Configure device health settings
builder.Services.Configure<DeviceHealthOptions>(
    builder.Configuration.GetSection(DeviceHealthOptions.SectionName)
);

// Register device health services
builder.Services.AddScoped<IDeviceRegistryService, DeviceRegistryService>();

// Configure alert monitoring settings
builder.Services.Configure<AlertMonitoringOptions>(
    builder.Configuration.GetSection(AlertMonitoringOptions.SectionName)
);
builder.Services.AddScoped<WebhookRequestSender>();

// Register alert engines
builder.Services.AddScoped<IAlertRulesEngine, AlertRulesEngine>();
builder.Services.AddScoped<IAlertProcessingService, AlertProcessingService>();
builder.Services.AddScoped<IAlertOrchestrator, AlertOrchestrator>();
builder.Services.AddScoped<IDeviceAlertEngine, DeviceAlertEngine>();
builder.Services.AddScoped<INotifierDispatcher, NotifierDispatcher>();
builder.Services.AddScoped<INotifier, SignalRNotifier>();
builder.Services.AddScoped<INotifier, WebhookNotifier>();

var pushoverApiToken =
    builder.Configuration[ServiceNames.ConfigKeys.PushoverApiToken]
    ?? builder.Configuration[ServiceNames.ConfigKeys.PushoverApiTokenEnv];
var pushoverUserKey =
    builder.Configuration[ServiceNames.ConfigKeys.PushoverUserKey]
    ?? builder.Configuration[ServiceNames.ConfigKeys.PushoverUserKeyEnv];

if (!string.IsNullOrWhiteSpace(pushoverApiToken) && !string.IsNullOrWhiteSpace(pushoverUserKey))
{
    builder.Services.AddHttpClient<IPushoverService, PushoverService>();
    builder.Services.AddScoped<INotifier, PushoverNotifier>();
}

// Register device health monitoring background service
builder.Services.AddHostedService<DeviceHealthMonitoringService>();

// Configure analytics settings
builder.Services.Configure<AnalyticsConfiguration>(
    builder.Configuration.GetSection(AnalyticsConfiguration.SectionName)
);

// Register analytics services
builder.Services.AddScoped<IAnalyticsService, AnalyticsService>();

// Register connector health service
builder.Services.AddScoped<IConnectorHealthService, ConnectorHealthService>();

// Register migration job service for data migration from Nightscout
builder.Services.AddSingleton<
    Nocturne.API.Services.Migration.IMigrationJobService,
    Nocturne.API.Services.Migration.MigrationJobService
>();

// Register migration startup service to check for pending migrations and create admin notifications
builder.Services.AddHostedService<Nocturne.API.Services.Migration.MigrationStartupService>();

// Register notification resolution background service for auto-resolving notifications
builder.Services.AddHostedService<NotificationResolutionService>();

var app = builder.Build();

// Configure middleware pipeline
app.UseCors();

// Add JSON extension middleware to handle .json suffixes for legacy compatibility
app.UseMiddleware<JsonExtensionMiddleware>();

// Add Nightscout authentication middleware
app.UseMiddleware<AuthenticationMiddleware>();

// Add authentication and authorization middleware
app.UseAuthentication();
app.UseAuthorization();

// Map native API controllers
app.MapControllers();

// Map SignalR hubs for real-time communication
app.MapHub<DataHub>("/hubs/data");
app.MapHub<AlarmHub>("/hubs/alarms");
app.MapHub<ConfigHub>("/hubs/config");

// Note: Using NSwag instead of Microsoft.AspNetCore.OpenApi for better compatibility
// OpenAPI documents are served at /openapi/{documentName}.json
app.MapOpenApi();

// Scalar API Reference provides interactive API documentation at /scalar
app.MapScalarApiReference(options =>
{
    options.WithTitle("Nocturne API Documentation");
    options.WithTheme(Scalar.AspNetCore.ScalarTheme.Mars);
});

// Add root endpoint to serve a basic info page
app.MapGet(
    "/",
    async (IPostgreSqlService postgreSqlService) =>
    {
        // Check database connection by fetching the latest entry
        string databaseStatus = "unknown";
        object? latestEntry = null;

        try
        {
            var entry = await postgreSqlService.GetCurrentEntryAsync();

            if (entry != null)
            {
                databaseStatus = "connected";
                latestEntry = new
                {
                    date = entry.Date,
                    dateString = entry.DateString,
                    sgv = entry.Sgv,
                    mbg = entry.Mbg,
                    direction = entry.Direction,
                };
            }
            else
            {
                databaseStatus = "connected_no_data";
            }
        }
        catch (Exception)
        {
            databaseStatus = "disconnected";
        }

        return Results.Json(
            new
            {
                name = "Nocturne API",
                version = "1.0.0",
                description = "Modern C# rewrite of Nightscout API",
                api_documentation = "/openapi/v1.json",
                aspire_dashboard_note = "API documentation is available via Scalar in the Aspire dashboard",
                database_status = databaseStatus,
                latest_entry = latestEntry,
                endpoints = new
                {
                    status = "/api/v1/status",
                    entries = "/api/v1/entries",
                    treatments = "/api/v1/treatments",
                    profile = "/api/v1/profile",
                    versions = "/api/versions",
                },
            }
        );
    }
);

app.MapDefaultEndpoints();

// Skip database migrations when running in NSwag/OpenAPI generation mode
// NSwag launches the app to extract the OpenAPI schema, but we don't need DB access for that
var isNSwagGeneration = IsRunningInNSwagContext();
if (!isNSwagGeneration)
{
    // Initialize PostgreSQL database with migrations
    // IMPORTANT: Do not catch exceptions here - if migrations fail, the app should not start
    // This ensures the database schema is always in a valid state before handling requests
    Console.WriteLine("Running PostgreSQL database migrations...");
    await app.Services.MigrateDatabaseAsync();
    Console.WriteLine("PostgreSQL database migrations completed successfully.");
}
else
{
    Console.WriteLine("[NSwag] Skipping database migrations - running in OpenAPI generation mode");
}

app.Run();

// Detects if the application is being run by NSwag for OpenAPI document generation.
// NSwag uses its AspNetCore.Launcher to load and introspect the app without actually running it.
static bool IsRunningInNSwagContext()
{
    // Check if the entry assembly is the NSwag launcher
    var entryAssembly = System.Reflection.Assembly.GetEntryAssembly();
    if (
        entryAssembly?.GetName().Name?.Contains("NSwag", StringComparison.OrdinalIgnoreCase) == true
    )
    {
        return true;
    }

    // Check command line for NSwag invocation (covers dotnet exec scenarios)
    var commandLine = Environment.CommandLine;
    if (
        commandLine.Contains("NSwag", StringComparison.OrdinalIgnoreCase)
        || commandLine.Contains("nswag", StringComparison.OrdinalIgnoreCase)
    )
    {
        return true;
    }

    return false;
}

// Make Program accessible for testing
namespace Nocturne.API
{
    public partial class Program { }
}

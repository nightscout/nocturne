using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.ServiceDiscovery;
using Microsoft.IdentityModel.Tokens;
using Nocturne.API.Configuration;
using Nocturne.API.Extensions;
using Nocturne.API.Hubs;
using Nocturne.API.Middleware;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Services;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.BackgroundServices;

using Nocturne.Connectors.Configurations;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Dexcom.Services;
using Nocturne.Connectors.FreeStyle.Services;
using Nocturne.Connectors.Glooko.Services;
using Nocturne.Connectors.MiniMed.Services;
using Nocturne.Connectors.MyFitnessPal.Services;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Cache.Extensions;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;
using NSwag;
using OpenTelemetry.Logs;
using Polly;
using Scalar.AspNetCore;

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

// Configure Kestrel to allow larger request bodies for analytics endpoints
// 90 days of demo data can exceed the 30MB default limit
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 100 * 1024 * 1024; // 100 MB
});

builder.AddServiceDefaults();

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

// Data source service for services/connectors management
builder.Services.AddScoped<IDataSourceService, DataSourceService>();

// Deduplication service for linking records from multiple data sources
builder.Services.AddScoped<IDeduplicationService, DeduplicationService>();

// Connector sync service for triggering granular syncs
builder.Services.AddScoped<IConnectorSyncService, ConnectorSyncService>();

// HTTP client for connector sync operations
// Connector sync calls can take a long time (multiple API calls to fetch data)
// Timeouts are set to be less than the minimum sync interval (5 minutes) to prevent overlapping syncs
builder
    .Services.AddHttpClient(
        "ConnectorSync",
        client =>
        {
            // Allow up to 5 minutes for the entire sync operation
            // This should be less than the minimum sync interval to prevent overlap
            client.Timeout = TimeSpan.FromMinutes(5);
        }
    )
    .AddResilienceHandler(
        "ConnectorSyncResilience",
        builder =>
        {
            // Total timeout for the sync operation (including retries)
            // Must be <= HttpClient.Timeout to be effective
            builder.AddTimeout(TimeSpan.FromMinutes(5));

            // Retry transient failures (but not timeouts - those should propagate)
            builder.AddRetry(
                new Microsoft.Extensions.Http.Resilience.HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = 1, // Only 1 retry to stay within timeout budget
                    Delay = TimeSpan.FromSeconds(3),
                    BackoffType = Polly.DelayBackoffType.Constant,
                    UseJitter = false,
                    // Only retry on connection/network errors, not timeouts
                    ShouldHandle = args =>
                        ValueTask.FromResult(
                            args.Outcome.Exception is HttpRequestException
                                || args.Outcome.Result?.StatusCode
                                    == System.Net.HttpStatusCode.ServiceUnavailable
                                || args.Outcome.Result?.StatusCode
                                    == System.Net.HttpStatusCode.BadGateway
                                || args.Outcome.Result?.StatusCode
                                    == System.Net.HttpStatusCode.GatewayTimeout
                        ),
                }
            );

            // Per-attempt timeout (each individual request)
            builder.AddTimeout(TimeSpan.FromMinutes(2));
        }
    );

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

// Register tracker seed service for creating default definitions
builder.Services.AddScoped<ITrackerSeedService, TrackerSeedService>();

// Register tracker alert service for evaluating tracker thresholds and generating alerts
builder.Services.AddScoped<ITrackerAlertService, TrackerAlertService>();

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

// Register alert engines
builder.Services.AddScoped<IAlertRulesEngine, AlertRulesEngine>();
builder.Services.AddScoped<IDeviceAlertEngine, DeviceAlertEngine>();

// Register device health monitoring background service
builder.Services.AddHostedService<DeviceHealthMonitoringService>();

// Configure analytics settings
builder.Services.Configure<AnalyticsConfiguration>(
    builder.Configuration.GetSection(AnalyticsConfiguration.SectionName)
);

// Register analytics services
builder.Services.AddScoped<IAnalyticsService, AnalyticsService>();

// Register connector health service with service discovery-enabled HTTP client
builder.Services.AddHttpClient(ConnectorHealthService.HttpClientName).AddServiceDiscovery();
builder.Services.AddScoped<IConnectorHealthService, ConnectorHealthService>();

// Register migration job service for data migration from Nightscout
builder.Services.AddSingleton<Nocturne.API.Services.Migration.IMigrationJobService, Nocturne.API.Services.Migration.MigrationJobService>();

// Register migration startup service to check for pending migrations and create admin notifications
builder.Services.AddHostedService<Nocturne.API.Services.Migration.MigrationStartupService>();

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

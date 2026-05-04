using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using Nocturne.API.Authorization;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Audit;
using Nocturne.API.Extensions;
using Nocturne.API.Filters;
using Nocturne.API.Hubs;
using Nocturne.API.Middleware;
using Nocturne.API.Multitenancy;
using OpenApi.Remote.Processors;
using Nocturne.API.OpenApi;
using Scalar.AspNetCore;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Cache.Extensions;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using OpenTelemetry.Logs;
using FluentValidation;
using FluentValidation.AspNetCore;
using JwtOptions = Nocturne.Core.Models.Configuration.JwtOptions;

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

// Config layering (later sources override earlier):
//   1. appsettings.example.json — committed defaults, safe to ship in container images.
//   2. appsettings.json — gitignored user overrides (optional; developers copy from example).
//   3. appsettings.{Environment}.json — environment-specific overrides.
//   4. Environment variables — runtime overrides (takes precedence over all files).
// Secrets should NEVER live in appsettings.json — use env vars or user-secrets.
builder.Configuration.AddJsonFile("appsettings.example.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
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
// Two connection strings: app role (nocturne-postgres) for runtime, migrator role
// (nocturne-postgres-migrator) for running migrations at startup. Both are required
// when migrations run; the migrator string is optional in NSwag/Testing mode.
var isTesting = builder.Environment.IsEnvironment("Testing");
var aspirePostgreSqlConnection = builder.Configuration.GetConnectionString(ServiceNames.PostgreSql)
    ?? (isTesting ? "" : throw new InvalidOperationException(
        $"ConnectionStrings:{ServiceNames.PostgreSql} is required."));
var migratorConnectionString = builder.Configuration.GetConnectionString($"{ServiceNames.PostgreSql}-migrator");

if (!isTesting)
{
    builder.Services.AddPostgreSqlInfrastructure(
        aspirePostgreSqlConnection,
        config =>
        {
            config.EnableDetailedErrors = builder.Environment.IsDevelopment();
            config.EnableSensitiveDataLogging = builder.Environment.IsDevelopment();
        }
    );
}
else
{
    // In Testing mode, skip NpgsqlDataSource creation (test factories provide their
    // own SQLite-backed IDbContextFactory) but still register repositories and shared
    // services so the DI container can resolve them for endpoint routing.
    builder.Services.AddDataServices();
}

builder.Services.AddDiscrepancyAnalysisRepository();
builder.Services.AddAlertRepositories();

builder.Services.AddDataProtection();

// Add compatibility proxy services
builder.Services.AddCompatibilityProxyServices(builder.Configuration);

// Use in-memory cache for single-user deployments
builder.Services.AddNocturneMemoryCache();

builder.Logging.ClearProviders();
builder.Logging.AddOpenTelemetry(logging => logging.AddConsoleExporter());

var loopApnsKeyId = builder.Configuration["Loop:ApnsKeyId"];
Console.WriteLine(
    $"Loop configuration loaded - APNS Key ID: {(string.IsNullOrEmpty(loopApnsKeyId) ? "Not configured" : $"{loopApnsKeyId[..Math.Min(4, loopApnsKeyId.Length)]}****")}"
);

// Add response caching for GET endpoints
builder.Services.AddResponseCaching();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAuditContext, AuditContext>();
builder.Services.AddHostedService<AuditRetentionService>();

// Add native API services for strangler pattern
// Note: NightscoutJsonFilter is added globally to apply null-omission and
// NocturneOnly field exclusion to v1-v3 API responses only
builder.Services.AddScoped<ReadAccessAuditFilter>();
builder.Services.AddControllers(options =>
{
    options.Filters.Add<NightscoutJsonFilter>();
    options.Filters.AddService<ReadAccessAuditFilter>();
})
.ConfigureApplicationPartManager(manager =>
{
    if (!builder.Environment.IsDevelopment())
    {
        manager.FeatureProviders.Add(new RemoveDevOnlyControllersFeatureProvider());
    }
});
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();

// ── OpenAPI document generation ──────────────────────────────────────
// NSwag generates the "nocturne" spec at BUILD TIME for TypeScript client codegen.
// Microsoft OpenAPI serves specs at RUNTIME for Scalar interactive docs.

// NSwag (build-time only — used by nswag.json MSBuild target for TS client generation)
builder.Services.AddOpenApiDocument(config =>
{
    config.DocumentName = "nocturne";

    config.AddOperationFilter(ctx =>
    {
        var ns = ctx.ControllerType.Namespace ?? string.Empty;
        return ns.Contains(".Controllers.V4.")
            || ns.EndsWith(".Controllers.V4", StringComparison.Ordinal)
            || ns.Contains(".Controllers.Authentication")
            || ns == "Nocturne.API.Controllers";
    });

    config.OperationProcessors.Add(new RemoteFunctionOperationProcessor());
    config.OperationProcessors.Add(new ConsumesContentTypeOperationProcessor());
    config.OperationProcessors.Add(new ControllerNameTagOperationProcessor());
    config.OperationProcessors.Add(new SummaryToDescriptionOperationProcessor());

    config.PostProcess = document =>
    {
        document.Info.Version = "0.0.1";
        document.Info.Title = "Nocturne API";
    };
});

// Microsoft OpenAPI (runtime — serves specs for Scalar docs UI)
builder.Services.AddOpenApi("nocturne", options =>
{
    options.ShouldInclude = desc =>
    {
        var ns = (desc.ActionDescriptor as Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor)
            ?.ControllerTypeInfo.Namespace ?? string.Empty;
        return ns.Contains(".Controllers.V4.")
            || ns.EndsWith(".Controllers.V4", StringComparison.Ordinal)
            || ns.Contains(".Controllers.Authentication")
            || ns == "Nocturne.API.Controllers";
    };
    options.AddOperationTransformer<SummaryToDescriptionOperationTransformer>();
    options.AddOperationTransformer<FolderBasedTagOperationTransformer>();
    options.AddOperationTransformer<SecurityRequirementOperationTransformer>();
    options.AddDocumentTransformer<TagDescriptionDocumentTransformer>();
    options.AddDocumentTransformer<SecuritySchemeDocumentTransformer>();
    options.AddDocumentTransformer<DiagramDescriptionDocumentTransformer>();
    options.AddDocumentTransformer<ScalarExtensionsDocumentTransformer>();
});

builder.Services.AddOpenApi("nightscout", options =>
{
    options.ShouldInclude = desc =>
    {
        var ns = (desc.ActionDescriptor as Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor)
            ?.ControllerTypeInfo.Namespace ?? string.Empty;
        return ns.Contains(".Controllers.V1.")
            || ns.EndsWith(".Controllers.V1", StringComparison.Ordinal)
            || ns.Contains(".Controllers.V2.")
            || ns.EndsWith(".Controllers.V2", StringComparison.Ordinal)
            || ns.Contains(".Controllers.V3.")
            || ns.EndsWith(".Controllers.V3", StringComparison.Ordinal);
    };
    options.AddOperationTransformer<SummaryToDescriptionOperationTransformer>();
    options.AddOperationTransformer<FolderBasedTagOperationTransformer>();
    options.AddOperationTransformer<SecurityRequirementOperationTransformer>();
    options.AddDocumentTransformer<TagDescriptionDocumentTransformer>();
    options.AddDocumentTransformer<SecuritySchemeDocumentTransformer>();
    options.AddDocumentTransformer<DiagramDescriptionDocumentTransformer>();
    options.AddDocumentTransformer<ScalarExtensionsDocumentTransformer>();
});

// ── Service registration (grouped by concern) ──────────────────────────
builder.Services.AddApiCoreServices(builder.Configuration);
builder.Services.AddAuthenticationAndIdentity(builder.Configuration);
builder.Services.AddDomainServices();
builder.Services.AddV4Infrastructure();
builder.Services.AddRealTimeAndNotifications(builder.Configuration);
builder.Services.AddAlertingAndMonitoring(builder.Configuration);
builder.Services.AddConnectorInfrastructure(builder.Configuration);
builder.Services.AddMigrationServices();


// Configure JWT authentication - derive signing key from instance key
var secretKey =
    builder.Configuration[$"Parameters:{ServiceNames.Parameters.InstanceKey}"]
    ?? builder.Configuration[ServiceNames.ConfigKeys.InstanceKey]
    ?? (isTesting ? "test-instance-key-for-unit-tests-minimum-length" : throw new InvalidOperationException("Instance key must be configured for JWT signing. Set Parameters:instance-key or INSTANCE_KEY."));
var key = Encoding.UTF8.GetBytes(secretKey);

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

builder.Services.AddTransient<IAuthorizationHandler, HasPermissionsHandler>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(PolicyNames.HasPermissions, policy =>
        policy.Requirements.Add(new HasPermissionsRequirement()));
});

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

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                             | ForwardedHeaders.XForwardedProto
                             | ForwardedHeaders.XForwardedHost;
    // Trust any proxy — the API is only reachable through the gateway.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

// Configure middleware pipeline
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseResponseCaching();
app.UseCors();
app.UseStaticFiles();
app.UseForwardedHeaders();

// Strip .json suffixes before routing so /api/v1/treatments.json matches
// the TreatmentsController route /api/v1/treatments. Must run before
// UseRouting so the rewritten path is what the router sees.
app.UseMiddleware<JsonExtensionMiddleware>();

// Explicit UseRouting so TenantSetupMiddleware can read endpoint metadata
// (e.g. [AllowDuringSetup]). Minimal hosting would insert this automatically
// but we make it explicit for clarity.
app.UseRouting();

// Documentation paths (/scalar, /openapi) bypass the entire tenant/auth
// middleware stack — they're tenantless and publicly accessible.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (path.StartsWith("/scalar", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase))
    {
        // Jump straight to the endpoint (MapOpenApi / MapScalarApiReference)
        var endpoint = context.GetEndpoint();
        if (endpoint != null)
        {
            await endpoint.RequestDelegate!(context);
            return;
        }
    }
    await next();
});

// Redirect OIDC callbacks from apex to the originating tenant subdomain
app.UseMiddleware<OidcCallbackRedirectMiddleware>();

// Resolve tenant from subdomain (must run before authentication)
app.UseMiddleware<TenantResolutionMiddleware>();

// Block API traffic for freshly provisioned tenants with no passkey credentials
app.UseMiddleware<TenantSetupMiddleware>();

// Add Nightscout authentication middleware
app.UseMiddleware<AuthenticationMiddleware>();

// Add member scope middleware (resolves membership role and restricts scopes)
app.UseMiddleware<MemberScopeMiddleware>();

// Add audit context middleware (captures actor metadata for mutation audit log)
app.UseMiddleware<AuditContextMiddleware>();

// Add site security middleware (enforces authentication when site lockdown is enabled)
app.UseMiddleware<SiteSecurityMiddleware>();

// Add authentication and authorization middleware
app.UseAuthentication();
app.UseAuthorization();

// Add rate limiting
app.UseRateLimiter();

// Add compatibility proxy middleware (background comparison against Nightscout for v1/v2/v3 GET requests)
app.UseMiddleware<CompatibilityProxyMiddleware>();

// Map native API controllers
app.MapControllers();

// Map SignalR hubs for real-time communication
app.MapHub<DataHub>("/hubs/data");
app.MapHub<AlarmHub>("/hubs/alarms");
app.MapHub<AlertHub>("/hubs/alerts");
app.MapHub<ConfigHub>("/hubs/config");

// Serve OpenAPI specs at /openapi/{documentName}.json
app.MapOpenApi();

// Scalar interactive API docs at /scalar/{documentName}
app.MapScalarApiReference(options =>
{
    options.WithTheme(ScalarTheme.Mars);
    options.WithOpenApiRoutePattern("/openapi/{documentName}.json");
    options.AddDocument("nocturne", "Nocturne API", isDefault: true);
    options.AddDocument("nightscout", "Nightscout API");
    options.AddHeadContent(MermaidLazyLoader.HeadContent);
});

// Add root endpoint to serve a basic info page
app.MapGet(
    "/",
    async (IEntryStore entryStore) =>
    {
        // Check database connection by fetching the latest entry
        string databaseStatus = "unknown";
        object? latestEntry = null;

        try
        {
            var entry = await entryStore.GetCurrentAsync();

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
if (!isNSwagGeneration && !app.Environment.IsEnvironment("Testing"))
{
    // Validate that the migrator connection string is present and uses a different role.
    if (string.IsNullOrWhiteSpace(migratorConnectionString))
    {
        throw new InvalidOperationException(
            $"ConnectionStrings:{ServiceNames.PostgreSql}-migrator is required. " +
            "See docs/postgres/bootstrap-roles.sql.");
    }

    DatabaseInitializationExtensions.ValidateRoleSeparation(aspirePostgreSqlConnection, migratorConnectionString);

    // Run migrations under the dedicated migrator role using a throwaway data source.
    {
        using var scope = app.Services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        var interceptor = scope.ServiceProvider.GetRequiredService<TenantConnectionInterceptor>();
        await DatabaseInitializationExtensions.RunMigrationsAsync(migratorConnectionString, logger, interceptor);
    }

    // Validate RLS, ownership, default privileges, and NoResetOnClose under the app role.
    await app.Services.ValidateDatabaseConfigurationAsync();

    // Sync config-managed OIDC providers to the database (satisfies FK constraints)
    await OidcProviderService.SyncConfigProvidersAsync(app.Services);
}
else if (isNSwagGeneration)
{
    Console.WriteLine("[NSwag] Skipping database migrations - running in OpenAPI generation mode");
}

// Bootstrap platform admin on startup
if (!isNSwagGeneration && !app.Environment.IsEnvironment("Testing"))
{
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
        var platformOptions = scope.ServiceProvider.GetRequiredService<IOptions<PlatformOptions>>();
        var bootstrap = new PlatformAdminBootstrapService(db, platformOptions);
        await bootstrap.BootstrapAsync(CancellationToken.None);
    }
}

await app.RunAsync();

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

/// <summary>
/// Removes controllers in the .DevOnly namespace from non-development environments.
/// Defined here to avoid creating a Nocturne.API.Infrastructure namespace that
/// collides with relative namespace resolution in other files.
/// </summary>
file class RemoveDevOnlyControllersFeatureProvider
    : Microsoft.AspNetCore.Mvc.Controllers.ControllerFeatureProvider
{
    protected override bool IsController(System.Reflection.TypeInfo typeInfo)
    {
        if (typeInfo.Namespace?.Contains(".DevOnly", StringComparison.Ordinal) == true)
            return false;
        return base.IsController(typeInfo);
    }
}

// Make Program accessible for testing
namespace Nocturne.API
{
    public partial class Program { }
}

// NSwag 14.x discovers the host via reflection on the entry-point type's DeclaringType.
// .NET 10.0.104 compiles top-level statements into a global "Program" class (not Nocturne.API.Program),
// so this partial must be in the global namespace for NSwag to find CreateHostBuilder.
public partial class Program
{
    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseStartup<NSwagStartup>();
            });
}

/// <summary>Minimal startup used only by NSwag for OpenAPI schema extraction.</summary>
internal class NSwagStartup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddControllers()
            .AddApplicationPart(typeof(Nocturne.API.Program).Assembly);

        // NSwag schema extraction: register only the "nocturne" document (V4 + root controllers).
        // nswag.json targets documentName "nocturne" so only this document is emitted.
        services.AddOpenApiDocument(config =>
        {
            config.DocumentName = "nocturne";

            config.AddOperationFilter(ctx =>
            {
                var ns = ctx.ControllerType.Namespace ?? string.Empty;
                return ns.Contains(".Controllers.V4.")
                    || ns.EndsWith(".Controllers.V4", StringComparison.Ordinal)
                    || ns.Contains(".Controllers.Authentication")
                    || ns == "Nocturne.API.Controllers";
            });

            config.OperationProcessors.Add(new RemoteFunctionOperationProcessor());
            config.OperationProcessors.Add(new ConsumesContentTypeOperationProcessor());
            config.OperationProcessors.Add(new ControllerNameTagOperationProcessor());
        });
    }

    public void Configure(IApplicationBuilder app)
    {
        app.UseRouting();
    }
}

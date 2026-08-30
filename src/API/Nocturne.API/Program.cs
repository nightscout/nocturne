using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Nocturne.API.Authorization;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Services.DevOnly;
using Nocturne.API.Services.Docs;
using Nocturne.API.Services.Seeding;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Contracts.Audit;
using Nocturne.API.Extensions;
using Nocturne.API.Filters;
using Nocturne.API.Hubs;
using Nocturne.API.Middleware;
using Nocturne.API.Multitenancy;
using Nocturne.API.OpenApi;
using Scalar.AspNetCore;
using Nocturne.Aspire.Scalar;
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

builder.Services.AddDataProtection()
    // Never change this string. It is part of the root purpose for every payload, and TOTP secrets
    // are persisted under it — changing it makes every stored secret permanently unreadable while
    // DataProtectionKeys still looks healthy. Left unset, it defaults to ContentRootPath, so a
    // changed container WORKDIR would do the same.
    .SetApplicationName("Nocturne")
    .PersistKeysToNocturneDb();

// Add compatibility proxy services
builder.Services.AddCompatibilityProxyServices(builder.Configuration);

// In-process, so each replica caches independently and entries are lost on restart.
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
builder.Services.AddHostedService<SoftDeleteCleanupService>();

// Consumed by the dev-only admin controllers (Development) and the demo admin
// controller's seed-extras endpoint (demo container, all environments).
builder.Services.AddScoped<SampleDataSeeder>();

builder.Services.AddScoped<ReadAccessAuditFilter>();
builder.Services.AddControllers(options =>
{
    options.Filters.Add<NightscoutJsonFilter>();
    options.Filters.Add<TenantCacheVaryFilter>();
    options.Filters.AddService<ReadAccessAuditFilter>();
})
.ConfigureApplicationPartManager(manager =>
    AuthorizationConfiguration.ConfigureControllerDiscovery(
        manager, builder.Environment.IsDevelopment()));
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiErrorEnvelopeHandler>();
builder.Services.AddEndpointsApiExplorer();

// ── OpenAPI document generation ──────────────────────────────────────
// Microsoft OpenAPI serves specs at RUNTIME for Scalar interactive docs. The build-time NSwag
// spec that feeds TypeScript and SDK codegen is configured in NSwagDocumentConfiguration, which
// the application host never reaches — NSwag boots the app through NSwagStartup.

builder.Services.AddOpenApi("nocturne", options =>
{
    options.ShouldInclude = ApiDocumentMembership.InNocturneDocument;
    options.AddOperationTransformer<SummaryToDescriptionOperationTransformer>();
    options.AddOperationTransformer<SecurityRequirementOperationTransformer>();
    options.AddDocumentTransformer<TagDescriptionDocumentTransformer>();
    options.AddDocumentTransformer<SecuritySchemeDocumentTransformer>();
    options.AddDocumentTransformer<DiagramDescriptionDocumentTransformer>();
    options.AddDocumentTransformer<ScalarExtensionsDocumentTransformer>();
});

builder.Services.AddOpenApi("nightscout", options =>
{
    options.ShouldInclude = ApiDocumentMembership.InNightscoutDocument;
    options.AddOperationTransformer<SummaryToDescriptionOperationTransformer>();
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

builder.Services.AddNocturneAuthorization();

// Configure CORS for frontend with credentials support.
// AllowAnyOrigin() cannot be combined with AllowCredentials() per the CORS spec, and a
// static allow-list can't cover the open-ended per-tenant wildcard subdomains
// ({slug}.{BaseDomain}) or public shares ({token}.share.{BaseDomain}). Instead validate the
// origin against the configured base domain: apex + any subdomain are allowed, loopback
// origins only in development. See CorsOriginPolicy.
// Normalize the configured base domain once (strip scheme/path/port/stray dots) so
// misformatted values like "https://nocturne.run" or "nocturne.run/" still resolve to
// a matchable host instead of silently disabling cross-origin CORS. See CorsOriginPolicy.
const string PublicDocsCorsPolicy = "PublicDocs";
var rawCorsBaseDomain = builder.Configuration[BaseDomainOptions.ConfigKey] ?? "";
var corsBaseDomain = CorsOriginPolicy.NormalizeBaseHost(rawCorsBaseDomain);
var corsAllowLocalhost = builder.Environment.IsDevelopment();
// A credentialed CORS base must be a real multi-label domain; a bare suffix ("com") or
// single-label/empty value would either widen the allow-list or disable it. The predicate
// already fails closed on such values — validity is surfaced at startup below.
var corsBaseDomainIsValid = corsBaseDomain.Length > 0 && corsBaseDomain.Contains('.');
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .SetIsOriginAllowed(origin => CorsOriginPolicy.IsAllowed(origin, corsBaseDomain, corsAllowLocalhost))
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials(); // Required for cookies/auth to work cross-origin
    });

    // The OpenAPI specs and Scalar assets are tenantless, unauthenticated, and already
    // readable by anyone, so they're served to any origin — this is what lets docs sites
    // hosted off the base domain (getnocturne.dev) embed the reference. Kept as a separate
    // policy because the default one allows credentials, which the CORS spec forbids
    // combining with AllowAnyOrigin. No credentials here, so no tenant data is reachable.
    options.AddPolicy(PublicDocsCorsPolicy, policy =>
    {
        policy
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

var app = builder.Build();

// Surface the effective credentialed-CORS base domain so operators can see what's active.
// An invalid base (bare suffix, single-label, or empty) fails closed: cross-origin CORS is
// disabled and only same-origin (plus loopback in Development) requests are admitted.
if (corsBaseDomainIsValid)
{
    app.Logger.LogInformation("CORS base domain: {CorsBaseDomain}", corsBaseDomain);
}
else if (app.Environment.IsDevelopment())
{
    app.Logger.LogInformation(
        "CORS base domain '{RawCorsBaseDomain}' is not a multi-label host; cross-origin CORS is "
        + "disabled (loopback origins are still allowed in Development).",
        rawCorsBaseDomain);
}
else
{
    app.Logger.LogError(
        "CORS base domain '{RawCorsBaseDomain}' is not a valid multi-label host ({ConfigKey}). "
        + "Cross-origin CORS is disabled (fail closed) — tenant and share subdomains will be "
        + "rejected until this is corrected.",
        rawCorsBaseDomain, BaseDomainOptions.ConfigKey);
}

// Configure middleware pipeline
app.UseExceptionHandler();
app.UseStatusCodePages();
// Documentation paths get the any-origin policy, everything else the credentialed default.
// The two branches are complementary so exactly one CORS middleware ever runs per request —
// chaining both would emit conflicting Access-Control-Allow-Origin headers. Both sit ahead of
// UseStaticFiles so the Scalar assets under wwwroot/scalar are covered, and ahead of
// UseRouting so preflights short-circuit.
app.UseWhen(PublicDocsMiddleware.IsPublicDocsPath, branch => branch.UseCors(PublicDocsCorsPolicy));
app.UseWhen(context => !PublicDocsMiddleware.IsPublicDocsPath(context), branch => branch.UseCors());
app.UseStaticFiles();
app.UseNocturneForwardedHeaders(builder.Configuration);

// Response caching must run after UseForwardedHeaders so its cache key uses the per-tenant
// Host (rewritten from X-Forwarded-Host) rather than the constant gateway destination host
// (the gateway suppresses the original Host). Paired with TenantCacheVaryFilter (Vary: Cookie),
// this keeps tenant-scoped responses isolated per tenant and per credential in the shared cache.
app.UseResponseCaching();

// Strip .json suffixes before routing so /api/v1/treatments.json matches
// the TreatmentsController route /api/v1/treatments. Must run before
// UseRouting so the rewritten path is what the router sees.
app.UseMiddleware<JsonExtensionMiddleware>();

// Routing must run here, not where minimal hosting would insert it, so that
// TenantSetupMiddleware below can read endpoint metadata such as [AllowDuringSetup].
app.UseRouting();

// Ahead of the documentation branch below, which jumps straight to its endpoint and would
// otherwise skip the limiter entirely; the policies are attached to endpoints, so this needs
// UseRouting to have run. Everything without a policy passes through untouched, and every
// policy that exists partitions on pre-auth request data (the remote address or the Host),
// so none of their accounting depends on running after UseAuthorization.
app.UseRateLimiter();

app.UseMiddleware<PublicDocsMiddleware>();

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

// There is no app.UseAuthentication() call here, and adding one would be a security regression.
//
// The framework's authentication middleware is NOT absent — minimal hosting auto-inserts it at the
// HEAD of the pipeline because AddAuthentication is registered, and an explicit app.UseAuthentication()
// is what suppresses that auto-add. So calling it here would move the JwtBearer scheme from before
// AuthenticationMiddleware to after it, letting the scheme's principal overwrite whatever the handler
// chain decided. That scheme validates strictly less — no issuer or audience check, no tenant pin, no
// revocation check — while trusting the same signing key, so it would re-admit exactly the tokens the
// chain rejects and undo the rejection in SetUnauthenticated.
//
// Running ahead of the chain is harmless only because AuthenticationMiddleware assigns
// context.User on every path, success and rejection alike, so it always owns the final principal.
// That invariant is what makes this safe; do not weaken it.
//
// The scheme also gives UseAuthorization a challenge scheme, so an anonymous request gets 401
// rather than 500. No policy names an authentication scheme, so PolicyEvaluator reads context.User
// directly.
app.UseAuthorization();

// Add compatibility proxy middleware (background comparison against Nightscout for v1/v2/v3 GET requests)
app.UseMiddleware<CompatibilityProxyMiddleware>();

// Map native API controllers
app.MapControllers();

// Map SignalR hubs for real-time communication
app.MapHub<DataHub>("/hubs/data");
app.MapHub<AlarmHub>("/hubs/alarms");
app.MapHub<AlertHub>("/hubs/alerts");
app.MapHub<ConfigHub>("/hubs/config");
app.MapHub<HomeAssistantHub>("/hubs/home-assistant");
app.MapHub<OverviewHub>("/hubs/overview");

// Serve OpenAPI specs at /openapi/{documentName}.json
app.MapOpenApi().RequireRateLimiting(ServiceRegistrationExtensions.DocsRateLimitPolicy);

var scalarCss = app.Configuration["SCALAR_CUSTOM_CSS"];

// Scalar interactive API docs at /scalar/{documentName}
app.MapScalarApiReference((options, httpContext) =>
{
    options.WithTheme(ScalarTheme.Mars);
    options.WithOpenApiRoutePattern("/openapi/{documentName}.json");
    options.AddDocument("nocturne", "Nocturne API", isDefault: true);
    options.AddDocument("nightscout", "Nightscout API");
    options.AddHeadContent(MermaidLazyLoader.HeadContent);
    if (!string.IsNullOrEmpty(scalarCss))
        options.WithCustomCss(scalarCss);
    options.EnablePersistentAuthentication();

    // Resolved per request by ScalarAuthProvider; absent when the host resolves to no
    // tenant (a bare instance, or a share host), in which case the reference still
    // renders and only "Send request" is unusable.
    var scalarAuth = httpContext.Items[ScalarAuthContext.HttpContextItemKey] as ScalarAuthContext;

    // Pre-configure authentication so Scalar's "Authorize" UI works out of the box.
    options
        .AddPreferredSecuritySchemes("oauth2", "bearer", "apiSecret")
        .AddAuthorizationCodeFlow("oauth2", flow =>
        {
            // The client is registered per tenant against this exact redirect URI;
            // authorize-time matching is byte-exact. Left unset when no tenant resolved,
            // so the flow is visibly unconfigured rather than pointing somewhere wrong.
            if (scalarAuth is not null)
            {
                flow.ClientId = scalarAuth.ClientId;
                flow.RedirectUri = scalarAuth.RedirectUri;
            }
            flow.Pkce = Pkce.Sha256;
            flow.SelectedScopes = [Scope.FullAccess];
        })
        .AddApiKeyAuthentication("apiSecret", apiKey =>
        {
            apiKey.Value = string.Empty;
        });

    // On a demo tenant, hand Scalar a token for the shared demo member so requests work
    // with no sign-in step. Never populated for a real tenant.
    if (scalarAuth?.BearerToken is { Length: > 0 } demoToken)
    {
        options
            .AddPreferredSecuritySchemes("bearer", "oauth2", "apiSecret")
            .WithHttpBearerAuthentication(bearer => bearer.Token = demoToken);
    }
}).RequireRateLimiting(ServiceRegistrationExtensions.DocsRateLimitPolicy);

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
).AllowAnonymous();

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

        // Apply the per-category public-share RLS policies, derived from the C# category map,
        // so they cannot drift from the code. Runs under the migrator role like migrations.
        await DatabaseInitializationExtensions.ReconcileShareRlsPoliciesAsync(migratorConnectionString, logger);

        // Background job records left Pending/Running by a previous process are orphans —
        // the detached tasks died with it. Mark them Interrupted so polls report the truth.
        await DatabaseInitializationExtensions.MarkInterruptedJobsAsync(migratorConnectionString, logger);
    }

    // Validate RLS, ownership, default privileges, and NoResetOnClose under the app role.
    await app.Services.ValidateDatabaseConfigurationAsync();

    // Sync config-managed OIDC providers to the database (satisfies FK constraints)
    await OidcProviderService.SyncConfigProvidersAsync(app.Services);

    // Bring pre-existing credential columns onto their at-rest storage format. Runs after
    // migrations (it depends on the widened share_token column) and before the server accepts
    // requests, so no request can read a column in the old format.
    await CredentialAtRestStartupTask.RunAsync(app.Services);
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
        var bootstrap = scope.ServiceProvider.GetRequiredService<PlatformAdminBootstrapService>();
        await bootstrap.BootstrapAsync(CancellationToken.None);
    }
}

// Development only: re-seed the committed dev identity fixture (real WebAuthn
// public keys) so a database wipe doesn't cost developers their passkey login.
if (app.Environment.IsDevelopment() && !isNSwagGeneration)
{
    using var devSeedScope = app.Services.CreateScope();
    var devSeedDb = devSeedScope.ServiceProvider.GetRequiredService<NocturneDbContext>();
    var devSeedLogger = devSeedScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await DevIdentityFixtureSeeder.SeedAsync(
        devSeedDb, app.Configuration, devSeedLogger, CancellationToken.None);
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

        services.AddOpenApiDocument(NSwagDocumentConfiguration.Configure);
    }

    public void Configure(IApplicationBuilder app)
    {
        app.UseRouting();
    }
}

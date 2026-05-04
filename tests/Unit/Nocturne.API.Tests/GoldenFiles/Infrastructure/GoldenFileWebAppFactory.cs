using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Middleware.Handlers;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.GoldenFiles.Infrastructure;

/// <summary>
/// WebApplicationFactory for golden file tests. Runs the full ASP.NET pipeline
/// with SQLite in-memory and bypassed authentication.
/// </summary>
public class GoldenFileWebAppFactory : WebApplicationFactory<Program>
{
    // Force minimal hosting path — without this override, WebApplicationFactory discovers
    // the global Program.CreateHostBuilder (used by NSwag) and uses NSwagStartup instead
    // of the real Program.cs pipeline, resulting in zero mapped endpoints.
    protected override IHostBuilder? CreateHostBuilder() => null;
    private SqliteConnection? _connection;

    public SqliteConnection Connection => _connection
        ?? throw new InvalidOperationException("Factory not initialized");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.Sources.Clear();
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Environment"] = "Testing",
                ["Features:EnableExternalConnectors"] = "false",
                ["Features:EnableRealTimeNotifications"] = "false",
                ["ConnectionStrings:DefaultConnection"] = "Data Source=:memory:",
                ["Logging:LogLevel:Default"] = "Error",
                ["INSTANCE_KEY"] = "golden-file-test-api-secret-key-minimum-length",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Remove all DbContext registrations
            var dbContextDescriptors = services
                .Where(d => d.ServiceType.Name.Contains("DbContext")
                    || d.ServiceType.Name.Contains("Migration")
                    || d.ServiceType.Name.Contains("Database"))
                .ToList();
            foreach (var d in dbContextDescriptors) services.Remove(d);

            // Register IDbContextFactory backed by the shared SQLite connection
            var conn = _connection;
            var mockFactory = new Mock<IDbContextFactory<NocturneDbContext>>();
            mockFactory.Setup(f => f.CreateDbContext()).Returns(() =>
            {
                var opts = new DbContextOptionsBuilder<NocturneDbContext>()
                    .UseSqlite(conn)
                    .ConfigureWarnings(w =>
                        w.Ignore(RelationalEventId.PendingModelChangesWarning))
                    .Options;
                return new NocturneDbContext(opts);
            });
            mockFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => mockFactory.Object.CreateDbContext());
            services.AddSingleton<IDbContextFactory<NocturneDbContext>>(mockFactory.Object);

            // Register scoped NocturneDbContext that sets TenantId from ITenantAccessor,
            // mirroring the production ServiceCollectionExtensions registration.
            services.AddScoped(sp =>
            {
                var factory = sp.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
                var context = factory.CreateDbContext();
                var tenantAccessor = sp.GetService<Nocturne.Core.Contracts.Multitenancy.ITenantAccessor>();
                if (tenantAccessor?.IsResolved == true)
                {
                    context.TenantId = tenantAccessor.TenantId;
                }
                return context;
            });

            // Create schema and seed all entities required by the middleware pipeline
            var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
            db.Database.EnsureCreated();
            Nocturne.API.Tests.Infrastructure.TestDatabaseSeeder.Seed(db);

            // Mock cache service — use DefaultValue.Empty so that GetAsync<T>()
            // returns a completed Task<T?> with default(T) for any T, not just object.
            var mockCache = new Mock<ICacheService> { DefaultValue = DefaultValue.Empty };
            mockCache.Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<object>(),
                It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            mockCache.Setup(x => x.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            RemoveService<ICacheService>(services);
            services.AddSingleton(mockCache.Object);

            // Register audit config cache (missed when bypassing AddPostgreSqlInfrastructure)
            services.AddSingleton<ITenantAuditConfigCache, TenantAuditConfigCache>();

            // Register test auth handler (middleware-based, not ASP.NET Core auth-based)
            // This will authenticate all requests with admin permissions for golden file tests
            // NOTE: We add this IN ADDITION to the existing handlers. It has Priority 0 so it runs first.
            services.AddSingleton<IAuthHandler, TestAuthHandlerImpl>();

            // Also configure the ASP.NET Core authentication system for completeness
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "Test";
                    options.DefaultChallengeScheme = "Test";
                    options.DefaultScheme = "Test";
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });

            // Remove hosted services
            var hostedServices = services
                .Where(x => x.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
                .ToList();
            foreach (var s in hostedServices) services.Remove(s);
        });

        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Warning);
        });
    }

    private static void RemoveService<T>(IServiceCollection services)
    {
        var descriptors = services.Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var d in descriptors) services.Remove(d);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection?.Dispose();
    }
}

/// <summary>
/// Test authentication handler that auto-authenticates all requests
/// </summary>
public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "test-user"),
            new Claim(ClaimTypes.Name, "Test User"),
            new Claim(ClaimTypes.Role, "admin"),
            new Claim("permissions", "*"),
            new Claim("tenant_id", "00000000-0000-0000-0000-000000000001"),
        };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, "Test");
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

/// <summary>
/// Test auth handler implementation for the middleware pipeline.
/// Authenticates all requests with full admin permissions for golden file tests.
/// </summary>
public class TestAuthHandlerImpl : IAuthHandler
{
    public int Priority => 0; // Run first, before all other handlers

    public string Name => "TestAuthHandlerImpl";

    public Task<AuthResult> AuthenticateAsync(HttpContext context)
    {
        // Always authenticate with admin permissions for golden file tests
        return Task.FromResult(AuthResult.Success(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.InstanceKey,
            SubjectId = Guid.Parse("00000000-0000-0000-0000-000000000099"),
            SubjectName = "test-user",
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Permissions = ["*"],
            Roles = ["admin"],
        }));
    }
}

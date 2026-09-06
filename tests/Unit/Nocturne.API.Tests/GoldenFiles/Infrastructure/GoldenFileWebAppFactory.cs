using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.GoldenFiles.Infrastructure;

/// <summary>
/// WebApplicationFactory for golden file tests. Runs the full ASP.NET pipeline
/// with SQLite in-memory and bypassed authentication.
/// </summary>
public class GoldenFileWebAppFactory : SqliteWebAppFactoryBase<Program>
{
    protected override string InstanceKey => "golden-file-test-api-secret-key-minimum-length";

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        // Mirrors the production ServiceCollectionExtensions registration, which pins the
        // tenant from ITenantAccessor.
        services.AddScoped(sp =>
        {
            var factory = sp.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
            var context = factory.CreateDbContext();
            var tenantAccessor = sp.GetService<ITenantAccessor>();
            if (tenantAccessor?.IsResolved == true)
            {
                context.TenantId = tenantAccessor.TenantId;
            }
            return context;
        });

        services.AddScoped<ITenantDbContextFactory>(sp =>
        {
            var factory = sp.GetRequiredService<IDbContextFactory<NocturneDbContext>>();
            var tenantAccessor = sp.GetService<ITenantAccessor>();
            return new TestTenantDbContextFactory(factory, tenantAccessor);
        });

        // DefaultValue.Empty so GetAsync<T>() returns a completed Task<T?> with default(T)
        // for any T, not just object.
        var mockCache = new Mock<ICacheService> { DefaultValue = DefaultValue.Empty };
        mockCache.Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<object>(),
            It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockCache.Setup(x => x.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        services.AddSingleton(mockCache.Object);

        // Added IN ADDITION to the existing handlers; Priority 0 puts it first.
        services.AddSingleton<IAuthHandler, TestAuthHandlerImpl>();

        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
                options.DefaultScheme = "Test";
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
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
    public int Priority => 0;

    public string Name => "TestAuthHandlerImpl";

    public Task<AuthResult> AuthenticateAsync(HttpContext context)
    {
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

/// <summary>
/// Test-only <see cref="ITenantDbContextFactory"/> that creates tenant-scoped contexts
/// from the shared <see cref="IDbContextFactory{NocturneDbContext}"/> mock.
/// </summary>
file sealed class TestTenantDbContextFactory(
    IDbContextFactory<NocturneDbContext> pool,
    ITenantAccessor? tenantAccessor) : ITenantDbContextFactory
{
    public async ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default)
    {
        var ctx = await pool.CreateDbContextAsync(ct);
        if (tenantAccessor?.IsResolved == true)
            ctx.TenantId = tenantAccessor.TenantId;
        return ctx;
    }
}

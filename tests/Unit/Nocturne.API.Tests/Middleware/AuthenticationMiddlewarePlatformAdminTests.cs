using System.Security.Claims;
using System.Threading;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Middleware;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Middleware;

/// <summary>
/// Platform admin reaches every tenant on the instance and the controllers that gate on it
/// (<c>Controllers/V4/PlatformAdmin</c>) require the role claim and nothing else, so which
/// credentials may present it is a privilege boundary rather than a convenience.
/// </summary>
/// <remarks>
/// The two cases are load-bearing as a pair. An exception anywhere before the principal is built
/// lands in the catch that calls SetUnauthenticated, which satisfies the "does not carry" case for
/// the wrong reason; the runs differ only in the credential type, so a fault breaking one would
/// fail the other.
/// </remarks>
[Trait("Category", "Unit")]
public class AuthenticationMiddlewarePlatformAdminTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();

    private sealed class StubHandler(AuthContext authContext) : IAuthHandler
    {
        public int Priority => 50;

        public string Name => "Stub";

        public Task<AuthResult> AuthenticateAsync(HttpContext httpContext) =>
            Task.FromResult(AuthResult.Success(authContext));
    }

    /// <summary>
    /// Builds the middleware over a platform-admin subject who is a member of the resolved tenant,
    /// authenticating with <paramref name="authType"/>.
    /// </summary>
    private static async Task<DefaultHttpContext> RunAsync(AuthType authType)
    {
        var memberService = new Mock<ITenantMemberService>();
        memberService
            .Setup(s => s.IsMemberAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var dbName = $"auth_platform_admin_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddScoped<ICategoryReadContext, CategoryReadContext>();
        services.AddSingleton(memberService.Object);
        services.AddScoped(_ => TestDbContextFactory.CreateInMemoryContext(dbName));
        var provider = services.BuildServiceProvider();

        using (var seedScope = provider.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<NocturneDbContext>();
            db.Subjects.Add(new SubjectEntity
            {
                Id = SubjectId,
                Name = "Operator",
                IsActive = true,
                IsPlatformAdmin = true,
                ApprovalStatus = "Approved",
            });
            await db.SaveChangesAsync();
        }

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Items["TenantContext"] = new TenantContext(TenantId, "site", "Site", true, false);

        var middleware = new AuthenticationMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<AuthenticationMiddleware>.Instance,
            handlers:
            [
                new StubHandler(new AuthContext
                {
                    IsAuthenticated = true,
                    AuthType = authType,
                    SubjectId = SubjectId,
                    TenantId = TenantId,
                    SubjectName = "Operator",
                }),
            ],
            environment: Mock.Of<IHostEnvironment>(e => e.EnvironmentName == "Production"),
            publicAccessCacheService: null!,
            oidcOptions: Options.Create(new OidcOptions()),
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());

        await middleware.InvokeAsync(context);
        return context;
    }

    [Theory]
    [InlineData(AuthType.SessionCookie)]
    [InlineData(AuthType.OidcToken)]
    [InlineData(AuthType.LegacyJwt)]
    [InlineData(AuthType.PlatformAccess)]
    public async Task A_credential_that_is_the_person_carries_their_platform_admin_standing(
        AuthType authType)
    {
        var context = await RunAsync(authType);

        context.User.Claims
            .Should().Contain(c => c.Type == ClaimTypes.Role && c.Value == "platform_admin");
    }

    [Theory]
    [InlineData(AuthType.DirectGrant)]
    [InlineData(AuthType.ApiKey)]
    [InlineData(AuthType.OAuthAccessToken)]
    [InlineData(AuthType.Guest)]
    public async Task A_delegated_credential_does_not_carry_it(AuthType authType)
    {
        var context = await RunAsync(authType);

        // An uploader token imported onto the owner's subject, or one the owner minted for a phone,
        // would otherwise reach /api/v4/admin/tenants on every tenant of the instance.
        context.User.Identity?.IsAuthenticated.Should().BeTrue(
            "the credential is still valid; only the platform-admin standing is withheld");
        context.User.Claims
            .Should().NotContain(c => c.Type == ClaimTypes.Role && c.Value == "platform_admin");
    }
}

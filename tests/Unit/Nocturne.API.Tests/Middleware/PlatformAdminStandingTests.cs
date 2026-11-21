using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Authorization;
using Nocturne.API.Middleware;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Middleware;

/// <summary>
/// Pins which credential types present their subject's platform-admin standing, through
/// <see cref="AuthenticationMiddleware"/> for a subject who is a platform admin and a member of the
/// resolved tenant.
/// </summary>
/// <remarks>
/// Platform admin reaches every tenant on the instance and its controllers require the role claim
/// and nothing else, so a new credential type must not be added without a decision here.
/// The rows that carry and the rows that do not are load-bearing together: an exception before the
/// principal is built lands in the catch that calls SetUnauthenticated, which passes a "does not
/// carry" row for the wrong reason, and the runs differ only in the credential type.
/// </remarks>
[Trait("Category", "Unit")]
public class PlatformAdminStandingTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();

    /// <summary>
    /// Every credential type, and whether it presents platform-admin standing. Only the instance key,
    /// the operator's own secret, and a credential from the person's own sign-in do.
    /// </summary>
    private static readonly IReadOnlyDictionary<AuthType, bool> CarriesStanding =
        new Dictionary<AuthType, bool>
        {
            [AuthType.None] = false,
            [AuthType.SessionCookie] = true,
            [AuthType.LegacyJwt] = true,
            [AuthType.PlatformAccess] = true,
            [AuthType.InstanceKey] = true,
            [AuthType.ApiKey] = false,
            [AuthType.DirectGrant] = false,
            [AuthType.OAuthAccessToken] = false,
            [AuthType.Guest] = false,
        };

    public static TheoryData<AuthType> CredentialTypes => new(Enum.GetValues<AuthType>());

    [Fact]
    public void EveryCredentialType_IsClassified() =>
        CarriesStanding.Keys.Should().BeEquivalentTo(Enum.GetValues<AuthType>());

    [Theory]
    [MemberData(nameof(CredentialTypes))]
    public async Task OnlyAClassifiedCredential_PresentsPlatformAdminStanding(AuthType authType)
    {
        var context = await RunAsync(authType);

        context.User.Identity?.IsAuthenticated.Should().BeTrue(
            "the credential is valid; only the standing is in question");
        context.User.Claims
            .Any(c => c.Type == ClaimTypes.Role && c.Value == "platform_admin")
            .Should().Be(CarriesStanding.GetValueOrDefault(authType));
    }

    private sealed class StubHandler(AuthContext authContext) : IAuthHandler
    {
        public int Priority => 50;

        public string Name => "Stub";

        public Task<AuthResult> AuthenticateAsync(HttpContext httpContext) =>
            Task.FromResult(AuthResult.Success(authContext));
    }

    /// <summary>
    /// The instance key goes through its own handler, which asserts the standing itself; every other
    /// type is a context naming the platform-admin subject.
    /// </summary>
    private static IAuthHandler HandlerFor(AuthType authType) =>
        authType == AuthType.InstanceKey
            ? new InstanceKeyHandler(
                Mock.Of<IInstanceKeyValidator>(v => v.Classify(It.IsAny<HttpContext>()) == InstanceKeyRequestKind.Valid),
                NullLogger<InstanceKeyHandler>.Instance)
            : new StubHandler(new AuthContext
            {
                IsAuthenticated = true,
                AuthType = authType,
                SubjectId = SubjectId,
                TenantId = TenantId,
                SubjectName = "Operator",
            });

    private static async Task<DefaultHttpContext> RunAsync(AuthType authType)
    {
        var dbName = $"platform_admin_standing_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddScoped<ICategoryReadContext>(_ => new CategoryReadContext());
        services.AddScoped(_ => TestDbContextFactory.CreateInMemoryContext(dbName).AsFactory());
        services.AddTransient<ITenantMembershipRepository, TenantMembershipRepository>();
        services.AddTransient<ISubjectRepository, SubjectRepository>();
        services.AddSingleton(AccountLinkRoutes.Off);
        var provider = services.BuildServiceProvider();

        using (var seedScope = provider.CreateScope())
        {
            await using var db = await seedScope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();
            db.Subjects.Add(new SubjectEntity
            {
                Id = SubjectId,
                Name = "Operator",
                IsActive = true,
                IsPlatformAdmin = true,
                ApprovalStatus = "Approved",
            });
            db.TenantMembers.Add(new TenantMemberEntity
            {
                Id = Guid.CreateVersion7(), TenantId = TenantId, SubjectId = SubjectId,
            });
            await db.SaveChangesAsync();
        }

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Items["TenantContext"] = new TenantContext(TenantId, "site", "Site", true, false);

        var middleware = new AuthenticationMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<AuthenticationMiddleware>.Instance,
            handlers: [HandlerFor(authType)],
            environment: Mock.Of<IHostEnvironment>(e => e.EnvironmentName == "Production"),
            publicAccessCacheService: null!,
            oidcOptions: Options.Create(new OidcOptions()),
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());

        await middleware.InvokeAsync(context);
        return context;
    }
}

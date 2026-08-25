using System.Security.Claims;
using System.Threading;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Middleware;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Middleware;

/// <summary>
/// A subject who is not a member of the resolved tenant is reduced to anonymous — which is every
/// invitee, right up until they accept. Session cookies are domain-wide, so a clinician who
/// already follows one patient on the instance arrives at another patient's join page signed in,
/// was silently treated as anonymous, and was pushed into creating a second identity.
/// </summary>
/// <remarks>
/// The exemption is narrow by construction, and these tests pin each edge of it: the endpoint must
/// carry <see cref="InviteTokenAuthorizedAttribute"/>, the route's token must name a currently
/// valid invite, the lookup must be bounded by the tenant the request resolved to, and what
/// survives must be identity and nothing else.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class AuthenticationMiddlewareInviteIdentityTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ForeignTenantId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private const string Token = "invite-token";

    private sealed class StubHandler(AuthContext context) : IAuthHandler
    {
        public int Priority => 50;

        public string Name => "Stub";

        public Task<AuthResult> AuthenticateAsync(HttpContext httpContext) =>
            Task.FromResult(AuthResult.Success(context));
    }

    /// <summary>
    /// An invite as the tenant-bounded lookup returns it. <paramref name="tenantId"/> is what the
    /// service found, so a stub that ignores the tenant argument still cannot fake a cross-tenant
    /// pass — the tests that need one make the lookup return null, which is what the real
    /// tenant-bounded query does.
    /// </summary>
    private static MemberInviteInfo Invite(bool isValid = true, Guid? tenantId = null) =>
        new(
            Guid.CreateVersion7(),
            tenantId ?? TenantId,
            "Chris",
            "Chris",
            [],
            [Scope.GlucoseRead],
            "Dr. Smith",
            false,
            DateTime.UtcNow.AddDays(7),
            null,
            0,
            isValid,
            !isValid,
            false,
            DateTime.UtcNow,
            []);

    private sealed record Harness(
        AuthenticationMiddleware Middleware,
        DefaultHttpContext Context,
        Mock<IMemberInviteService> InviteService);

    private static Harness Build(
        MemberInviteInfo? inviteForThisTenant,
        bool markEndpoint = true,
        bool withTokenRouteValue = true)
    {
        var memberService = new Mock<ITenantMemberService>();
        memberService
            .Setup(s => s.IsMemberAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var inviteService = new Mock<IMemberInviteService>();
        inviteService
            .Setup(s => s.GetInviteByTokenAsync(It.IsAny<string>(), TenantId))
            .ReturnsAsync(inviteForThisTenant);

        var services = new ServiceCollection();
        services.AddScoped<ICategoryReadContext, CategoryReadContext>();
        services.AddSingleton(memberService.Object);
        services.AddSingleton(inviteService.Object);
        services.AddScoped(_ => TestDbContextFactory.CreateInMemoryContext($"invite_identity_{Guid.NewGuid()}"));
        var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Items["TenantContext"] = new TenantContext(TenantId, "chris", "Chris", true, false);

        if (markEndpoint)
        {
            context.SetEndpoint(new Endpoint(
                _ => Task.CompletedTask,
                new EndpointMetadataCollection(new InviteTokenAuthorizedAttribute()),
                "AcceptInvite"));
        }
        else
        {
            context.SetEndpoint(new Endpoint(
                _ => Task.CompletedTask, EndpointMetadataCollection.Empty, "SensorGlucose"));
        }

        if (withTokenRouteValue)
        {
            context.Request.RouteValues[InviteTokenAuthorizedAttribute.TokenRouteValue] = Token;
        }

        var authContext = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = SubjectId,
            TenantId = TenantId,
            SubjectName = "Dr. Smith",
            Email = "smith@example.test",
            Roles = ["admin"],
            Permissions = [Scope.FullAccess],
        };

        var middleware = new AuthenticationMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<AuthenticationMiddleware>.Instance,
            handlers: [new StubHandler(authContext)],
            environment: Mock.Of<IHostEnvironment>(e => e.EnvironmentName == "Production"),
            publicAccessCacheService: null!,
            oidcOptions: Options.Create(new OidcOptions()),
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());

        return new Harness(middleware, context, inviteService);
    }

    [Fact]
    public async Task A_non_member_holding_a_valid_invite_stays_identifiable()
    {
        var harness = Build(Invite());

        await harness.Middleware.InvokeAsync(harness.Context);

        harness.Context.GetAuthContext().Should().BeEquivalentTo(
            new { IsAuthenticated = true, SubjectId = (Guid?)SubjectId, TenantId = (Guid?)TenantId },
            "the accept endpoint has to know who is asking, and [Authorize] gates it");
        harness.Context.User.Identity?.IsAuthenticated.Should().BeTrue();
        harness.Context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value.Should().Be(SubjectId.ToString());
    }

    /// <summary>
    /// The exemption buys the caller the right to be named and nothing else. A tenantless or
    /// exempted endpoint carries no permission gate of its own, so anything left in the trie, the
    /// granted scopes or the principal's claims becomes an anonymous capability on a tenant this
    /// subject has no membership of.
    /// </summary>
    [Fact]
    public async Task A_non_member_holding_a_valid_invite_gains_no_access()
    {
        var harness = Build(Invite());

        await harness.Middleware.InvokeAsync(harness.Context);

        var auth = harness.Context.GetAuthContext()!;
        auth.Permissions.Should().BeEmpty();
        auth.Roles.Should().BeEmpty();
        auth.Scopes.Should().BeEmpty();
        auth.IsPlatformAdmin.Should().BeFalse();

        harness.Context.GetGrantedScopes().Should().BeEmpty();
        (harness.Context.GetPermissionTrie())!.IsEmpty.Should().BeTrue();

        harness.Context.User.Claims.Should().NotContain(
            c => c.Type == "permission" || c.Type == ClaimTypes.Role,
            "a permission or role claim is read by policies that never see the invite");
    }

    /// <summary>
    /// Invariant of the whole feature: the invite is looked up on the tenant the request resolved
    /// to. Presenting tenant A's token while resolved on tenant B finds nothing, so the caller is
    /// rejected exactly as any other non-member is.
    /// </summary>
    [Fact]
    public async Task An_invite_token_from_another_tenant_is_rejected()
    {
        // The lookup is bounded by the resolved tenant, so a foreign token simply is not found —
        // which is what the tenant-scoped query returns. The invite the foreign tenant does hold
        // is set up here to prove the middleware never reaches for it.
        var harness = Build(inviteForThisTenant: null);
        harness.InviteService
            .Setup(s => s.GetInviteByTokenAsync(It.IsAny<string>(), ForeignTenantId))
            .ReturnsAsync(Invite(tenantId: ForeignTenantId));

        await harness.Middleware.InvokeAsync(harness.Context);

        harness.Context.GetAuthContext()!.IsAuthenticated.Should().BeFalse();
        harness.Context.User.Identity?.IsAuthenticated.Should().NotBe(true);
        harness.Context.User.Claims.Should().BeEmpty();

        harness.InviteService.Verify(
            s => s.GetInviteByTokenAsync(Token, TenantId),
            Times.Once,
            "the token must be resolved against the request's tenant, never the token's own");
        harness.InviteService.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Expired, revoked and exhausted all arrive here as <c>IsValid == false</c>; a spent invite
    /// must not keep a non-member identifiable.
    /// </summary>
    [Fact]
    public async Task An_invite_that_is_no_longer_valid_is_rejected()
    {
        var harness = Build(Invite(isValid: false));

        await harness.Middleware.InvokeAsync(harness.Context);

        harness.Context.GetAuthContext()!.IsAuthenticated.Should().BeFalse();
        harness.Context.User.Claims.Should().BeEmpty();
    }

    /// <summary>
    /// The membership requirement is untouched everywhere else: holding a live invite does not
    /// make a non-member authenticated against an endpoint that is not part of the join.
    /// </summary>
    [Fact]
    public async Task A_valid_invite_does_not_exempt_an_unmarked_endpoint()
    {
        var harness = Build(Invite(), markEndpoint: false);

        await harness.Middleware.InvokeAsync(harness.Context);

        harness.Context.GetAuthContext()!.IsAuthenticated.Should().BeFalse();
        harness.Context.User.Claims.Should().BeEmpty();
        harness.Context.GetGrantedScopes().Should().BeEmpty();

        harness.InviteService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_marked_endpoint_reached_without_a_token_is_rejected()
    {
        var harness = Build(Invite(), withTokenRouteValue: false);

        await harness.Middleware.InvokeAsync(harness.Context);

        harness.Context.GetAuthContext()!.IsAuthenticated.Should().BeFalse();
        harness.InviteService.VerifyNoOtherCalls();
    }
}

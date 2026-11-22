using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Attributes;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// A write to something every tenant reads needs standing over the instance: the platform-admin
/// flag, or full access on the instance's only tenant.
/// </summary>
public class RequireInstanceStandingAttributeTests
{
    private static readonly Guid RequestTenant = Guid.CreateVersion7();

    [Fact]
    public async Task A_platform_admin_has_standing_without_the_tenant_being_counted()
    {
        var tenants = new Mock<ITenantRepository>(MockBehavior.Strict);
        var context = Build(Authenticated(platformAdmin: true), tenantResolved: true, tenants.Object);

        await new RequireInstanceStandingAttribute().OnAuthorizationAsync(context);

        context.Result.Should().BeNull();
    }

    [Fact]
    public async Task A_caller_on_the_only_tenant_has_standing()
    {
        var context = Build(Authenticated(platformAdmin: false), tenantResolved: true, NonDemoTenants(RequestTenant));

        await new RequireInstanceStandingAttribute().OnAuthorizationAsync(context);

        context.Result.Should().BeNull();
    }

    [Fact]
    public async Task A_caller_on_one_of_several_tenants_is_refused()
    {
        var context = Build(Authenticated(platformAdmin: false), tenantResolved: true, NonDemoTenants(RequestTenant, Guid.CreateVersion7()));

        await new RequireInstanceStandingAttribute().OnAuthorizationAsync(context);

        context.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task A_caller_on_a_tenant_that_is_not_the_only_one_is_refused()
    {
        var context = Build(Authenticated(platformAdmin: false), tenantResolved: true, NonDemoTenants(Guid.CreateVersion7()));

        await new RequireInstanceStandingAttribute().OnAuthorizationAsync(context);

        context.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task A_caller_with_no_resolved_tenant_is_refused()
    {
        var context = Build(Authenticated(platformAdmin: false), tenantResolved: false, NonDemoTenants(Guid.Empty));

        await new RequireInstanceStandingAttribute().OnAuthorizationAsync(context);

        context.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_left_to_the_action_s_other_requirements()
    {
        var tenants = new Mock<ITenantRepository>(MockBehavior.Strict);
        var context = Build(null, tenantResolved: true, tenants.Object);

        await new RequireInstanceStandingAttribute().OnAuthorizationAsync(context);

        context.Result.Should().BeNull();
    }

    private static AuthContext Authenticated(bool platformAdmin) =>
        new() { IsAuthenticated = true, AuthType = AuthType.SessionCookie, IsPlatformAdmin = platformAdmin };

    private static ITenantRepository NonDemoTenants(params Guid[] ids)
    {
        var tenants = new Mock<ITenantRepository>();
        tenants.Setup(t => t.ListNonDemoAsync(2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ids.Select(id => new TenantRecord(id, "tenant", "Tenant", IsActive: true, IsDemo: false,
                AllowAccessRequests: false, AllowPublicDocs: false, null, null, null, null, null, null, null, null,
                null, null, null, DateTime.UnixEpoch, DateTime.UnixEpoch, null)).ToList());
        return tenants.Object;
    }

    private static AuthorizationFilterContext Build(AuthContext? auth, bool tenantResolved, ITenantRepository tenants)
    {
        var accessor = new Mock<ITenantAccessor>();
        accessor.SetupGet(a => a.IsResolved).Returns(tenantResolved);
        accessor.SetupGet(a => a.TenantId).Returns(tenantResolved ? RequestTenant : Guid.Empty);

        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton(accessor.Object)
                .AddSingleton(tenants)
                .BuildServiceProvider(),
        };
        if (auth is not null)
            httpContext.SetAuthContext(auth);

        return new AuthorizationFilterContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>());
    }
}

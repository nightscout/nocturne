using System.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// The socket.io bridge joins a browser to the tenant-wide room on this answer, so it has to refuse
/// that room to exactly the credentials the SignalR hub refuses it to.
/// </summary>
[Trait("Category", "Unit")]
public class RealtimeAdmissionControllerTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static bool TenantRelayFor(AuthContext authContext)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.SetAuthContext(authContext);
        httpContext.SetTenantContext(
            new TenantContext(Tenant, "sleepy", "Sleepy", IsActive: true, IsDemo: false));
        httpContext.SetGrantedScopes(new HashSet<string> { Scope.GlucoseRead });

        var controller = new RealtimeAdmissionController
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        var result = controller.GetRealtimeAdmission().Result as OkObjectResult;
        result.Should().NotBeNull();
        return result!.Value.Should().BeOfType<RealtimeAdmission>().Subject.TenantRelay;
    }

    [Fact]
    public void A_member_session_may_join_the_tenant_room()
    {
        TenantRelayFor(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = Guid.CreateVersion7(),
        }).Should().BeTrue();
    }

    [Fact]
    public void An_api_secret_may_join_the_tenant_room()
    {
        TenantRelayFor(new AuthContext { IsAuthenticated = true, AuthType = AuthType.ApiKey })
            .Should().BeTrue();
    }

    [Fact]
    public void A_guest_session_holding_glucose_may_not_join_the_tenant_room()
    {
        TenantRelayFor(new AuthContext { IsAuthenticated = true, AuthType = AuthType.Guest })
            .Should().BeFalse();
    }

    [Fact]
    public void An_anonymous_public_share_may_not_join_the_tenant_room()
    {
        TenantRelayFor(new AuthContext
        {
            IsAuthenticated = false,
            AuthType = AuthType.None,
            SubjectId = Guid.CreateVersion7(),
            TenantId = Tenant,
        }).Should().BeFalse();
    }

    [Fact]
    public void Admission_requires_the_glucose_read_a_live_glucose_feed_needs()
    {
        var method = typeof(RealtimeAdmissionController)
            .GetMethod(nameof(RealtimeAdmissionController.GetRealtimeAdmission))!;

        method.GetCustomAttributes(inherit: true).OfType<RequireScopeAttribute>()
            .Should().ContainSingle()
            .Which.Scopes.Should().Equal(Scope.GlucoseRead);
    }

    [Fact]
    public void Admission_does_not_401_an_anonymous_share()
    {
        typeof(RealtimeAdmissionController).GetCustomAttributes(inherit: true)
            .OfType<IAuthorizeData>()
            .Should().BeEmpty();
    }
}

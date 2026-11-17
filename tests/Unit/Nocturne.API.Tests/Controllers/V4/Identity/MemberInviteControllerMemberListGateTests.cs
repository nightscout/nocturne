using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

[Trait("Category", "Unit")]
public class MemberInviteControllerMemberListGateTests
{
    [Fact]
    public void GetMembers_RefusesAnAlertsReadOnlyCaller()
    {
        Authorize(new HashSet<string> { Scope.AlertsRead }).Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public void GetMembers_RefusesAGuestLinkCaller()
    {
        var guestScopes = Scope.Normalize(
            Scope.ValidateGrantScopes(Scope.AllowedGuestScopes, OAuthGrantTypes.Guest));

        guestScopes.Should().Contain(Scope.AlertsRead);
        Authorize(guestScopes).Should().BeOfType<ForbidResult>();
    }

    [Theory]
    [InlineData(Scope.MembersInvite)]
    [InlineData(Scope.MembersManage)]
    [InlineData(Scope.SharingManage)]
    [InlineData(Scope.FullAccess)]
    public void GetMembers_AdmitsACallerWhoCanAdministerMembersOrSharing(string scope)
    {
        Authorize(new HashSet<string> { scope }).Should().BeNull();
    }

    private static IActionResult? Authorize(IReadOnlySet<string> grantedScopes) =>
        ActionGate.Evaluate(
            typeof(MemberInviteController),
            nameof(MemberInviteController.GetMembers),
            grantedScopes,
            new AuthContext { IsAuthenticated = true });
}

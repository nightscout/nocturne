using FluentAssertions;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// Every action that reads or decides other people's requests to join is gated on
/// <c>members.manage</c>; asking to join, and reading one's own request, are not.
/// </summary>
public sealed class MembershipRequestControllerScopeTests
{
    private static readonly AuthContext Member = new() { IsAuthenticated = true, AuthType = AuthType.SessionCookie };

    [Theory]
    [InlineData(nameof(MembershipRequestController.GetPendingRequests))]
    [InlineData(nameof(MembershipRequestController.ApproveRequest))]
    [InlineData(nameof(MembershipRequestController.DenyRequest))]
    [InlineData(nameof(MembershipRequestController.GetMembershipRequestSettings))]
    [InlineData(nameof(MembershipRequestController.SetMembershipRequestSettings))]
    public void A_caller_without_members_manage_is_forbidden_before_the_handler(string action)
    {
        ActionGate.Evaluate(
                typeof(MembershipRequestController),
                action,
                new HashSet<string> { Scope.MembersInvite, Scope.SharingManage },
                Member)
            .ShouldBeScopeRefusal(Scope.MembersManage);

        ActionGate.Evaluate(
                typeof(MembershipRequestController), action, new HashSet<string> { Scope.MembersManage }, Member)
            .Should().BeNull("a caller holding members.manage reaches the handler");
    }
}

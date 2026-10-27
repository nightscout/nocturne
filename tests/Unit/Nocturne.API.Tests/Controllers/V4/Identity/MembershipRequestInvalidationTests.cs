using System.Reflection;
using FluentAssertions;
using Nocturne.API.Controllers.V4.Identity;
using OpenApi.Remote.Attributes;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// An approved requester becomes a member, so approving refreshes the member list as well as the
/// request list. The member list is another controller's query, which an entry names by its full
/// operation id.
/// </summary>
[Trait("Category", "Unit")]
public class MembershipRequestInvalidationTests
{
    [Fact]
    public void ApprovingARequest_RefreshesTheMemberListByItsOperationId()
    {
        var approve = typeof(MembershipRequestController)
            .GetMethod(nameof(MembershipRequestController.ApproveRequest))!
            .GetCustomAttribute<RemoteCommandAttribute>()!;

        approve.Invalidates.Should().Contain(
        [
            nameof(MembershipRequestController.GetPendingRequests),
            $"{nameof(MemberInviteController).Replace("Controller", "")}_{nameof(MemberInviteController.GetMembers)}",
        ]);
    }

    [Fact]
    public void TheMemberListApprovingRefreshes_IsAQueryTheCommandCanRefresh()
    {
        typeof(MemberInviteController)
            .GetMethod(nameof(MemberInviteController.GetMembers))!
            .GetCustomAttribute<RemoteQueryAttribute>().Should().NotBeNull();
    }
}

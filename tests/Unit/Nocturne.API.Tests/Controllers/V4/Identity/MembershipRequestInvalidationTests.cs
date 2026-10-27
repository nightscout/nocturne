using FluentAssertions;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Tests.Infrastructure;
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
        var approve = RemoteAttributes.Command<MembershipRequestController>(
            nameof(MembershipRequestController.ApproveRequest));

        approve.Invalidates.Should().Contain(
        [
            nameof(MembershipRequestController.GetPendingRequests),
            $"{nameof(MemberInviteController).Replace("Controller", "")}_{nameof(MemberInviteController.GetMembers)}",
        ]);
    }

    [Fact]
    public void TheMemberListApprovingRefreshes_IsAQueryTheCommandCanRefresh()
    {
        RemoteAttributes.Query<MemberInviteController>(nameof(MemberInviteController.GetMembers))
            .Should().NotBeNull();
    }
}

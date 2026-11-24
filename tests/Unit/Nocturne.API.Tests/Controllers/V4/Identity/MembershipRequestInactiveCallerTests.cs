using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Middleware;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

/// <summary>
/// A member whose subject was deactivated still holds a session until it expires, so approving a
/// membership request, which grants membership to another subject, refuses that caller.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MembershipRequestInactiveCallerTests
{
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _approverId = Guid.CreateVersion7();
    private readonly Mock<IMembershipRequestService> _service = new(MockBehavior.Strict);

    private async Task<IActionResult> ApproveAsync(bool subjectIsInactive)
    {
        var accessor = new Mock<ITenantAccessor>();
        accessor.SetupGet(a => a.TenantId).Returns(_tenantId);

        var http = new DefaultHttpContext();
        http.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string> { Scope.MembersManage };
        http.Items["AuthContext"] = new AuthContext { IsAuthenticated = true, SubjectId = _approverId, TenantId = _tenantId };
        new RequestMembership(_approverId, _tenantId, null) { SubjectIsInactive = subjectIsInactive }.StoreOn(http);

        var controller = new MembershipRequestController(_service.Object, accessor.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        return await controller.ApproveRequest(
            Guid.CreateVersion7(), new ApproveMembershipRequestRequest([Guid.CreateVersion7()], null), TimeProvider.System,
            CancellationToken.None);
    }

    [Fact]
    public async Task An_inactive_approver_is_refused_and_nobody_is_added()
    {
        var result = await ApproveAsync(subjectIsInactive: true);

        result.Should().BeOfType<ChallengeResult>();
        _service.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task An_active_approver_reaches_the_service()
    {
        _service
            .Setup(s => s.ApproveRequestAsync(
                It.IsAny<Guid>(), _tenantId, It.IsAny<List<Guid>>(), _approverId,
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateTime?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecideMembershipRequestResult(true, null));

        var result = await ApproveAsync(subjectIsInactive: false);

        result.Should().BeOfType<OkObjectResult>();
    }
}

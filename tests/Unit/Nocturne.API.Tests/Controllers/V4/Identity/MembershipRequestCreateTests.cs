using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Extensions;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Identity;

[Trait("Category", "Unit")]
public class MembershipRequestCreateTests
{
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();
    private readonly Mock<IMembershipRequestService> _service = new();

    private MembershipRequestController Controller()
    {
        var tenant = new Mock<ITenantAccessor>();
        tenant.SetupGet(t => t.TenantId).Returns(_tenantId);

        var http = new DefaultHttpContext();
        http.Items[AuthContextKeys.AuthContext] = new AuthContext { IsAuthenticated = true, SubjectId = _subjectId };

        return new MembershipRequestController(_service.Object, tenant.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            ProblemDetailsFactory = new EchoingProblemDetailsFactory(),
        };
    }

    [Fact]
    public async Task AnExistingMember_GetsAConflictProblemCarryingTheReason()
    {
        _service
            .Setup(s => s.CreateRequestAsync(_tenantId, _subjectId, "hi", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateMembershipRequestResult(CreateMembershipRequestOutcome.AlreadyMember, "You are already a member."));

        var result = await Controller().CreateRequest(new CreateMembershipRequestRequest("hi"), CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Be("You are already a member.");
    }

    [Fact]
    public async Task AnyOtherRefusal_StaysABadRequest()
    {
        _service
            .Setup(s => s.CreateRequestAsync(_tenantId, _subjectId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateMembershipRequestResult(CreateMembershipRequestOutcome.Refused, "A pending request already exists."));

        var result = await Controller().CreateRequest(new CreateMembershipRequestRequest(null), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }
}

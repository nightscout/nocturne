using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.PlatformAdmin;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Tests.Controllers.Admin;

public class TenantControllerAttachOidcTests
{
    private readonly Mock<ISubjectService> _subjectService = new();
    private readonly TenantController _controller;

    public TenantControllerAttachOidcTests()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.InstanceKey,
            IsPlatformAdmin = true,
        };

        _controller = new TenantController(new Mock<ITenantService>().Object, new Mock<ITenantRoleService>().Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    [Fact]
    public async Task AttachOidcIdentity_linked_to_another_subject_carries_the_code_an_external_client_reads()
    {
        _subjectService
            .Setup(s => s.AttachOidcIdentityAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((OidcLinkOutcome.AlreadyLinkedToOther, (Guid?)null));

        var result = await _controller.AttachOidcIdentity(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new AdminAttachOidcRequest(Guid.NewGuid(), "oidc-sub", "https://issuer.example", null),
            _subjectService.Object,
            CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Detail.Should().Be("This provider account is already linked to another Nocturne user.");
        problem.Extensions.Should().ContainKey("error").WhoseValue.Should().Be("AlreadyLinkedToOther");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Removing_a_members_last_factor_answers_409_with_the_code_a_client_reads(bool passkey)
    {
        var tenantId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var factorId = Guid.NewGuid();
        _subjectService.Setup(s => s.TryRemovePasskeyCredentialAsync(subjectId, factorId))
            .ReturnsAsync(FactorRemovalResult.LastPrimaryFactor);
        _subjectService.Setup(s => s.TryRemoveOidcIdentityAsync(subjectId, factorId))
            .ReturnsAsync(FactorRemovalResult.LastPrimaryFactor);

        var result = passkey
            ? await _controller.RemovePasskeyCredential(tenantId, subjectId, factorId, _subjectService.Object, CancellationToken.None)
            : await _controller.RemoveOidcIdentity(tenantId, subjectId, factorId, _subjectService.Object, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Detail.Should().NotBeNullOrWhiteSpace();
        problem.Extensions.Should().ContainKey("error").WhoseValue.Should().Be("last_factor");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Removing_a_factor_the_member_does_not_have_answers_404(bool passkey)
    {
        var tenantId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var factorId = Guid.NewGuid();
        _subjectService.Setup(s => s.TryRemovePasskeyCredentialAsync(subjectId, factorId))
            .ReturnsAsync(FactorRemovalResult.NotFound);
        _subjectService.Setup(s => s.TryRemoveOidcIdentityAsync(subjectId, factorId))
            .ReturnsAsync(FactorRemovalResult.NotFound);

        var result = passkey
            ? await _controller.RemovePasskeyCredential(tenantId, subjectId, factorId, _subjectService.Object, CancellationToken.None)
            : await _controller.RemoveOidcIdentity(tenantId, subjectId, factorId, _subjectService.Object, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }
}

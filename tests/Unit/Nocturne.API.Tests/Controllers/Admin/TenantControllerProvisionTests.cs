using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.PlatformAdmin;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.API.Tests.Controllers.Admin;

public class TenantControllerProvisionTests
{
    private readonly Mock<ITenantService> _tenantService = new();
    private readonly Mock<ITenantRoleService> _roleService = new();
    private readonly TenantController _controller;

    public TenantControllerProvisionTests()
    {
        _controller = new TenantController(_tenantService.Object, _roleService.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    [Fact]
    public async Task Provision_WithPasskeyCredential_Returns201()
    {
        var credential = new ProvisionCredentialData(
            CredentialId: Convert.ToBase64String(new byte[32]),
            PublicKey: Convert.ToBase64String(new byte[64]),
            SignCount: 0,
            Transports: ["internal"],
            AaGuid: Guid.NewGuid(),
            SubjectId: null);

        var expected = new ProvisionResult(Guid.NewGuid(), Guid.NewGuid(), "test-slug");

        _tenantService
            .Setup(s => s.ProvisionWithOwnerAsync(
                "test-slug", "Test", "owner", "owner@test.com",
                credential, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var request = new ProvisionRequest("test-slug", "Test", "owner", "owner@test.com", credential);

        var result = await _controller.Provision(request, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status201Created);
        objectResult.Value.Should().Be(expected);
    }

    [Fact]
    public async Task Provision_WithOidcIdentity_Returns201()
    {
        var oidcIdentity = new ProvisionOidcIdentityData(
            Provider: "Google",
            OidcSubjectId: "google-sub-123",
            Issuer: "https://accounts.google.com",
            Email: "owner@test.com",
            SubjectId: null);

        var expected = new ProvisionResult(Guid.NewGuid(), Guid.NewGuid(), "test-slug");

        _tenantService
            .Setup(s => s.ProvisionWithOwnerAsync(
                "test-slug", "Test", "owner", "owner@test.com",
                null, oidcIdentity, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var request = new ProvisionRequest("test-slug", "Test", "owner", "owner@test.com", OidcIdentity: oidcIdentity);

        var result = await _controller.Provision(request, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status201Created);
        objectResult.Value.Should().Be(expected);
    }

    [Fact]
    public async Task Provision_NeitherCredentialNorOidc_Returns400()
    {
        var request = new ProvisionRequest("test-slug", "Test", "owner", "owner@test.com");

        var result = await _controller.Provision(request, CancellationToken.None);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Provision_BothCredentialAndOidc_Returns400()
    {
        var credential = new ProvisionCredentialData(
            CredentialId: Convert.ToBase64String(new byte[32]),
            PublicKey: Convert.ToBase64String(new byte[64]),
            SignCount: 0,
            Transports: ["internal"],
            AaGuid: Guid.NewGuid(),
            SubjectId: null);

        var oidcIdentity = new ProvisionOidcIdentityData(
            Provider: "Google",
            OidcSubjectId: "google-sub-123",
            Issuer: "https://accounts.google.com",
            Email: "owner@test.com",
            SubjectId: null);

        var request = new ProvisionRequest("test-slug", "Test", "owner", "owner@test.com", credential, oidcIdentity);

        var result = await _controller.Provision(request, CancellationToken.None);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }
}

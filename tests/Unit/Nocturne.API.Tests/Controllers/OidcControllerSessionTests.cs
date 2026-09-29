using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// The session carries the tenant's default glucose units beside the member's own preferences,
/// so a member who has chosen none reads the default and an inherited default is never taken for
/// a saved choice.
/// </summary>
[Trait("Category", "Unit")]
public class OidcControllerSessionTests
{
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private readonly Mock<IOidcAuthService> _authService = new();
    private readonly Mock<ITenantMemberService> _tenantMemberService = new();
    private readonly Mock<ITenantService> _tenants = new(MockBehavior.Strict);

    [Fact]
    public async Task GetSession_CarriesTheTenantDefaultApartFromTheMembersOwnPreferences()
    {
        var own = new UserDisplayPreferences { TimeFormat = "24" };
        _tenants.Setup(t => t.GetDefaultGlucoseUnitsAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("mmol");

        var session = await GetSession(own, TenantId);

        session.DefaultGlucoseUnits.Should().Be("mmol");
        session.Preferences.Should().BeSameAs(own);
        session.Preferences!.GlucoseUnits.Should().BeNull();
    }

    [Fact]
    public async Task GetSession_ReadsNoTenantDefaultOutsideATenant()
    {
        var session = await GetSession(new UserDisplayPreferences(), tenantId: null);

        session.DefaultGlucoseUnits.Should().BeNull();
        _tenants.VerifyNoOtherCalls();
    }

    private async Task<SessionInfo> GetSession(UserDisplayPreferences own, Guid? tenantId)
    {
        _authService.Setup(a => a.GetUserInfoAsync(SubjectId))
            .ReturnsAsync(new OidcUserInfo { SubjectId = SubjectId, Preferences = own });
        _tenantMemberService.Setup(t => t.GetMemberRoleNamesAsync(SubjectId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var controller = new OidcController(
            _authService.Object,
            new Mock<IOidcProviderService>().Object,
            new Mock<ISubjectService>().Object,
            new Mock<IAuthAuditService>().Object,
            _tenantMemberService.Object,
            Options.Create(new OidcOptions()),
            Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example.com" }),
            NullLogger<OidcController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.HttpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = SubjectId,
            TenantId = tenantId,
        };

        var result = await controller.GetSession(_tenants.Object, CancellationToken.None);
        return result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<SessionInfo>().Subject;
    }
}

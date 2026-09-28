using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// The session carries the preferences the web app renders with, so a member who has never chosen
/// glucose units reads the tenant's default there.
/// </summary>
[Trait("Category", "Unit")]
public class OidcControllerSessionTests
{
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private readonly Mock<IOidcAuthService> _authService = new();
    private readonly Mock<ITenantMemberService> _tenantMemberService = new();
    private readonly Mock<IUnitsAndTimezoneService> _unitsAndTimezone = new(MockBehavior.Strict);

    [Fact]
    public async Task GetSession_GivesAMemberTheTenantDefaultUnits()
    {
        var own = new UserDisplayPreferences { TimeFormat = "24" };
        _unitsAndTimezone.Setup(u => u.WithTenantDefaultsAsync(own, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserDisplayPreferences { TimeFormat = "24", GlucoseUnits = "mmol" });

        var session = await GetSession(own, TenantId);

        session.Preferences!.GlucoseUnits.Should().Be("mmol");
        session.Preferences.TimeFormat.Should().Be("24");
    }

    [Fact]
    public async Task GetSession_ReadsNoTenantDefaultOutsideATenant()
    {
        var own = new UserDisplayPreferences();

        var session = await GetSession(own, tenantId: null);

        session.Preferences.Should().BeSameAs(own);
        _unitsAndTimezone.VerifyNoOtherCalls();
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

        var result = await controller.GetSession(_unitsAndTimezone.Object, CancellationToken.None);
        return result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<SessionInfo>().Subject;
    }
}

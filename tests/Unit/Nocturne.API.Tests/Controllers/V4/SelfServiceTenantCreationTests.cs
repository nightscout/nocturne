using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers.V4;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>
/// Both tenantless tenant-creation endpoints are bare <c>[Authorize]</c>, so any subject that
/// reached an authenticated state — including one who only accepted an invite to someone else's
/// tenant — reaches them. <c>OperatorConfiguration.AllowSelfServiceCreation</c> is the only gate,
/// so it must be honoured by BOTH and must fail closed. Operators that gate tenant creation behind
/// billing set <c>Operator__AllowSelfServiceCreation=false</c>.
/// </summary>
public class SelfServiceTenantCreationTests
{
    private static ControllerContext AuthenticatedContext()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = Guid.NewGuid(),
        };
        return new ControllerContext { HttpContext = httpContext };
    }

    private static PlatformController Platform(bool allowSelfService)
    {
        var tenantService = new Mock<ITenantService>(MockBehavior.Strict);
        return new PlatformController(
            tenantService.Object,
            Options.Create(new OperatorConfiguration { AllowSelfServiceCreation = allowSelfService }),
            Options.Create(new BaseDomainOptions()))
        {
            ControllerContext = AuthenticatedContext(),
        };
    }

    private static MyTenantsController MyTenants(bool allowSelfService)
    {
        var tenantService = new Mock<ITenantService>(MockBehavior.Strict);
        var overviewService = new Mock<ITenantOverviewService>(MockBehavior.Strict);
        return new MyTenantsController(
            tenantService.Object,
            overviewService.Object,
            Options.Create(new OperatorConfiguration { AllowSelfServiceCreation = allowSelfService }))
        {
            ControllerContext = AuthenticatedContext(),
        };
    }

    [Fact]
    public async Task PlatformTenantCreation_IsForbiddenWhenSelfServiceIsDisabled()
    {
        // MockBehavior.Strict on ITenantService also proves the deny happens before any
        // slug validation or tenant write is attempted.
        var result = await Platform(allowSelfService: false)
            .CreateTenant(new CreatePlatformTenantRequest("mine", "Mine"), default);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task MyTenantsCreation_IsForbiddenWhenSelfServiceIsDisabled()
    {
        var result = await MyTenants(allowSelfService: false)
            .CreateTenant(new CreateMyTenantRequest("mine", "Mine"), default);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public void SelfServiceCreation_DefaultsToEnabledForSelfHosters()
    {
        // Nocturne is self-hosted by individuals, for whom creating their own tenant IS the
        // expected flow. SaaS operators turn it off via configuration.
        new OperatorConfiguration().AllowSelfServiceCreation.Should().BeTrue();
    }

    [Fact]
    public void OperatorSectionName_IsTheRootLevelSectionTheEnvVarDependsOn()
    {
        // Operator__AllowSelfServiceCreation=false is the documented deployment override, which is
        // only correct while the bound section stays a single root-level "Operator".
        OperatorConfiguration.SectionName.Should().Be("Operator");
    }
}

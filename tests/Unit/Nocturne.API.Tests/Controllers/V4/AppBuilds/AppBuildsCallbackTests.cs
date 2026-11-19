using System.Reflection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nocturne.API.Controllers.V4.AppBuilds;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.AppBuilds;
using Nocturne.API.Tests.Services.AppBuilds;
using Nocturne.Core.Contracts.AppBuilds;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Tests.Controllers.V4.AppBuilds;

[Trait("Category", "Unit")]
public class AppBuildsCallbackTests
{
    private readonly FakeGitHub _github = new();
    private readonly Mock<IAppBuildLinkRepository> _links = new();

    [Fact]
    public void The_registered_callback_path_is_the_routed_one()
    {
        var controllerRoute = typeof(AppBuildsController).GetCustomAttribute<RouteAttribute>()!.Template;
        var actionRoute = typeof(AppBuildsController).GetMethod(nameof(AppBuildsController.CompleteAppBuildLink))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template;

        $"/{controllerRoute}/{actionRoute}".Should().Be(AppBuildsController.CallbackPath);
    }

    [Fact]
    public async Task A_refused_callback_returns_to_the_web_page_with_the_error_and_stores_nothing()
    {
        var result = await Controller().CompleteAppBuildLink("code", FakeGitHub.InstallationId, "forged", default);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/settings/app-builds?error=invalid_state");
        _links.Verify(l => l.SaveAsync(It.IsAny<Core.Models.AppBuilds.AppBuildLink>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private AppBuildsController Controller()
    {
        var tenant = new Mock<ITenantAccessor>();
        tenant.SetupGet(t => t.TenantId).Returns(Guid.NewGuid());
        var service = new AppBuildService(_links.Object, _github.CreateClient(),
            new AppBuildInstallState(new EphemeralDataProtectionProvider(), new MemoryCache(new MemoryCacheOptions()), _github.Time),
            Options.Create(_github.Options), tenant.Object, NullLogger<AppBuildService>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext { IsAuthenticated = true, SubjectId = Guid.NewGuid() };
        return new AppBuildsController(service, Options.Create(new BaseDomainOptions()))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }
}

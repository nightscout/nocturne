using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.API.Tests.Multitenancy;

/// <summary>
/// The AppHost's dev tenant seed (Aspire.Hosting.DevTenantSeeder.ProbePath) seeds only when this
/// path answers 503 on the apex, and reads anything else as "a tenant exists". Making the path
/// tenantless, or resolving it as the sole tenant on an empty instance, would turn the seed off
/// for good without failing anything else.
/// </summary>
public sealed class DevSeedProbeTests : TenantResolutionMiddlewareTestBase
{
    private const string ProbePath = "/api/Metadata/auth-error-codes";

    protected override string BaseDomain => "nocturne.localhost:1612";

    [Fact]
    public async Task With_no_tenant_the_probe_answers_503()
    {
        var (context, served) = await InvokeAsync("localhost", ProbePath);

        served.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task With_only_a_demo_tenant_the_probe_answers_503()
    {
        SeedTenant("demo", isDemo: true);

        var (context, served) = await InvokeAsync("localhost", ProbePath);

        served.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task With_a_sole_tenant_the_probe_resolves_it()
    {
        var tenantId = SeedTenant("dev");

        var (context, served) = await InvokeAsync("localhost", ProbePath);

        served.Should().BeTrue();
        Resolve<ITenantAccessor>(context).TenantId.Should().Be(tenantId);
    }

    [Fact]
    public async Task With_several_tenants_the_probe_answers_404()
    {
        SeedTenant("alpha");
        SeedTenant("beta");

        var (context, served) = await InvokeAsync("localhost", ProbePath);

        served.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// A sole tenant that has yet to register an owner must still answer 200, so the setup gate
    /// has to let the probe through: its 503 would read as "no tenant" and seed over the tenant.
    /// </summary>
    [Fact]
    public void The_probe_passes_the_setup_gate()
    {
        var controller = typeof(MetadataController);
        controller.GetCustomAttribute<AllowDuringSetupAttribute>().Should().NotBeNull();
        controller.GetCustomAttribute<RouteAttribute>()!.Template.Should().Be("api/[controller]");
        controller.GetMethods()
            .Select(m => m.GetCustomAttribute<HttpGetAttribute>()?.Template)
            .Should().Contain("auth-error-codes");
    }
}

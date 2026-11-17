using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Multitenancy;

/// <summary>
/// A context created in the request's scope before <see cref="TenantResolutionMiddleware"/> runs reads
/// the tenant it resolves. Unauthenticated requests (the setup/onboarding flow) have no
/// auth handler to set the tenant, so this is the only thing scoping their reads and writes.
/// </summary>
public sealed class TenantResolutionMiddlewareTenantPinTests : TenantResolutionMiddlewareTestBase
{
    private const string Slug = "acme";

    private readonly Guid _tenantId;

    public TenantResolutionMiddlewareTenantPinTests() => _tenantId = SeedTenant(Slug);

    [Fact]
    public async Task A_context_resolved_before_tenant_resolution_reads_the_resolved_tenant()
    {
        var scope = Root.CreateScope();
        await using var scoped = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreateAsync();
        scoped.TenantId.Should().Be(Guid.Empty);

        var (context, nextCalled) = await InvokeAsync(
            scope, $"{Slug}.{BaseDomain}", "/api/v4/migration/start-from-connector/nightscout");

        nextCalled.Should().BeTrue();
        scoped.TenantId.Should().Be(_tenantId);
        Resolve<ITenantAccessor>(context).TenantId.Should().Be(_tenantId);
    }

    [Fact]
    public async Task Unknown_subdomain_short_circuits_with_404()
    {
        var (context, nextCalled) = await InvokeAsync(
            Root.CreateScope(), $"nope.{BaseDomain}", "/api/v4/migration/history");

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }
}

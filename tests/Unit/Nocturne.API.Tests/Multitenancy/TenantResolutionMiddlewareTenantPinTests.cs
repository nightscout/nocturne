using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Multitenancy;

/// <summary>
/// Verifies that <see cref="TenantResolutionMiddleware"/> pins the resolved tenant onto the
/// request-scoped <see cref="NocturneDbContext"/>. The scoped context is pool-leased and its
/// <c>TenantId</c> is a custom property that pooling does not reset, so without this pin an
/// unauthenticated request (e.g. the setup/onboarding flow, which has no auth handler to set the
/// tenant) would read and write under a previous lessee's <em>stale</em> tenant — the root cause
/// of an onboarding migration importing one tenant's data into another's.
/// </summary>
public sealed class TenantResolutionMiddlewareTenantPinTests : TenantResolutionMiddlewareTestBase
{
    private const string Slug = "acme";

    private readonly Guid _tenantId;

    public TenantResolutionMiddlewareTenantPinTests() => _tenantId = SeedTenant(Slug);

    /// <summary>
    /// A scope whose <see cref="NocturneDbContext"/> already carries <paramref name="staleTenant"/>,
    /// as a pooled context arriving from a previous request does.
    /// </summary>
    private (IServiceScope Scope, NocturneDbContext Context) StaleScope(Guid staleTenant)
    {
        var scope = Root.CreateScope();
        var scoped = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
        scoped.TenantId = staleTenant;
        return (scope, scoped);
    }

    [Fact]
    public async Task Resolving_a_tenant_pins_it_onto_the_scoped_DbContext_overwriting_a_stale_value()
    {
        var staleTenant = Guid.CreateVersion7();
        var (scope, scoped) = StaleScope(staleTenant);

        var (context, nextCalled) = await InvokeAsync(
            scope, $"{Slug}.{BaseDomain}", "/api/v4/migration/start-from-connector/nightscout");

        nextCalled.Should().BeTrue();
        // The very instance downstream services inject is now scoped to the resolved tenant,
        // not the stale pooled value — so RLS (and the connector-config read) run under "acme".
        scoped.TenantId.Should().Be(_tenantId);
        scoped.TenantId.Should().NotBe(staleTenant);
        Resolve<ITenantAccessor>(context).TenantId.Should().Be(_tenantId);
    }

    [Fact]
    public async Task Unknown_subdomain_short_circuits_with_404_and_never_pins()
    {
        var (scope, _) = StaleScope(Guid.CreateVersion7());

        var (context, nextCalled) = await InvokeAsync(
            scope, $"nope.{BaseDomain}", "/api/v4/migration/history");

        // An unresolvable subdomain is rejected before reaching any controller, so no stale
        // tenant can be acted upon.
        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }
}

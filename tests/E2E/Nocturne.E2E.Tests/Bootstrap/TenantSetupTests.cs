using FluentAssertions;
using Nocturne.E2E.Tests.Fixtures;
using Xunit;

namespace Nocturne.E2E.Tests.Bootstrap;

[Collection("e2e")]
[Trait("Category", "E2E")]
public class TenantSetupTests
{
    private readonly AppHostFixture _fixture;
    private readonly DevSeedClient _seed;

    public TenantSetupTests(AppHostFixture fixture)
    {
        _fixture = fixture;
        _seed = new DevSeedClient(fixture);
    }

    [Fact]
    public async Task SeedingTenantSucceeds()
    {
        var ctx = await _seed.SeedTenantAsync();

        ctx.TenantId.Should().NotBe(Guid.Empty);
        ctx.SubjectId.Should().NotBe(Guid.Empty);
        ctx.AccessToken.Should().NotBeNullOrEmpty();

        // Prove the access token works against a tenant-scoped endpoint
        using var http = _fixture.CreateGatewayClient(ctx.Slug, ctx.AccessToken);
        var meResponse = await http.GetAsync("/api/v4/me/tenants");
        meResponse.IsSuccessStatusCode.Should().BeTrue(
            "the seeded session should authenticate against tenant-scoped /me/tenants");
    }
}

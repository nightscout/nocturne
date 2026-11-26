using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// Activation attempts against one tenant's guest codes are bounded in total, whatever client
/// addresses they come from. Each test works on tenants of its own, since the budget it spends is
/// shared by every test that activates on the same tenant.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class GuestActivationTenantBudgetIntegrationTests : ApiIntegrationTestBase
{
    private const int AttemptsTried = 200;
    private const string WrongCode = "AAA-AAAA";

    private string _baseDomain = null!;

    public GuestActivationTenantBudgetIntegrationTests(
        ApiIntegrationTestFixture fixture,
        ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _baseDomain = AuthTestHelpers.GetBaseDomain(ApiClient);
    }

    [Fact]
    public async Task WrongCodesFromEveryAddress_SpendOneTenantBudget_AndOnlyThatTenants()
    {
        var (slug, code) = await SeedTenantWithGuestCodeAsync("budget");
        var (otherSlug, otherCode) = await SeedTenantWithGuestCodeAsync("other");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < AttemptsTried; i++)
        {
            using var response = await ActivateFromNewAddressAsync(slug, WrongCode);
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                break;
        }

        statuses.Should().Contain(HttpStatusCode.TooManyRequests,
            "wrong codes sent from {0} distinct addresses must meet a bound that is not per address", statuses.Count);
        statuses.Count(s => s == HttpStatusCode.BadRequest).Should().Be(
            Nocturne.API.RateLimiting.GuestActivationLimiter.PermitLimit,
            "every attempt before the tenant's budget is spent is checked and refused as a wrong code");

        using var realGuest = await ActivateFromNewAddressAsync(slug, code);
        realGuest.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "a spent budget refuses every attempt on the tenant until the window ends, the right code included");

        using var otherTenant = await ActivateFromNewAddressAsync(otherSlug, otherCode);
        otherTenant.StatusCode.Should().Be(HttpStatusCode.OK, "another tenant's budget is its own");
    }

    [Fact]
    public async Task TenantBudgetRefusal_AnswersAsThePerAddressRefusalDoes()
    {
        var (perAddressSlug, _) = await SeedTenantWithGuestCodeAsync("addr");
        using var oneAddress = AuthTestHelpers.CreateTenantClient(Fixture, perAddressSlug, _baseDomain);
        HttpResponseMessage? perAddress = null;
        for (var i = 0; i < AttemptsTried && perAddress is null; i++)
        {
            var response = await oneAddress.PostAsJsonAsync("/api/v4/guest-links/activate", new { code = WrongCode });
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                perAddress = response;
            else
                response.Dispose();
        }

        var (tenantSlug, _) = await SeedTenantWithGuestCodeAsync("tenant");
        HttpResponseMessage? tenantWide = null;
        for (var i = 0; i < AttemptsTried && tenantWide is null; i++)
        {
            var response = await ActivateFromNewAddressAsync(tenantSlug, WrongCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                tenantWide = response;
            else
                response.Dispose();
        }

        perAddress.Should().NotBeNull("one address meets the per-address limit");
        tenantWide.Should().NotBeNull("many addresses meet the tenant's budget");

        using (perAddress)
        using (tenantWide)
        {
            (await RefusalWithoutTraceAsync(tenantWide!)).Should().Be(await RefusalWithoutTraceAsync(perAddress!));
            tenantWide.Content.Headers.ContentType?.MediaType.Should().Be(perAddress.Content.Headers.ContentType?.MediaType);
            tenantWide.Headers.RetryAfter.Should().NotBeNull("the per-address refusal says when to try again, and so does this one");
            perAddress.Headers.RetryAfter.Should().NotBeNull();
        }
    }

    // Every problem document carries its own request's traceId; the rest must match.
    private static async Task<string> RefusalWithoutTraceAsync(HttpResponseMessage refusal)
    {
        var body = JsonNode.Parse(await refusal.Content.ReadAsStringAsync())!.AsObject();
        body["traceId"].Should().NotBeNull();
        body.Remove("traceId");
        return body.ToJsonString();
    }

    private async Task<HttpResponseMessage> ActivateFromNewAddressAsync(string slug, string code)
    {
        using var client = AuthTestHelpers.CreateTenantClient(Fixture, slug, _baseDomain);
        return await client.PostAsJsonAsync("/api/v4/guest-links/activate", new { code });
    }

    private async Task<(string Slug, string Code)> SeedTenantWithGuestCodeAsync(string prefix)
    {
        var slug = $"{prefix}-{Guid.NewGuid():N}"[..20];
        var tenantId = await AuthTestHelpers.SeedTenantAsync(Fixture, slug, prefix);

        await using var conn = await OpenPostgresAsync();
        var (subjectId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, $"{prefix} owner");
        var (_, code) = await AuthTestHelpers.SeedGuestLinkAsync(conn, subjectId);

        Log($"Seeded tenant {slug} ({tenantId}) with a guest code");
        return (slug, code);
    }
}

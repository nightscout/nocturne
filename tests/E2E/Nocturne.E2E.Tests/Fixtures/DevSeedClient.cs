using System.Net.Http.Json;

namespace Nocturne.E2E.Tests.Fixtures;

public sealed class DevSeedClient
{
    private readonly AppHostFixture _fixture;

    public DevSeedClient(AppHostFixture fixture) => _fixture = fixture;

    public async Task<TenantContext> SeedTenantAsync(string? slug = null, string? username = null)
    {
        slug ??= "e2e-" + Guid.NewGuid().ToString("N")[..8];
        username ??= "owner-" + slug;

        // Apex (no tenant slug) -- seed-tenant endpoint is tenantless
        using var client = _fixture.CreateGatewayClient();

        var response = await client.PostAsJsonAsync("/api/v4/dev-only/admin/seed-tenant", new
        {
            slug,
            displayName = $"E2E {slug}",
            ownerUsername = username
        });
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"seed-tenant returned {response.StatusCode}: {errorBody}");
        }

        var body = await response.Content.ReadFromJsonAsync<SeedTenantResponse>()
            ?? throw new InvalidOperationException("seed-tenant returned null body");

        return new TenantContext(
            body.TenantId, slug, body.SubjectId, username,
            body.AccessToken, body.RefreshToken);
    }

    private record SeedTenantResponse(
        Guid TenantId,
        Guid SubjectId,
        string AccessToken,
        string RefreshToken,
        int ExpiresInSeconds);
}

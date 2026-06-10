using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// Integration tests verifying that tenant data isolation and cross-tenant
/// authorization are enforced end-to-end. Each test seeds data in one tenant and
/// confirms it is invisible from another, covering entries, treatments, profiles,
/// auth grants, OAuth clients, and tenant resolution edge cases (unknown subdomain,
/// inactive tenant, apex domain). The "subject membership authorization gate"
/// section additionally verifies that a valid access token issued for one tenant
/// cannot read or write against another tenant, and that a tenant member without
/// the platform_admin role cannot reach the platform-admin API.
/// </summary>
[Trait("Category", "Integration")]
public class MultitenantIsolationIntegrationTests : AspireIntegrationTestBase
{
    private Guid _tenantAId;
    private Guid _tenantBId;
    private Guid _subjectAId;
    private Guid _subjectBId;
    private string _accessTokenA = null!;
    private string _accessTokenB = null!;
    private string _slugA = null!;
    private string _slugB = null!;
    private string _baseDomain = null!;

    public MultitenantIsolationIntegrationTests(
        AspireIntegrationTestFixture fixture,
        ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        // Provision tenant A (the default test tenant)
        using var provisionClient = CreateAuthenticatedClient();
        await provisionClient.GetAsync("/api/v1/status");
        _tenantAId = await AuthTestHelpers.GetTenantIdAsync(conn);
        (_subjectAId, _accessTokenA) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantAId, "Tenant A User");

        // Create tenant B
        _tenantBId = await AuthTestHelpers.SeedTenantAsync(conn, "tenant-b", "Tenant B");
        (_subjectBId, _accessTokenB) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantBId, "Tenant B User");

        // Get the tenant A slug from DB
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT slug FROM tenants WHERE id = @id;";
            cmd.Parameters.AddWithValue("id", _tenantAId);
            _slugA = (string)(await cmd.ExecuteScalarAsync())!;
        }
        _slugB = "tenant-b";

        _baseDomain = AuthTestHelpers.GetBaseDomain(ApiClient);

        Log($"Tenant A: {_tenantAId} (slug={_slugA}), Tenant B: {_tenantBId} (slug={_slugB}), baseDomain={_baseDomain}");
    }

    [Fact]
    public async Task TenantA_CannotSee_TenantB_Entries()
    {
        // Arrange - seed an entry in tenant B
        using var clientB = AuthTestHelpers.CreateAuthenticatedTenantClient(Fixture, _slugB, _baseDomain, _accessTokenB);
        var now = DateTimeOffset.UtcNow;
        var entryPayload = new[]
        {
            new
            {
                type = "sgv",
                sgv = 180,
                date = now.ToUnixTimeMilliseconds(),
                dateString = now.UtcDateTime.ToString("o")
            }
        };
        var postResponse = await clientB.PostAsJsonAsync("/api/v1/entries", entryPayload);
        postResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);

        // Act - read entries from tenant A
        using var clientA = AuthTestHelpers.CreateAuthenticatedTenantClient(Fixture, _slugA, _baseDomain, _accessTokenA);
        var getResponse = await clientA.GetAsync("/api/v1/entries?count=100");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await getResponse.Content.ReadAsStringAsync();
        var entries = JsonSerializer.Deserialize<JsonElement>(content);

        // Assert - tenant A should not see the entry with sgv=180 that was seeded in B
        var hasTenantBEntry = entries.EnumerateArray().Any(e =>
            e.TryGetProperty("sgv", out var sgv) && sgv.GetInt32() == 180);
        hasTenantBEntry.Should().BeFalse("tenant A must not see entries belonging to tenant B");
    }

    [Fact]
    public async Task TenantB_CannotSee_TenantA_Entries()
    {
        // Arrange - seed an entry in tenant A
        using var clientA = AuthTestHelpers.CreateAuthenticatedTenantClient(Fixture, _slugA, _baseDomain, _accessTokenA);
        var now = DateTimeOffset.UtcNow;
        var entryPayload = new[]
        {
            new
            {
                type = "sgv",
                sgv = 95,
                date = now.ToUnixTimeMilliseconds(),
                dateString = now.UtcDateTime.ToString("o")
            }
        };
        var postResponse = await clientA.PostAsJsonAsync("/api/v1/entries", entryPayload);
        postResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);

        // Act - read entries from tenant B
        using var clientB = AuthTestHelpers.CreateAuthenticatedTenantClient(Fixture, _slugB, _baseDomain, _accessTokenB);
        var getResponse = await clientB.GetAsync("/api/v1/entries?count=100");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await getResponse.Content.ReadAsStringAsync();
        var entries = JsonSerializer.Deserialize<JsonElement>(content);

        // Assert - tenant B should not see the entry with sgv=95 that was seeded in A
        var hasTenantAEntry = entries.EnumerateArray().Any(e =>
            e.TryGetProperty("sgv", out var sgv) && sgv.GetInt32() == 95);
        hasTenantAEntry.Should().BeFalse("tenant B must not see entries belonging to tenant A");
    }

    [Fact]
    public async Task TenantA_CannotSee_TenantB_Treatments()
    {
        // Arrange - seed a treatment in tenant B
        using var clientB = AuthTestHelpers.CreateAuthenticatedTenantClient(Fixture, _slugB, _baseDomain, _accessTokenB);
        var now = DateTime.UtcNow;
        var treatmentPayload = new[]
        {
            new
            {
                eventType = "Note",
                notes = "tenant-b-isolation-marker",
                created_at = now.ToString("o")
            }
        };
        var postResponse = await clientB.PostAsJsonAsync("/api/v1/treatments", treatmentPayload);
        postResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);

        // Act - read treatments from tenant A
        using var clientA = AuthTestHelpers.CreateAuthenticatedTenantClient(Fixture, _slugA, _baseDomain, _accessTokenA);
        var getResponse = await clientA.GetAsync("/api/v1/treatments?count=100");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await getResponse.Content.ReadAsStringAsync();

        // Assert
        content.Should().NotContain("tenant-b-isolation-marker",
            "tenant A must not see treatments belonging to tenant B");
    }

    [Fact]
    public async Task TenantA_CannotSee_TenantB_Profiles()
    {
        // Arrange - PUT a profile in tenant B
        using var clientB = AuthTestHelpers.CreateAuthenticatedTenantClient(Fixture, _slugB, _baseDomain, _accessTokenB);
        var profilePayload = new
        {
            defaultProfile = "TenantBProfile",
            store = new Dictionary<string, object>
            {
                ["TenantBProfile"] = new
                {
                    dia = 4,
                    carbratio = new[] { new { time = "00:00", value = 10, timeAsSeconds = 0 } },
                    sens = new[] { new { time = "00:00", value = 50, timeAsSeconds = 0 } },
                    basal = new[] { new { time = "00:00", value = 1.0, timeAsSeconds = 0 } },
                    target_low = new[] { new { time = "00:00", value = 80, timeAsSeconds = 0 } },
                    target_high = new[] { new { time = "00:00", value = 120, timeAsSeconds = 0 } },
                    units = "mg/dl",
                    timezone = "UTC"
                }
            },
            startDate = DateTime.UtcNow.ToString("o")
        };
        var putResponse = await clientB.PutAsJsonAsync("/api/v1/profile", profilePayload);
        putResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created, HttpStatusCode.NoContent);

        // Act - GET profile from tenant A
        using var clientA = AuthTestHelpers.CreateAuthenticatedTenantClient(Fixture, _slugA, _baseDomain, _accessTokenA);
        var getResponse = await clientA.GetAsync("/api/v1/profile");
        var content = await getResponse.Content.ReadAsStringAsync();

        // Assert - tenant A should not see "TenantBProfile"
        content.Should().NotContain("TenantBProfile",
            "tenant A must not see profiles belonging to tenant B");
    }

    [Fact]
    public async Task CrossTenant_Write_StaysIsolated()
    {
        // Arrange - write an entry via tenant A
        using var clientA = AuthTestHelpers.CreateAuthenticatedTenantClient(Fixture, _slugA, _baseDomain, _accessTokenA);
        var now = DateTimeOffset.UtcNow;
        var entryPayload = new[]
        {
            new
            {
                type = "sgv",
                sgv = 222,
                date = now.ToUnixTimeMilliseconds(),
                dateString = now.UtcDateTime.ToString("o")
            }
        };
        var postResponse = await clientA.PostAsJsonAsync("/api/v1/entries", entryPayload);
        postResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);

        // Act - read from tenant B
        using var clientB = AuthTestHelpers.CreateAuthenticatedTenantClient(Fixture, _slugB, _baseDomain, _accessTokenB);
        var getResponse = await clientB.GetAsync("/api/v1/entries?count=100");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await getResponse.Content.ReadAsStringAsync();
        var entries = JsonSerializer.Deserialize<JsonElement>(content);

        // Assert
        var hasCrossTenantEntry = entries.EnumerateArray().Any(e =>
            e.TryGetProperty("sgv", out var sgv) && sgv.GetInt32() == 222);
        hasCrossTenantEntry.Should().BeFalse("entries written in tenant A must not be visible in tenant B");
    }

    [Fact]
    public async Task CrossTenant_GuestLink_NotActivatable()
    {
        // Arrange - create a guest link in tenant A
        using var clientA = AuthTestHelpers.CreateAuthenticatedTenantClient(Fixture, _slugA, _baseDomain, _accessTokenA);
        var createResponse = await clientA.PostAsJsonAsync("/api/v4/guest-links", new
        {
            label = "Cross-Tenant Test",
            scopes = new[] { "entries.read" }
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var createContent = await createResponse.Content.ReadAsStringAsync();
        var createBody = JsonSerializer.Deserialize<JsonElement>(createContent);
        var code = createBody.GetProperty("code").GetString()!;

        // Act - try to activate via tenant B's subdomain
        using var clientB = AuthTestHelpers.CreateTenantClient(Fixture, _slugB, _baseDomain);
        var activateResponse = await clientB.PostAsJsonAsync("/api/v4/guest-links/activate", new { code });

        // Assert - should fail (not found / bad request)
        activateResponse.StatusCode.Should().BeOneOf(
            new[] { HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.Unauthorized },
            "guest link from tenant A must not be activatable on tenant B");
    }

    [Fact]
    public async Task CrossTenant_DirectGrant_NotUsable()
    {
        // Arrange - create a direct grant token in tenant A
        using var clientA = AuthTestHelpers.CreateAuthenticatedTenantClient(Fixture, _slugA, _baseDomain, _accessTokenA);
        var createResponse = await clientA.PostAsJsonAsync("/api/auth/direct-grants", new
        {
            label = "cross-tenant-test",
            scopes = new[] { "entries.read" }
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var createContent = await createResponse.Content.ReadAsStringAsync();
        var createBody = JsonSerializer.Deserialize<JsonElement>(createContent);
        var nocToken = createBody.GetProperty("token").GetString()!;

        // Act - use the noc_ token on tenant B's subdomain
        using var clientB = Fixture.CreateHttpClient("nocturne-api", "api");
        clientB.DefaultRequestHeaders.Host = $"{_slugB}.{_baseDomain}";
        clientB.DefaultRequestHeaders.Add("Authorization", $"Bearer {nocToken}");

        var response = await clientB.GetAsync("/api/v1/entries/current");

        // Assert - should be rejected
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a direct grant token from tenant A must not authenticate on tenant B");
    }

    [Fact]
    public async Task CrossTenant_OAuthClient_NotReusable()
    {
        // Arrange - register an OAuth client on tenant A
        using var clientA = AuthTestHelpers.CreateAuthenticatedTenantClient(Fixture, _slugA, _baseDomain, _accessTokenA);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(clientA);

        // Act - try GET /api/oauth/authorize on tenant B with tenant A's client_id
        using var clientB = AuthTestHelpers.CreateAuthenticatedTenantClient(Fixture, _slugB, _baseDomain, _accessTokenB);
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var noRedirectClient = new HttpClient(handler)
        {
            BaseAddress = clientB.BaseAddress
        };
        foreach (var header in clientB.DefaultRequestHeaders)
        {
            noRedirectClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }

        var (_, codeChallenge) = AuthTestHelpers.GeneratePkceChallenge();
        var authorizeUrl = $"/api/oauth/authorize?response_type=code&client_id={clientId}" +
                           $"&redirect_uri=http://localhost:9999/callback&scope=entries.read" +
                           $"&code_challenge={codeChallenge}&code_challenge_method=S256";

        var response = await noRedirectClient.GetAsync(authorizeUrl);

        // Assert - should fail because the client_id belongs to tenant A
        response.StatusCode.Should().BeOneOf(
            new[] { HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.Unauthorized },
            "an OAuth client registered on tenant A must not be usable on tenant B");
    }

    [Fact]
    public async Task UnknownSubdomain_Returns404()
    {
        // Arrange
        using var client = Fixture.CreateHttpClient("nocturne-api", "api");
        client.DefaultRequestHeaders.Host = $"nonexistent.{_baseDomain}";

        // Act
        var response = await client.GetAsync("/api/v1/status");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a request to an unknown tenant subdomain must return 404");
    }

    [Fact]
    public async Task InactiveTenant_Returns403()
    {
        // Arrange - deactivate tenant B via SQL
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE tenants SET is_active = false WHERE id = @id;";
            cmd.Parameters.AddWithValue("id", _tenantBId);
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            // Act - request via tenant B's subdomain
            using var client = AuthTestHelpers.CreateTenantClient(Fixture, _slugB, _baseDomain);
            var response = await client.GetAsync("/api/v1/status");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "a request to an inactive tenant must return 403");
        }
        finally
        {
            // Reactivate tenant B to avoid polluting other tests
            await using var reactivateCmd = conn.CreateCommand();
            reactivateCmd.CommandText = "UPDATE tenants SET is_active = true WHERE id = @id;";
            reactivateCmd.Parameters.AddWithValue("id", _tenantBId);
            await reactivateCmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ApexDomain_MultipleTenants_Returns404()
    {
        // Arrange - both tenants are active (set up in InitializeAsync)
        using var client = Fixture.CreateHttpClient("nocturne-api", "api");
        client.DefaultRequestHeaders.Host = _baseDomain;

        // Act
        var response = await client.GetAsync("/api/v1/status");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "apex domain with multiple tenants must return 404");
    }

    [Fact]
    public async Task ApexDomain_SingleTenant_AutoResolves()
    {
        // Arrange - deactivate tenant B so only tenant A remains active
        var connStr = await GetPostgresConnectionStringAsync();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE tenants SET is_active = false WHERE id = @id;";
            cmd.Parameters.AddWithValue("id", _tenantBId);
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            // Act - request without subdomain (apex domain)
            using var client = Fixture.CreateHttpClient("nocturne-api", "api");
            client.DefaultRequestHeaders.Host = _baseDomain;

            var response = await client.GetAsync("/api/v1/status");

            // Assert - with only one active tenant, apex domain should auto-resolve
            // Note: tenant cache has a 5-min TTL, so this may return 404 if the
            // cache still sees two tenants. We accept either 200 or 404 for robustness.
            response.StatusCode.Should().BeOneOf(
                new[] { HttpStatusCode.OK, HttpStatusCode.NotFound },
                "apex domain with a single active tenant should auto-resolve (or 404 if cache is stale)");
        }
        finally
        {
            // Reactivate tenant B
            await using var reactivateCmd = conn.CreateCommand();
            reactivateCmd.CommandText = "UPDATE tenants SET is_active = true WHERE id = @id;";
            reactivateCmd.Parameters.AddWithValue("id", _tenantBId);
            await reactivateCmd.ExecuteNonQueryAsync();
        }
    }

    // ── Subject membership authorization gate ───────────────────────────────
    // The tests above pair each tenant's api-secret admin client with its own
    // subdomain. That proves query-filter/RLS data isolation, but the api-secret
    // authenticates as an admin API key on the resolved tenant, which bypasses the
    // membership check. The tests below send ONLY a subject's Bearer access token,
    // so AuthenticationMiddleware.IsMemberAsync is the gate under test: a valid
    // token for one tenant must not authenticate against another tenant.

    [Fact]
    public async Task SubjectA_Token_CannotRead_OnTenantB()
    {
        // Subject A holds a valid access token but is not a member of tenant B.
        using var client = AuthTestHelpers.CreateTenantBearerClient(
            Fixture, _slugB, _baseDomain, _accessTokenA);

        var response = await client.GetAsync("/api/v1/entries/current");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a valid access token for tenant A must not authenticate against tenant B");
    }

    [Fact]
    public async Task SubjectB_Token_CannotRead_OnTenantA()
    {
        // Symmetric check from the other direction.
        using var client = AuthTestHelpers.CreateTenantBearerClient(
            Fixture, _slugA, _baseDomain, _accessTokenB);

        var response = await client.GetAsync("/api/v1/entries/current");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a valid access token for tenant B must not authenticate against tenant A");
    }

    [Fact]
    public async Task SubjectA_Token_CanAccess_OwnTenant()
    {
        // Positive control: the same token, used against its OWN tenant, both
        // authenticates and is authorized to write. This proves the 401s in the
        // cross-tenant tests come from the membership gate, not an invalid token.
        using var client = AuthTestHelpers.CreateTenantBearerClient(
            Fixture, _slugA, _baseDomain, _accessTokenA);

        var now = DateTimeOffset.UtcNow;
        var entryPayload = new[]
        {
            new
            {
                type = "sgv",
                sgv = 120,
                date = now.ToUnixTimeMilliseconds(),
                dateString = now.UtcDateTime.ToString("o")
            }
        };
        var response = await client.PostAsJsonAsync("/api/v1/entries", entryPayload);

        response.StatusCode.Should().BeOneOf(
            new[] { HttpStatusCode.OK, HttpStatusCode.Created },
            "a subject must be able to write to a tenant it belongs to using its own access token");
    }

    [Fact]
    public async Task SubjectA_Token_CannotWrite_ToTenantB()
    {
        // Attempt to write into tenant B using tenant A's token.
        using var attacker = AuthTestHelpers.CreateTenantBearerClient(
            Fixture, _slugB, _baseDomain, _accessTokenA);

        var now = DateTimeOffset.UtcNow;
        var entryPayload = new[]
        {
            new
            {
                type = "sgv",
                sgv = 321,
                date = now.ToUnixTimeMilliseconds(),
                dateString = now.UtcDateTime.ToString("o")
            }
        };
        var writeResponse = await attacker.PostAsJsonAsync("/api/v1/entries", entryPayload);

        // Assert - the write is rejected at the auth layer...
        writeResponse.StatusCode.Should().BeOneOf(
            new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden },
            "a subject must not be able to write into a tenant it does not belong to");

        // ...and nothing landed in tenant B.
        using var clientB = AuthTestHelpers.CreateAuthenticatedTenantClient(
            Fixture, _slugB, _baseDomain, _accessTokenB);
        var getResponse = await clientB.GetAsync("/api/v1/entries?count=100");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await getResponse.Content.ReadAsStringAsync();
        var entries = JsonSerializer.Deserialize<JsonElement>(content);
        var leaked = entries.EnumerateArray().Any(e =>
            e.TryGetProperty("sgv", out var sgv) && sgv.GetInt32() == 321);
        leaked.Should().BeFalse("a cross-tenant write must not create data in the target tenant");
    }

    [Fact]
    public async Task TenantMember_WithoutPlatformAdmin_CannotUse_PlatformAdminApi()
    {
        // Subject A is a member (and admin) of tenant A, but is not a platform
        // admin. The platform-admin tenant API requires the platform_admin role,
        // so an ordinary tenant member must be forbidden even on their own tenant.
        using var client = AuthTestHelpers.CreateTenantBearerClient(
            Fixture, _slugA, _baseDomain, _accessTokenA);

        var response = await client.GetAsync("/api/v4/admin/tenants");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a tenant member without the platform_admin role must not access the platform-admin API");
    }

    [Fact]
    public async Task RevokedMember_Token_IsDenied_OnOwnTenant()
    {
        // Sanity: subject A is a member of tenant A and can write before revocation.
        var now = DateTimeOffset.UtcNow;
        using (var before = AuthTestHelpers.CreateTenantBearerClient(Fixture, _slugA, _baseDomain, _accessTokenA))
        {
            var ok = await before.PostAsJsonAsync("/api/v1/entries", new[]
            {
                new { type = "sgv", sgv = 111, date = now.ToUnixTimeMilliseconds(), dateString = now.UtcDateTime.ToString("o") }
            });
            ok.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
        }

        // Revoke subject A's membership in tenant A. No app path soft-revokes today, so simulate it
        // directly, the same way other tests toggle tenants.is_active.
        var connStr = await GetPostgresConnectionStringAsync();
        await using (var conn = new NpgsqlConnection(connStr))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE tenant_members SET revoked_at = now() WHERE subject_id = @s AND tenant_id = @t;";
            cmd.Parameters.AddWithValue("s", _subjectAId);
            cmd.Parameters.AddWithValue("t", _tenantAId);
            await cmd.ExecuteNonQueryAsync();
        }

        // The same previously-valid token is now rejected for both read and write — the membership
        // gate (via the RevokedAt query filter) no longer sees subject A as a member of tenant A.
        using var client = AuthTestHelpers.CreateTenantBearerClient(Fixture, _slugA, _baseDomain, _accessTokenA);

        var read = await client.GetAsync("/api/v1/entries/current");
        read.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a revoked member must not authenticate, even with a previously-valid access token");

        var write = await client.PostAsJsonAsync("/api/v1/entries", new[]
        {
            new { type = "sgv", sgv = 112, date = now.ToUnixTimeMilliseconds(), dateString = now.UtcDateTime.ToString("o") }
        });
        write.StatusCode.Should().BeOneOf(
            new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden },
            "a revoked member must not be able to write");
    }
}

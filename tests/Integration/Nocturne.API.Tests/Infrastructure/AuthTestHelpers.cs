using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Core.Models.Authorization;
using Npgsql;

namespace Nocturne.API.Tests.Integration.Infrastructure;

/// <summary>
/// Result of a full PKCE-based OAuth authorization code flow.
/// </summary>
public class OAuthFlowResult
{
    public required string AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public required string AuthorizationCode { get; init; }
    public required string CodeVerifier { get; init; }
    public string? Scope { get; init; }
}

/// <summary>
/// Shared helper methods for auth integration tests.
/// Provides database seeding, OAuth client registration, PKCE flow execution,
/// and HttpClient creation utilities used across auth test classes.
/// </summary>
public static class AuthTestHelpers
{
    private const string GuestCodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int GuestCodeLength = 7;

    /// <summary>
    /// Creates a subject with a passkey credential, tenant membership, and admin role, and issues
    /// it a direct grant carrying the returned token. The grant is what makes that token
    /// authenticate, since a subject holds no credential of its own.
    /// </summary>
    public static async Task<(Guid SubjectId, string AccessToken)> SeedAuthenticatedSubjectAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string name)
    {
        var subjectId = Guid.CreateVersion7();
        var accessToken = $"{name.ToLowerInvariant().Replace(" ", "-")}-{Guid.NewGuid():N}";

        // Insert subject
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO subjects (id, name, is_active, is_system_subject, created_at, updated_at, approval_status)
                VALUES (@id, @name, true, false, now(), now(), 'Approved');
                """;
            cmd.Parameters.AddWithValue("id", subjectId);
            cmd.Parameters.AddWithValue("name", name);
            await cmd.ExecuteNonQueryAsync();
        }

        // Insert passkey credential
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO passkey_credentials (id, subject_id, credential_id, public_key, sign_count, transports, created_at)
                VALUES (@id, @subjectId, @credentialId, @publicKey, 0, '{}', now());
                """;
            cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
            cmd.Parameters.AddWithValue("subjectId", subjectId);
            cmd.Parameters.AddWithValue("credentialId", Encoding.UTF8.GetBytes($"cred-{subjectId:N}"));
            cmd.Parameters.AddWithValue("publicKey", Encoding.UTF8.GetBytes($"pk-{subjectId:N}"));
            await cmd.ExecuteNonQueryAsync();
        }

        // Insert tenant member
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO tenant_members (id, tenant_id, subject_id, direct_permissions, sys_created_at, sys_updated_at, limit_to_24_hours)
                VALUES (@id, @tenantId, @subjectId, '["*"]'::jsonb, now(), now(), false);
                """;
            cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("subjectId", subjectId);
            await cmd.ExecuteNonQueryAsync();
        }

        // Grant admin role
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO subject_roles (subject_id, role_id)
                SELECT @subjectId, r.id
                FROM roles r WHERE r.name = 'admin'
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("subjectId", subjectId);
            await cmd.ExecuteNonQueryAsync();
        }

        // The credential. Without it the returned token matches nothing, and a caller sending it
        // alongside an api-secret would silently authenticate as the api-secret's principal
        // instead, which is indistinguishable from success until a test asks who it is talking to.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT set_config('app.current_tenant_id', @tenantText, true);
                INSERT INTO oauth_grants (id, tenant_id, subject_id, grant_type, scopes, label, token_hash, created_at)
                VALUES (@id, @tenantId, @subjectId, 'direct', ARRAY['*'], @label, @hash, now());
                """;
            cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("tenantText", tenantId.ToString());
            cmd.Parameters.AddWithValue("subjectId", subjectId);
            cmd.Parameters.AddWithValue("label", name);
            cmd.Parameters.AddWithValue("hash", HashUtils.Sha256Hex(accessToken));
            await cmd.ExecuteNonQueryAsync();
        }

        return (subjectId, accessToken);
    }

    /// <summary>
    /// Seeds the full-access direct grant that the <c>api-secret</c> header used across these tests
    /// authenticates against, and returns its id.
    /// </summary>
    /// <remarks>
    /// <c>ApiKeyHandler</c> matches a non-<c>noc_</c> header value against <c>legacy_secret_hash</c>
    /// verbatim, and <c>CleanupDatabaseAsync</c> truncates <c>oauth_grants</c> before every test, so
    /// without this row the header matches nothing. A request that also carries a bearer token is
    /// unaffected either way — <c>ApiKeyHandler</c> runs last — but a SignalR hub connection carries
    /// the header alone, so it is this row that decides whether the connection has a credential.
    /// </remarks>
    /// <param name="conn">Open connection to the test database.</param>
    /// <param name="tenantId">The tenant the grant belongs to.</param>
    /// <param name="subjectId">An existing subject to own the grant.</param>
    /// <param name="apiSecret">The value sent in the <c>api-secret</c> header.</param>
    public static async Task<Guid> SeedApiSecretGrantAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid subjectId,
        string apiSecret = "test-secret-for-integration-tests")
    {
        var grantId = Guid.CreateVersion7();

        // Set RLS context
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tenantId, false);";
            cmd.Parameters.AddWithValue("tenantId", tenantId.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO oauth_grants (id, tenant_id, subject_id, grant_type, scopes, legacy_secret_hash, created_at)
                VALUES (@id, @tenantId, @subjectId, @grantType, @scopes, @legacySecretHash, now());
                """;
            cmd.Parameters.AddWithValue("id", grantId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("subjectId", subjectId);
            cmd.Parameters.AddWithValue("grantType", OAuthGrantTypes.Direct);
            cmd.Parameters.AddWithValue("scopes", new[] { Scope.FullAccess });
            cmd.Parameters.AddWithValue("legacySecretHash", apiSecret.ToLowerInvariant());
            await cmd.ExecuteNonQueryAsync();
        }

        return grantId;
    }

    /// <summary>
    /// Creates a subject with tenant membership and admin role but WITHOUT a passkey credential.
    /// Used for testing tenant setup guard scenarios where passkey enrollment is required.
    /// </summary>
    public static async Task<(Guid SubjectId, string AccessToken)> SeedSubjectWithoutPasskeyAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string name)
    {
        var subjectId = Guid.CreateVersion7();
        var accessToken = $"{name.ToLowerInvariant().Replace(" ", "-")}-{Guid.NewGuid():N}";

        // Insert subject
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO subjects (id, name, is_active, is_system_subject, created_at, updated_at, approval_status)
                VALUES (@id, @name, true, false, now(), now(), 'Approved');
                """;
            cmd.Parameters.AddWithValue("id", subjectId);
            cmd.Parameters.AddWithValue("name", name);
            await cmd.ExecuteNonQueryAsync();
        }

        // Insert tenant member
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO tenant_members (id, tenant_id, subject_id, direct_permissions, sys_created_at, sys_updated_at, limit_to_24_hours)
                VALUES (@id, @tenantId, @subjectId, '["*"]'::jsonb, now(), now(), false);
                """;
            cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("subjectId", subjectId);
            await cmd.ExecuteNonQueryAsync();
        }

        // Grant admin role
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO subject_roles (subject_id, role_id)
                SELECT @subjectId, r.id
                FROM roles r WHERE r.name = 'admin'
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("subjectId", subjectId);
            await cmd.ExecuteNonQueryAsync();
        }

        return (subjectId, accessToken);
    }

    /// <summary>
    /// Registers an OAuth client via the dynamic client registration endpoint.
    /// Returns the assigned client_id.
    /// </summary>
    public static async Task<string> RegisterOAuthClientAsync(
        HttpClient client,
        string redirectUri = "http://localhost:9999/callback",
        string scope = "glucose.read treatments.read")
    {
        var payload = new
        {
            client_name = $"test-client-{Guid.NewGuid():N}",
            redirect_uris = new[] { redirectUri },
            scope
        };

        var response = await client.PostAsJsonAsync("/api/oauth/register", payload);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(content);
        return doc.RootElement.GetProperty("client_id").GetString()
               ?? throw new InvalidOperationException("OAuth client registration did not return a client_id.");
    }

    /// <summary>
    /// Generates a PKCE code_verifier and code_challenge pair using the S256 method.
    /// </summary>
    public static (string CodeVerifier, string CodeChallenge) GeneratePkceChallenge()
    {
        var verifier = PkceValidator.GenerateCodeVerifier();
        var challenge = PkceValidator.ComputeCodeChallenge(verifier);
        return (verifier, challenge);
    }

    /// <summary>
    /// Executes a full PKCE-based OAuth authorization code flow:
    /// consent, redirect capture, and token exchange.
    /// The provided client must already be authenticated (api-secret + Bearer headers).
    /// </summary>
    public static async Task<OAuthFlowResult> ExecutePkceFlowAsync(
        HttpClient authenticatedClient,
        string clientId,
        string redirectUri = "http://localhost:9999/callback",
        string scope = "glucose.read treatments.read")
    {
        var (codeVerifier, codeChallenge) = GeneratePkceChallenge();

        // POST consent to authorize endpoint - use a handler that doesn't follow redirects
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var noRedirectClient = new HttpClient(handler)
        {
            BaseAddress = authenticatedClient.BaseAddress
        };

        // Copy auth headers from the authenticated client
        foreach (var header in authenticatedClient.DefaultRequestHeaders)
        {
            noRedirectClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }

        var consentForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = scope,
            ["code_challenge"] = codeChallenge,
            ["approved"] = "true"
        });

        var authorizeResponse = await noRedirectClient.PostAsync("/api/oauth/authorize", consentForm);

        // Extract authorization code from redirect Location header
        var location = authorizeResponse.Headers.Location
                       ?? throw new InvalidOperationException(
                           $"Authorize response did not contain a Location header. Status: {authorizeResponse.StatusCode}");

        var query = System.Web.HttpUtility.ParseQueryString(location.Query);
        var code = query["code"]
                   ?? throw new InvalidOperationException("Authorization redirect did not contain a code parameter.");

        // Exchange code for tokens
        var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId
        });

        var tokenResponse = await authenticatedClient.PostAsync("/api/oauth/token", tokenForm);
        tokenResponse.EnsureSuccessStatusCode();

        var tokenContent = await tokenResponse.Content.ReadAsStringAsync();
        var tokenDoc = JsonDocument.Parse(tokenContent);

        return new OAuthFlowResult
        {
            AccessToken = tokenDoc.RootElement.GetProperty("access_token").GetString()!,
            RefreshToken = tokenDoc.RootElement.TryGetProperty("refresh_token", out var rtEl) ? rtEl.GetString() : null,
            AuthorizationCode = code,
            CodeVerifier = codeVerifier,
            Scope = tokenDoc.RootElement.TryGetProperty("scope", out var scopeEl)
                ? scopeEl.GetString() ?? scope
                : scope
        };
    }

    /// <summary>
    /// Seeds a guest link grant in the oauth_grants table.
    /// Returns the grant ID and the plaintext guest code.
    /// </summary>
    public static async Task<(Guid GrantId, string Code)> SeedGuestLinkAsync(
        NpgsqlConnection conn,
        Guid dataOwnerSubjectId,
        DateTime? expiresAt = null,
        DateTime? revokedAt = null,
        DateTime? activatedAt = null)
    {
        var grantId = Guid.CreateVersion7();
        var code = GenerateGuestCode();
        var codeHash = HashUtils.Sha256Hex(code.ToUpperInvariant());

        // Resolve tenant for the data owner
        Guid tenantId;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT tenant_id FROM tenant_members WHERE subject_id = @subjectId LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("subjectId", dataOwnerSubjectId);
            var result = await cmd.ExecuteScalarAsync()
                         ?? throw new InvalidOperationException(
                             $"Subject {dataOwnerSubjectId} has no tenant membership.");
            tenantId = (Guid)result;
        }

        // Set RLS tenant context for the insert
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tenantId, false);";
            cmd.Parameters.AddWithValue("tenantId", tenantId.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO oauth_grants (id, tenant_id, subject_id, grant_type, scopes, token_hash, created_at, expires_at, revoked_at, activated_at)
                VALUES (@id, @tenantId, @subjectId, 'guest', ARRAY['glucose.read','treatments.read'], @tokenHash, now(), @expiresAt, @revokedAt, @activatedAt);
                """;
            cmd.Parameters.AddWithValue("id", grantId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("subjectId", dataOwnerSubjectId);
            cmd.Parameters.AddWithValue("tokenHash", codeHash);
            cmd.Parameters.AddWithValue("expiresAt", (object?)(expiresAt ?? DateTime.UtcNow.AddHours(48)) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("revokedAt", (object?)revokedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("activatedAt", (object?)activatedAt ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        return (grantId, code);
    }

    /// <summary>
    /// Gets the id of the tenant <see cref="ApiIntegrationTestFixture"/> seeds.
    /// </summary>
    public static async Task<Guid> GetTenantIdAsync(NpgsqlConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM tenants WHERE slug = @slug;";
        cmd.Parameters.AddWithValue("slug", ApiIntegrationTestFixture.TenantSlug);
        var result = await cmd.ExecuteScalarAsync()
                     ?? throw new InvalidOperationException($"Tenant '{ApiIntegrationTestFixture.TenantSlug}' does not exist.");
        return (Guid)result;
    }

    /// <summary>
    /// Gets the tenant_members ID for the Public system subject in the given tenant.
    /// </summary>
    public static async Task<Guid> GetPublicMemberIdAsync(NpgsqlConnection conn, Guid tenantId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT tm.id
            FROM tenant_members tm
            JOIN subjects s ON s.id = tm.subject_id
            WHERE tm.tenant_id = @tenantId AND s.is_system_subject = true AND s.name = 'Public'
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("tenantId", tenantId);
        var result = await cmd.ExecuteScalarAsync()
                     ?? throw new InvalidOperationException(
                         $"Public system subject not found in tenant {tenantId}.");
        return (Guid)result;
    }

    /// <summary>
    /// Gets the id of one of a tenant's seeded roles (<see cref="RoleSeeds"/>) by its slug.
    /// </summary>
    public static async Task<Guid> GetTenantRoleIdAsync(NpgsqlConnection conn, Guid tenantId, string slug)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM tenant_roles WHERE tenant_id = @tenantId AND slug = @slug;";
        cmd.Parameters.AddWithValue("tenantId", tenantId);
        cmd.Parameters.AddWithValue("slug", slug);
        var result = await cmd.ExecuteScalarAsync()
                     ?? throw new InvalidOperationException($"Tenant {tenantId} has no '{slug}' role.");
        return (Guid)result;
    }

    /// <summary>
    /// Creates an HttpClient with a Bearer authorization header for the given access token.
    /// </summary>
    public static HttpClient CreateBearerClient(
        ApiIntegrationTestFixture fixture,
        string accessToken)
    {
        var client = fixture.CreateHttpClient("nocturne-api", "http");
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
        return client;
    }

    /// <summary>
    /// Creates an HttpClient with both an api-secret header and a Bearer authorization header.
    /// </summary>
    public static HttpClient CreateAuthenticatedSubjectClient(
        ApiIntegrationTestFixture fixture,
        string accessToken,
        string apiSecret = "test-secret-for-integration-tests")
    {
        var client = fixture.CreateHttpClient("nocturne-api", "http");
        client.DefaultRequestHeaders.Add("api-secret", apiSecret);
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
        return client;
    }

    /// <summary>
    /// Creates a tenant the way the product does, with its roles, its Public subject membership and
    /// its bundled OAuth clients, and returns its id.
    /// </summary>
    public static async Task<Guid> SeedTenantAsync(
        ApiIntegrationTestFixture fixture,
        string slug,
        string displayName)
    {
        using var scope = fixture.Services.CreateScope();
        var tenants = scope.ServiceProvider.GetRequiredService<ITenantService>();
        var created = await tenants.CreateWithoutOwnerAsync(slug, displayName);
        return created.Id;
    }

    /// <summary>
    /// Creates an HttpClient targeting a specific tenant by slug via the Host header.
    /// </summary>
    public static HttpClient CreateTenantClient(
        ApiIntegrationTestFixture fixture,
        string slug,
        string baseDomain)
    {
        var client = fixture.CreateHttpClient("nocturne-api", "http");
        client.DefaultRequestHeaders.Host = $"{slug}.{baseDomain}";
        return client;
    }

    /// <summary>
    /// Creates an authenticated HttpClient targeting a specific tenant by slug via the Host header.
    /// Includes api-secret and Bearer authorization headers.
    /// </summary>
    public static HttpClient CreateAuthenticatedTenantClient(
        ApiIntegrationTestFixture fixture,
        string slug,
        string baseDomain,
        string accessToken,
        string apiSecret = "test-secret-for-integration-tests")
    {
        var client = fixture.CreateHttpClient("nocturne-api", "http");
        client.DefaultRequestHeaders.Host = $"{slug}.{baseDomain}";
        client.DefaultRequestHeaders.Add("api-secret", apiSecret);
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
        return client;
    }

    /// <summary>
    /// Creates an HttpClient targeting a specific tenant by slug with ONLY a Bearer
    /// access token (no api-secret). Use this to exercise the subject-membership
    /// authorization gate in <c>AuthenticationMiddleware</c>: an api-secret header
    /// authenticates as an admin API key on the resolved tenant and bypasses the
    /// membership check, so it would mask cross-tenant authorization failures.
    /// </summary>
    public static HttpClient CreateTenantBearerClient(
        ApiIntegrationTestFixture fixture,
        string slug,
        string baseDomain,
        string accessToken)
    {
        var client = fixture.CreateHttpClient("nocturne-api", "http");
        client.DefaultRequestHeaders.Host = $"{slug}.{baseDomain}";
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
        return client;
    }

    /// <summary>
    /// Extracts the base domain (host:port) from an HttpClient's BaseAddress.
    /// </summary>
    public static string GetBaseDomain(HttpClient apiClient)
    {
        var baseAddress = apiClient.BaseAddress
            ?? throw new InvalidOperationException("HttpClient has no BaseAddress set.");
        return $"{baseAddress.Host}:{baseAddress.Port}";
    }

    /// <summary>
    /// Seeds an enabled-by-default threshold alert rule with one web push channel, and returns its id.
    /// </summary>
    /// <remarks>
    /// Written through the entities, which keep in step with the schema where a raw column list
    /// silently falls behind it.
    /// </remarks>
    public static async Task<Guid> SeedAlertRuleAsync(
        ApiIntegrationTestFixture fixture,
        Guid tenantId,
        string name = "Test High Alert",
        AlertConditionType conditionType = AlertConditionType.Threshold,
        bool isEnabled = true)
    {
        await using var db = fixture.CreateDbContext(tenantId);
        var rule = new AlertRuleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = name,
            ConditionType = conditionType,
            ConditionParams = """{"direction":"above","value":180}""",
            IsEnabled = isEnabled,
        };
        db.AlertRules.Add(rule);
        db.AlertRuleChannels.Add(new AlertRuleChannelEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            AlertRuleId = rule.Id,
            ChannelType = ChannelType.WebPush,
            Destination = "default",
        });
        await db.SaveChangesAsync();
        return rule.Id;
    }

    /// <summary>
    /// Seeds an open excursion for a rule and its triggered alert instance.
    /// </summary>
    public static async Task<(Guid ExcursionId, Guid InstanceId)> SeedAlertExcursionAsync(
        ApiIntegrationTestFixture fixture,
        Guid tenantId,
        Guid alertRuleId,
        DateTime? acknowledgedAt = null)
    {
        await using var db = fixture.CreateDbContext(tenantId);
        var now = DateTime.UtcNow;
        var excursion = new AlertExcursionEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            AlertRuleId = alertRuleId,
            StartedAt = now,
            AcknowledgedAt = acknowledgedAt,
        };
        var instance = new AlertInstanceEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            AlertExcursionId = excursion.Id,
            TriggeredAt = now,
        };
        db.AlertExcursions.Add(excursion);
        db.AlertInstances.Add(instance);
        await db.SaveChangesAsync();
        return (excursion.Id, instance.Id);
    }

    /// <summary>
    /// Generates a random guest code using the restricted alphabet (no ambiguous characters).
    /// </summary>
    private static string GenerateGuestCode()
    {
        var chars = new char[GuestCodeLength];
        for (var i = 0; i < GuestCodeLength; i++)
        {
            chars[i] = GuestCodeAlphabet[RandomNumberGenerator.GetInt32(GuestCodeAlphabet.Length)];
        }
        return new string(chars);
    }
}

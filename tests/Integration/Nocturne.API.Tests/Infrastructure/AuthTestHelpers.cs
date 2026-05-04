using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    /// Creates a subject with a passkey credential, tenant membership, and admin role.
    /// Returns the subject ID and a plaintext access token (stored as SHA-256 hash).
    /// </summary>
    public static async Task<(Guid SubjectId, string AccessToken)> SeedAuthenticatedSubjectAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string name)
    {
        var subjectId = Guid.CreateVersion7();
        var accessToken = $"{name.ToLowerInvariant().Replace(" ", "-")}-{Guid.NewGuid():N}";
        var tokenHash = ComputeSha256Hex(accessToken);
        var prefix = accessToken.Length > 10 ? accessToken[..10] + "..." : accessToken;

        // Insert subject
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO subjects (id, name, access_token_hash, access_token_prefix, is_active, is_system_subject, created_at, updated_at, approval_status)
                VALUES (@id, @name, @hash, @prefix, true, false, now(), now(), 'Approved');
                """;
            cmd.Parameters.AddWithValue("id", subjectId);
            cmd.Parameters.AddWithValue("name", name);
            cmd.Parameters.AddWithValue("hash", tokenHash);
            cmd.Parameters.AddWithValue("prefix", prefix);
            await cmd.ExecuteNonQueryAsync();
        }

        // Insert passkey credential
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO passkey_credentials (id, subject_id, credential_id, public_key, sign_count, created_at)
                VALUES (@id, @subjectId, @credentialId, @publicKey, 0, now());
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
                INSERT INTO tenant_members (id, tenant_id, subject_id, sys_created_at, sys_updated_at, limit_to_24_hours)
                VALUES (@id, @tenantId, @subjectId, now(), now(), false);
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
                INSERT INTO subject_roles (id, subject_id, role_id, sys_created_at, sys_updated_at)
                SELECT @id, @subjectId, r.id, now(), now()
                FROM roles r WHERE r.name = 'admin'
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
            cmd.Parameters.AddWithValue("subjectId", subjectId);
            await cmd.ExecuteNonQueryAsync();
        }

        return (subjectId, accessToken);
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
        var tokenHash = ComputeSha256Hex(accessToken);
        var prefix = accessToken.Length > 10 ? accessToken[..10] + "..." : accessToken;

        // Insert subject
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO subjects (id, name, access_token_hash, access_token_prefix, is_active, is_system_subject, created_at, updated_at, approval_status)
                VALUES (@id, @name, @hash, @prefix, true, false, now(), now(), 'Approved');
                """;
            cmd.Parameters.AddWithValue("id", subjectId);
            cmd.Parameters.AddWithValue("name", name);
            cmd.Parameters.AddWithValue("hash", tokenHash);
            cmd.Parameters.AddWithValue("prefix", prefix);
            await cmd.ExecuteNonQueryAsync();
        }

        // Insert tenant member
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO tenant_members (id, tenant_id, subject_id, sys_created_at, sys_updated_at, limit_to_24_hours)
                VALUES (@id, @tenantId, @subjectId, now(), now(), false);
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
                INSERT INTO subject_roles (id, subject_id, role_id, sys_created_at, sys_updated_at)
                SELECT @id, @subjectId, r.id, now(), now()
                FROM roles r WHERE r.name = 'admin'
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
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
        string scope = "entries.read treatments.read")
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
        string scope = "entries.read treatments.read")
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
        var codeHash = ComputeSha256Hex(code.ToUpperInvariant());

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
                VALUES (@id, @tenantId, @subjectId, 'guest', '["entries.read","treatments.read"]'::jsonb, @tokenHash, now(), @expiresAt, @revokedAt, @activatedAt);
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
    /// Gets the first tenant ID from the database.
    /// </summary>
    public static async Task<Guid> GetTenantIdAsync(NpgsqlConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM tenants LIMIT 1;";
        var result = await cmd.ExecuteScalarAsync();
        return (Guid)result!;
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
    /// Gets role IDs by name.
    /// </summary>
    public static async Task<Dictionary<string, Guid>> GetRoleIdsByNameAsync(
        NpgsqlConnection conn,
        params string[] names)
    {
        var result = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name FROM roles WHERE name = ANY(@names);";
        cmd.Parameters.AddWithValue("names", names);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[reader.GetString(1)] = reader.GetGuid(0);
        }

        return result;
    }

    /// <summary>
    /// Creates an HttpClient with a Bearer authorization header for the given access token.
    /// </summary>
    public static HttpClient CreateBearerClient(
        AspireIntegrationTestFixture fixture,
        string accessToken)
    {
        var client = fixture.CreateHttpClient("nocturne-api", "api");
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
        return client;
    }

    /// <summary>
    /// Creates an HttpClient with both an api-secret header and a Bearer authorization header.
    /// </summary>
    public static HttpClient CreateAuthenticatedSubjectClient(
        AspireIntegrationTestFixture fixture,
        string accessToken,
        string apiSecret = "test-secret-for-integration-tests")
    {
        var client = fixture.CreateHttpClient("nocturne-api", "api");
        client.DefaultRequestHeaders.Add("api-secret", apiSecret);
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
        return client;
    }

    /// <summary>
    /// Seeds a new tenant and creates the Public system subject tenant membership for it.
    /// Returns the new tenant ID.
    /// </summary>
    public static async Task<Guid> SeedTenantAsync(
        NpgsqlConnection conn,
        string slug,
        string displayName)
    {
        var tenantId = Guid.CreateVersion7();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, created_at, updated_at)
                VALUES (@id, @slug, @displayName, true, now(), now());
                """;
            cmd.Parameters.AddWithValue("id", tenantId);
            cmd.Parameters.AddWithValue("slug", slug);
            cmd.Parameters.AddWithValue("displayName", displayName);
            await cmd.ExecuteNonQueryAsync();
        }

        // Create Public system subject tenant member
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO tenant_members (id, tenant_id, subject_id, sys_created_at, sys_updated_at, limit_to_24_hours)
                SELECT @id, @tenantId, s.id, now(), now(), false
                FROM subjects s
                WHERE s.name = 'Public' AND s.is_system_subject = true
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            await cmd.ExecuteNonQueryAsync();
        }

        return tenantId;
    }

    /// <summary>
    /// Creates an HttpClient targeting a specific tenant by slug via the Host header.
    /// </summary>
    public static HttpClient CreateTenantClient(
        AspireIntegrationTestFixture fixture,
        string slug,
        string baseDomain)
    {
        var client = fixture.CreateHttpClient("nocturne-api", "api");
        client.DefaultRequestHeaders.Host = $"{slug}.{baseDomain}";
        return client;
    }

    /// <summary>
    /// Creates an authenticated HttpClient targeting a specific tenant by slug via the Host header.
    /// Includes api-secret and Bearer authorization headers.
    /// </summary>
    public static HttpClient CreateAuthenticatedTenantClient(
        AspireIntegrationTestFixture fixture,
        string slug,
        string baseDomain,
        string accessToken,
        string apiSecret = "test-secret-for-integration-tests")
    {
        var client = fixture.CreateHttpClient("nocturne-api", "api");
        client.DefaultRequestHeaders.Host = $"{slug}.{baseDomain}";
        client.DefaultRequestHeaders.Add("api-secret", apiSecret);
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
    /// Seeds an alert rule with a default schedule, escalation step, and step channel.
    /// Returns the alert rule ID.
    /// </summary>
    public static async Task<Guid> SeedAlertRuleAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        string name = "Test High Alert",
        string conditionType = "Threshold",
        bool isEnabled = true)
    {
        var ruleId = Guid.CreateVersion7();
        var scheduleId = Guid.CreateVersion7();
        var stepId = Guid.CreateVersion7();
        var channelId = Guid.CreateVersion7();

        // Set RLS context
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tenantId, false);";
            cmd.Parameters.AddWithValue("tenantId", tenantId.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        // Insert alert rule
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO alert_rules (id, tenant_id, name, condition_type, condition_params, hysteresis_minutes, confirmation_readings, severity, is_enabled, sort_order, created_at, updated_at)
                VALUES (@id, @tenantId, @name, @conditionType, '{"direction":"above","value":180}'::jsonb, 15, 2, 'Normal', @isEnabled, 0, now(), now());
                """;
            cmd.Parameters.AddWithValue("id", ruleId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("name", name);
            cmd.Parameters.AddWithValue("conditionType", conditionType);
            cmd.Parameters.AddWithValue("isEnabled", isEnabled);
            await cmd.ExecuteNonQueryAsync();
        }

        // Insert alert schedule
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO alert_schedules (id, tenant_id, alert_rule_id, name, is_default, timezone, quiet_hours_override_critical, created_at, updated_at)
                VALUES (@id, @tenantId, @ruleId, 'Default', true, 'UTC', true, now(), now());
                """;
            cmd.Parameters.AddWithValue("id", scheduleId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("ruleId", ruleId);
            await cmd.ExecuteNonQueryAsync();
        }

        // Insert alert escalation step
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO alert_escalation_steps (id, tenant_id, alert_schedule_id, step_order, delay_seconds, created_at)
                VALUES (@id, @tenantId, @scheduleId, 0, 0, now());
                """;
            cmd.Parameters.AddWithValue("id", stepId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("scheduleId", scheduleId);
            await cmd.ExecuteNonQueryAsync();
        }

        // Insert alert step channel
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO alert_step_channels (id, tenant_id, escalation_step_id, channel_type, destination, created_at)
                VALUES (@id, @tenantId, @stepId, 'WebPush', 'default', now());
                """;
            cmd.Parameters.AddWithValue("id", channelId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("stepId", stepId);
            await cmd.ExecuteNonQueryAsync();
        }

        return ruleId;
    }

    /// <summary>
    /// Seeds an alert excursion and associated alert instance for a given rule.
    /// Returns the excursion ID and instance ID.
    /// </summary>
    public static async Task<(Guid ExcursionId, Guid InstanceId)> SeedAlertExcursionAsync(
        NpgsqlConnection conn,
        Guid tenantId,
        Guid alertRuleId,
        DateTime? acknowledgedAt = null)
    {
        var excursionId = Guid.CreateVersion7();
        var instanceId = Guid.CreateVersion7();

        // Set RLS context
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tenantId, false);";
            cmd.Parameters.AddWithValue("tenantId", tenantId.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        // Insert alert excursion
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO alert_excursions (id, tenant_id, alert_rule_id, started_at, acknowledged_at)
                VALUES (@id, @tenantId, @ruleId, now(), @acknowledgedAt);
                """;
            cmd.Parameters.AddWithValue("id", excursionId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("ruleId", alertRuleId);
            cmd.Parameters.AddWithValue("acknowledgedAt", (object?)acknowledgedAt ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        // Find schedule for the rule
        Guid scheduleId;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM alert_schedules WHERE alert_rule_id = @ruleId LIMIT 1;";
            cmd.Parameters.AddWithValue("ruleId", alertRuleId);
            var result = await cmd.ExecuteScalarAsync()
                         ?? throw new InvalidOperationException(
                             $"No alert schedule found for rule {alertRuleId}.");
            scheduleId = (Guid)result;
        }

        // Insert alert instance
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO alert_instances (id, tenant_id, alert_excursion_id, alert_schedule_id, current_step_order, status, triggered_at, snooze_count)
                VALUES (@id, @tenantId, @excursionId, @scheduleId, 0, 'active', now(), 0);
                """;
            cmd.Parameters.AddWithValue("id", instanceId);
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("excursionId", excursionId);
            cmd.Parameters.AddWithValue("scheduleId", scheduleId);
            await cmd.ExecuteNonQueryAsync();
        }

        return (excursionId, instanceId);
    }

    /// <summary>
    /// Computes the SHA-256 hash of a string, returned as lowercase hex.
    /// Matches DirectGrantTokenHandler.ComputeSha256Hex.
    /// </summary>
    public static string ComputeSha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
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

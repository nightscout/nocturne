using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Constants;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// A credential minted for a subject is issued, refreshed and redeemed only while the subject is
/// active, and the refusal reads as the one an unknown or spent credential gets.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class InactiveSubjectCredentialIntegrationTests : ApiIntegrationTestBase
{
    public InactiveSubjectCredentialIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task A_deactivated_subjects_refresh_is_answered_as_a_revoked_grant_is()
    {
        await using var conn = await OpenAsync();
        var (subjectId, client) = await SignInAsync(conn, "Refreshing Member");
        using var _ = client;
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(client);
        var tokens = await AuthTestHelpers.ExecutePkceFlowAsync(client, clientId);
        var revokedClientId = await AuthTestHelpers.RegisterOAuthClientAsync(client);
        var revoked = await AuthTestHelpers.ExecutePkceFlowAsync(client, revokedClientId);
        await RevokeGrantOfAsync(conn, revokedClientId);
        var revokedGrantAnswer = await ShapeOfAsync(await RefreshAsync(client, revokedClientId, revoked.RefreshToken!));

        await SetActiveAsync(conn, subjectId, false);
        try
        {
            var response = await RefreshAsync(client, clientId, tokens.RefreshToken!);

            (await ShapeOfAsync(response)).Should().Be(revokedGrantAnswer);
        }
        finally
        {
            await SetActiveAsync(conn, subjectId, true);
        }
    }

    [Fact]
    public async Task A_refused_refresh_rotates_nothing_and_the_same_token_refreshes_once_the_subject_is_active()
    {
        await using var conn = await OpenAsync();
        var (subjectId, client) = await SignInAsync(conn, "Returning Member");
        using var _ = client;
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(client);
        var tokens = await AuthTestHelpers.ExecutePkceFlowAsync(client, clientId);
        var refreshTokensBefore = await CountRefreshTokensOfAsync(conn, clientId);

        await SetActiveAsync(conn, subjectId, false);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                (await RefreshAsync(client, clientId, tokens.RefreshToken!)).StatusCode
                    .Should().Be(HttpStatusCode.BadRequest);
            }

            (await CountRefreshTokensOfAsync(conn, clientId)).Should().Be(refreshTokensBefore);
        }
        finally
        {
            await SetActiveAsync(conn, subjectId, true);
        }

        var reactivated = await RefreshAsync(client, clientId, tokens.RefreshToken!);

        reactivated.StatusCode.Should().Be(HttpStatusCode.OK, await reactivated.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_deactivated_subjects_authorization_code_is_answered_as_an_invalid_code_is_and_writes_no_grant()
    {
        await using var conn = await OpenAsync();
        var (subjectId, client) = await SignInAsync(conn, "Consenting Member");
        using var _ = client;
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(client);
        var (code, verifier) = await AuthTestHelpers.AuthorizeAsync(client, clientId);
        var invalidCodeAnswer = await ShapeOfAsync(
            await ExchangeCodeAsync(client, clientId, $"unknown-{Guid.NewGuid():N}", verifier));
        var grantsBefore = await CountGrantsOfAsync(conn, clientId);

        await SetActiveAsync(conn, subjectId, false);
        try
        {
            var response = await ExchangeCodeAsync(client, clientId, code, verifier);

            (await ShapeOfAsync(response)).Should().Be(invalidCodeAnswer);
            (await CountGrantsOfAsync(conn, clientId)).Should().Be(grantsBefore);
        }
        finally
        {
            await SetActiveAsync(conn, subjectId, true);
        }

        var replayed = await ExchangeCodeAsync(client, clientId, code, verifier);
        (await ShapeOfAsync(replayed)).Should().Be(invalidCodeAnswer, "the refused exchange spent the code");
    }

    [Fact]
    public async Task A_deactivated_subjects_device_code_is_answered_as_an_invalid_device_code_is()
    {
        await using var conn = await OpenAsync();
        var (subjectId, client) = await SignInAsync(conn, "Approving Member");
        using var _ = client;
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(client);
        var authorization = await client.PostAsync("/api/oauth/device", Form(
            ("client_id", clientId),
            ("scope", "glucose.read")));
        authorization.EnsureSuccessStatusCode();
        var device = JsonSerializer.Deserialize<JsonElement>(await authorization.Content.ReadAsStringAsync());
        var approval = await client.PostAsync("/api/oauth/device-approve", Form(
            ("user_code", device.GetProperty("user_code").GetString()!),
            ("approved", "true")));
        approval.EnsureSuccessStatusCode();
        var invalidCodeAnswer = await ShapeOfAsync(
            await ExchangeDeviceCodeAsync(client, clientId, $"unknown-{Guid.NewGuid():N}"));

        await SetActiveAsync(conn, subjectId, false);
        try
        {
            var response = await ExchangeDeviceCodeAsync(
                client, clientId, device.GetProperty("device_code").GetString()!);

            (await ShapeOfAsync(response)).Should().Be(invalidCodeAnswer);
            (await CountRefreshTokensOfAsync(conn, clientId)).Should().Be(0);
        }
        finally
        {
            await SetActiveAsync(conn, subjectId, true);
        }
    }

    [Fact]
    public async Task A_login_code_for_a_deactivated_member_is_refused_like_a_spent_code_and_issues_no_session()
    {
        await using var conn = await OpenAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (memberId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Handed Off Member");
        var code = $"login-{Guid.NewGuid():N}";
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO login_codes (id, tenant_id, subject_id, code_hash, purpose, expires_at, created_at)
                VALUES (@id, @tenantId, @subjectId, @hash, 'Login', now() + interval '5 minutes', now());
                """;
            cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("subjectId", memberId);
            cmd.Parameters.AddWithValue("hash", HashUtils.Sha256Hex(code));
            await cmd.ExecuteNonQueryAsync();
        }

        await SetActiveAsync(conn, memberId, false);
        try
        {
            using var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);
            var spent = await client.PostAsJsonAsync("/api/auth/handoff", new { code = $"spent-{Guid.NewGuid():N}" });

            var response = await client.PostAsJsonAsync("/api/auth/handoff", new { code });

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await DetailOfAsync(response)).Should().Be(await DetailOfAsync(spent));
            response.Headers.Contains("Set-Cookie").Should().BeFalse();

            await SetActiveAsync(conn, memberId, true);
            var reactivated = await client.PostAsJsonAsync("/api/auth/handoff", new { code });
            reactivated.StatusCode.Should().Be(HttpStatusCode.OK, "the refused attempt does not spend the code");
        }
        finally
        {
            await SetActiveAsync(conn, memberId, true);
        }
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        return conn;
    }

    private async Task<(Guid SubjectId, HttpClient Client)> SignInAsync(NpgsqlConnection conn, string name)
    {
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (subjectId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, name);
        var session = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);
        return (subjectId, AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, session));
    }

    private static async Task SetActiveAsync(NpgsqlConnection conn, Guid subjectId, bool isActive)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE subjects SET is_active = @isActive WHERE id = @id;";
        cmd.Parameters.AddWithValue("isActive", isActive);
        cmd.Parameters.AddWithValue("id", subjectId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    private static async Task<string?> DetailOfAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("detail").GetString();

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string clientId, string refreshToken) =>
        client.PostAsync("/api/oauth/token", Form(
            ("grant_type", "refresh_token"),
            ("refresh_token", refreshToken),
            ("client_id", clientId)));

    private static Task<HttpResponseMessage> ExchangeCodeAsync(
        HttpClient client, string clientId, string code, string verifier) =>
        client.PostAsync("/api/oauth/token", Form(
            ("grant_type", "authorization_code"),
            ("code", code),
            ("code_verifier", verifier),
            ("redirect_uri", "http://localhost:9999/callback"),
            ("client_id", clientId)));

    private static Task<HttpResponseMessage> ExchangeDeviceCodeAsync(
        HttpClient client, string clientId, string deviceCode) =>
        client.PostAsync("/api/oauth/token", Form(
            ("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
            ("device_code", deviceCode),
            ("client_id", clientId)));

    /// <summary>The status, every header but <c>Date</c>, and the body, as one string to compare.</summary>
    private static async Task<string> ShapeOfAsync(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(h => h.Key != "Date")
            .OrderBy(h => h.Key, StringComparer.Ordinal)
            .Select(h => $"{h.Key}: {string.Join(", ", h.Value)}");
        return $"{(int)response.StatusCode}\n{string.Join("\n", headers)}\n\n{await response.Content.ReadAsStringAsync()}";
    }

    private static Task<long> CountGrantsOfAsync(NpgsqlConnection conn, string clientId) =>
        ScalarAsync<long>(conn, """
            SELECT count(*) FROM oauth_grants g
            JOIN oauth_clients c ON c.id = g.client_id
            WHERE c.client_id = @key;
            """, clientId);

    private static Task<long> CountRefreshTokensOfAsync(NpgsqlConnection conn, string clientId) =>
        ScalarAsync<long>(conn, """
            SELECT count(*) FROM oauth_refresh_tokens t
            JOIN oauth_grants g ON g.id = t.grant_id
            JOIN oauth_clients c ON c.id = g.client_id
            WHERE c.client_id = @key;
            """, clientId);

    private static Task<int> RevokeGrantOfAsync(NpgsqlConnection conn, string clientId) =>
        ScalarAsync<int>(conn, """
            WITH revoked AS (
                UPDATE oauth_grants SET revoked_at = now()
                WHERE client_id IN (SELECT id FROM oauth_clients WHERE client_id = @key)
                RETURNING 1)
            SELECT count(*)::int FROM revoked;
            """, clientId);

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, string sql, object key)
    {
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        await using (var pin = conn.CreateCommand())
        {
            pin.CommandText = "SELECT set_config('app.current_tenant_id', @tenantId, false);";
            pin.Parameters.AddWithValue("tenantId", tenantId.ToString());
            await pin.ExecuteNonQueryAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("key", key);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}

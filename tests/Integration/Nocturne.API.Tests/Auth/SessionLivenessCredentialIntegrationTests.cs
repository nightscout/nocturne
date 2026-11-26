using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// A session caller mints a credential only while its session is live, and a caller whose session
/// has ended is answered as an anonymous caller is.
/// </summary>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public partial class SessionLivenessCredentialIntegrationTests : ApiIntegrationTestBase
{
    private static readonly object DirectGrantBody = new { label = "minted", scopes = new[] { "glucose.read" } };

    public SessionLivenessCredentialIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task A_session_ended_from_the_sessions_list_mints_no_direct_grant_and_is_answered_as_an_anonymous_caller_is()
    {
        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (subjectId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Ending Member");
        var ended = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);
        var other = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);
        using var endedClient = AuthTestHelpers.CreateBearerClient(Fixture, ended);
        using var otherClient = AuthTestHelpers.CreateBearerClient(Fixture, other);
        using var anonymousClient = Fixture.CreateHttpClient(ServiceNames.NocturneApi);
        var anonymous = await ShapeOfAsync(await anonymousClient.PostAsJsonAsync("/api/auth/direct-grants", DirectGrantBody));

        (await otherClient.DeleteAsync($"/api/v4/account/sessions/{SessionIdOf(ended)}")).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
        var grantsBefore = await CountDirectGrantsOfAsync(conn, subjectId);

        (await ShapeOfAsync(await endedClient.PostAsJsonAsync("/api/auth/direct-grants", DirectGrantBody)))
            .Should().Be(anonymous);
        (await CountDirectGrantsOfAsync(conn, subjectId)).Should().Be(grantsBefore);

        (await otherClient.PostAsJsonAsync("/api/auth/direct-grants", DirectGrantBody)).StatusCode
            .Should().Be(HttpStatusCode.OK, "the session that ended the other is still live");
    }

    [Fact]
    public async Task A_cookie_session_ended_from_the_sessions_list_mints_no_direct_grant_and_is_answered_as_an_anonymous_caller_is()
    {
        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (subjectId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Ending Cookie Member");
        var ended = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);
        var other = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);
        using var endedClient = CookieClient(ended);
        using var otherClient = CookieClient(other);
        using var anonymousClient = Fixture.CreateHttpClient(ServiceNames.NocturneApi);
        var anonymous = await ShapeOfAsync(await anonymousClient.PostAsJsonAsync("/api/auth/direct-grants", DirectGrantBody));

        (await otherClient.DeleteAsync($"/api/v4/account/sessions/{SessionIdOf(ended)}")).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
        var grantsBefore = await CountDirectGrantsOfAsync(conn, subjectId);

        (await ShapeOfAsync(await endedClient.PostAsJsonAsync("/api/auth/direct-grants", DirectGrantBody)))
            .Should().Be(anonymous);
        (await CountDirectGrantsOfAsync(conn, subjectId)).Should().Be(grantsBefore);

        var live = await otherClient.PostAsJsonAsync("/api/auth/direct-grants", DirectGrantBody);
        live.StatusCode.Should().Be(HttpStatusCode.OK, await live.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_session_ended_from_the_sessions_list_starts_no_passkey_enrolment_and_is_answered_as_an_anonymous_caller_is()
    {
        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (subjectId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Enrolling Member");
        var ended = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);
        var other = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);
        using var endedClient = AuthTestHelpers.CreateBearerClient(Fixture, ended);
        using var otherClient = AuthTestHelpers.CreateBearerClient(Fixture, other);
        using var anonymousClient = Fixture.CreateHttpClient(ServiceNames.NocturneApi);
        object body = new { username = "enrolling-member" };
        const string path = "/api/auth/passkey/register/options";
        var anonymous = await ShapeOfAsync(await anonymousClient.PostAsJsonAsync(path, body));

        (await otherClient.DeleteAsync($"/api/v4/account/sessions/{SessionIdOf(ended)}")).StatusCode
            .Should().Be(HttpStatusCode.NoContent);

        var refused = await endedClient.PostAsJsonAsync(path, body);
        (await ShapeOfAsync(refused)).Should().Be(anonymous);
        (await refused.Content.ReadAsStringAsync()).Should().NotContain("challengeToken");

        var live = await otherClient.PostAsJsonAsync(path, body);
        live.StatusCode.Should().Be(HttpStatusCode.OK, await live.Content.ReadAsStringAsync());
        (await live.Content.ReadAsStringAsync()).Should().Contain("challengeToken");
    }

    [Fact]
    public async Task A_session_ended_from_the_sessions_list_regenerates_no_recovery_codes()
    {
        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (subjectId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Recovering Member");
        var ended = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);
        var other = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);
        using var endedClient = AuthTestHelpers.CreateBearerClient(Fixture, ended);
        using var otherClient = AuthTestHelpers.CreateBearerClient(Fixture, other);
        const string path = "/api/auth/passkey/recovery/regenerate";
        var first = await otherClient.PostAsync(path, null);
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        var codesBefore = await UsableRecoveryCodesOfAsync(conn, subjectId);
        codesBefore.Should().NotBeEmpty();

        (await otherClient.DeleteAsync($"/api/v4/account/sessions/{SessionIdOf(ended)}")).StatusCode
            .Should().Be(HttpStatusCode.NoContent);

        var refused = await endedClient.PostAsync(path, null);
        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await refused.Content.ReadAsStringAsync()).Should().NotContain("codes");
        (await UsableRecoveryCodesOfAsync(conn, subjectId)).Should().Equal(codesBefore, "the codes the live session made still stand");

        var live = await otherClient.PostAsync(path, null);
        live.StatusCode.Should().Be(HttpStatusCode.OK, await live.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Signing_out_with_a_rotated_out_refresh_token_ends_its_whole_session_and_no_other()
    {
        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (subjectId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Signing Out Member");
        using var scope = Fixture.Services.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
        var signedOut = await sessions.IssueSessionAsync(subjectId, new SessionContext(DeviceDescription: "integration-test"));
        var successor = await sessions.RotateSessionAsync(signedOut.RefreshToken, new SessionContext());
        successor.Should().NotBeNull();
        var other = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);
        using var anonymousClient = Fixture.CreateHttpClient(ServiceNames.NocturneApi);
        var anonymous = await ShapeOfAsync(await anonymousClient.PostAsJsonAsync("/api/auth/direct-grants", DirectGrantBody));

        using var signingOut = Fixture.CreateHttpClient(ServiceNames.NocturneApi);
        signingOut.DefaultRequestHeaders.Add(
            "Cookie", $"{AccessCookie}={signedOut.AccessToken}; {RefreshCookie}={signedOut.RefreshToken}");
        var logout = await signingOut.PostAsync("/api/auth/oidc/logout", null);
        logout.StatusCode.Should().Be(HttpStatusCode.OK, await logout.Content.ReadAsStringAsync());

        (await IsLiveAsync(subjectId, SessionIdOf(signedOut.AccessToken))).Should().BeFalse();
        using var successorClient = AuthTestHelpers.CreateBearerClient(Fixture, successor!.AccessToken);
        (await ShapeOfAsync(await successorClient.PostAsJsonAsync("/api/auth/direct-grants", DirectGrantBody)))
            .Should().Be(anonymous, "the successor the rotation made belongs to the session that was signed out of");
        (await IsLiveAsync(subjectId, SessionIdOf(other))).Should().BeTrue();
        using var otherClient = AuthTestHelpers.CreateBearerClient(Fixture, other);
        (await otherClient.PostAsJsonAsync("/api/auth/direct-grants", DirectGrantBody)).StatusCode
            .Should().Be(HttpStatusCode.OK, "the subject's other session is not signed out");
    }

    [Fact]
    public async Task Signing_out_with_one_sessions_rotated_out_refresh_token_and_another_sessions_access_token_ends_both_and_no_third()
    {
        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (subjectId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Split Pair Member");
        using var scope = Fixture.Services.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
        var presented = await sessions.IssueSessionAsync(subjectId, new SessionContext(DeviceDescription: "integration-test"));
        var successor = await sessions.RotateSessionAsync(presented.RefreshToken, new SessionContext());
        successor.Should().NotBeNull();
        var signedIn = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);
        var third = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);
        SessionIdOf(signedIn).Should().NotBe(SessionIdOf(presented.AccessToken));

        using var signingOut = Fixture.CreateHttpClient(ServiceNames.NocturneApi);
        signingOut.DefaultRequestHeaders.Add(
            "Cookie", $"{AccessCookie}={signedIn}; {RefreshCookie}={presented.RefreshToken}");
        var logout = await signingOut.PostAsync("/api/auth/oidc/logout", null);
        logout.StatusCode.Should().Be(HttpStatusCode.OK, await logout.Content.ReadAsStringAsync());

        (await IsLiveAsync(subjectId, SessionIdOf(presented.AccessToken))).Should().BeFalse(
            "the rotated-out refresh token is found and its session ends, though the access token names another");
        (await IsLiveAsync(subjectId, SessionIdOf(signedIn))).Should().BeFalse();
        (await IsLiveAsync(subjectId, SessionIdOf(third))).Should().BeTrue();
        using var thirdClient = AuthTestHelpers.CreateBearerClient(Fixture, third);
        (await thirdClient.PostAsJsonAsync("/api/auth/direct-grants", DirectGrantBody)).StatusCode
            .Should().Be(HttpStatusCode.OK, "a session neither credential names is not signed out");
    }

    [Fact]
    public async Task Signing_out_with_no_refresh_token_ends_the_session_the_caller_is_signed_in_with_and_no_other()
    {
        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (subjectId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Bearer Signing Out");
        var signedOut = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);
        var other = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);
        using var anonymousClient = Fixture.CreateHttpClient(ServiceNames.NocturneApi);
        var anonymous = await ShapeOfAsync(await anonymousClient.PostAsJsonAsync("/api/auth/direct-grants", DirectGrantBody));
        using var signedOutClient = AuthTestHelpers.CreateBearerClient(Fixture, signedOut);

        var logout = await signedOutClient.PostAsync("/api/auth/oidc/logout", null);
        logout.StatusCode.Should().Be(HttpStatusCode.OK, await logout.Content.ReadAsStringAsync());

        (await IsLiveAsync(subjectId, SessionIdOf(signedOut))).Should().BeFalse();
        (await ShapeOfAsync(await signedOutClient.PostAsJsonAsync("/api/auth/direct-grants", DirectGrantBody)))
            .Should().Be(anonymous);
        (await IsLiveAsync(subjectId, SessionIdOf(other))).Should().BeTrue();
        using var otherClient = AuthTestHelpers.CreateBearerClient(Fixture, other);
        (await otherClient.PostAsJsonAsync("/api/auth/direct-grants", DirectGrantBody)).StatusCode
            .Should().Be(HttpStatusCode.OK, "the subject's other session is not signed out");
    }

    [Fact]
    public async Task A_platform_admin_session_ended_from_the_sessions_list_mints_no_tenant_direct_grant()
    {
        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (adminId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Ending Operator");
        var (memberId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Granted Member");
        await SetPlatformAdminAsync(conn, adminId, true);
        try
        {
            var ended = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, adminId);
            var other = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, adminId);
            var path = $"/api/v4/admin/tenants/{tenantId}/direct-grants";
            object body = new { subjectId = memberId, label = "minted", scopes = new[] { "glucose.read" } };
            using var anonymousClient = Fixture.CreateApexClient();
            var anonymous = await ShapeOfAsync(await anonymousClient.PostAsJsonAsync(path, body));

            using var otherClient = AuthTestHelpers.CreateBearerClient(Fixture, other);
            (await otherClient.DeleteAsync($"/api/v4/account/sessions/{SessionIdOf(ended)}")).StatusCode
                .Should().Be(HttpStatusCode.NoContent);
            var grantsBefore = await CountDirectGrantsOfAsync(conn, memberId);

            using var endedAdmin = Fixture.CreateApexClient();
            endedAdmin.DefaultRequestHeaders.Add("Authorization", $"Bearer {ended}");
            (await ShapeOfAsync(await endedAdmin.PostAsJsonAsync(path, body))).Should().Be(anonymous);
            (await CountDirectGrantsOfAsync(conn, memberId)).Should().Be(grantsBefore);

            using var otherAdmin = Fixture.CreateApexClient();
            otherAdmin.DefaultRequestHeaders.Add("Authorization", $"Bearer {other}");
            var live = await otherAdmin.PostAsJsonAsync(path, body);
            live.StatusCode.Should().Be(HttpStatusCode.OK, await live.Content.ReadAsStringAsync());
        }
        finally
        {
            await SetPlatformAdminAsync(conn, adminId, false);
        }
    }

    [Fact]
    public async Task A_session_refreshed_while_its_old_access_token_is_in_flight_is_still_live()
    {
        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (subjectId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Refreshing Member");
        using var scope = Fixture.Services.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
        var issued = await sessions.IssueSessionAsync(subjectId, new SessionContext(DeviceDescription: "integration-test"));

        var rotated = await sessions.RotateSessionAsync(issued.RefreshToken, new SessionContext());

        rotated.Should().NotBeNull();
        SessionIdOf(rotated!.AccessToken).Should().Be(SessionIdOf(issued.AccessToken));
        (await CountRefreshTokensAsync(conn, subjectId, revoked: true)).Should().Be(1, "the old token was rotated out");
        foreach (var token in new[] { issued.AccessToken, rotated.AccessToken })
        {
            using var client = AuthTestHelpers.CreateBearerClient(Fixture, token);
            var response = await client.PostAsJsonAsync("/api/auth/direct-grants", DirectGrantBody);
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Credentials_that_are_not_sessions_still_mint_once_the_subjects_session_has_ended()
    {
        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (subjectId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Delegating Member");
        var session = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);
        using var sessionClient = AuthTestHelpers.CreateBearerClient(Fixture, session);
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(sessionClient);
        var oauth = await AuthTestHelpers.ExecutePkceFlowAsync(sessionClient, clientId, scope: Scope.AlertsReadWrite);
        var minted = await sessionClient.PostAsJsonAsync(
            "/api/auth/direct-grants", new { label = "uploader", scopes = new[] { Scope.AlertsReadWrite } });
        minted.StatusCode.Should().Be(HttpStatusCode.OK, await minted.Content.ReadAsStringAsync());
        var grantToken = JsonSerializer.Deserialize<JsonElement>(await minted.Content.ReadAsStringAsync())
            .GetProperty("token").GetString()!;
        var ruleId = await AuthTestHelpers.SeedAlertRuleAsync(Fixture, tenantId);
        var channelId = await conn.ScalarInTenantAsync<Guid>("SELECT id FROM alert_rule_channels WHERE alert_rule_id = @key;", ruleId);
        object body = new { alertRuleChannelId = channelId };
        using var anonymousClient = Fixture.CreateHttpClient(ServiceNames.NocturneApi);
        var anonymous = await ShapeOfAsync(await anonymousClient.PostAsJsonAsync("/api/v4/alert-invites", body));

        (await sessionClient.DeleteAsync($"/api/v4/account/sessions/{SessionIdOf(session)}")).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
        (await ShapeOfAsync(await sessionClient.PostAsJsonAsync("/api/v4/alert-invites", body)))
            .Should().Be(anonymous, "the ended session itself mints nothing");

        var credentials = new (string Name, Action<HttpClient> Present)[]
        {
            ("oauth grant", c => c.DefaultRequestHeaders.Add("Authorization", $"Bearer {oauth.AccessToken}")),
            ("direct grant", c => c.DefaultRequestHeaders.Add("Authorization", $"Bearer {grantToken}")),
            ("api-secret", c => c.DefaultRequestHeaders.Add("api-secret", grantToken)),
            ("hashed api-secret", c => c.DefaultRequestHeaders.Add("api-secret", HashUtils.Sha1Hex(grantToken))),
        };
        foreach (var (name, present) in credentials)
        {
            using var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);
            present(client);

            var response = await client.PostAsJsonAsync("/api/v4/alert-invites", body);

            response.StatusCode.Should().Be(HttpStatusCode.Created, $"{name}: {await response.Content.ReadAsStringAsync()}");
        }
    }

    private const string AccessCookie = ".Nocturne.AccessToken";
    private const string RefreshCookie = ".Nocturne.RefreshToken";

    /// <summary>A client that presents <paramref name="accessToken"/> as the browser's session cookie.</summary>
    private HttpClient CookieClient(string accessToken)
    {
        var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);
        client.DefaultRequestHeaders.Add("Cookie", $"{AccessCookie}={accessToken}");
        return client;
    }

    private async Task<bool> IsLiveAsync(Guid subjectId, string sessionId)
    {
        using var scope = Fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IFirstPartyTokenRepository>()
            .HasLiveSessionAsync(subjectId, sessionId, DateTime.UtcNow);
    }

    /// <summary>The hashes of the subject's unspent, unvoided recovery codes, in a stable order.</summary>
    private static async Task<List<string>> UsableRecoveryCodesOfAsync(NpgsqlConnection conn, Guid subjectId)
    {
        return await conn.QueryAsync(
            "SELECT code_hash FROM recovery_codes WHERE subject_id = @key AND used_at IS NULL AND invalidated_at IS NULL ORDER BY code_hash;",
            reader => reader.GetString(0), ("key", subjectId));
    }

    /// <summary>The <c>sid</c> a session access token names.</summary>
    private static string SessionIdOf(string accessToken)
    {
        var payload = accessToken.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(Convert.FromBase64String(payload)))
            .GetProperty("sid").GetString()!;
    }

    private static async Task SetPlatformAdminAsync(NpgsqlConnection conn, Guid subjectId, bool isPlatformAdmin)
    {
        await conn.ExecuteAsync("UPDATE subjects SET is_platform_admin = @isPlatformAdmin WHERE id = @id;", ("isPlatformAdmin", isPlatformAdmin), ("id", subjectId));
    }

    /// <summary>The status, every header but <c>Date</c>, and the body, as one string to compare.</summary>
    private static async Task<string> ShapeOfAsync(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(h => h.Key != "Date")
            .OrderBy(h => h.Key, StringComparer.Ordinal)
            .Select(h => $"{h.Key}: {string.Join(", ", h.Value)}");
        var body = TraceId().Replace(await response.Content.ReadAsStringAsync(), "");
        return $"{(int)response.StatusCode}\n{string.Join("\n", headers)}\n\n{body}";
    }

    /// <summary>The per-request trace id a problem body carries, which no two responses share.</summary>
    [GeneratedRegex("\"traceId\":\"[^\"]*\"")]
    private static partial Regex TraceId();

    private static Task<long> CountRefreshTokensAsync(NpgsqlConnection conn, Guid subjectId, bool revoked) =>
        conn.ScalarInTenantAsync<long>(revoked
                ? "SELECT count(*) FROM refresh_tokens WHERE subject_id = @key AND revoked_at IS NOT NULL;"
                : "SELECT count(*) FROM refresh_tokens WHERE subject_id = @key AND revoked_at IS NULL;",
            subjectId);

    private static Task<long> CountDirectGrantsOfAsync(NpgsqlConnection conn, Guid subjectId) =>
        conn.ScalarInTenantAsync<long>("SELECT count(*) FROM oauth_grants WHERE subject_id = @key AND grant_type = 'direct';", subjectId);
}

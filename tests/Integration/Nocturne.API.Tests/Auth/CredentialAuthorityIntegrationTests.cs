using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Migrations;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// What a credential may do beyond reading and writing data: a member's keys stop with the
/// membership, a key cannot mint or widen keys, and only the person's own sign-in manages their
/// credentials and sign-in factors.
/// </summary>
[Collection(ApiIntegrationShards.Four)]
[Trait("Category", "Integration")]
public class CredentialAuthorityIntegrationTests : ApiIntegrationTestBase
{
    private Guid _tenantId;
    private Guid _ownerId;
    private Guid _memberId;
    private string _ownerSession = null!;
    private string _memberSession = null!;

    public CredentialAuthorityIntegrationTests(
        ApiIntegrationTestFixture fixture,
        ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        using var client = CreateAuthenticatedClient();
        (await client.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var conn = await OpenPostgresAsync();

        _tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        (_ownerId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, "Authority Owner");
        (_memberId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, _tenantId, "Authority Member");
        _ownerSession = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, _ownerId);
        _memberSession = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, _memberId);
    }

    [Fact]
    public async Task RemovedMember_ApiSecret_IsDenied()
    {
        var token = await MintAsync(_memberSession, "glucose.read");
        using var uploader = ApiSecretClient(token);
        (await uploader.GetAsync("/api/v1/entries/current")).StatusCode.Should().Be(HttpStatusCode.OK);

        using var owner = AuthTestHelpers.CreateBearerClient(Fixture, _ownerSession);
        (await owner.DeleteAsync($"/api/v4/member-invites/members/{_memberId}"))
            .IsSuccessStatusCode.Should().BeTrue();

        (await uploader.GetAsync("/api/v1/entries/current")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The membership row going is enough on its own, with the grant still live: the api-secret is
    /// held to membership exactly as the bearer form of the same token is.
    /// </summary>
    [Fact]
    public async Task ApiSecret_OfASubjectWithNoMembership_IsDenied()
    {
        var token = await MintAsync(_memberSession, "treatments.readwrite");

        await using (var conn = await OpenPostgresAsync())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM tenant_members WHERE subject_id = @s AND tenant_id = @t;";
            cmd.Parameters.AddWithValue("s", _memberId);
            cmd.Parameters.AddWithValue("t", _tenantId);
            await cmd.ExecuteNonQueryAsync();
        }

        using var uploader = ApiSecretClient(token);
        (await uploader.GetAsync("/api/v1/treatments")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await uploader.PostAsJsonAsync("/api/v1/treatments", new[]
        {
            new { eventType = "Note", notes = "after removal", created_at = DateTime.UtcNow.ToString("o") },
        })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("bearer")]
    [InlineData("api-secret")]
    public async Task NarrowKey_CannotManageCredentialsOrSignInFactors(string presentation)
    {
        var token = await MintAsync(_memberSession, "glucose.read");
        using var key = presentation == "bearer"
            ? AuthTestHelpers.CreateBearerClient(Fixture, token)
            : ApiSecretClient(token);

        (await key.PostAsJsonAsync("/api/auth/direct-grants", new { label = "wider", scopes = new[] { "*" } }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await key.GetAsync("/api/auth/direct-grants")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await key.GetAsync("/api/oauth/grants")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await key.PostAsync("/api/auth/passkey/recovery/regenerate", null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await key.PostAsJsonAsync("/api/auth/passkey/register/options", new { username = "second-device" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await key.DeleteAsync($"/api/auth/totp/{Guid.CreateVersion7()}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A grant with no scopes mints an exchange JWT with no scope claim. It must still read as the
    /// grant it came from, never as a session carrying the member's whole role.
    /// </summary>
    [Fact]
    public async Task ExchangeJwt_OfAGrantWithNoScopes_IsNotASession()
    {
        var (grantId, token) = await MintGrantAsync(_ownerSession, "glucose.read");
        using var owner = AuthTestHelpers.CreateBearerClient(Fixture, _ownerSession);

        (await owner.PatchAsJsonAsync($"/api/oauth/grants/{grantId}", new { scopes = Array.Empty<string>() }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // A row left with no scopes by an older release.
        await using (var conn = await OpenPostgresAsync())
        {
            await conn.PinTenantAsync(_tenantId);
            await conn.ExecuteAsync("UPDATE oauth_grants SET scopes = ARRAY[]::text[] WHERE id = @g;", ("g", grantId));
        }

        using var anonymous = Fixture.CreateHttpClient("nocturne-api", "http");
        var exchange = await anonymous.GetAsync($"/api/v2/authorization/request/{token}");
        var body = await exchange.Content.ReadAsStringAsync();
        exchange.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var jwt = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("token").GetString()!;

        using var exchanged = AuthTestHelpers.CreateBearerClient(Fixture, jwt);
        (await exchanged.PostAsJsonAsync("/api/auth/direct-grants", new { label = "wider", scopes = new[] { "*" } }))
            .StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        (await exchanged.PostAsync("/api/auth/passkey/recovery/regenerate", null))
            .StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        (await exchanged.GetAsync("/api/v1/entries/current")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A JWT exchanged from a Nightscout access token is that access token, not a sign-in: wherever
    /// only the person's own sign-in is admitted it is refused exactly as the access token is.
    /// </summary>
    [Fact]
    public async Task TokenExchangedFromANightscoutAccessToken_IsNotASignIn()
    {
        var token = await MintAsync(_ownerSession, "glucose.readwrite", "treatments.readwrite");

        using var anonymous = Fixture.CreateHttpClient("nocturne-api", "http");
        var exchange = await anonymous.GetAsync($"/api/v2/authorization/request/{token}");
        var body = await exchange.Content.ReadAsStringAsync();
        exchange.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var jwt = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("token").GetString()!;

        using var exchanged = AuthTestHelpers.CreateBearerClient(Fixture, jwt);
        using var key = AuthTestHelpers.CreateBearerClient(Fixture, token);

        (await exchanged.GetAsync("/api/v1/entries/current")).StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var caller in new[] { exchanged, key })
        {
            (await caller.PostAsJsonAsync("/api/auth/direct-grants", new { label = "another", scopes = new[] { "glucose.read" } }))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await caller.GetAsync("/api/auth/direct-grants")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await caller.GetAsync("/api/oauth/grants")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await caller.PostAsync("/api/auth/passkey/recovery/regenerate", null))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await caller.PostAsJsonAsync("/api/auth/passkey/register/options", new { username = "second-device" }))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await caller.PostAsync("/api/auth/totp/setup", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await caller.PostAsJsonAsync("/api/v4/chat-identity/links/claim", new { token = "any" }))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
    }

    public enum LoginCodeIssuer
    {
        InstanceKey,
        PlatformAdminOwner,
    }

    /// <summary>
    /// A login code is issued by someone other than the member, so the session it redeems for signs
    /// the member in but is not their own sign-in: it is refused wherever only that is admitted,
    /// with the code that tells the web to ask the member to sign in. The member's own sign-in then
    /// passes.
    /// </summary>
    [Theory]
    [InlineData(LoginCodeIssuer.InstanceKey)]
    [InlineData(LoginCodeIssuer.PlatformAdminOwner)]
    public async Task SessionRedeemedFromALoginCode_IsNotTheMembersOwnSignIn(LoginCodeIssuer issuer)
    {
        await using var conn = await OpenPostgresAsync();
        if (issuer == LoginCodeIssuer.PlatformAdminOwner)
            await MakePlatformAdminOwnerAsync(conn, _ownerId);

        try
        {
            using var issuing = issuer == LoginCodeIssuer.InstanceKey
                ? InstanceKeyClient()
                : AuthTestHelpers.CreateBearerClient(Fixture, _ownerSession);
            using var session = CookieClient(await HandOffAsync(issuing, _memberId));
            (await session.GetAsync("/api/v1/entries/current")).StatusCode.Should().Be(HttpStatusCode.OK,
                "the handed-off session signs the member in");

            await ExpectSignInRequiredAsync(session);

            using var signedIn = AuthTestHelpers.CreateBearerClient(
                Fixture, await AuthTestHelpers.IssueSessionTokenAsync(Fixture, _memberId));
            (await signedIn.GetAsync("/api/auth/direct-grants")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await signedIn.PostAsJsonAsync("/api/auth/direct-grants", new { label = "another", scopes = new[] { "*" } }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE subjects SET is_platform_admin = false WHERE id = @id;";
            cmd.Parameters.AddWithValue("id", _ownerId);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task SessionRedeemedFromALoginCode_KeepsItsMarkAcrossARefresh()
    {
        using var issuing = InstanceKeyClient();
        var handoff = await HandOffAsync(issuing, _memberId);

        using var refreshing = CookieClient(handoff);
        var refreshed = await refreshing.PostAsync("/api/auth/oidc/refresh", null);
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK, await refreshed.Content.ReadAsStringAsync());

        using var session = CookieClient(refreshed);
        (await session.GetAsync("/api/v1/entries/current")).StatusCode.Should().Be(HttpStatusCode.OK);
        await ExpectSignInRequiredAsync(session);
    }

    /// <summary>
    /// What a new owner opens first on the session the hosted checkout hands them: their data, the
    /// setup hub and the settings pages all read as usual, and the session says it was handed off so
    /// the web can offer the sign-in up front.
    /// </summary>
    [Fact]
    public async Task SessionRedeemedFromALoginCode_ReadsTheMembersDataAndSaysItWasHandedOff()
    {
        using var issuing = InstanceKeyClient();
        using var session = CookieClient(await HandOffAsync(issuing, _memberId));

        foreach (var path in new[]
        {
            "/api/v1/entries/current", "/api/v1/treatments", "/api/v4/setup-hub", "/api/v4/alert-rules",
            "/api/v4/notifications", "/api/v4/ui-settings", "/api/v4/tenant-settings", "/api/v4/profile/summary",
            "/api/v4/trackers/definitions",
        })
        {
            var response = await session.GetAsync(path);
            ((int)response.StatusCode).Should().BeLessThan(400, $"{path} answered {await response.Content.ReadAsStringAsync()}");
        }

        var info = JsonSerializer.Deserialize<JsonElement>(await session.GetStringAsync("/api/auth/oidc/session"));
        info.GetProperty("isAuthenticated").GetBoolean().Should().BeTrue();
        info.GetProperty("isHandedOff").GetBoolean().Should().BeTrue();

        using var signedIn = AuthTestHelpers.CreateBearerClient(Fixture, _memberSession);
        var own = JsonSerializer.Deserialize<JsonElement>(await signedIn.GetStringAsync("/api/auth/oidc/session"));
        own.GetProperty("isHandedOff").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task SessionRedeemedFromALoginCode_CarriesNoPlatformAdminStanding()
    {
        await using var conn = await OpenPostgresAsync();
        await using (var promote = conn.CreateCommand())
        {
            promote.CommandText = "UPDATE subjects SET is_platform_admin = true WHERE id = @id;";
            promote.Parameters.AddWithValue("id", _memberId);
            await promote.ExecuteNonQueryAsync();
        }

        try
        {
            using var issuing = InstanceKeyClient();
            using var session = CookieClient(await HandOffAsync(issuing, _memberId));
            (await session.GetAsync("/api/v4/admin/tenants")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

            using var signedIn = AuthTestHelpers.CreateBearerClient(
                Fixture, await AuthTestHelpers.IssueSessionTokenAsync(Fixture, _memberId));
            (await signedIn.GetAsync("/api/v4/admin/tenants")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await using var demote = conn.CreateCommand();
            demote.CommandText = "UPDATE subjects SET is_platform_admin = false WHERE id = @id;";
            demote.Parameters.AddWithValue("id", _memberId);
            await demote.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// The person confirms it is them by signing in over the handed-off session's cookies. That
    /// sign-in ends the handed-off chain, so its refresh token is refused, and the session it issues
    /// is their own.
    /// </summary>
    /// <remarks>
    /// Driven through the authenticator step, the one sign-in completion the harness can reach over
    /// HTTP. Its primary factor (a passkey assertion or a provider callback) is not: the step-up token
    /// that factor would mint is minted by <see cref="ITotpService"/> directly. The passkey and
    /// provider completions share the same revoke and are pinned at the controller.
    /// </remarks>
    [Fact]
    public async Task SigningInOverAHandedOffSession_EndsItAndIssuesTheirOwn()
    {
        using var member = AuthTestHelpers.CreateBearerClient(Fixture, _memberSession);
        var setup = JsonSerializer.Deserialize<JsonElement>(
            await (await member.PostAsync("/api/auth/totp/setup", null)).Content.ReadAsStringAsync());
        var secret = Base32Decode(setup.GetProperty("base32Secret").GetString()!);
        var enrolled = await member.PostAsJsonAsync("/api/auth/totp/verify-setup", new
        {
            code = TotpHelper.ComputeTotp(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            label = "phone",
            challengeToken = setup.GetProperty("challengeToken").GetString(),
        });
        enrolled.StatusCode.Should().Be(HttpStatusCode.OK, await enrolled.Content.ReadAsStringAsync());

        using var issuing = InstanceKeyClient();
        var handoff = await HandOffAsync(issuing, _memberId);
        using var handedOff = CookieClient(handoff);

        string stepUpToken;
        using (var scope = Fixture.Services.CreateScope())
        {
            stepUpToken = await scope.ServiceProvider.GetRequiredService<ITotpService>()
                .CreateStepUpTokenAsync(_memberId, _tenantId, admittedByInvite: false);
        }

        // The setup consumed the current step, so the sign-in uses the next one.
        var signIn = await handedOff.PostAsJsonAsync("/api/auth/totp/login", new
        {
            stepUpToken,
            code = TotpHelper.ComputeTotp(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30),
        });
        signIn.StatusCode.Should().Be(HttpStatusCode.OK, await signIn.Content.ReadAsStringAsync());

        (await handedOff.PostAsync("/api/auth/oidc/refresh", null)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "the sign-in ended the handed-off chain");

        using var own = CookieClient(signIn);
        var info = JsonSerializer.Deserialize<JsonElement>(await own.GetStringAsync("/api/auth/oidc/session"));
        info.GetProperty("isHandedOff").GetBoolean().Should().BeFalse();
        (await own.GetAsync("/api/auth/direct-grants")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A session handed off before redemptions recorded their login code carries no mark; the
    /// migration that added the mark revokes it. Refreshing it is refused like any revoked token,
    /// and since it has no successor, reuse detection revokes nothing further and writes nothing.
    /// </summary>
    [Fact]
    public async Task SessionHandedOffBeforeTheMark_IsEndedByTheMigration_AndARefreshIsAPlain401()
    {
        using var issuing = InstanceKeyClient();
        var handoff = await HandOffAsync(issuing, _memberId);
        using var handedOff = CookieClient(handoff);

        await using var conn = await OpenPostgresAsync();
        await conn.ExecuteAsync($"""
            UPDATE refresh_tokens SET login_code_id = NULL
             WHERE subject_id = '{_memberId}' AND device_description = 'Login handoff'
            """);
        (await conn.ExecuteAsync(AddLoginCodeIdToRefreshTokens.RevokeUnmarkedHandoffsSql)).Should().Be(1);
        var auditRowsBefore = await conn.ScalarAsync<long>($"SELECT count(*) FROM auth_audit_log WHERE target_subject_id = '{_memberId}' OR actor_subject_id = '{_memberId}'");

        var refused = await handedOff.PostAsync("/api/auth/oidc/refresh", null);

        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        JsonSerializer.Deserialize<JsonElement>(await refused.Content.ReadAsStringAsync())
            .GetProperty("error").GetString().Should().Be("invalid_refresh_token");
        (await conn.ScalarAsync<long>($"""
            SELECT count(*) FROM refresh_tokens
             WHERE subject_id = '{_memberId}' AND revoked_at IS NULL AND device_description = 'integration-test'
            """)).Should().BeGreaterThan(0, "the member's other session is not revoked as a reused family");
        (await conn.ScalarAsync<long>($"SELECT count(*) FROM refresh_tokens WHERE subject_id = '{_memberId}' AND revoked_reason = 'Token reuse detected'"))
            .Should().Be(0);
        (await conn.ScalarAsync<long>($"SELECT count(*) FROM auth_audit_log WHERE target_subject_id = '{_memberId}' OR actor_subject_id = '{_memberId}'"))
            .Should().Be(auditRowsBefore);
    }

    private static byte[] Base32Decode(string encoded)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in encoded.TrimEnd('='))
        {
            buffer = (buffer << 5) | alphabet.IndexOf(char.ToUpperInvariant(c));
            bits += 5;
            if (bits < 8) continue;
            bits -= 8;
            bytes.Add((byte)(buffer >> bits));
            buffer &= (1 << bits) - 1;
        }

        return bytes.ToArray();
    }

    private async Task<HttpResponseMessage> HandOffAsync(HttpClient issuing, Guid subjectId)
    {
        var issued = await issuing.PostAsync(
            $"/api/v4/admin/tenants/{_tenantId}/members/{subjectId}/login-code", null);
        var issuedBody = await issued.Content.ReadAsStringAsync();
        issued.StatusCode.Should().Be(HttpStatusCode.OK, issuedBody);
        var code = JsonSerializer.Deserialize<JsonElement>(issuedBody).GetProperty("code").GetString()!;

        using var browser = Fixture.CreateHttpClient("nocturne-api", "http");
        var handoff = await browser.PostAsJsonAsync("/api/auth/handoff", new { code });
        handoff.StatusCode.Should().Be(HttpStatusCode.OK, await handoff.Content.ReadAsStringAsync());
        return handoff;
    }

    private static async Task ExpectSignInRequiredAsync(HttpClient session)
    {
        var refusals = new[]
        {
            await session.PostAsJsonAsync("/api/auth/direct-grants", new { label = "another", scopes = new[] { "*" } }),
            await session.GetAsync("/api/auth/direct-grants"),
            await session.GetAsync("/api/oauth/grants"),
            await session.PostAsync("/api/auth/passkey/recovery/regenerate", null),
            await session.PostAsJsonAsync("/api/auth/passkey/register/options", new { username = "second-device" }),
            await session.PostAsync("/api/auth/totp/setup", null),
            await session.GetAsync("/api/v4/services/connections"),
        };

        foreach (var refused in refusals)
        {
            var body = await refused.Content.ReadAsStringAsync();
            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{refused.RequestMessage?.RequestUri} answered {body}");
            JsonSerializer.Deserialize<JsonElement>(body).GetProperty("code").GetString()
                .Should().Be("sign_in_required");
        }
    }

    [Fact]
    public async Task Session_ManagesItsOwnCredentialsAndSignInFactors()
    {
        using var session = AuthTestHelpers.CreateBearerClient(Fixture, _memberSession);

        (await session.PostAsync("/api/auth/passkey/recovery/regenerate", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await session.PostAsync("/api/auth/totp/setup", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await session.GetAsync("/api/oauth/grants")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await session.GetAsync("/api/auth/direct-grants")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task NightscoutSecret_SavedByAMember_KeepsAuthenticatingAfterTheyLeave()
    {
        const string secret = "integration-nightscout-secret";

        using var member = AuthTestHelpers.CreateBearerClient(Fixture, _memberSession);
        (await member.PutAsJsonAsync("/api/v4/connectors/config/nightscout/secrets", new { apiSecret = secret }))
            .IsSuccessStatusCode.Should().BeTrue();

        using var owner = AuthTestHelpers.CreateBearerClient(Fixture, _ownerSession);
        (await owner.DeleteAsync($"/api/v4/member-invites/members/{_memberId}"))
            .IsSuccessStatusCode.Should().BeTrue();

        using var uploader = ApiSecretClient(Sha1(secret));
        (await uploader.GetAsync("/api/v1/entries/current")).StatusCode.Should().Be(HttpStatusCode.OK,
            "the site's secret belongs to the site, not to whoever saved it");
    }

    [Fact]
    public async Task NightscoutSecret_SavedByACaretaker_IsKeptForTheConnectorAndMakesNoCredential()
    {
        const string secret = "integration-caretaker-nightscout-secret";
        Guid caretakerId;
        await using (var conn = await OpenPostgresAsync())
        {
            (caretakerId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(
                conn, _tenantId, "Authority Caretaker", RoleSeeds.Permissions[RoleSeeds.Caretaker]);
        }

        using var caretaker = AuthTestHelpers.CreateBearerClient(Fixture, await AuthTestHelpers.IssueSessionTokenAsync(Fixture, caretakerId));
        var saved = await caretaker.PutAsJsonAsync("/api/v4/connectors/config/nightscout/secrets", new { apiSecret = secret });
        var body = await saved.Content.ReadAsStringAsync();
        saved.StatusCode.Should().Be(HttpStatusCode.OK, body);
        JsonSerializer.Deserialize<JsonElement>(body).GetProperty("notAdopted").GetBoolean().Should().BeTrue();

        using var uploader = ApiSecretClient(Sha1(secret));
        (await uploader.GetAsync("/api/v1/entries/current")).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a caretaker cannot make a credential wider than their own access");
    }

    private async Task<string> MintAsync(string session, params string[] scopes) =>
        (await MintGrantAsync(session, scopes)).Token;

    private async Task<(Guid Id, string Token)> MintGrantAsync(string session, params string[] scopes)
    {
        using var client = AuthTestHelpers.CreateBearerClient(Fixture, session);
        var response = await client.PostAsJsonAsync("/api/auth/direct-grants", new { label = "key", scopes });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var created = JsonSerializer.Deserialize<JsonElement>(body);
        return (created.GetProperty("id").GetGuid(), created.GetProperty("token").GetString()!);
    }

    private HttpClient ApiSecretClient(string secret)
    {
        var client = Fixture.CreateHttpClient("nocturne-api", "http");
        client.DefaultRequestHeaders.Add("api-secret", secret);
        return client;
    }

    private HttpClient InstanceKeyClient()
    {
        var client = Fixture.CreateHttpClient("nocturne-api", "http");
        client.DefaultRequestHeaders.Add("X-Instance-Key", HashUtils.Sha256Hex(ApiIntegrationTestFixture.InstanceKey));
        client.DefaultRequestHeaders.Add("X-Instance-Service", "nocturne-billing");
        return client;
    }

    /// <summary>A client that presents the cookies <paramref name="response"/> set, as a browser would.</summary>
    private HttpClient CookieClient(HttpResponseMessage response)
    {
        var cookies = response.Headers.GetValues("Set-Cookie")
            .Select(c => c.Split(';', 2)[0])
            .Where(pair => !pair.EndsWith('='));
        var client = Fixture.CreateHttpClient("nocturne-api", "http");
        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies));
        return client;
    }

    private async Task MakePlatformAdminOwnerAsync(NpgsqlConnection conn, Guid subjectId)
    {
        var ownerRole = await AuthTestHelpers.GetTenantRoleIdAsync(conn, _tenantId, "owner");
        await conn.ExecuteAsync("""
            UPDATE subjects SET is_platform_admin = true WHERE id = @subjectId;
            INSERT INTO tenant_member_roles (id, tenant_member_id, tenant_role_id)
            SELECT gen_random_uuid(), id, @roleId FROM tenant_members
             WHERE tenant_id = @tenantId AND subject_id = @subjectId;
            """, ("subjectId", subjectId), ("tenantId", _tenantId), ("roleId", ownerRole));
    }

    private static string Sha1(string value) =>
        Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(value)));
}

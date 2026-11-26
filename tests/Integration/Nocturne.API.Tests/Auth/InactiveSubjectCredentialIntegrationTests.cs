using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Tests.Shared.Infrastructure;
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
public partial class InactiveSubjectCredentialIntegrationTests : ApiIntegrationTestBase
{
    public InactiveSubjectCredentialIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task A_deactivated_subjects_refresh_is_answered_as_a_revoked_grant_is()
    {
        await using var conn = await OpenPostgresAsync();
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
        await using var conn = await OpenPostgresAsync();
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
        await using var conn = await OpenPostgresAsync();
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
        await using var conn = await OpenPostgresAsync();
        var (subjectId, client) = await SignInAsync(conn, "Approving Member");
        using var _ = client;
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(client);
        var deviceCode = await ApprovedDeviceCodeAsync(client, clientId);
        var invalidCodeAnswer = await ShapeOfAsync(
            await ExchangeDeviceCodeAsync(client, clientId, $"unknown-{Guid.NewGuid():N}"));

        await SetActiveAsync(conn, subjectId, false);
        try
        {
            var response = await ExchangeDeviceCodeAsync(client, clientId, deviceCode);

            (await ShapeOfAsync(response)).Should().Be(invalidCodeAnswer);
            (await CountRefreshTokensOfAsync(conn, clientId)).Should().Be(0);
        }
        finally
        {
            await SetActiveAsync(conn, subjectId, true);
        }
    }

    [Fact]
    public async Task A_refused_device_code_is_answered_as_an_unknown_one_on_every_later_poll()
    {
        await using var conn = await OpenPostgresAsync();
        var (subjectId, client) = await SignInAsync(conn, "Polling Member");
        using var _ = client;
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(client);
        var deviceCode = await ApprovedDeviceCodeAsync(client, clientId);
        var unknownCode = $"unknown-{Guid.NewGuid():N}";
        await ExchangeDeviceCodeAsync(client, clientId, unknownCode);
        var unknownCodeAnswer = await ShapeOfAsync(await ExchangeDeviceCodeAsync(client, clientId, unknownCode));

        await SetActiveAsync(conn, subjectId, false);
        try
        {
            (await ShapeOfAsync(await ExchangeDeviceCodeAsync(client, clientId, deviceCode)))
                .Should().Be(unknownCodeAnswer);

            (await ShapeOfAsync(await ExchangeDeviceCodeAsync(client, clientId, deviceCode)))
                .Should().Be(unknownCodeAnswer, "a poll inside the interval reads as one on an unknown code");

            await RewindDevicePollsOfAsync(conn, clientId);
            (await ShapeOfAsync(await ExchangeDeviceCodeAsync(client, clientId, deviceCode)))
                .Should().Be(unknownCodeAnswer, "a poll after the interval reads as one on an unknown code");
        }
        finally
        {
            await SetActiveAsync(conn, subjectId, true);
        }

        await RewindDevicePollsOfAsync(conn, clientId);
        (await ShapeOfAsync(await ExchangeDeviceCodeAsync(client, clientId, deviceCode)))
            .Should().Be(unknownCodeAnswer, "the refusal spent the code, and reactivation does not revive it");
        (await CountRefreshTokensOfAsync(conn, clientId)).Should().Be(0);

        var renewed = await ExchangeDeviceCodeAsync(client, clientId, await ApprovedDeviceCodeAsync(client, clientId));
        renewed.StatusCode.Should().Be(HttpStatusCode.OK, await renewed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_login_code_for_a_deactivated_member_is_refused_like_a_spent_code_and_issues_no_session()
    {
        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (memberId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Handed Off Member");
        var code = $"login-{Guid.NewGuid():N}";
        await conn.ExecuteAsync(
            """
            INSERT INTO login_codes (id, tenant_id, subject_id, code_hash, purpose, expires_at, created_at)
            VALUES (@id, @tenantId, @subjectId, @hash, 'Login', now() + interval '5 minutes', now());
            """,
            ("id", Guid.CreateVersion7()), ("tenantId", tenantId), ("subjectId", memberId), ("hash", HashUtils.Sha256Hex(code)));

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

    [Fact]
    public async Task A_deactivated_subjects_direct_grant_is_refused_on_every_transport_as_a_revoked_one_is()
    {
        await using var conn = await OpenPostgresAsync();
        var (subjectId, session) = await SignInWithSessionOnlyAsync(conn, "Uploading Member");
        using var _ = session;
        var live = await MintDirectGrantAsync(session, "live");
        var revoked = await MintDirectGrantAsync(session, "revoked");
        (await session.DeleteAsync($"/api/auth/direct-grants/{revoked.Id}")).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
        var transports = new (string Name, Func<string, HttpRequestMessage> Request)[]
        {
            ("bearer", token => Get(r => r.Headers.Add("Authorization", $"Bearer {token}"))),
            ("query", token => Get(token: token)),
            ("api-secret", token => Get(r => r.Headers.Add("api-secret", token))),
            ("hashed api-secret", token => Get(r => r.Headers.Add("api-secret", HashUtils.Sha1Hex(token)))),
        };
        var revokedAnswers = new Dictionary<string, string>();
        foreach (var (name, request) in transports)
        {
            revokedAnswers[name] = await ShapeOfAsync(await Fixture.CreateHttpClient(ServiceNames.NocturneApi)
                .SendAsync(request(revoked.Token)));
        }

        await SetActiveAsync(conn, subjectId, false);
        try
        {
            foreach (var (name, request) in transports)
            {
                var response = await Fixture.CreateHttpClient(ServiceNames.NocturneApi).SendAsync(request(live.Token));

                (await ShapeOfAsync(response)).Should().Be(revokedAnswers[name], name);
            }
        }
        finally
        {
            await SetActiveAsync(conn, subjectId, true);
        }

        foreach (var (name, request) in transports)
        {
            var reactivated = await Fixture.CreateHttpClient(ServiceNames.NocturneApi).SendAsync(request(live.Token));
            reactivated.StatusCode.Should().Be(HttpStatusCode.OK, name);
        }
    }

    [Fact]
    public async Task A_deactivated_subject_mints_no_direct_grant_and_is_answered_as_an_anonymous_caller_is()
    {
        await using var conn = await OpenPostgresAsync();
        var (subjectId, session) = await SignInWithSessionOnlyAsync(conn, "Minting Member");
        using var _ = session;
        object body = new { label = "after", scopes = new[] { "glucose.read" } };
        var anonymous = await ShapeOfAsync(await AnonymousClient().PostAsJsonAsync("/api/auth/direct-grants", body));
        var grantsBefore = await CountDirectGrantsOfAsync(conn, subjectId);

        await SetActiveAsync(conn, subjectId, false);
        try
        {
            var response = await session.PostAsJsonAsync("/api/auth/direct-grants", body);

            (await ShapeOfAsync(response)).Should().Be(anonymous);
            (await CountDirectGrantsOfAsync(conn, subjectId)).Should().Be(grantsBefore);
        }
        finally
        {
            await SetActiveAsync(conn, subjectId, true);
        }

        (await session.PostAsJsonAsync("/api/auth/direct-grants", body)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_deactivated_subject_mints_no_guest_link_and_is_answered_as_an_anonymous_caller_is()
    {
        await using var conn = await OpenPostgresAsync();
        var (subjectId, session) = await SignInWithSessionOnlyAsync(conn, "Sharing Member");
        using var _ = session;
        object body = new { label = "after", scopes = new[] { "glucose.read" } };
        var anonymous = await ShapeOfAsync(await AnonymousClient().PostAsJsonAsync("/api/v4/guest-links", body));
        var linksBefore = await CountGuestLinksOfAsync(conn, subjectId);

        await SetActiveAsync(conn, subjectId, false);
        try
        {
            var response = await session.PostAsJsonAsync("/api/v4/guest-links", body);

            (await ShapeOfAsync(response)).Should().Be(anonymous);
            (await CountGuestLinksOfAsync(conn, subjectId)).Should().Be(linksBefore);
        }
        finally
        {
            await SetActiveAsync(conn, subjectId, true);
        }

        (await session.PostAsJsonAsync("/api/v4/guest-links", body)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_deactivated_subject_approves_no_device_and_is_answered_as_an_anonymous_caller_is()
    {
        await using var conn = await OpenPostgresAsync();
        var (subjectId, session) = await SignInWithSessionOnlyAsync(conn, "Approving Again Member");
        using var _ = session;
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(session);
        var authorization = await session.PostAsync("/api/oauth/device", Form(("client_id", clientId), ("scope", "glucose.read")));
        authorization.EnsureSuccessStatusCode();
        var device = JsonSerializer.Deserialize<JsonElement>(await authorization.Content.ReadAsStringAsync());
        Func<FormUrlEncodedContent> approval = () => Form(
            ("user_code", device.GetProperty("user_code").GetString()!), ("approved", "true"));
        var anonymous = await ShapeOfAsync(await AnonymousClient().PostAsync("/api/oauth/device-approve", approval()));

        await SetActiveAsync(conn, subjectId, false);
        try
        {
            var response = await session.PostAsync("/api/oauth/device-approve", approval());

            (await ShapeOfAsync(response)).Should().Be(anonymous);
            (await CountGrantsOfAsync(conn, clientId)).Should().Be(0);
        }
        finally
        {
            await SetActiveAsync(conn, subjectId, true);
        }

        (await session.PostAsync("/api/oauth/device-approve", approval())).StatusCode
            .Should().Be(HttpStatusCode.OK, "the refused approval left the code pending");
    }

    [Fact]
    public async Task A_deactivated_subject_is_issued_no_authorization_code_and_is_answered_as_an_anonymous_caller_is()
    {
        await using var conn = await OpenPostgresAsync();
        var (subjectId, session) = await SignInWithSessionOnlyAsync(conn, "Consenting Again Member");
        using var _ = session;
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(session);
        Func<FormUrlEncodedContent> consent = () => Form(
            ("client_id", clientId),
            ("redirect_uri", "http://localhost:9999/callback"),
            ("scope", "glucose.read"),
            ("state", "s"),
            ("code_challenge", "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"),
            ("approved", "true"));
        using var anonymousClient = Fixture.CreateHttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var anonymous = await ShapeOfAsync(await anonymousClient.PostAsync("/api/oauth/authorize", consent()));
        using var consenting = Fixture.CreateHttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        consenting.DefaultRequestHeaders.Authorization = session.DefaultRequestHeaders.Authorization;
        var codesBefore = await CountAuthorizationCodesOfAsync(conn, clientId);

        await SetActiveAsync(conn, subjectId, false);
        try
        {
            var response = await consenting.PostAsync("/api/oauth/authorize", consent());

            (await ShapeOfAsync(response)).Should().Be(anonymous);
            (await CountAuthorizationCodesOfAsync(conn, clientId)).Should().Be(codesBefore);
        }
        finally
        {
            await SetActiveAsync(conn, subjectId, true);
        }

        (await consenting.PostAsync("/api/oauth/authorize", consent())).StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await CountAuthorizationCodesOfAsync(conn, clientId)).Should().Be(codesBefore + 1);
    }

    [Fact]
    public async Task A_deactivated_subject_asking_to_authorize_is_refused_to_the_client_and_not_sent_to_sign_in()
    {
        await using var conn = await OpenPostgresAsync();
        var (subjectId, session) = await SignInWithSessionOnlyAsync(conn, "Authorizing Member");
        using var _ = session;
        var clientId = await AuthTestHelpers.RegisterOAuthClientAsync(session);
        var authorize = "/api/oauth/authorize?response_type=code&scope=glucose.read&state=s"
            + "&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256"
            + $"&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString("http://localhost:9999/callback")}";
        using var anonymousClient = Fixture.CreateHttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        (await anonymousClient.GetAsync(authorize)).Headers.Location!.OriginalString.Should().StartWith("/auth/login");
        using var authorizing = Fixture.CreateHttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        authorizing.DefaultRequestHeaders.Authorization = session.DefaultRequestHeaders.Authorization;
        var codesBefore = await CountAuthorizationCodesOfAsync(conn, clientId);

        await SetActiveAsync(conn, subjectId, false);
        try
        {
            var response = await authorizing.GetAsync(authorize);

            response.StatusCode.Should().Be(HttpStatusCode.Redirect);
            response.Headers.Location!.OriginalString.Should().NotStartWith("/auth/login")
                .And.StartWith("http://localhost:9999/callback?error=access_denied&")
                .And.EndWith("&state=s");
            (await CountAuthorizationCodesOfAsync(conn, clientId)).Should().Be(codesBefore);
        }
        finally
        {
            await SetActiveAsync(conn, subjectId, true);
        }

        var reactivated = await authorizing.GetAsync(authorize);
        reactivated.StatusCode.Should().Be(HttpStatusCode.Redirect);
        reactivated.Headers.Location!.OriginalString.Should().NotStartWith("/auth/login").And.NotContain("error=");
    }

    [Fact]
    public async Task A_deactivated_subject_creates_no_v2_subject_and_is_answered_as_an_anonymous_caller_is()
    {
        await using var conn = await OpenPostgresAsync();
        var (subjectId, session) = await SignInWithSessionOnlyAsync(conn, "V2 Creating Member");
        using var _ = session;

        await AssertMintRefusedAsync(
            conn,
            subjectId,
            session,
            client => client.PostAsJsonAsync(
                "/api/v2/authorization/subjects", new { name = "uploader", roles = new[] { "readable" } }),
            HttpStatusCode.Created,
            () => CountGrantsMadeByAsync(conn, subjectId));
    }

    [Fact]
    public async Task A_deactivated_subject_widens_no_v2_subject_and_is_answered_as_an_anonymous_caller_is()
    {
        await using var conn = await OpenPostgresAsync();
        var (subjectId, session) = await SignInWithSessionOnlyAsync(conn, "V2 Updating Member");
        using var _ = session;
        var created = await session.PostAsJsonAsync(
            "/api/v2/authorization/subjects", new { name = "uploader", roles = new[] { "readable" } });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var grantId = await conn.ScalarInTenantAsync<Guid>("SELECT id FROM oauth_grants WHERE created_by_subject_id = @key;", subjectId);
        var id = grantId.ToString();

        await AssertMintRefusedAsync(
            conn,
            subjectId,
            session,
            client => client.PutAsJsonAsync(
                "/api/v2/authorization/subjects", new { id, name = "uploader", roles = new[] { "admin" } }),
            HttpStatusCode.OK,
            () => ScopesOfGrantAsync(conn, grantId));
    }

    [Fact]
    public async Task A_deactivated_subject_adopts_no_nightscout_secret_and_is_answered_as_an_anonymous_caller_is()
    {
        await using var conn = await OpenPostgresAsync();
        var (subjectId, session) = await SignInWithSessionOnlyAsync(conn, "Adopting Member");
        using var _ = session;
        var secret = $"inactive-adoption-{Guid.NewGuid():N}";

        await AssertMintRefusedAsync(
            conn,
            subjectId,
            session,
            client => client.PutAsJsonAsync("/api/v4/connectors/config/nightscout/secrets", new { apiSecret = secret }),
            HttpStatusCode.OK,
            () => CountGrantsMadeByAsync(conn, subjectId));
    }

    [Fact]
    public async Task A_deactivated_subject_creates_no_member_invite_and_is_answered_as_an_anonymous_caller_is()
    {
        await using var conn = await OpenPostgresAsync();
        var (subjectId, session) = await SignInWithSessionOnlyAsync(conn, "Inviting Member");
        using var _ = session;

        await AssertMintRefusedAsync(
            conn,
            subjectId,
            session,
            client => client.PostAsJsonAsync("/api/v4/member-invites", new
            {
                roleIds = Array.Empty<Guid>(),
                directPermissions = new[] { "glucose.read" },
                label = "after",
            }),
            HttpStatusCode.Created,
            () => CountMemberInvitesOfAsync(conn, subjectId));
    }

    [Fact]
    public async Task A_deactivated_subject_rotates_no_share_link_and_is_answered_as_an_anonymous_caller_is()
    {
        await using var conn = await OpenPostgresAsync();
        var (subjectId, session) = await SignInWithSessionOnlyAsync(conn, "Rotating Member");
        using var _ = session;

        await AssertMintRefusedAsync(
            conn,
            subjectId,
            session,
            client => client.PostAsync("/api/v4/share/rotate", null),
            HttpStatusCode.OK,
            () => ShareTokenAsync(conn));
    }

    [Fact]
    public async Task A_deactivated_subject_creates_no_alert_invite_and_is_answered_as_an_anonymous_caller_is()
    {
        await using var conn = await OpenPostgresAsync();
        var (subjectId, session) = await SignInWithSessionOnlyAsync(conn, "Alert Inviting Member");
        using var _ = session;

        // No channel is seeded, so the reactivated caller reaches the action's own channel check.
        await AssertMintRefusedAsync(
            conn,
            subjectId,
            session,
            client => client.PostAsJsonAsync("/api/v4/alert-invites", new { alertRuleChannelId = Guid.NewGuid() }),
            HttpStatusCode.BadRequest,
            () => CountAlertInvitesOfAsync(conn, subjectId));
    }

    [Fact]
    public async Task A_deactivated_subject_is_issued_no_desktop_link_token_and_is_answered_as_an_anonymous_caller_is()
    {
        await using var conn = await OpenPostgresAsync();
        var (subjectId, session) = await SignInWithSessionOnlyAsync(conn, "Desktop Linking Member");
        using var _ = session;
        var anonymous = await ShapeOfAsync(await AnonymousClient().PostAsync(DesktopTokenPath, null));

        await SetActiveAsync(conn, subjectId, false);
        try
        {
            (await ShapeOfAsync(await session.PostAsync(DesktopTokenPath, null))).Should().Be(anonymous);
        }
        finally
        {
            await SetActiveAsync(conn, subjectId, true);
        }

        (await DesktopLinkTokenAsync(session)).Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Deactivated, the caller is answered as an anonymous caller is and changes nothing
    /// <paramref name="state"/> reads; reactivated, the action itself answers <paramref name="activeStatus"/>.
    /// </summary>
    private async Task AssertMintRefusedAsync<T>(
        NpgsqlConnection conn,
        Guid subjectId,
        HttpClient session,
        Func<HttpClient, Task<HttpResponseMessage>> send,
        HttpStatusCode activeStatus,
        Func<Task<T>> state)
    {
        var anonymous = await ShapeOfAsync(await send(AnonymousClient()));
        var before = await state();

        await SetActiveAsync(conn, subjectId, false);
        try
        {
            (await ShapeOfAsync(await send(session))).Should().Be(anonymous);
            (await state()).Should().Be(before);
        }
        finally
        {
            await SetActiveAsync(conn, subjectId, true);
        }

        var reactivated = await send(session);
        reactivated.StatusCode.Should().Be(activeStatus, await reactivated.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_deactivated_platform_admins_session_holds_no_platform_admin_standing_on_any_host()
    {
        await using var conn = await OpenPostgresAsync();
        var (subjectId, _) = await SignInWithSessionOnlyAsync(conn, "Operating Member");
        var (memberId, _) = await SignInWithSessionOnlyAsync(conn, "Ordinary Member");
        await SetPlatformAdminAsync(conn, subjectId, true);
        try
        {
            var token = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);
            var memberToken = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, memberId);
            var hosts = new (string Name, Func<HttpClient> Create)[]
            {
                ("tenant", () => Fixture.CreateHttpClient(ServiceNames.NocturneApi)),
                ("apex", () => Fixture.CreateApexClient()),
            };
            var memberAnswers = new Dictionary<string, string>();
            foreach (var (name, create) in hosts)
            {
                using var member = create();
                member.DefaultRequestHeaders.Add("Authorization", $"Bearer {memberToken}");
                memberAnswers[name] = await ShapeOfAsync(await member.GetAsync("/api/v4/admin/tenants"));

                using var admin = create();
                admin.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
                (await admin.GetAsync("/api/v4/admin/tenants")).StatusCode.Should().Be(HttpStatusCode.OK, name);
            }

            await SetActiveAsync(conn, subjectId, false);
            try
            {
                foreach (var (name, create) in hosts)
                {
                    using var admin = create();
                    admin.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

                    (await ShapeOfAsync(await admin.GetAsync("/api/v4/admin/tenants")))
                        .Should().Be(memberAnswers[name], name);
                }
            }
            finally
            {
                await SetActiveAsync(conn, subjectId, true);
            }
        }
        finally
        {
            await SetPlatformAdminAsync(conn, subjectId, false);
        }
    }

    private async Task<(Guid SubjectId, HttpClient Client)> SignInAsync(NpgsqlConnection conn, string name)
    {
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (subjectId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, name);
        var session = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);
        return (subjectId, AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, session));
    }

    private HttpClient AnonymousClient() => Fixture.CreateHttpClient(ServiceNames.NocturneApi);

    private static HttpRequestMessage Get(Action<HttpRequestMessage>? configure = null, string? token = null)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            token is null ? "/api/v1/entries/current" : $"/api/v1/entries/current?token={Uri.EscapeDataString(token)}");
        configure?.Invoke(request);
        return request;
    }

    private static async Task<(Guid Id, string Token)> MintDirectGrantAsync(HttpClient session, string label)
    {
        var response = await session.PostAsJsonAsync(
            "/api/auth/direct-grants", new { label, scopes = new[] { "glucose.read" } });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return (body.GetProperty("id").GetGuid(), body.GetProperty("token").GetString()!);
    }

    private static async Task SetPlatformAdminAsync(NpgsqlConnection conn, Guid subjectId, bool isPlatformAdmin)
    {
        await conn.ExecuteAsync("UPDATE subjects SET is_platform_admin = @isPlatformAdmin WHERE id = @id;", ("isPlatformAdmin", isPlatformAdmin), ("id", subjectId));
    }

    private async Task<(Guid SubjectId, HttpClient Client)> SignInWithSessionOnlyAsync(NpgsqlConnection conn, string name)
    {
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (subjectId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, name);
        var session = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);
        return (subjectId, AuthTestHelpers.CreateBearerClient(Fixture, session));
    }

    private static async Task SetActiveAsync(NpgsqlConnection conn, Guid subjectId, bool isActive)
    {
        await conn.ExecuteAsync("UPDATE subjects SET is_active = @isActive WHERE id = @id;", ("isActive", isActive), ("id", subjectId));
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

    private static async Task<string> ApprovedDeviceCodeAsync(HttpClient client, string clientId)
    {
        var authorization = await client.PostAsync("/api/oauth/device", Form(
            ("client_id", clientId),
            ("scope", "glucose.read")));
        authorization.EnsureSuccessStatusCode();
        var device = JsonSerializer.Deserialize<JsonElement>(await authorization.Content.ReadAsStringAsync());
        var approval = await client.PostAsync("/api/oauth/device-approve", Form(
            ("user_code", device.GetProperty("user_code").GetString()!),
            ("approved", "true")));
        approval.EnsureSuccessStatusCode();
        return device.GetProperty("device_code").GetString()!;
    }

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
        var body = TraceId().Replace(await response.Content.ReadAsStringAsync(), "");
        return $"{(int)response.StatusCode}\n{string.Join("\n", headers)}\n\n{body}";
    }

    /// <summary>The per-request trace id a problem body carries, which no two responses share.</summary>
    [GeneratedRegex("\"traceId\":\"[^\"]*\"")]
    private static partial Regex TraceId();

    private static Task<long> CountGrantsOfAsync(NpgsqlConnection conn, string clientId) =>
        conn.ScalarInTenantAsync<long>("""
            SELECT count(*) FROM oauth_grants g
            JOIN oauth_clients c ON c.id = g.client_id
            WHERE c.client_id = @key;
            """, clientId);

    private static Task<long> CountRefreshTokensOfAsync(NpgsqlConnection conn, string clientId) =>
        conn.ScalarInTenantAsync<long>("""
            SELECT count(*) FROM oauth_refresh_tokens t
            JOIN oauth_grants g ON g.id = t.grant_id
            JOIN oauth_clients c ON c.id = g.client_id
            WHERE c.client_id = @key;
            """, clientId);

    private static Task<long> CountDirectGrantsOfAsync(NpgsqlConnection conn, Guid subjectId) =>
        conn.ScalarInTenantAsync<long>("SELECT count(*) FROM oauth_grants WHERE subject_id = @key AND grant_type = 'direct';", subjectId);

    private static Task<long> CountGuestLinksOfAsync(NpgsqlConnection conn, Guid subjectId) =>
        conn.ScalarInTenantAsync<long>("SELECT count(*) FROM oauth_grants WHERE subject_id = @key AND grant_type = 'guest';", subjectId);

    private static Task<long> CountAuthorizationCodesOfAsync(NpgsqlConnection conn, string clientId) =>
        conn.ScalarInTenantAsync<long>("""
            SELECT count(*) FROM oauth_authorization_codes a
            JOIN oauth_clients c ON c.id = a.client_entity_id
            WHERE c.client_id = @key;
            """, clientId);

    private const string DesktopTokenPath = "/api/v4/connectors/carelink/connect/desktop-token";

    /// <summary>The bearer token a desktop link code carries.</summary>
    private static async Task<string?> DesktopLinkTokenAsync(HttpClient session)
    {
        var response = await session.PostAsync(DesktopTokenPath, null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var linkCode = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("linkCode").GetString()!;
        return System.Web.HttpUtility.ParseQueryString(new Uri(linkCode).Query)["token"];
    }

    private static Task<long> CountGrantsMadeByAsync(NpgsqlConnection conn, Guid subjectId) =>
        conn.ScalarInTenantAsync<long>("SELECT count(*) FROM oauth_grants WHERE created_by_subject_id = @key;", subjectId);

    private static Task<string> ScopesOfGrantAsync(NpgsqlConnection conn, Guid grantId) =>
        conn.ScalarInTenantAsync<string>("SELECT array_to_string(scopes, ' ') FROM oauth_grants WHERE id = @key;", grantId);

    private static Task<long> CountMemberInvitesOfAsync(NpgsqlConnection conn, Guid subjectId) =>
        conn.ScalarInTenantAsync<long>("SELECT count(*) FROM member_invites WHERE created_by_subject_id = @key;", subjectId);

    private static Task<long> CountAlertInvitesOfAsync(NpgsqlConnection conn, Guid subjectId) =>
        conn.ScalarInTenantAsync<long>("SELECT count(*) FROM alert_invites WHERE created_by = @key;", subjectId);

    private static async Task<string> ShareTokenAsync(NpgsqlConnection conn) =>
        await conn.ScalarInTenantAsync<string>("SELECT coalesce(share_token, '') FROM tenants WHERE id = @key;", await AuthTestHelpers.GetTenantIdAsync(conn));

    private static Task<int> RevokeGrantOfAsync(NpgsqlConnection conn, string clientId) =>
        conn.ScalarInTenantAsync<int>("""
            WITH revoked AS (
                UPDATE oauth_grants SET revoked_at = now()
                WHERE client_id IN (SELECT id FROM oauth_clients WHERE client_id = @key)
                RETURNING 1)
            SELECT count(*)::int FROM revoked;
            """, clientId);

    private static Task<int> RewindDevicePollsOfAsync(NpgsqlConnection conn, string clientId) =>
        conn.ScalarInTenantAsync<int>("""
            WITH rewound AS (
                UPDATE oauth_device_codes SET last_polled_at = last_polled_at - make_interval(secs => "interval" + 1)
                WHERE client_id = @key AND last_polled_at IS NOT NULL
                RETURNING 1)
            SELECT count(*)::int FROM rewound;
            """, clientId);
}

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// A signed-in member who proves a provider identity that only an unclaimed subject holds (no
/// membership, no other factor, no session, never signed in) takes the identity, and the unclaimed
/// subject is removed. An identity held by any other subject stays where it is.
/// </summary>
/// <remarks>
/// The provider redirect, the code exchange, the ID-token validation and the link callback in
/// <c>OidcController</c> (its session check, state cookie, audit row and redirect) are not driven:
/// each test calls <see cref="OidcAuthService.AttachVerifiedIdentityAsync"/> with the state and
/// claims the callback passes it, against Postgres under the app role.
/// </remarks>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class OidcLinkLeftoverIntegrationTests : ApiIntegrationTestBase
{
    private readonly string _issuer = $"https://link-leftover-{Guid.NewGuid():N}.example";
    private Guid _providerId;
    private Guid _otherProviderId;

    public OidcLinkLeftoverIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        _providerId = Guid.CreateVersion7();
        _otherProviderId = Guid.CreateVersion7();
        await using var conn = await OpenPostgresAsync();
        await conn.ExecuteAsync("""
            INSERT INTO oidc_providers (id, name, issuer_url, client_id, is_enabled, display_order, scopes, claim_mappings, default_roles, created_at, updated_at)
            VALUES (@provider, 'Leftover IdP', @issuer, @client, true, 0, '["openid"]'::jsonb, '{}'::jsonb, '[]'::jsonb, now(), now()),
                   (@other, 'Other IdP', @otherIssuer, @otherClient, true, 0, '["openid"]'::jsonb, '{}'::jsonb, '[]'::jsonb, now(), now());
            """,
            ("provider", _providerId), ("issuer", _issuer), ("client", $"client-{_providerId:N}"),
            ("other", _otherProviderId), ("otherIssuer", _issuer + "/other"), ("otherClient", $"client-{_otherProviderId:N}"));
    }

    public override async Task DisposeAsync()
    {
        await using (var conn = await OpenPostgresAsync())
        {
            await conn.PinTenantAsync(Fixture.TenantId);
            await conn.ExecuteAsync("""
                CREATE TEMP TABLE leftover_subjects AS
                    SELECT subject_id FROM subject_oidc_identities
                    WHERE provider_id IN (@provider, @other) AND subject_id <> @owner;
                DELETE FROM tenant_members WHERE subject_id IN (SELECT subject_id FROM leftover_subjects);
                DELETE FROM subject_roles WHERE assigned_by_id IN (SELECT subject_id FROM leftover_subjects);
                DELETE FROM oauth_grants WHERE created_by_subject_id IN (SELECT subject_id FROM leftover_subjects);
                DELETE FROM subject_oidc_identities WHERE provider_id IN (@provider, @other);
                DELETE FROM subjects WHERE id IN (SELECT subject_id FROM leftover_subjects);
                DELETE FROM oidc_providers WHERE id IN (@provider, @other);
                """,
                ("provider", _providerId), ("other", _otherProviderId), ("owner", Fixture.OwnerSubjectId));
        }

        await base.DisposeAsync();
    }

    [Fact]
    public async Task A_member_linking_an_identity_an_unclaimed_subject_holds_takes_it_and_the_subject_is_removed()
    {
        var leftover = await CreateLeftoverAsync("leftover-sub");
        await using (var conn = await OpenPostgresAsync())
        {
            await conn.ExecuteAsync("""
                INSERT INTO subject_roles (subject_id, role_id, assigned_at)
                SELECT @subject, id, now() FROM roles ORDER BY name LIMIT 1;
                """,
                ("subject", leftover));
        }

        var result = await LinkAsync(Fixture.OwnerSubjectId, Fixture.OwnerSubjectId, "leftover-sub");

        result.Success.Should().BeTrue(result.ErrorDescription);
        result.Added.Should().BeTrue();
        (await LinkedSubjectAsync("leftover-sub")).Should().Be(Fixture.OwnerSubjectId);
        (await CountAsync("SELECT count(*) FROM subjects WHERE id = @subject", leftover)).Should().Be(0);
        (await CountAsync("SELECT count(*) FROM subject_roles WHERE subject_id = @subject", leftover)).Should().Be(0);
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("system subject")]
    [InlineData("demo subject")]
    [InlineData("platform admin")]
    [InlineData("last login")]
    [InlineData("another identity")]
    [InlineData("membership")]
    [InlineData("passkey")]
    [InlineData("authenticator")]
    [InlineData("recovery code")]
    [InlineData("session")]
    [InlineData("expired session")]
    [InlineData("trusted device")]
    [InlineData("step-up token")]
    [InlineData("avatar")]
    [InlineData("chat identity")]
    [InlineData("tenant creation invite")]
    [InlineData("role it assigned")]
    [InlineData("login code")]
    [InlineData("member invite")]
    [InlineData("grant")]
    [InlineData("grant it created")]
    [InlineData("authorisation code")]
    [InlineData("excursion mute")]
    [InlineData("membership request")]
    [InlineData("membership request it decided")]
    [InlineData("client device")]
    [InlineData("device code")]
    [InlineData("alert invite it created")]
    [InlineData("alert invite it used")]
    [InlineData("alert receipt")]
    public async Task Linking_leaves_an_identity_with_a_subject_that_holds(string held)
    {
        var holder = await CreateLeftoverAsync("held-sub");
        await GiveAsync(holder, held);

        var result = await LinkAsync(Fixture.OwnerSubjectId, Fixture.OwnerSubjectId, "held-sub");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("identity_already_linked");
        (await LinkedSubjectAsync("held-sub")).Should().Be(holder);
        (await CountAsync("SELECT count(*) FROM subjects WHERE id = @subject", holder)).Should().Be(1);
    }

    [Fact]
    public async Task A_member_whose_history_is_clamped_takes_no_identity_from_another_subject()
    {
        var leftover = await CreateLeftoverAsync("clamped-sub");

        var result = await LinkAsync(Fixture.OwnerSubjectId, Fixture.OwnerSubjectId, "clamped-sub", clamped: true);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("identity_already_linked");
        (await LinkedSubjectAsync("clamped-sub")).Should().Be(leftover);
    }

    [Fact]
    public async Task A_link_whose_state_was_issued_for_another_subject_takes_nothing()
    {
        var leftover = await CreateLeftoverAsync("someone-sub");

        var result = await LinkAsync(Fixture.OwnerSubjectId, stateSubjectId: Guid.CreateVersion7(), "someone-sub");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("invalid_state");
        (await LinkedSubjectAsync("someone-sub")).Should().Be(leftover);
    }

    /// <summary>
    /// A subject holding only the identity, made the way a provider sign-in that was later refused
    /// once left one: no membership, no session and no last login.
    /// </summary>
    private async Task<Guid> CreateLeftoverAsync(string externalId)
    {
        using var scope = Fixture.Services.CreateScope();
        var subject = await scope.ServiceProvider.GetRequiredService<ISubjectService>()
            .FindOrCreateFromOidcAsync(_providerId, externalId, _issuer, name: externalId);
        return subject.Id;
    }

    private async Task<OidcLinkResult> LinkAsync(
        Guid signedInSubjectId, Guid stateSubjectId, string externalId, bool clamped = false)
    {
        using var scope = Fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(
            new TenantContext(Fixture.TenantId, "link", "Link", true, false));
        if (clamped)
            scope.ServiceProvider.GetRequiredService<ICategoryReadContext>().ClampMemberHistory();
        var service = (OidcAuthService)scope.ServiceProvider.GetRequiredService<IOidcAuthService>();
        var provider = await scope.ServiceProvider.GetRequiredService<IOidcProviderService>()
            .GetProviderByIdAsync(_providerId);

        return await service.AttachVerifiedIdentityAsync(
            new OidcAuthService.OidcStateData
            {
                Intent = OidcAuthService.LinkIntent,
                ProviderId = _providerId,
                ReturnUrl = "/settings/account",
                Nonce = "nonce",
                SubjectId = stateSubjectId,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            },
            provider!,
            new OidcAuthService.OidcIdTokenClaims { Sub = externalId, Name = externalId },
            signedInSubjectId);
    }

    private async Task GiveAsync(Guid subjectId, string held)
    {
        await using var conn = await OpenPostgresAsync();
        var ruleId = Guid.Empty;
        var excursionId = Guid.Empty;
        if (held is "excursion mute" or "alert receipt" or "alert invite it created" or "alert invite it used")
        {
            ruleId = await AuthTestHelpers.SeedAlertRuleAsync(Fixture, Fixture.TenantId);
            (excursionId, _) = await AuthTestHelpers.SeedAlertExcursionAsync(Fixture, Fixture.TenantId, ruleId);
        }

        await conn.PinTenantAsync(Fixture.TenantId);
        var sql = held switch
        {
            "inactive" => "UPDATE subjects SET is_active = false WHERE id = @subject;",
            "system subject" => "UPDATE subjects SET is_system_subject = true WHERE id = @subject;",
            "demo subject" => "UPDATE subjects SET is_demo_subject = true WHERE id = @subject;",
            "membership" => """
                INSERT INTO tenant_members (id, tenant_id, subject_id, sys_created_at, sys_updated_at)
                VALUES (gen_random_uuid(), @tenant, @subject, now(), now());
                """,
            "passkey" => """
                INSERT INTO passkey_credentials (id, subject_id, credential_id, public_key, sign_count, transports, created_at)
                VALUES (gen_random_uuid(), @subject, @credential, @credential, 0, '{}', now());
                """,
            "authenticator" => """
                INSERT INTO totp_credentials (id, subject_id, secret_key, created_at)
                VALUES (gen_random_uuid(), @subject, '\x00'::bytea, now());
                """,
            "recovery code" => """
                INSERT INTO recovery_codes (id, subject_id, code_hash, created_at)
                VALUES (gen_random_uuid(), @subject, md5(@subject::text), now());
                """,
            "session" => """
                INSERT INTO refresh_tokens (id, token_hash, subject_id, issued_at, expires_at, created_at, updated_at)
                VALUES (gen_random_uuid(), md5(@subject::text), @subject, now(), now() + interval '7 days', now(), now());
                """,
            "expired session" => """
                INSERT INTO refresh_tokens (id, token_hash, subject_id, issued_at, expires_at, created_at, updated_at)
                VALUES (gen_random_uuid(), md5(@subject::text), @subject, now() - interval '9 days', now() - interval '2 days', now(), now());
                """,
            "trusted device" => """
                INSERT INTO trusted_devices (id, subject_id, token_hash, created_at, expires_at)
                VALUES (gen_random_uuid(), @subject, md5(@subject::text), now(), now() + interval '30 days');
                """,
            "step-up token" => """
                INSERT INTO totp_step_up_tokens (id, subject_id, created_at, expires_at)
                VALUES (gen_random_uuid(), @subject, now(), now() + interval '5 minutes');
                """,
            "avatar" => """
                INSERT INTO subject_avatars (id, subject_id, data, content_type, file_size)
                VALUES (gen_random_uuid(), @subject, '\x00'::bytea, 'image/png', 1);
                """,
            "chat identity" => """
                INSERT INTO chat_identity_directory (id, platform, platform_user_id, tenant_id, nocturne_user_id, label, display_name, is_default, display_unit, is_active, created_at)
                VALUES (gen_random_uuid(), 'discord', @subject::text, @tenant, @subject, 'me', 'Me', true, 'mg/dL', true, now());
                """,
            "tenant creation invite" => """
                INSERT INTO tenant_creation_invites (id, created_by_subject_id, token_hash, expires_at, use_count)
                VALUES (gen_random_uuid(), @subject, md5(@subject::text), now() + interval '1 day', 0);
                """,
            "role it assigned" => """
                INSERT INTO subject_roles (subject_id, role_id, assigned_at, assigned_by_id)
                SELECT @owner, id, now(), @subject FROM roles
                WHERE id NOT IN (SELECT role_id FROM subject_roles WHERE subject_id = @owner) LIMIT 1;
                """,
            "login code" => """
                INSERT INTO login_codes (id, tenant_id, subject_id, code_hash, expires_at)
                VALUES (gen_random_uuid(), @tenant, @subject, md5(@subject::text), now() + interval '10 minutes');
                """,
            "member invite" => """
                INSERT INTO member_invites (id, tenant_id, created_by_subject_id, token_hash, role_ids, limit_to_24_hours, expires_at, use_count, created_at)
                VALUES (gen_random_uuid(), @tenant, @subject, md5(@subject::text), '[]'::jsonb, false, now() + interval '1 day', 0, now());
                """,
            "grant" => """
                INSERT INTO oauth_grants (id, tenant_id, subject_id, grant_type, scopes, created_at)
                VALUES (gen_random_uuid(), @tenant, @subject, 'direct', '{}', now());
                """,
            "grant it created" => """
                INSERT INTO oauth_grants (id, tenant_id, subject_id, created_by_subject_id, grant_type, scopes, created_at)
                VALUES (gen_random_uuid(), @tenant, @owner, @subject, 'direct', '{}', now());
                """,
            "authorisation code" => """
                INSERT INTO oauth_clients (id, client_id, tenant_id) VALUES (@client, @client::text, @tenant);
                INSERT INTO oauth_authorization_codes (id, client_entity_id, subject_id, code_hash, scopes, redirect_uri, code_challenge, expires_at, tenant_id)
                VALUES (gen_random_uuid(), @client, @subject, md5(@subject::text), '{}', 'https://app.example/cb', 'challenge', now() + interval '1 minute', @tenant);
                """,
            "excursion mute" => """
                INSERT INTO alert_excursion_mutes (id, tenant_id, subject_id, alert_excursion_id)
                VALUES (gen_random_uuid(), @tenant, @subject, @excursion);
                """,
            "membership request" => """
                INSERT INTO membership_requests (id, tenant_id, subject_id, status, created_at)
                VALUES (gen_random_uuid(), @tenant, @subject, 'pending', now());
                """,
            "membership request it decided" => """
                INSERT INTO membership_requests (id, tenant_id, subject_id, status, decided_by_subject_id, decided_at, created_at)
                VALUES (gen_random_uuid(), @tenant, @owner, 'declined', @subject, now(), now());
                """,
            "client device" => """
                INSERT INTO client_devices (id, tenant_id, subject_id, install_id, kind, capabilities)
                VALUES (gen_random_uuid(), @tenant, @subject, md5(@subject::text), 'app', '{}');
                """,
            "device code" => """
                INSERT INTO oauth_device_codes (id, client_id, device_code_hash, user_code, scopes, expires_at, approved_at, subject_id, tenant_id)
                VALUES (gen_random_uuid(), 'client', md5(@subject::text), left(md5(@subject::text), 8), '{}', now() + interval '10 minutes', now(), @subject, @tenant);
                """,
            "alert invite it created" => """
                INSERT INTO alert_invites (id, tenant_id, created_by, token, alert_rule_channel_id, is_used, expires_at)
                SELECT gen_random_uuid(), @tenant, @subject, md5(@subject::text), id, false, now() + interval '1 day'
                FROM alert_rule_channels WHERE alert_rule_id = @rule;
                """,
            "alert invite it used" => """
                INSERT INTO alert_invites (id, tenant_id, created_by, token, alert_rule_channel_id, is_used, used_by, expires_at)
                SELECT gen_random_uuid(), @tenant, @owner, md5(@subject::text), id, true, @subject, now() + interval '1 day'
                FROM alert_rule_channels WHERE alert_rule_id = @rule;
                """,
            "alert receipt" => """
                INSERT INTO alert_excursion_receipts (id, tenant_id, alert_excursion_id, actor_kind, actor_id, seen_at)
                VALUES (gen_random_uuid(), @tenant, @excursion, 'Member', @subject, now());
                """,
            "platform admin" => "UPDATE subjects SET is_platform_admin = true WHERE id = @subject;",
            "last login" => "UPDATE subjects SET last_login_at = now() WHERE id = @subject;",
            "another identity" => """
                INSERT INTO subject_oidc_identities (id, subject_id, provider_id, oidc_subject_id, issuer, linked_at)
                VALUES (gen_random_uuid(), @subject, @other, @subject::text, @otherIssuer, now());
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(held), held, null),
        };
        await conn.ExecuteAsync(sql,
            ("subject", subjectId), ("tenant", Fixture.TenantId),
            ("credential", Guid.NewGuid().ToByteArray()), ("other", _otherProviderId),
            ("otherIssuer", _issuer + "/other"), ("owner", Fixture.OwnerSubjectId),
            ("client", Guid.CreateVersion7()), ("excursion", excursionId), ("rule", ruleId));
    }

    private async Task<Guid?> LinkedSubjectAsync(string externalId)
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.ScalarAsync<Guid?>("SELECT subject_id FROM subject_oidc_identities WHERE issuer = @issuer AND oidc_subject_id = @sub", ("issuer", _issuer), ("sub", externalId));
    }

    private async Task<long> CountAsync(string sql, Guid subjectId)
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.ScalarAsync<long>(sql, ("subject", subjectId));
    }
}

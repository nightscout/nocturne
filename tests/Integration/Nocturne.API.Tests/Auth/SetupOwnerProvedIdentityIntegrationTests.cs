using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// The setup owner step completed by an identity provider: a caller who proves an identity that a
/// subject with no membership already holds completes setup as that subject, and no other subject
/// the instance holds is made owner.
/// </summary>
/// <remarks>
/// The owner step runs over HTTP (<c>POST /api/v4/setup/owner/options</c>, which creates the owner
/// subject with the same code as <c>owner/oidc</c>; that one needs a live provider's discovery
/// document). The provider redirect, the code exchange, the ID-token validation and
/// <c>SetupController.OidcCallback</c> (its setup-open gate, state cookie, platform-admin grant and
/// cookies) are not driven: each test calls <see cref="OidcAuthService.CompleteSetupAsync"/> with
/// the state and claims the callback passes it. The tenant is put back to a fresh install as
/// <see cref="SetupOwnerIntegrationTests"/> does.
/// <para>
/// The sign-in that ends setup runs <see cref="SignInCompletion"/> in a scope that resolves no
/// tenant, as the setup endpoints' apex requests do. The passkey's attestation is not driven: its
/// store is a row written in its place.
/// </para>
/// </remarks>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public class SetupOwnerProvedIdentityIntegrationTests : ApiIntegrationTestBase
{
    private readonly string _issuer = $"https://setup-proved-{Guid.NewGuid():N}.example";
    private readonly List<Guid> _deactivated = [];
    private readonly List<Guid> _signedIn = [];
    private Guid _providerId;

    public SetupOwnerProvedIdentityIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        await using var conn = await OpenPostgresAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE subjects SET is_active = false
                WHERE is_active AND NOT is_system_subject AND NOT is_demo_subject
                RETURNING id;
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                _deactivated.Add(reader.GetGuid(0));
        }

        _providerId = Guid.CreateVersion7();
        await conn.ExecuteAsync("""
            DELETE FROM passkey_credentials WHERE subject_id IN
                (SELECT subject_id FROM tenant_members WHERE tenant_id = @tenant);
            INSERT INTO oidc_providers (id, name, issuer_url, client_id, is_enabled, display_order, scopes, claim_mappings, default_roles, created_at, updated_at)
            VALUES (@provider, 'Setup proved IdP', @issuer, @client, true, 0, '["openid"]'::jsonb, '{}'::jsonb, '[]'::jsonb, now(), now());
            """,
            ("tenant", Fixture.TenantId), ("provider", _providerId), ("issuer", _issuer),
            ("client", $"client-{_providerId:N}"));
    }

    public override async Task DisposeAsync()
    {
        await using (var conn = await OpenPostgresAsync())
        {
            await conn.ExecuteAsync("""
                UPDATE subjects SET is_platform_admin = false WHERE id = ANY(@signedIn);
                DELETE FROM passkey_credentials WHERE subject_id = ANY(@signedIn);
                DELETE FROM refresh_tokens WHERE subject_id = ANY(@signedIn);
                CREATE TEMP TABLE proved_subjects AS
                    SELECT subject_id FROM subject_oidc_identities WHERE provider_id = @provider;
                UPDATE subjects SET is_active = false
                WHERE is_active AND NOT is_system_subject AND NOT is_demo_subject;
                UPDATE subjects SET is_active = true, is_platform_admin = false WHERE id = ANY(@restore);
                DELETE FROM passkey_credentials WHERE subject_id IN (SELECT subject_id FROM proved_subjects);
                DELETE FROM totp_credentials WHERE subject_id IN (SELECT subject_id FROM proved_subjects);
                DELETE FROM refresh_tokens WHERE subject_id IN (SELECT subject_id FROM proved_subjects);
                DELETE FROM subject_roles WHERE subject_id IN (SELECT subject_id FROM proved_subjects);
                DELETE FROM subject_oidc_identities WHERE provider_id = @provider;
                DELETE FROM subjects WHERE id IN (SELECT subject_id FROM proved_subjects)
                    AND NOT EXISTS (SELECT 1 FROM tenant_members m WHERE m.subject_id = subjects.id);
                DELETE FROM oidc_providers WHERE id = @provider;
                DELETE FROM tenant_creation_invites WHERE label = @issuer;
                """,
                ("restore", _deactivated.ToArray()), ("provider", _providerId), ("issuer", _issuer),
                ("signedIn", _signedIn.ToArray()));
        }

        await base.DisposeAsync();
    }

    [Fact]
    public async Task Setup_by_a_provider_identity_a_subject_without_membership_holds_completes_as_that_subject()
    {
        var holder = await SignInOnTheApexAsync("operator-sub");
        var earlierSessions = await LiveSessionIdsAsync(holder);
        earlierSessions.Should().NotBeEmpty();

        using var apex = Fixture.CreateApexClient();
        await StartOwnerSetupAsync(apex, "operator", "The Operator");
        var created = (await OwnerSubjectIdsAsync()).Should().ContainSingle().Subject;

        var result = await CompleteSetupAsync(created, "operator-sub");

        result.Success.Should().BeTrue(result.ErrorDescription);
        result.SubjectId.Should().Be(holder);
        result.ProviderId.Should().Be(_providerId);
        (await OwnerSubjectIdsAsync()).Should().Equal(holder);
        (await MemberUsernameAsync(holder)).Should().Be("operator");
        (await GlobalRolesAsync(holder)).Should().Contain("admin");
        (await SubjectExistsAsync(created)).Should().BeFalse("the factor-less subject the owner step made is discarded");
        (await LinkedSubjectAsync("operator-sub")).Should().Be(holder);
        (await LiveSessionIdsAsync(holder)).Should().BeEmpty(
            "the adoption ends the holder's earlier sessions, and the setup callback signs the owner in after it");
    }

    [Theory]
    [InlineData("created owner", SignInMethod.Passkey)]
    [InlineData("created owner", SignInMethod.Oidc)]
    [InlineData("adopted holder", SignInMethod.Passkey)]
    [InlineData("adopted holder", SignInMethod.Oidc)]
    public async Task The_sign_in_that_ends_setup_on_a_host_that_resolves_no_tenant_signs_the_owner_in_once(
        string owner, SignInMethod method)
    {
        Guid? holder = owner == "adopted holder" ? await SignInOnTheApexAsync("adopted-sub") : null;

        using var apex = Fixture.CreateApexClient();
        await StartOwnerSetupAsync(apex, "operator", "The Operator");
        var created = (await OwnerSubjectIdsAsync()).Should().ContainSingle().Subject;

        Guid ownerId;
        if (holder is null && method == SignInMethod.Passkey)
        {
            await using var conn = await OpenPostgresAsync();
            await conn.ExecuteAsync("""
                INSERT INTO passkey_credentials (id, subject_id, credential_id, public_key, sign_count, transports, created_at)
                VALUES (gen_random_uuid(), @subject, @credential, @credential, 0, '{}', now());
                UPDATE tenants SET setup_owner_subject_id = NULL WHERE setup_owner_subject_id = @subject;
                """,
                ("subject", created), ("credential", Guid.NewGuid().ToByteArray()));
            ownerId = created;
        }
        else
        {
            var linked = await CompleteSetupAsync(created, holder is null ? "operator-own-sub" : "adopted-sub");
            linked.Success.Should().BeTrue(linked.ErrorDescription);
            ownerId = linked.SubjectId!.Value;
            ownerId.Should().Be(holder ?? created);
        }
        _signedIn.Add(ownerId);

        SignInOutcome outcome;
        using (var scope = Fixture.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantAccessor>().IsResolved.Should().BeFalse();
            await scope.ServiceProvider.GetRequiredService<PlatformAdminBootstrapService>()
                .EnsureFirstOwnerIsPlatformAdminAsync(ownerId, CancellationToken.None);

            outcome = await scope.ServiceProvider.GetRequiredService<SignInCompletion>().CompleteAsync(new SignInAttempt(
                ownerId,
                method,
                new SignInAdmission.Member(Fixture.TenantId),
                new SecondFactorProof.Primary(TrustedDeviceToken: null),
                new SessionContext(DeviceDescription: "Setup", IpAddress: "203.0.113.7", UserAgent: "setup-agent"),
                method == SignInMethod.Oidc ? _providerId : null,
                Enrolment: SignInEnrolment.Setup));
        }

        outcome.Should().BeOfType<SignInOutcome.SignedIn>().Which.Session.SubjectId.Should().Be(ownerId);
        (await LiveSessionIdsAsync(ownerId)).Should().ContainSingle();
        var row = (await SetupSignInRowsAsync(ownerId)).Should().ContainSingle().Subject;
        row.Should().Be((
            AuthAuditEventType.Login,
            true,
            method == SignInMethod.Passkey ? "passkey" : "oidc",
            "setup",
            2L,
            (Guid?)null));
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("system subject")]
    [InlineData("demo subject")]
    [InlineData("platform admin")]
    [InlineData("membership")]
    [InlineData("passkey")]
    [InlineData("authenticator")]
    [InlineData("another identity")]
    public async Task Setup_by_a_provider_identity_does_not_adopt_a_subject_that_holds(string held)
    {
        var holder = await SignInOnTheApexAsync("held-sub");

        using var apex = Fixture.CreateApexClient();
        await StartOwnerSetupAsync(apex, "operator", "The Operator");
        var created = (await OwnerSubjectIdsAsync()).Should().ContainSingle().Subject;
        await GiveAsync(holder, held);

        var result = await CompleteSetupAsync(created, "held-sub");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("identity_already_linked");
        (await LinkedSubjectAsync("held-sub")).Should().Be(holder);
        (await OwnerSubjectIdsAsync()).Should().Equal(created);
        (await MemberUsernameAsync(holder)).Should().BeNull();
    }

    [Theory]
    [InlineData("member invite")]
    [InlineData("grant")]
    [InlineData("recovery code")]
    [InlineData("expired session")]
    [InlineData("last login")]
    public async Task Setup_keeps_a_resumed_subject_that_holds_and_adopts_nothing(string held)
    {
        var holder = await SignInOnTheApexAsync("resumed-sub");

        using var apex = Fixture.CreateApexClient();
        await StartOwnerSetupAsync(apex, "operator", "The Operator");
        var resumed = (await OwnerSubjectIdsAsync()).Should().ContainSingle().Subject;
        await GiveResumedAsync(resumed, held);
        await StartOwnerSetupAsync(apex, "operator", "The Operator");
        (await OwnerSubjectIdsAsync()).Should().Equal(new[] { resumed }, "setup reopened resumes the earlier subject");

        var result = await CompleteSetupAsync(resumed, "resumed-sub");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("identity_already_linked");
        (await SubjectExistsAsync(resumed)).Should().BeTrue();
        (await OwnerSubjectIdsAsync()).Should().Equal(resumed);
        (await MemberUsernameAsync(holder)).Should().BeNull();
        (await LinkedSubjectAsync("resumed-sub")).Should().Be(holder);
        (await RowsNamingAsync(resumed, held)).Should().Be(1);
    }

    [Theory]
    [InlineData("membership in another tenant")]
    [InlineData("membership without the owner role")]
    [InlineData("two memberships")]
    [InlineData("another global role")]
    public async Task Setup_adopts_only_in_place_of_the_owner_this_run_made(string shape)
    {
        var holder = await SignInOnTheApexAsync("shaped-sub");

        using var apex = Fixture.CreateApexClient();
        await StartOwnerSetupAsync(apex, "operator", "The Operator");
        var created = (await OwnerSubjectIdsAsync()).Should().ContainSingle().Subject;
        await ReshapeAsync(created, shape);

        var result = await CompleteSetupAsync(created, "shaped-sub");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("identity_already_linked");
        (await SubjectExistsAsync(created)).Should().BeTrue();
        (await MembershipCountAsync(holder)).Should().Be(0);
        (await LinkedSubjectAsync("shaped-sub")).Should().Be(holder);
    }

    [Fact]
    public async Task Setup_adopts_nothing_for_a_state_that_names_no_tenant()
    {
        var holder = await SignInOnTheApexAsync("tenantless-sub");

        using var apex = Fixture.CreateApexClient();
        await StartOwnerSetupAsync(apex, "operator", "The Operator");
        var created = (await OwnerSubjectIdsAsync()).Should().ContainSingle().Subject;

        var result = await CompleteSetupAsync(created, "tenantless-sub", tenantSlug: null);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("identity_already_linked");
        (await SubjectExistsAsync(created)).Should().BeTrue();
        (await OwnerSubjectIdsAsync()).Should().Equal(created);
        (await MembershipCountAsync(holder)).Should().Be(0);
        (await LinkedSubjectAsync("tenantless-sub")).Should().Be(holder);
    }

    [Fact]
    public async Task A_session_revoke_that_fails_leaves_the_adoption_undone()
    {
        var holder = await SignInOnTheApexAsync("revoke-sub");
        var earlierSessions = await LiveSessionIdsAsync(holder);

        using var apex = Fixture.CreateApexClient();
        await StartOwnerSetupAsync(apex, "operator", "The Operator");
        var created = (await OwnerSubjectIdsAsync()).Should().ContainSingle().Subject;

        var trigger = $"refuse_revoke_{holder:N}";
        await using (var conn = await OpenPostgresAsync())
        {
            await conn.ExecuteAsync($"""
                CREATE FUNCTION {trigger}() RETURNS trigger LANGUAGE plpgsql AS
                    $$ BEGIN RAISE EXCEPTION 'revoke refused'; END $$;
                CREATE TRIGGER {trigger} BEFORE UPDATE ON refresh_tokens FOR EACH ROW
                    WHEN (NEW.subject_id = '{holder}' AND NEW.revoked_at IS NOT NULL AND OLD.revoked_at IS NULL)
                    EXECUTE FUNCTION {trigger}();
                """);
        }

        try
        {
            var complete = () => CompleteSetupAsync(created, "revoke-sub");

            await complete.Should().ThrowAsync<DbUpdateException>();
        }
        finally
        {
            await using var conn = await OpenPostgresAsync();
            await conn.ExecuteAsync($"""
                DROP TRIGGER {trigger} ON refresh_tokens;
                DROP FUNCTION {trigger}();
                """);
        }

        (await OwnerSubjectIdsAsync()).Should().Equal(created);
        (await SubjectExistsAsync(created)).Should().BeTrue();
        (await MemberUsernameAsync(holder)).Should().BeNull();
        (await LinkedSubjectAsync("revoke-sub")).Should().Be(holder);
        (await LiveSessionIdsAsync(holder)).Should().BeEquivalentTo(earlierSessions);
    }

    [Fact]
    public async Task Setup_by_a_different_identity_makes_owner_the_subject_the_owner_step_created()
    {
        var stranger = await SignInOnTheApexAsync("stranger-sub");

        using var apex = Fixture.CreateApexClient();
        await StartOwnerSetupAsync(apex, "operator", "The Operator");
        var created = (await OwnerSubjectIdsAsync()).Should().ContainSingle().Subject;

        var result = await CompleteSetupAsync(created, "operator-own-sub");

        result.Success.Should().BeTrue(result.ErrorDescription);
        result.SubjectId.Should().Be(created);
        (await OwnerSubjectIdsAsync()).Should().Equal(created);
        (await LinkedSubjectAsync("operator-own-sub")).Should().Be(created);
        (await LinkedSubjectAsync("stranger-sub")).Should().Be(stranger);
        (await MemberUsernameAsync(stranger)).Should().BeNull();
        (await SubjectExistsAsync(stranger)).Should().BeTrue();
    }

    [Fact]
    public async Task A_state_not_issued_for_setup_adopts_nothing()
    {
        var holder = await SignInOnTheApexAsync("login-sub");

        using var apex = Fixture.CreateApexClient();
        await StartOwnerSetupAsync(apex, "operator", "The Operator");
        var created = (await OwnerSubjectIdsAsync()).Should().ContainSingle().Subject;

        var result = await CompleteSetupAsync(created, "login-sub", intent: "login");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("invalid_intent");
        (await OwnerSubjectIdsAsync()).Should().Equal(created);
        (await LinkedSubjectAsync("login-sub")).Should().Be(holder);
    }

    [Fact]
    public async Task The_owner_step_refuses_a_username_a_former_member_holds()
    {
        var former = await MemberUsernameAsync(Fixture.OwnerSubjectId);
        await SetMemberUsernameAsync(Fixture.OwnerSubjectId, "former");
        try
        {
            using var apex = Fixture.CreateApexClient();
            using var response = await apex.PostAsJsonAsync(
                "/api/v4/setup/owner/options", new { username = "former", displayName = "The Operator" });

            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
            body.Should().Contain("This username is already taken");
            (await OwnerSubjectIdsAsync()).Should().BeEmpty();
        }
        finally
        {
            await SetMemberUsernameAsync(Fixture.OwnerSubjectId, former);
        }
    }

    private async Task SetMemberUsernameAsync(Guid subjectId, string? username)
    {
        await using var conn = await OpenPostgresAsync();
        await conn.PinTenantAsync(Fixture.TenantId);
        await conn.ExecuteAsync("""
            UPDATE tenant_members SET username = @username WHERE tenant_id = @tenant AND subject_id = @subject;
            """, ("tenant", Fixture.TenantId), ("subject", subjectId), ("username", (object?)username ?? DBNull.Value));
    }

    /// <summary>
    /// What the provider's sign-in callback does on the apex for an identity it has not seen, from
    /// a tenant-creation link that was never redeemed: a subject holding the identity, a session
    /// and a last login, and no membership.
    /// </summary>
    private async Task<Guid> SignInOnTheApexAsync(string externalId)
    {
        var token = $"start-{Guid.NewGuid():N}";
        await using (var conn = await OpenPostgresAsync())
        {
            await conn.ExecuteAsync("""
                INSERT INTO tenant_creation_invites (id, created_by_subject_id, token_hash, label, expires_at, use_count)
                VALUES (@id, @creator, @hash, @label, now() + interval '1 day', 0);
                """,
                ("id", Guid.CreateVersion7()), ("creator", Fixture.OwnerSubjectId),
                ("hash", HashUtils.Sha256Hex(token)), ("label", _issuer));
        }

        using var scope = Fixture.Services.CreateScope();
        var service = (OidcAuthService)scope.ServiceProvider.GetRequiredService<IOidcAuthService>();
        var provider = await scope.ServiceProvider.GetRequiredService<IOidcProviderService>()
            .GetProviderByIdAsync(_providerId);

        var result = await service.CompleteLoginAsync(
            State("login", subjectId: null, returnUrl: $"/start?token={token}"),
            provider!,
            new OidcAuthService.OidcIdTokenClaims { Sub = externalId, Name = externalId },
            currentTenantId: null,
            ipAddress: null,
            userAgent: null);

        result.Success.Should().BeTrue(result.ErrorDescription);
        return result.SubjectId!.Value;
    }

    private async Task<OidcSetupCallbackResult> CompleteSetupAsync(
        Guid createdSubjectId, string externalId, string intent = "setup",
        string? tenantSlug = ApiIntegrationTestFixture.TenantSlug)
    {
        using var scope = Fixture.Services.CreateScope();
        var service = (OidcAuthService)scope.ServiceProvider.GetRequiredService<IOidcAuthService>();
        var provider = await scope.ServiceProvider.GetRequiredService<IOidcProviderService>()
            .GetProviderByIdAsync(_providerId);

        return await service.CompleteSetupAsync(
            State(intent, createdSubjectId, tenantSlug),
            provider!,
            new OidcAuthService.OidcIdTokenClaims { Sub = externalId, Name = externalId },
            Fixture.TenantId);
    }

    private OidcAuthService.OidcStateData State(
        string intent, Guid? subjectId, string? tenantSlug = null, string? returnUrl = null) => new()
    {
        Intent = intent,
        ProviderId = _providerId,
        ReturnUrl = returnUrl ?? (intent == "setup" ? "/setup" : "/"),
        Nonce = "nonce",
        SubjectId = subjectId,
        TenantSlug = tenantSlug,
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
    };

    private async Task GiveAsync(Guid subjectId, string held)
    {
        await using var conn = await OpenPostgresAsync();
        switch (held)
        {
            case "passkey":
                await conn.ExecuteAsync("""
                    INSERT INTO passkey_credentials (id, subject_id, credential_id, public_key, sign_count, transports, created_at)
                    VALUES (gen_random_uuid(), @subject, @credential, @credential, 0, '{}', now());
                    """,
                    ("subject", subjectId), ("credential", Guid.NewGuid().ToByteArray()));
                break;
            case "authenticator":
                await conn.ExecuteAsync("""
                    INSERT INTO totp_credentials (id, subject_id, secret_key, created_at)
                    VALUES (gen_random_uuid(), @subject, '\x00'::bytea, now());
                    """,
                    ("subject", subjectId));
                break;
            case "inactive":
                await conn.ExecuteAsync("UPDATE subjects SET is_active = false WHERE id = @subject;",
                    ("subject", subjectId));
                break;
            case "system subject":
                await conn.ExecuteAsync("UPDATE subjects SET is_system_subject = true WHERE id = @subject;",
                    ("subject", subjectId));
                break;
            case "demo subject":
                await conn.ExecuteAsync("UPDATE subjects SET is_demo_subject = true WHERE id = @subject;",
                    ("subject", subjectId));
                break;
            case "another identity":
                await conn.ExecuteAsync("""
                    INSERT INTO subject_oidc_identities (id, subject_id, provider_id, oidc_subject_id, issuer, linked_at)
                    VALUES (gen_random_uuid(), @subject, @provider, 'second-sub', @issuer, now());
                    """,
                    ("subject", subjectId), ("provider", _providerId), ("issuer", _issuer));
                break;
            case "platform admin":
                await conn.ExecuteAsync("UPDATE subjects SET is_platform_admin = true WHERE id = @subject;",
                    ("subject", subjectId));
                break;
            case "membership":
                var other = await AuthTestHelpers.SeedTenantAsync(Fixture, $"other-{Guid.NewGuid():N}"[..20], "Other");
                await conn.PinTenantAsync(other);
                await conn.ExecuteAsync("""
                    INSERT INTO tenant_members (id, tenant_id, subject_id, sys_created_at, sys_updated_at)
                    VALUES (gen_random_uuid(), @tenant, @subject, now(), now());
                    """,
                    ("tenant", other), ("subject", subjectId));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(held), held, null);
        }
    }

    /// <summary>
    /// A row a subject setup resumes can hold from before: it was a member, or set out to be one,
    /// without ever holding a factor.
    /// </summary>
    private async Task GiveResumedAsync(Guid subjectId, string held)
    {
        await using var conn = await OpenPostgresAsync();
        await conn.PinTenantAsync(Fixture.TenantId);
        var sql = held switch
        {
            "member invite" => """
                INSERT INTO member_invites (id, tenant_id, created_by_subject_id, token_hash, role_ids, limit_to_24_hours, expires_at, use_count, created_at)
                VALUES (gen_random_uuid(), @tenant, @subject, md5(@subject::text), '[]'::jsonb, false, now() + interval '1 day', 0, now());
                """,
            "grant" => """
                INSERT INTO oauth_grants (id, tenant_id, subject_id, grant_type, scopes, created_at)
                VALUES (gen_random_uuid(), @tenant, @subject, 'direct', '{}', now());
                """,
            "recovery code" => """
                INSERT INTO recovery_codes (id, subject_id, code_hash, created_at)
                VALUES (gen_random_uuid(), @subject, md5(@subject::text), now());
                """,
            "expired session" => """
                INSERT INTO refresh_tokens (id, token_hash, subject_id, issued_at, expires_at, created_at, updated_at)
                VALUES (gen_random_uuid(), md5(@subject::text), @subject, now() - interval '9 days', now() - interval '2 days', now(), now());
                """,
            "last login" => "UPDATE subjects SET last_login_at = now() WHERE id = @subject;",
            _ => throw new ArgumentOutOfRangeException(nameof(held), held, null),
        };
        await conn.ExecuteAsync(sql,
            ("subject", subjectId), ("tenant", Fixture.TenantId));
    }

    /// <summary>
    /// Leaves the subject the owner step made in a shape other than the one that step writes: the
    /// owner membership of the tenant being set up and the global admin role.
    /// </summary>
    private async Task ReshapeAsync(Guid subjectId, string shape)
    {
        await using var conn = await OpenPostgresAsync();
        switch (shape)
        {
            case "membership in another tenant":
                var elsewhere = await AuthTestHelpers.SeedTenantAsync(Fixture, $"else-{Guid.NewGuid():N}"[..20], "Elsewhere");
                await conn.ExecuteAsync("""
                    UPDATE tenant_member_roles SET tenant_role_id =
                        (SELECT id FROM tenant_roles WHERE tenant_id = @other AND slug = 'owner')
                    WHERE tenant_member_id IN (SELECT id FROM tenant_members WHERE subject_id = @subject);
                    UPDATE tenant_members SET tenant_id = @other WHERE subject_id = @subject;
                    """,
                    ("other", elsewhere), ("subject", subjectId));
                break;
            case "membership without the owner role":
                await conn.ExecuteAsync("""
                    UPDATE tenant_member_roles SET tenant_role_id =
                        (SELECT id FROM tenant_roles WHERE tenant_id = @tenant AND slug = 'viewer')
                    WHERE tenant_member_id IN (SELECT id FROM tenant_members WHERE subject_id = @subject);
                    """,
                    ("tenant", Fixture.TenantId), ("subject", subjectId));
                break;
            case "two memberships":
                var second = await AuthTestHelpers.SeedTenantAsync(Fixture, $"second-{Guid.NewGuid():N}"[..20], "Second");
                await conn.PinTenantAsync(second);
                await conn.ExecuteAsync("""
                    INSERT INTO tenant_members (id, tenant_id, subject_id, sys_created_at, sys_updated_at)
                    VALUES (gen_random_uuid(), @tenant, @subject, now(), now());
                    """,
                    ("tenant", second), ("subject", subjectId));
                break;
            case "another global role":
                await conn.ExecuteAsync("""
                    INSERT INTO subject_roles (subject_id, role_id, assigned_at)
                    SELECT @subject, id, now() FROM roles WHERE name = 'readable';
                    """,
                    ("subject", subjectId));
                (await GlobalRolesAsync(subjectId)).Should().Contain("readable");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
    }

    private async Task<long> MembershipCountAsync(Guid subjectId)
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.ScalarAsync<long>("SELECT count(*) FROM tenant_members WHERE subject_id = @subject", ("subject", subjectId));
    }

    private async Task<long> RowsNamingAsync(Guid subjectId, string held)
    {
        await using var conn = await OpenPostgresAsync();
        var sql = held switch
        {
            "member invite" => "SELECT count(*) FROM member_invites WHERE created_by_subject_id = @subject",
            "grant" => "SELECT count(*) FROM oauth_grants WHERE subject_id = @subject",
            "recovery code" => "SELECT count(*) FROM recovery_codes WHERE subject_id = @subject",
            "expired session" => "SELECT count(*) FROM refresh_tokens WHERE subject_id = @subject",
            "last login" => "SELECT count(*) FROM subjects WHERE id = @subject AND last_login_at IS NOT NULL",
            _ => throw new ArgumentOutOfRangeException(nameof(held), held, null),
        };
        return await conn.ScalarAsync<long>(sql, ("subject", subjectId));
    }

    private static async Task StartOwnerSetupAsync(HttpClient apex, string username, string displayName)
    {
        using var response = await apex.PostAsJsonAsync(
            "/api/v4/setup/owner/options", new { username, displayName });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
    }

    private async Task<List<Guid>> OwnerSubjectIdsAsync()
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.QueryAsync("""
            SELECT m.subject_id
            FROM tenant_members m
            JOIN tenant_member_roles mr ON mr.tenant_member_id = m.id
            JOIN tenant_roles r ON r.id = mr.tenant_role_id
            JOIN subjects s ON s.id = m.subject_id
            WHERE m.tenant_id = @tenant AND r.slug = 'owner' AND s.is_active
            ORDER BY m.subject_id;
            """, reader => reader.GetGuid(0), ("tenant", Fixture.TenantId));
    }

    private async Task<string?> MemberUsernameAsync(Guid subjectId)
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.ScalarAsync<string?>("SELECT username FROM tenant_members WHERE tenant_id = @tenant AND subject_id = @subject", ("tenant", Fixture.TenantId), ("subject", subjectId));
    }

    private async Task<List<Guid>> LiveSessionIdsAsync(Guid subjectId)
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.QueryAsync("SELECT id FROM refresh_tokens WHERE subject_id = @subject AND revoked_at IS NULL AND expires_at > now()", reader => reader.GetGuid(0), ("subject", subjectId));
    }

    /// <summary>
    /// The rows that name the setup enrolment for <paramref name="subjectId"/>: event, success, method,
    /// enrolment, how many keys the details hold, and the tenant.
    /// </summary>
    private async Task<List<(string EventType, bool Success, string? Method, string? Enrolment, long Keys, Guid? TenantId)>>
        SetupSignInRowsAsync(Guid subjectId)
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.QueryAsync<(string EventType, bool Success, string? Method, string? Enrolment, long Keys, Guid? TenantId)>("""
            SELECT event_type, success, details->>'method', details->>'enrolment',
                   (SELECT count(*) FROM jsonb_object_keys(details)), tenant_id
            FROM auth_audit_log
            WHERE target_subject_id = @subject AND details->>'enrolment' = 'setup';
            """, reader => (reader.GetString(0), reader.GetBoolean(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetGuid(5)), ("subject", subjectId));
    }

    private async Task<bool> SubjectExistsAsync(Guid subjectId)
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.ScalarAsync<long>("SELECT count(*) FROM subjects WHERE id = @subject", ("subject", subjectId)) == 1;
    }

    private async Task<Guid?> LinkedSubjectAsync(string externalId)
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.ScalarAsync<Guid?>("SELECT subject_id FROM subject_oidc_identities WHERE issuer = @issuer AND oidc_subject_id = @sub", ("issuer", _issuer), ("sub", externalId));
    }

    private async Task<List<string>> GlobalRolesAsync(Guid subjectId)
    {
        using var scope = Fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISubjectService>().GetSubjectRolesAsync(subjectId);
    }
}

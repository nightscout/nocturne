using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.Authorization;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// An anonymous invite enrolment creates a subject of its own, so an account under the username
/// it is given, with no passkey and no membership, is left as it was whatever else it holds.
/// </summary>
/// <remarks>
/// The complete step, which stores the passkey and issues the session, needs a WebAuthn attestation
/// and is not driven here. Its refusal of a challenge whose subject holds a way to sign in is pinned
/// by <c>SubjectRepositoryTests</c> and <c>PasskeyControllerTests</c>.
/// </remarks>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class InviteEnrolmentIntegrationTests : ApiIntegrationTestBase
{
    public InviteEnrolmentIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task An_account_holding_an_authenticator_is_left_as_it_was_by_an_invite_enrolment()
    {
        var (name, count) = await EnrolUnderTheUsernameOfAsync(heldBy: """
            INSERT INTO totp_credentials (id, subject_id, secret_key, created_at)
            VALUES (gen_random_uuid(), @id, '\x00'::bytea, now());
            """);

        name.Should().Be("Dormant Account", "the enrolment must start a subject of its own");
        count.Should().Be(2);
    }

    /// <remarks>Enabling the provider again would sign the identity in again.</remarks>
    [Fact]
    public async Task An_account_linked_to_a_disabled_provider_is_left_as_it_was_by_an_invite_enrolment()
    {
        var (name, count) = await EnrolUnderTheUsernameOfAsync(heldBy: """
            INSERT INTO oidc_providers (id, name, issuer_url, client_id, is_enabled, display_order, scopes, claim_mappings, default_roles, created_at, updated_at)
            VALUES (@providerId, 'Retired provider', @issuer, 'retired', false, 0, '["openid"]'::jsonb, '{}'::jsonb, '[]'::jsonb, now(), now());
            INSERT INTO subject_oidc_identities (id, subject_id, provider_id, oidc_subject_id, issuer, email, linked_at)
            VALUES (gen_random_uuid(), @id, @providerId, @username, @issuer, 'dormant@example.com', now());
            """);

        name.Should().Be("Dormant Account", "the enrolment must start a subject of its own");
        count.Should().Be(2);
    }

    [Fact]
    public async Task An_account_holding_an_unexpired_session_is_left_as_it_was_by_an_invite_enrolment()
    {
        var (name, count) = await EnrolUnderTheUsernameOfAsync(heldBy: """
            INSERT INTO refresh_tokens (id, token_hash, subject_id, issued_at, expires_at, created_at, updated_at)
            VALUES (gen_random_uuid(), md5(@username), @id, now(), now() + interval '7 days', now(), now());
            """);

        name.Should().Be("Dormant Account", "the enrolment must start a subject of its own");
        count.Should().Be(2);
    }

    /// <summary>
    /// Seeds an active subject with no passkey and no membership, adds what <paramref name="heldBy"/>
    /// inserts for it, then runs the invite options step under its username as someone else.
    /// </summary>
    /// <returns>The seeded subject's name afterwards, and how many subjects hold the username.</returns>
    private async Task<(string Name, long SubjectsWithTheUsername)> EnrolUnderTheUsernameOfAsync(string heldBy)
    {
        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);

        var username = $"dormant-{Guid.NewGuid():N}";
        var dormantId = Guid.CreateVersion7();
        var providerId = Guid.CreateVersion7();
        await conn.ExecuteAsync($"""
            INSERT INTO subjects (id, name, username, is_active, is_system_subject, created_at, updated_at, approval_status)
            VALUES (@id, 'Dormant Account', @username, true, false, now(), now(), 'Approved');
            {heldBy}
            """,
            ("id", dormantId), ("username", username), ("providerId", providerId),
            ("issuer", $"https://retired-{providerId:N}.example.com"));

        var token = $"invite-{Guid.NewGuid():N}";
        var viewerRoleId = await AuthTestHelpers.GetTenantRoleIdAsync(conn, tenantId, RoleSeeds.Viewer);
        await conn.PinTenantAsync(tenantId);
        await conn.ExecuteAsync("""
            INSERT INTO member_invites (id, tenant_id, created_by_subject_id, token_hash, role_ids, limit_to_24_hours, expires_at, use_count, created_at)
            VALUES (@inviteId, @tenantId, @ownerId, @hash, @roleIds::jsonb, false, now() + interval '1 day', 0, now());
            """,
            ("inviteId", Guid.CreateVersion7()), ("tenantId", tenantId),
            ("ownerId", Fixture.OwnerSubjectId), ("hash", HashUtils.Sha256Hex(token)),
            ("roleIds", $"[\"{viewerRoleId}\"]"));

        try
        {
            using var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);

            var options = await client.PostAsJsonAsync("/api/auth/passkey/invite/options",
                new { token, username, displayName = "Someone Else" });

            options.StatusCode.Should().Be(HttpStatusCode.OK, await options.Content.ReadAsStringAsync());
            return (
                await conn.ScalarAsync<string>("SELECT name FROM subjects WHERE id = @id;", ("id", dormantId)),
                await conn.ScalarAsync<long>("SELECT count(*) FROM subjects WHERE username = @username;", ("username", username)));
        }
        finally
        {
            await conn.ExecuteAsync("""
                DELETE FROM subject_oidc_identities WHERE provider_id = @providerId;
                DELETE FROM oidc_providers WHERE id = @providerId;
                DELETE FROM totp_credentials WHERE subject_id = @id;
                DELETE FROM refresh_tokens WHERE subject_id = @id;
                DELETE FROM subjects WHERE username = @username;
                """,
                ("providerId", providerId), ("id", dormantId), ("username", username));
        }
    }
}

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// The sign-in that ends a member invite and a tenant creation link, run under row-level security
/// as the API's role: the membership the invite or the creation made admits the new subject, the
/// gate owes no code, and one session and one <c>Login</c> row naming the enrolment are written.
/// </summary>
/// <remarks>
/// The options steps run over HTTP and create the enrolling subject. The complete steps need a
/// WebAuthn attestation and are not driven: the passkey's store is a row written in its place,
/// then the acceptance or the creation and <see cref="SignInCompletion"/> run in a scope shaped as
/// the complete step's request is: the inviting tenant's host for an invite, the apex for a link.
/// </remarks>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class InviteSignInCompletionIntegrationTests : ApiIntegrationTestBase
{
    private const string Ip = "203.0.113.8";
    private const string Agent = "invite-agent";

    private readonly List<Guid> _signedIn = [];
    private readonly List<Guid> _createdTenants = [];
    private readonly List<Guid> _creationLinks = [];

    public InviteSignInCompletionIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task DisposeAsync()
    {
        await using (var conn = await OpenPostgresAsync())
        {
            await conn.PinTenantAsync(Fixture.TenantId);
            await conn.ExecuteAsync("""
                DELETE FROM tenant_members WHERE tenant_id = @tenant AND subject_id = ANY(@signedIn);
                DELETE FROM tenant_members WHERE tenant_id = ANY(@created);
                DELETE FROM tenants WHERE id = ANY(@created);
                DELETE FROM tenant_creation_invites WHERE id = ANY(@links);
                DELETE FROM passkey_credentials WHERE subject_id = ANY(@signedIn);
                DELETE FROM refresh_tokens WHERE subject_id = ANY(@signedIn);
                DELETE FROM subjects WHERE id = ANY(@signedIn);
                """,
                ("tenant", Fixture.TenantId),
                ("created", _createdTenants.ToArray()), ("links", _creationLinks.ToArray()),
                ("signedIn", _signedIn.ToArray()));
        }

        await base.DisposeAsync();
    }

    [Fact]
    public async Task The_sign_in_that_completes_a_member_invite_on_the_inviting_tenants_host_signs_the_invitee_in_once()
    {
        var username = $"invitee-{Guid.NewGuid():N}"[..24];
        string token;
        await using (var conn = await OpenPostgresAsync())
            token = await SeedMemberInviteAsync(conn);

        using (var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi))
        {
            var options = await client.PostAsJsonAsync("/api/auth/passkey/invite/options",
                new { token, username, displayName = "Invitee" });
            options.StatusCode.Should().Be(HttpStatusCode.OK, await options.Content.ReadAsStringAsync());
        }

        var invitee = await EnrollingSubjectAsync(username);

        var outcome = await WithTenantScopeAsync(async services =>
        {
            var accepted = await services.GetRequiredService<IMemberInviteService>()
                .AcceptInviteAsync(token, invitee, Fixture.TenantId);
            accepted.Success.Should().BeTrue(accepted.ErrorDescription);

            return await services.GetRequiredService<SignInCompletion>().CompleteAsync(new SignInAttempt(
                invitee,
                SignInMethod.Passkey,
                new SignInAdmission.Member(Fixture.TenantId),
                new SecondFactorProof.Primary(TrustedDeviceToken: null),
                new SessionContext(DeviceDescription: "Invite Passkey", IpAddress: Ip, UserAgent: Agent),
                Enrolment: SignInEnrolment.Invite));
        });

        outcome.Should().BeOfType<SignInOutcome.SignedIn>().Which.Session.SubjectId.Should().Be(invitee);
        (await LiveSessionCountAsync(invitee)).Should().Be(1);
        (await EnrolmentRowsAsync(invitee)).Should().ContainSingle().Which.Should().Be(
            (AuthAuditEventType.Login, true, "passkey", "invite", 2L, (Guid?)Fixture.TenantId));
    }

    [Fact]
    public async Task The_sign_in_that_completes_a_creation_link_on_the_apex_signs_the_new_owner_in_once()
    {
        var username = $"founder-{Guid.NewGuid():N}"[..24];
        var slug = $"link-{Guid.NewGuid():N}"[..20];
        string token;
        await using (var conn = await OpenPostgresAsync())
            token = await SeedCreationLinkAsync(conn);

        using (var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi))
        {
            var options = await client.PostAsJsonAsync("/api/auth/passkey/tenant-invite/options",
                new { token, slug, siteName = "A Site", username, displayName = "Founder" });
            options.StatusCode.Should().Be(HttpStatusCode.OK, await options.Content.ReadAsStringAsync());
        }

        var owner = await EnrollingSubjectAsync(username);

        SignInOutcome outcome;
        Guid tenantId;
        using (var scope = Fixture.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantAccessor>().IsResolved.Should().BeFalse();
            var links = scope.ServiceProvider.GetRequiredService<ITenantCreationInviteService>();
            var claim = await links.ClaimAsync(token, CancellationToken.None);
            claim.Refusal.Should().BeNull();
            var created = await links.CreateTenantAsync(claim.Value, slug, "A Site", owner, username, CancellationToken.None);
            created.Refusal.Should().BeNull(created.Refusal?.Message);
            tenantId = created.Value!.TenantId;
            _createdTenants.Add(tenantId);

            outcome = await scope.ServiceProvider.GetRequiredService<SignInCompletion>().CompleteAsync(new SignInAttempt(
                owner,
                SignInMethod.Passkey,
                new SignInAdmission.Member(tenantId),
                new SecondFactorProof.Primary(TrustedDeviceToken: null),
                new SessionContext(DeviceDescription: "Invite Passkey", IpAddress: Ip, UserAgent: Agent),
                Enrolment: SignInEnrolment.TenantInvite));
        }

        outcome.Should().BeOfType<SignInOutcome.SignedIn>().Which.Session.SubjectId.Should().Be(owner);
        (await LiveSessionCountAsync(owner)).Should().Be(1);
        (await EnrolmentRowsAsync(owner)).Should().ContainSingle().Which.Should().Be(
            (AuthAuditEventType.Login, true, "passkey", "tenant_invite", 2L, (Guid?)null),
            "the link's requests run on the apex, which resolves no tenant for the row");
    }

    /// <summary>The subject the options step created under <paramref name="username"/>, with the passkey the complete step stores.</summary>
    private async Task<Guid> EnrollingSubjectAsync(string username)
    {
        await using var conn = await OpenPostgresAsync();
        var subjectId = await conn.ScalarAsync<Guid>("SELECT id FROM subjects WHERE username = @username;", ("username", username));

        await conn.ExecuteAsync("""
            INSERT INTO passkey_credentials (id, subject_id, credential_id, public_key, sign_count, transports, created_at)
            VALUES (gen_random_uuid(), @subject, @credential, @credential, 0, '{}', now());
            """,
            ("subject", subjectId), ("credential", Guid.NewGuid().ToByteArray()));
        _signedIn.Add(subjectId);
        return subjectId;
    }

    private async Task<string> SeedMemberInviteAsync(NpgsqlConnection conn)
    {
        var token = $"invite-{Guid.NewGuid():N}";
        var viewerRoleId = await AuthTestHelpers.GetTenantRoleIdAsync(conn, Fixture.TenantId, RoleSeeds.Viewer);
        await conn.PinTenantAsync(Fixture.TenantId);
        await conn.ExecuteAsync("""
            INSERT INTO member_invites (id, tenant_id, created_by_subject_id, token_hash, role_ids, limit_to_24_hours, expires_at, use_count, created_at)
            VALUES (@inviteId, @tenantId, @ownerId, @hash, @roleIds::jsonb, false, now() + interval '1 day', 0, now());
            """,
            ("inviteId", Guid.CreateVersion7()), ("tenantId", Fixture.TenantId),
            ("ownerId", Fixture.OwnerSubjectId), ("hash", HashUtils.Sha256Hex(token)),
            ("roleIds", $"[\"{viewerRoleId}\"]"));
        return token;
    }

    private async Task<string> SeedCreationLinkAsync(NpgsqlConnection conn)
    {
        var token = $"link-{Guid.NewGuid():N}";
        var id = Guid.CreateVersion7();
        await conn.ExecuteAsync("""
            INSERT INTO tenant_creation_invites (id, created_by_subject_id, token_hash, expires_at, use_count)
            VALUES (@id, @ownerId, @hash, now() + interval '1 day', 0);
            """,
            ("id", id), ("ownerId", Fixture.OwnerSubjectId), ("hash", HashUtils.Sha256Hex(token)));
        _creationLinks.Add(id);
        return token;
    }

    private async Task<long> LiveSessionCountAsync(Guid subjectId)
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.ScalarAsync<long>("""
            SELECT count(*) FROM refresh_tokens
            WHERE subject_id = @subject AND revoked_at IS NULL AND expires_at > now();
            """, ("subject", subjectId));
    }

    /// <summary>
    /// The rows that name an enrolment for <paramref name="subjectId"/>: event, success, method,
    /// enrolment, how many keys the details hold, and the tenant.
    /// </summary>
    private async Task<List<(string EventType, bool Success, string? Method, string? Enrolment, long Keys, Guid? TenantId)>>
        EnrolmentRowsAsync(Guid subjectId)
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.QueryAsync<(string EventType, bool Success, string? Method, string? Enrolment, long Keys, Guid? TenantId)>("""
            SELECT event_type, success, details->>'method', details->>'enrolment',
                   (SELECT count(*) FROM jsonb_object_keys(details)), tenant_id
            FROM auth_audit_log
            WHERE target_subject_id = @subject AND details->>'enrolment' IS NOT NULL;
            """, reader => (reader.GetString(0), reader.GetBoolean(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetGuid(5)), ("subject", subjectId));
    }
}

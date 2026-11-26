using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// First-run setup through the real API: the owner step resumes only the subject an earlier owner
/// step recorded for this tenant, and leaves every other subject the instance holds as it was.
/// </summary>
/// <remarks>
/// The tenant is put back to a fresh install as <see cref="SetupOwnerIntegrationTests"/> does.
/// The subject that is already there is written as the step that would leave it writes it
/// (<see cref="ISubjectRepository.CreateAsync"/>, as the invite and tenant-creation options steps
/// call it), with the rest of its shape seeded directly.
/// </remarks>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public class SetupOwnerResumeIntegrationTests : ApiIntegrationTestBase
{
    private readonly List<Guid> _deactivated = [];

    public SetupOwnerResumeIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
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

        await conn.ExecuteAsync("""
            DELETE FROM passkey_credentials WHERE subject_id IN
                (SELECT subject_id FROM tenant_members WHERE tenant_id = @tenant);
            """,
            ("tenant", Fixture.TenantId));
    }

    public override async Task DisposeAsync()
    {
        Fixture.LinkRoute.Configured = null;
        await using (var conn = await OpenPostgresAsync())
        {
            await conn.ExecuteAsync("""
                UPDATE subjects SET is_active = false
                WHERE is_active AND NOT is_system_subject AND NOT is_demo_subject;
                UPDATE subjects SET is_active = true WHERE id = ANY(@restore);
                """,
                ("restore", _deactivated.ToArray()));
        }

        await base.DisposeAsync();
    }

    [Theory]
    [InlineData("no membership")]
    [InlineData("membership without the owner role")]
    [InlineData("owner membership and a live session")]
    [InlineData("owner membership and an authenticator")]
    [InlineData("owner membership and another global role")]
    [InlineData("owner membership and a live operator recovery code")]
    [InlineData("owner membership and a confirmed address a recovery link can reach")]
    [InlineData("owner membership and the admin role, recorded by no owner step")]
    public async Task The_owner_step_leaves_a_subject_it_did_not_leave_for_this_tenant_as_it_was(string shape)
    {
        var existing = await CreateSubjectAsync("Someone Else", "someone-else");
        await ShapeAsync(existing, shape);
        var before = await SnapshotAsync(existing);
        var ownersBefore = await OwnerSubjectIdsAsync();

        using var apex = Fixture.CreateApexClient();
        await StartOwnerSetupAsync(apex, "operator", "The Operator");

        var created = (await OwnerSubjectIdsAsync()).Except(ownersBefore).Should().ContainSingle().Subject;
        created.Should().NotBe(existing);
        (await SnapshotAsync(existing)).Should().BeEquivalentTo(before);

        await StartOwnerSetupAsync(apex, "operator", "The Operator");
        (await OwnerSubjectIdsAsync()).Except(ownersBefore).Should().Equal(created);
    }

    /// <remarks>
    /// The passkey row ends the recorded subject's membership first: a member holding a passkey
    /// closes setup before the candidate is read, so only a recorded subject outside the tenant
    /// reaches the passkey clause.
    /// </remarks>
    [Theory]
    [InlineData("a live session")]
    [InlineData("a passkey and no membership")]
    [InlineData("an authenticator")]
    [InlineData("a live operator recovery code")]
    [InlineData("a confirmed address a recovery link can reach")]
    public async Task The_owner_step_does_not_resume_the_subject_it_recorded_once_that_subject_holds_a_way_in(
        string held)
    {
        using var apex = Fixture.CreateApexClient();
        await StartOwnerSetupAsync(apex, "operator", "The Operator");
        var recorded = await RecordedSetupOwnerAsync();
        recorded.Should().NotBeNull();
        (await OwnerSubjectIdsAsync()).Should().Contain(recorded!.Value);

        await GiveRecordedAsync(recorded.Value, held);
        var before = await SnapshotAsync(recorded.Value);

        await StartOwnerSetupAsync(apex, "second-operator", "The Second Operator");

        var created = await RecordedSetupOwnerAsync();
        created.Should().NotBeNull().And.NotBe(recorded.Value);
        (await OwnerSubjectIdsAsync()).Should().Contain(created!.Value);
        (await SnapshotAsync(created.Value)).Username.Should().Be("second-operator");
        (await SnapshotAsync(recorded.Value)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task An_owner_step_started_again_resumes_the_subject_the_first_one_created()
    {
        using var apex = Fixture.CreateApexClient();
        await StartOwnerSetupAsync(apex, "operator", "The Operator");
        var owner = (await OwnerSubjectIdsAsync()).Should().ContainSingle().Subject;

        await StartOwnerSetupAsync(apex, "operator", "The Operator Again");

        (await OwnerSubjectIdsAsync()).Should().Equal(owner);
        (await ActiveSubjectsNamedAsync("operator")).Should().Equal(owner);
        (await SnapshotAsync(owner)).Name.Should().Be("The Operator Again");
    }

    private async Task<Guid> CreateSubjectAsync(string name, string username)
    {
        using var scope = Fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISubjectRepository>()
            .CreateAsync(new NewSubject(name, Username: username), CancellationToken.None);
    }

    private async Task ShapeAsync(Guid subjectId, string shape)
    {
        await using var conn = await OpenPostgresAsync();
        switch (shape)
        {
            case "no membership":
                break;
            case "membership without the owner role":
                await AddMemberAsync(conn, subjectId, "viewer");
                break;
            case "owner membership and a live session":
                await AddMemberAsync(conn, subjectId, "owner");
                await conn.ExecuteAsync("""
                    INSERT INTO refresh_tokens (id, token_hash, subject_id, issued_at, expires_at, created_at, updated_at)
                    VALUES (gen_random_uuid(), md5(@subject::text), @subject, now(), now() + interval '7 days', now(), now());
                    """,
                    ("subject", subjectId));
                break;
            case "owner membership and an authenticator":
                await AddMemberAsync(conn, subjectId, "owner");
                await conn.ExecuteAsync("""
                    INSERT INTO totp_credentials (id, subject_id, secret_key, created_at)
                    VALUES (gen_random_uuid(), @subject, '\x00'::bytea, now());
                    """,
                    ("subject", subjectId));
                break;
            case "owner membership and another global role":
                await AddMemberAsync(conn, subjectId, "owner");
                await conn.ExecuteAsync("""
                    INSERT INTO subject_roles (subject_id, role_id, assigned_at)
                    SELECT @subject, id, now() FROM roles WHERE name = 'readable';
                    """,
                    ("subject", subjectId));
                break;
            case "owner membership and a live operator recovery code":
                await AddMemberAsync(conn, subjectId, "owner");
                await conn.PinTenantAsync(Fixture.TenantId);
                await conn.ExecuteAsync("""
                    INSERT INTO login_codes (id, tenant_id, subject_id, code_hash, purpose, expires_at, created_at)
                    VALUES (gen_random_uuid(), @tenant, @subject, md5(@subject::text), 'OperatorRecovery', now() + interval '1 day', now());
                    """,
                    ("tenant", Fixture.TenantId), ("subject", subjectId));
                break;
            case "owner membership and a confirmed address a recovery link can reach":
                await AddMemberAsync(conn, subjectId, "owner");
                await conn.ExecuteAsync("""
                    UPDATE subjects SET email = 'someone-else@example.test', email_confirmed_at = now() WHERE id = @subject;
                    """,
                    ("subject", subjectId));
                Fixture.LinkRoute.Configured = true;
                break;
            case "owner membership and the admin role, recorded by no owner step":
                await AddMemberAsync(conn, subjectId, "owner");
                await conn.ExecuteAsync("""
                    INSERT INTO subject_roles (subject_id, role_id, assigned_at)
                    SELECT @subject, id, now() FROM roles WHERE name = 'admin';
                    """,
                    ("subject", subjectId));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
    }

    private async Task GiveRecordedAsync(Guid subjectId, string held)
    {
        await using var conn = await OpenPostgresAsync();
        switch (held)
        {
            case "a live session":
                await conn.ExecuteAsync("""
                    INSERT INTO refresh_tokens (id, token_hash, subject_id, issued_at, expires_at, created_at, updated_at)
                    VALUES (gen_random_uuid(), md5(@subject::text), @subject, now(), now() + interval '7 days', now(), now());
                    """,
                    ("subject", subjectId));
                break;
            case "a passkey and no membership":
                await conn.PinTenantAsync(Fixture.TenantId);
                await conn.ExecuteAsync("""
                    DELETE FROM tenant_member_roles WHERE tenant_member_id IN
                        (SELECT id FROM tenant_members WHERE tenant_id = @tenant AND subject_id = @subject);
                    DELETE FROM tenant_members WHERE tenant_id = @tenant AND subject_id = @subject;
                    INSERT INTO passkey_credentials (id, subject_id, credential_id, public_key, sign_count, transports, created_at)
                    VALUES (gen_random_uuid(), @subject, @credential, @credential, 0, '{}', now());
                    """,
                    ("tenant", Fixture.TenantId),
                    ("subject", subjectId), ("credential", Guid.NewGuid().ToByteArray()));
                break;
            case "an authenticator":
                await conn.ExecuteAsync("""
                    INSERT INTO totp_credentials (id, subject_id, secret_key, created_at)
                    VALUES (gen_random_uuid(), @subject, '\x00'::bytea, now());
                    """,
                    ("subject", subjectId));
                break;
            case "a live operator recovery code":
                await conn.PinTenantAsync(Fixture.TenantId);
                await conn.ExecuteAsync("""
                    INSERT INTO login_codes (id, tenant_id, subject_id, code_hash, purpose, expires_at, created_at)
                    VALUES (gen_random_uuid(), @tenant, @subject, md5(@subject::text), 'OperatorRecovery', now() + interval '1 day', now());
                    """,
                    ("tenant", Fixture.TenantId), ("subject", subjectId));
                break;
            case "a confirmed address a recovery link can reach":
                await conn.ExecuteAsync("""
                    UPDATE subjects SET email = 'operator@example.test', email_confirmed_at = now() WHERE id = @subject;
                    """,
                    ("subject", subjectId));
                Fixture.LinkRoute.Configured = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(held), held, null);
        }
    }

    private async Task<Guid?> RecordedSetupOwnerAsync()
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.ScalarAsync<Guid?>("SELECT setup_owner_subject_id FROM tenants WHERE id = @tenant", ("tenant", Fixture.TenantId));
    }

    private async Task AddMemberAsync(NpgsqlConnection conn, Guid subjectId, string roleSlug)
    {
        await conn.PinTenantAsync(Fixture.TenantId);
        await conn.ExecuteAsync("""
            WITH member AS (
                INSERT INTO tenant_members (id, tenant_id, subject_id, username, sys_created_at, sys_updated_at)
                VALUES (gen_random_uuid(), @tenant, @subject, 'someone-else', now(), now())
                RETURNING id)
            INSERT INTO tenant_member_roles (id, tenant_member_id, tenant_role_id)
            SELECT gen_random_uuid(), member.id, r.id
            FROM member, tenant_roles r WHERE r.tenant_id = @tenant AND r.slug = @role;
            """,
            ("tenant", Fixture.TenantId),
            ("subject", subjectId), ("role", roleSlug));
    }

    private sealed record SubjectSnapshot(
        string? Name, string? Username, List<string> GlobalRoles, List<string> Memberships);

    /// <summary>The subject's names, global roles, and each membership with its username and roles.</summary>
    private async Task<SubjectSnapshot> SnapshotAsync(Guid subjectId)
    {
        await using var conn = await OpenPostgresAsync();
        var (name, username) = (await conn.QueryAsync(
            "SELECT name, username FROM subjects WHERE id = @subject",
            reader => (Name: reader.IsDBNull(0) ? null : reader.GetString(0), Username: reader.IsDBNull(1) ? null : reader.GetString(1)),
            ("subject", subjectId))).First();

        var roles = await StringsAsync(conn, """
            SELECT r.name FROM subject_roles sr JOIN roles r ON r.id = sr.role_id
            WHERE sr.subject_id = @subject ORDER BY r.name;
            """, subjectId);
        await conn.PinTenantAsync(Fixture.TenantId);
        var memberships = await StringsAsync(conn, """
            SELECT m.tenant_id::text || ':' || coalesce(m.username, '') || ':' ||
                coalesce((SELECT string_agg(tr.slug, ',' ORDER BY tr.slug)
                    FROM tenant_member_roles mr JOIN tenant_roles tr ON tr.id = mr.tenant_role_id
                    WHERE mr.tenant_member_id = m.id), '')
            FROM tenant_members m WHERE m.subject_id = @subject ORDER BY 1;
            """, subjectId);
        return new SubjectSnapshot(name, username, roles, memberships);
    }

    private async Task<List<string>> StringsAsync(NpgsqlConnection conn, string sql, Guid subjectId)
    {
        return await conn.QueryAsync(
            sql, reader => reader.GetString(0), ("subject", subjectId));
    }

    private async Task<List<Guid>> ActiveSubjectsNamedAsync(string username)
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.QueryAsync("""
            SELECT id FROM subjects
            WHERE username = @username AND is_active AND NOT is_system_subject AND NOT is_demo_subject
            ORDER BY id;
            """, reader => reader.GetGuid(0), ("username", username));
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
}

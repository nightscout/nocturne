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
/// Each options step of an anonymous enrolment creates a subject of its own and leaves every other
/// subject exactly as it was, including one an earlier attempt on the same invite, link or tenant
/// created.
/// </summary>
/// <remarks>
/// Only the options steps are driven. The complete steps need a WebAuthn attestation; that each
/// completes as the subject its own challenge names is pinned by <c>PasskeyControllerTests</c>.
/// </remarks>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class EnrolmentOwnSubjectIntegrationTests : ApiIntegrationTestBase
{
    private string _baseDomain = null!;
    private string _slugB = null!;
    private Guid _tenantB;

    public EnrolmentOwnSubjectIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _baseDomain = AuthTestHelpers.GetBaseDomain(ApiClient);
        _slugB = $"enrol-b-{Guid.NewGuid():N}"[..20];
        _tenantB = await AuthTestHelpers.SeedTenantAsync(Fixture, _slugB, "Enrolment B");
    }

    /// <summary>
    /// Also the multi-use case: two people on one invite who type the same username each get a
    /// subject of their own.
    /// </summary>
    [Fact]
    public async Task An_enrolment_started_again_on_the_same_invite_makes_a_second_subject_and_leaves_the_first_as_it_was()
    {
        await using var conn = await OpenPostgresAsync();
        var username = NewUsername();
        var invite = await SeedMemberInviteAsync(conn, Fixture.TenantId);

        await InviteOptionsAsync(Fixture.TenantId, invite, username, "First Try");
        var first = (await SubjectIdsWithUsernameAsync(conn, username)).Single();
        var before = await SnapshotAsync(conn, first);
        await InviteOptionsAsync(Fixture.TenantId, invite, username, "Second Try");

        (await SnapshotAsync(conn, first)).Should().Be(before);
        var second = (await SubjectIdsWithUsernameAsync(conn, username)).Should().HaveCount(2).And.Subject.Single(id => id != first);
        (await NameOfAsync(conn, second)).Should().Be("Second Try");
    }

    [Fact]
    public async Task An_enrolment_started_again_on_the_same_creation_link_makes_a_second_subject_and_leaves_the_first_as_it_was()
    {
        await using var conn = await OpenPostgresAsync();
        var username = NewUsername();
        var link = await SeedCreationLinkAsync(conn);

        await CreationLinkOptionsAsync(link, username, "First Try");
        var first = (await SubjectIdsWithUsernameAsync(conn, username)).Single();
        var before = await SnapshotAsync(conn, first);
        await CreationLinkOptionsAsync(link, username, "Second Try");

        (await SnapshotAsync(conn, first)).Should().Be(before);
        var second = (await SubjectIdsWithUsernameAsync(conn, username)).Should().HaveCount(2).And.Subject.Single(id => id != first);
        (await NameOfAsync(conn, second)).Should().Be("Second Try");
    }

    /// <summary>
    /// The first attempt never registered a passkey, so it is no pending request and the second is
    /// not refused as a conflict.
    /// </summary>
    [Fact]
    public async Task An_access_request_started_again_on_the_same_tenant_makes_a_second_subject_and_leaves_the_first_as_it_was()
    {
        await using var conn = await OpenPostgresAsync();
        var displayName = NewDisplayName();

        await AccessRequestOptionsAsync(_slugB, displayName, "first");
        var first = (await SubjectIdsNamedAsync(conn, displayName)).Single();
        var before = await SnapshotAsync(conn, first);
        await AccessRequestOptionsAsync(_slugB, displayName, "second");

        (await SnapshotAsync(conn, first)).Should().Be(before);
        (await SubjectIdsNamedAsync(conn, displayName)).Should().HaveCount(2);
    }

    [Fact]
    public async Task An_enrolment_on_another_invite_to_the_same_tenant_leaves_the_first_invites_subject_as_it_was()
    {
        await using var conn = await OpenPostgresAsync();
        var username = NewUsername();
        var first = await SeedMemberInviteAsync(conn, Fixture.TenantId);
        var second = await SeedMemberInviteAsync(conn, Fixture.TenantId);

        await InviteOptionsAsync(Fixture.TenantId, first, username, "First Person");
        var firstSubject = (await SubjectIdsWithUsernameAsync(conn, username)).Single();
        var before = await SnapshotAsync(conn, firstSubject);

        await InviteOptionsAsync(Fixture.TenantId, second, username, "Second Person");

        (await SnapshotAsync(conn, firstSubject)).Should().Be(before);
        (await SubjectIdsWithUsernameAsync(conn, username)).Should().HaveCount(2, "the second invite starts a subject of its own");
    }

    [Fact]
    public async Task An_enrolment_on_another_tenants_invite_leaves_the_first_invites_subject_as_it_was()
    {
        await using var conn = await OpenPostgresAsync();
        var username = NewUsername();
        var first = await SeedMemberInviteAsync(conn, Fixture.TenantId);
        var second = await SeedMemberInviteAsync(conn, _tenantB);

        await InviteOptionsAsync(Fixture.TenantId, first, username, "First Person");
        var firstSubject = (await SubjectIdsWithUsernameAsync(conn, username)).Single();
        var before = await SnapshotAsync(conn, firstSubject);

        await InviteOptionsAsync(_tenantB, second, username, "Second Person");

        (await SnapshotAsync(conn, firstSubject)).Should().Be(before);
        (await SubjectIdsWithUsernameAsync(conn, username)).Should().HaveCount(2, "the second invite starts a subject of its own");
    }

    [Fact]
    public async Task A_member_invite_enrolment_leaves_a_creation_links_subject_as_it_was()
    {
        await using var conn = await OpenPostgresAsync();
        var username = NewUsername();
        var link = await SeedCreationLinkAsync(conn);
        var invite = await SeedMemberInviteAsync(conn, Fixture.TenantId);

        await CreationLinkOptionsAsync(link, username, "Site Maker");
        var linkSubject = (await SubjectIdsWithUsernameAsync(conn, username)).Single();
        var before = await SnapshotAsync(conn, linkSubject);

        await InviteOptionsAsync(Fixture.TenantId, invite, username, "Invited Member");

        (await SnapshotAsync(conn, linkSubject)).Should().Be(before);
        (await SubjectIdsWithUsernameAsync(conn, username)).Should().HaveCount(2, "the invite starts a subject of its own");
    }

    [Fact]
    public async Task A_creation_link_enrolment_leaves_a_member_invites_subject_as_it_was()
    {
        await using var conn = await OpenPostgresAsync();
        var username = NewUsername();
        var invite = await SeedMemberInviteAsync(conn, Fixture.TenantId);
        var link = await SeedCreationLinkAsync(conn);

        await InviteOptionsAsync(Fixture.TenantId, invite, username, "Invited Member");
        var inviteSubject = (await SubjectIdsWithUsernameAsync(conn, username)).Single();
        var before = await SnapshotAsync(conn, inviteSubject);

        await CreationLinkOptionsAsync(link, username, "Site Maker");

        (await SnapshotAsync(conn, inviteSubject)).Should().Be(before);
        (await SubjectIdsWithUsernameAsync(conn, username)).Should().HaveCount(2, "the link starts a subject of its own");
    }

    /// <summary>
    /// A subject no enrolment created, with no factor and no membership, such as the owner of a
    /// deleted tenant who never enrolled a passkey: it keeps its global role, the platform-admin
    /// flag, its recovery codes and its last sign-in.
    /// </summary>
    [Theory]
    [InlineData("invite")]
    [InlineData("creation link")]
    public async Task An_enrolment_leaves_a_subject_no_enrolment_created_as_it_was(string flow)
    {
        await using var conn = await OpenPostgresAsync();
        var username = NewUsername();
        var leftId = Guid.CreateVersion7();
        await conn.ExecuteAsync("""
            INSERT INTO subjects (id, name, username, is_active, is_system_subject, is_platform_admin, last_login_at, created_at, updated_at, approval_status)
            VALUES (@id, 'Left Behind', @username, true, false, true, now() - interval '30 days', now(), now(), 'Approved');
            INSERT INTO subject_roles (subject_id, role_id, assigned_at)
            SELECT @id, id, now() FROM roles WHERE name = 'admin';
            INSERT INTO recovery_codes (id, subject_id, code_hash, created_at)
            VALUES (gen_random_uuid(), @id, md5(@username), now());
            """,
            ("id", leftId), ("username", username));
        var before = await SnapshotAsync(conn, leftId);

        if (flow == "invite")
            await InviteOptionsAsync(Fixture.TenantId, await SeedMemberInviteAsync(conn, Fixture.TenantId), username, "Someone Else");
        else
            await CreationLinkOptionsAsync(await SeedCreationLinkAsync(conn), username, "Someone Else");

        (await SnapshotAsync(conn, leftId)).Should().Be(before);
        (await SubjectIdsWithUsernameAsync(conn, username)).Should().HaveCount(2, "the enrolment starts a subject of its own");
    }

    [Fact]
    public async Task An_access_request_on_another_tenant_leaves_the_first_requests_subject_as_it_was()
    {
        await using var conn = await OpenPostgresAsync();
        var displayName = NewDisplayName();

        await AccessRequestOptionsAsync(ApiIntegrationTestFixture.TenantSlug, displayName, "for the first site");
        var firstSubject = (await SubjectIdsNamedAsync(conn, displayName)).Single();
        var before = await SnapshotAsync(conn, firstSubject);

        await AccessRequestOptionsAsync(_slugB, displayName, "for the second site");

        (await SnapshotAsync(conn, firstSubject)).Should().Be(before);
        (await SubjectIdsNamedAsync(conn, displayName)).Should().HaveCount(2, "the second request starts a subject of its own");
    }

    private static string NewUsername() => $"enrol-{Guid.NewGuid():N}"[..24];

    private static string NewDisplayName() => $"Visitor {Guid.NewGuid():N}"[..20];

    private string SlugOf(Guid tenantId) =>
        tenantId == _tenantB ? _slugB : ApiIntegrationTestFixture.TenantSlug;

    /// <summary>
    /// An invite the fixture owner made, who is made a member of <paramref name="tenantId"/> when it has
    /// no owner of its own: an invite is live only while its maker is a member in force.
    /// </summary>
    private async Task<string> SeedMemberInviteAsync(NpgsqlConnection conn, Guid tenantId)
    {
        var token = $"invite-{Guid.NewGuid():N}";
        await conn.PinTenantAsync(tenantId);
        var viewerRoleId = await AuthTestHelpers.GetTenantRoleIdAsync(conn, tenantId, RoleSeeds.Viewer);
        await conn.ExecuteAsync("""
            INSERT INTO tenant_members (id, tenant_id, subject_id, direct_permissions, sys_created_at, sys_updated_at, limit_to_24_hours)
            SELECT @memberId, @tenantId, @ownerId, @permissions::jsonb, now(), now(), false
            WHERE NOT EXISTS (SELECT 1 FROM tenant_members WHERE tenant_id = @tenantId AND subject_id = @ownerId);
            INSERT INTO member_invites (id, tenant_id, created_by_subject_id, token_hash, role_ids, limit_to_24_hours, expires_at, use_count, created_at)
            VALUES (@inviteId, @tenantId, @ownerId, @hash, @roleIds::jsonb, false, now() + interval '1 day', 0, now());
            """,
            ("memberId", Guid.CreateVersion7()), ("permissions", $"[\"{Scope.FullAccess}\"]"),
            ("inviteId", Guid.CreateVersion7()), ("tenantId", tenantId),
            ("ownerId", Fixture.OwnerSubjectId), ("hash", HashUtils.Sha256Hex(token)),
            ("roleIds", $"[\"{viewerRoleId}\"]"));
        return token;
    }

    private async Task<string> SeedCreationLinkAsync(NpgsqlConnection conn)
    {
        var token = $"link-{Guid.NewGuid():N}";
        await conn.ExecuteAsync("""
            INSERT INTO tenant_creation_invites (id, created_by_subject_id, token_hash, expires_at, use_count)
            VALUES (@id, @ownerId, @hash, now() + interval '1 day', 0);
            """,
            ("id", Guid.CreateVersion7()), ("ownerId", Fixture.OwnerSubjectId), ("hash", HashUtils.Sha256Hex(token)));
        return token;
    }

    private async Task InviteOptionsAsync(Guid tenantId, string token, string username, string displayName)
    {
        using var client = AuthTestHelpers.CreateTenantClient(Fixture, SlugOf(tenantId), _baseDomain);
        var response = await client.PostAsJsonAsync("/api/auth/passkey/invite/options",
            new { token, username, displayName });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task CreationLinkOptionsAsync(string token, string username, string displayName)
    {
        using var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);
        var response = await client.PostAsJsonAsync("/api/auth/passkey/tenant-invite/options",
            new { token, slug = $"site-{Guid.NewGuid():N}"[..20], siteName = "A Site", username, displayName });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task AccessRequestOptionsAsync(string slug, string displayName, string message)
    {
        using var client = AuthTestHelpers.CreateTenantClient(Fixture, slug, _baseDomain);
        var response = await client.PostAsJsonAsync("/api/auth/passkey/access-request/options",
            new { displayName, message });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<List<Guid>> SubjectIdsWithUsernameAsync(NpgsqlConnection conn, string username) =>
        await IdsAsync(conn, "SELECT id FROM subjects WHERE username = @value ORDER BY id;", username);

    private static async Task<List<Guid>> SubjectIdsNamedAsync(NpgsqlConnection conn, string name) =>
        await IdsAsync(conn, "SELECT id FROM subjects WHERE name = @value ORDER BY id;", name);

    private static async Task<List<Guid>> IdsAsync(NpgsqlConnection conn, string sql, string value)
    {
        return await conn.QueryAsync(sql, reader => reader.GetGuid(0), ("value", value));
    }

    private static async Task<string> NameOfAsync(NpgsqlConnection conn, Guid subjectId)
    {
        return await conn.ScalarAsync<string>("SELECT name FROM subjects WHERE id = @id;", ("id", subjectId));
    }

    /// <summary>The subject's row with what it holds: its roles, recovery codes and passkeys.</summary>
    private static async Task<string> SnapshotAsync(NpgsqlConnection conn, Guid subjectId)
    {
        return await conn.ScalarAsync<string>("""
            SELECT row_to_json(s)::text
                || (SELECT count(*) FROM subject_roles WHERE subject_id = s.id)::text
                || (SELECT count(*) FROM recovery_codes WHERE subject_id = s.id)::text
                || (SELECT count(*) FROM passkey_credentials WHERE subject_id = s.id)::text
            FROM subjects s WHERE s.id = @id;
            """, ("id", subjectId));
    }
}

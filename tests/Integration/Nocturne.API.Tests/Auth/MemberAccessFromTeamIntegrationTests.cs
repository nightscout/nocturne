using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// The member page's actions over HTTP for a member who holds a team's reason, written straight to the
/// tables with the staff rows it stands on: a member who holds only the team's is refused each
/// change with 409 <c>member_access_from_team</c>, and removing a member who holds both takes only
/// the direct reason, so the list then shows the team's alone. A caller who holds only a team's reason is
/// refused what they would give anyone with 409 <c>caller_access_from_team</c>, a code of its own.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class MemberAccessFromTeamIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private const string MemberCode = "member_access_from_team";
    private const string CallerCode = "caller_access_from_team";

    [Fact]
    public async Task A_member_with_only_a_teams_access_is_refused_every_change_and_a_mixed_member_loses_only_the_direct_reason()
    {
        var (_, ownerToken) = await SeedSubjectAsync("Team Reason Owner", owner: true);
        var (staff, _) = await SeedSubjectAsync("Team Reason Staff", [Scope.GlucoseRead]);
        var (mixed, _) = await SeedSubjectAsync("Team Reason Mixed", [Scope.GlucoseRead]);
        var (caller, callerToken) = await SeedSubjectAsync("Team Reason Caller", owner: true);
        var team = await TeamReasonsAsync(staff, mixed, caller);
        using var owner = AuthTestHelpers.CreateBearerClient(Fixture, ownerToken);

        var listed = await MembersAsync(owner);
        var staffEntry = listed.Single(m => m.GetProperty("subjectId").GetGuid() == staff);
        staffEntry.GetProperty("hasDirect").GetBoolean().Should().BeFalse();
        var reason = staffEntry.GetProperty("reasons").EnumerateArray().Should().ContainSingle().Subject;
        reason.GetProperty("kind").GetString().Should().Be("Team");
        reason.GetProperty("teamId").GetGuid().Should().Be(team);
        reason.GetProperty("teamName").GetString().Should().Be("Clinic");
        var staffMember = staffEntry.GetProperty("id").GetGuid();
        var viewer = await RoleIdAsync(RoleSeeds.Viewer);

        await ShouldRefuseWithAsync(MemberCode, await owner.PutAsJsonAsync(
            $"/api/v4/member-invites/members/{staffMember}/roles", new { roleIds = new[] { viewer } }));
        await ShouldRefuseWithAsync(MemberCode, await owner.PutAsJsonAsync(
            $"/api/v4/member-invites/members/{staffMember}/permissions", new { directPermissions = new[] { Scope.GlucoseRead } }));
        await ShouldRefuseWithAsync(MemberCode, await owner.PutAsJsonAsync(
            $"/api/v4/member-invites/members/{staffMember}/limit-to-24-hours", new { limitTo24Hours = true }));
        await ShouldRefuseWithAsync(MemberCode, await owner.PutAsJsonAsync(
            $"/api/v4/member-invites/members/{staffMember}/access-end", new { accessEndsAt = DateTimeOffset.UtcNow.AddDays(30) }));
        await ShouldRefuseWithAsync(MemberCode, await owner.DeleteAsync($"/api/v4/member-invites/members/{staff}"));

        using var teamOnly = AuthTestHelpers.CreateBearerClient(Fixture, callerToken);
        await ShouldRefuseWithAsync(CallerCode, await teamOnly.PutAsJsonAsync(
            $"/api/v4/member-invites/members/{staffMember}/roles", new { roleIds = new[] { viewer } }));
        await ShouldRefuseWithAsync(CallerCode, await teamOnly.PutAsJsonAsync(
            $"/api/v4/member-invites/members/{staffMember}/limit-to-24-hours", new { limitTo24Hours = true }));
        await ShouldRefuseWithAsync(CallerCode, await teamOnly.PostAsync(
            $"/api/v4/member-invites/team-access/{team}/staff/{staff}/approve", null));
        await ShouldRefuseWithAsync(CallerCode, await teamOnly.PostAsync(
            $"/api/v4/member-invites/team-access/{team}/staff/{staff}/decline", null));
        await ShouldRefuseWithAsync(CallerCode, await teamOnly.PutAsJsonAsync(
            $"/api/v4/member-invites/team-access/{team}/staff-rule", new { teamManagesStaff = false }));
        await ShouldRefuseWithAsync(CallerCode, await teamOnly.PutAsJsonAsync(
            $"/api/v4/member-invites/team-access/{team}", new { roleIds = new[] { viewer }, limitTo24Hours = false, accessEndsAt = (DateTimeOffset?)null }));

        (await owner.DeleteAsync($"/api/v4/member-invites/members/{mixed}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = await MembersAsync(owner);
        after.Single(m => m.GetProperty("subjectId").GetGuid() == staff).GetProperty("reasons").GetArrayLength().Should().Be(1);
        var mixedAfter = after.Single(m => m.GetProperty("subjectId").GetGuid() == mixed);
        mixedAfter.GetProperty("hasDirect").GetBoolean().Should().BeFalse();
        mixedAfter.GetProperty("reasons").EnumerateArray().Select(r => r.GetProperty("kind").GetString()).Should().Equal("Team");
    }

    private static async Task<List<JsonElement>> MembersAsync(HttpClient client) =>
        [.. (await client.GetFromJsonAsync<JsonElement>("/api/v4/member-invites/members")).EnumerateArray()];

    private static async Task ShouldRefuseWithAsync(string code, HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be(code);
    }

    private async Task<Guid> RoleIdAsync(string slug)
    {
        await using var conn = await OpenPostgresAsync();
        return await AuthTestHelpers.GetTenantRoleIdAsync(conn, await AuthTestHelpers.GetTenantIdAsync(conn), slug);
    }

    /// <summary>
    /// Gives a new team access to the tenant giving caretaker, with the three subjects as its approved staff,
    /// makes <paramref name="staff"/> hold only its reason and <paramref name="mixed"/> hold it beside a direct reason that gives what their row
    /// gave, with each row the sum of its reasons; <paramref name="caller"/> holds only its reason too, though
    /// their reason is written with the owner role, which an access never gives, so what they are refused is that
    /// they hold no direct reason. Returns the team.
    /// </summary>
    private async Task<Guid> TeamReasonsAsync(Guid staff, Guid mixed, Guid caller)
    {
        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var caretaker = await AuthTestHelpers.GetTenantRoleIdAsync(conn, tenantId, RoleSeeds.Caretaker);
        var team = Guid.CreateVersion7();
        var access = Guid.CreateVersion7();
        await conn.PinTenantAsync(tenantId);
        await conn.ExecuteAsync("""
            INSERT INTO teams (id, name) VALUES (@team, 'Clinic');
            INSERT INTO team_tenant_access (id, team_id, tenant_id, limit_to_24_hours) VALUES (@access, @team, @tenant, false);
            INSERT INTO team_tenant_access_roles (id, team_tenant_access_id, tenant_role_id) VALUES (gen_random_uuid(), @access, @caretaker);
            INSERT INTO team_members (id, team_id, subject_id, kind, limit_to_24_hours)
            SELECT gen_random_uuid(), @team, s, 'staff', false FROM unnest(ARRAY[@staff, @mixed, @caller]) AS s;
            INSERT INTO team_site_staff (id, team_tenant_access_id, team_member_id, status)
            SELECT gen_random_uuid(), @access, tm.id, 'approved' FROM team_members tm WHERE tm.team_id = @team;

            DELETE FROM tenant_member_reasons r USING tenant_members m
             WHERE r.tenant_member_id = m.id AND m.tenant_id = @tenant AND m.subject_id IN (@staff, @mixed, @caller);
            INSERT INTO tenant_member_reasons (id, tenant_member_id, kind, source_id, direct_permissions, limit_to_24_hours)
            SELECT gen_random_uuid(), m.id, 'direct', NULL, m.direct_permissions, m.limit_to_24_hours
              FROM tenant_members m WHERE m.tenant_id = @tenant AND m.subject_id = @mixed;
            INSERT INTO tenant_member_reasons (id, tenant_member_id, kind, source_id, limit_to_24_hours)
            SELECT gen_random_uuid(), m.id, 'team', @access, false
              FROM tenant_members m WHERE m.tenant_id = @tenant AND m.subject_id IN (@staff, @mixed, @caller);
            INSERT INTO tenant_member_reason_roles (id, tenant_member_reason_id, tenant_role_id)
            SELECT gen_random_uuid(), r.id, @caretaker FROM tenant_member_reasons r WHERE r.source_id = @access;
            UPDATE tenant_member_reason_roles rr SET tenant_role_id = @owner
              FROM tenant_member_reasons r JOIN tenant_members m ON m.id = r.tenant_member_id
             WHERE rr.tenant_member_reason_id = r.id AND r.source_id = @access AND m.subject_id = @caller;

            UPDATE tenant_members SET direct_permissions = NULL WHERE tenant_id = @tenant AND subject_id = @staff;
            DELETE FROM tenant_member_roles mr USING tenant_members m
             WHERE mr.tenant_member_id = m.id AND m.tenant_id = @tenant AND m.subject_id IN (@staff, @mixed);
            INSERT INTO tenant_member_roles (id, tenant_member_id, tenant_role_id)
            SELECT gen_random_uuid(), m.id, @caretaker FROM tenant_members m WHERE m.tenant_id = @tenant AND m.subject_id IN (@staff, @mixed);
            """, ("tenant", tenantId), ("team", team), ("access", access), ("staff", staff), ("mixed", mixed), ("caller", caller), ("caretaker", caretaker), ("owner", await AuthTestHelpers.GetTenantRoleIdAsync(conn, tenantId, RoleSeeds.Owner)));
        return team;
    }

    private async Task<(Guid Subject, string AccessToken)> SeedSubjectAsync(
        string name, IReadOnlyList<string>? permissions = null, bool owner = false)
    {
        using (var provisioning = CreateAuthenticatedClient())
            (await provisioning.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var seeded = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, name, permissions);
        if (owner)
        {
            await conn.ExecuteAsync("""
                INSERT INTO tenant_member_roles (id, tenant_member_id, tenant_role_id)
                SELECT gen_random_uuid(), id, @roleId FROM tenant_members
                 WHERE tenant_id = @tenantId AND subject_id = @subjectId;
                """, ("roleId", await AuthTestHelpers.GetTenantRoleIdAsync(conn, tenantId, RoleSeeds.Owner)), ("tenantId", tenantId), ("subjectId", seeded.SubjectId));
        }

        return seeded;
    }
}

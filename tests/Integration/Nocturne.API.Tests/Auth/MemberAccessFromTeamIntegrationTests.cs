using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// The member page's actions over HTTP for a member who holds a team's reason, written straight to the
/// tables since no product path gives one yet: a member who holds only the team's is refused each
/// change with 409 <c>member_access_from_team</c>, and removing a member who holds both takes only
/// the direct reason, so the list then shows the team's alone.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class MemberAccessFromTeamIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private const string Code = "member_access_from_team";

    [Fact]
    public async Task A_member_with_only_a_teams_access_is_refused_every_change_and_a_mixed_member_loses_only_the_direct_reason()
    {
        var (_, ownerToken) = await SeedSubjectAsync("Team Reason Owner", owner: true);
        var (staff, _) = await SeedSubjectAsync("Team Reason Staff", [Scope.GlucoseRead]);
        var (mixed, _) = await SeedSubjectAsync("Team Reason Mixed", [Scope.GlucoseRead]);
        var team = await TeamReasonsAsync(staff, mixed);
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

        await ShouldBeAccessFromTeamAsync(await owner.PutAsJsonAsync(
            $"/api/v4/member-invites/members/{staffMember}/roles", new { roleIds = new[] { viewer } }));
        await ShouldBeAccessFromTeamAsync(await owner.PutAsJsonAsync(
            $"/api/v4/member-invites/members/{staffMember}/permissions", new { directPermissions = new[] { Scope.GlucoseRead } }));
        await ShouldBeAccessFromTeamAsync(await owner.PutAsJsonAsync(
            $"/api/v4/member-invites/members/{staffMember}/limit-to-24-hours", new { limitTo24Hours = true }));
        await ShouldBeAccessFromTeamAsync(await owner.PutAsJsonAsync(
            $"/api/v4/member-invites/members/{staffMember}/access-end", new { accessEndsAt = DateTimeOffset.UtcNow.AddDays(30) }));
        await ShouldBeAccessFromTeamAsync(await owner.DeleteAsync($"/api/v4/member-invites/members/{staff}"));

        (await owner.DeleteAsync($"/api/v4/member-invites/members/{mixed}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = await MembersAsync(owner);
        after.Single(m => m.GetProperty("subjectId").GetGuid() == staff).GetProperty("reasons").GetArrayLength().Should().Be(1);
        var mixedAfter = after.Single(m => m.GetProperty("subjectId").GetGuid() == mixed);
        mixedAfter.GetProperty("hasDirect").GetBoolean().Should().BeFalse();
        mixedAfter.GetProperty("reasons").EnumerateArray().Select(r => r.GetProperty("kind").GetString()).Should().Equal("Team");
    }

    private static async Task<List<JsonElement>> MembersAsync(HttpClient client) =>
        [.. (await client.GetFromJsonAsync<JsonElement>("/api/v4/member-invites/members")).EnumerateArray()];

    private static async Task ShouldBeAccessFromTeamAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be(Code);
    }

    private async Task<Guid> RoleIdAsync(string slug)
    {
        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        return await AuthTestHelpers.GetTenantRoleIdAsync(conn, await AuthTestHelpers.GetTenantIdAsync(conn), slug);
    }

    /// <summary>
    /// Gives a new team access to the tenant giving caretaker, makes <paramref name="staff"/> hold only
    /// its reason and <paramref name="mixed"/> hold it beside a direct reason that gives what their row
    /// gave, with each row the sum of its reasons; returns the team.
    /// </summary>
    private async Task<Guid> TeamReasonsAsync(Guid staff, Guid mixed)
    {
        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var caretaker = await AuthTestHelpers.GetTenantRoleIdAsync(conn, tenantId, RoleSeeds.Caretaker);
        var team = Guid.CreateVersion7();
        var access = Guid.CreateVersion7();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT set_config('app.current_tenant_id', @tenant::text, false);
            INSERT INTO teams (id, name) VALUES (@team, 'Clinic');
            INSERT INTO team_tenant_access (id, team_id, tenant_id, limit_to_24_hours) VALUES (@access, @team, @tenant, false);

            DELETE FROM tenant_member_reasons r USING tenant_members m
             WHERE r.tenant_member_id = m.id AND m.tenant_id = @tenant AND m.subject_id IN (@staff, @mixed);
            INSERT INTO tenant_member_reasons (id, tenant_member_id, kind, source_id, direct_permissions, limit_to_24_hours)
            SELECT gen_random_uuid(), m.id, 'direct', NULL, m.direct_permissions, m.limit_to_24_hours
              FROM tenant_members m WHERE m.tenant_id = @tenant AND m.subject_id = @mixed;
            INSERT INTO tenant_member_reasons (id, tenant_member_id, kind, source_id, limit_to_24_hours)
            SELECT gen_random_uuid(), m.id, 'team', @access, false
              FROM tenant_members m WHERE m.tenant_id = @tenant AND m.subject_id IN (@staff, @mixed);
            INSERT INTO tenant_member_reason_roles (id, tenant_member_reason_id, tenant_role_id)
            SELECT gen_random_uuid(), r.id, @caretaker FROM tenant_member_reasons r WHERE r.source_id = @access;

            UPDATE tenant_members SET direct_permissions = NULL WHERE tenant_id = @tenant AND subject_id = @staff;
            DELETE FROM tenant_member_roles mr USING tenant_members m
             WHERE mr.tenant_member_id = m.id AND m.tenant_id = @tenant AND m.subject_id IN (@staff, @mixed);
            INSERT INTO tenant_member_roles (id, tenant_member_id, tenant_role_id)
            SELECT gen_random_uuid(), m.id, @caretaker FROM tenant_members m WHERE m.tenant_id = @tenant AND m.subject_id IN (@staff, @mixed);
            """;
        cmd.Parameters.AddWithValue("tenant", tenantId);
        cmd.Parameters.AddWithValue("team", team);
        cmd.Parameters.AddWithValue("access", access);
        cmd.Parameters.AddWithValue("staff", staff);
        cmd.Parameters.AddWithValue("mixed", mixed);
        cmd.Parameters.AddWithValue("caretaker", caretaker);
        await cmd.ExecuteNonQueryAsync();
        return team;
    }

    private async Task<(Guid Subject, string AccessToken)> SeedSubjectAsync(
        string name, IReadOnlyList<string>? permissions = null, bool owner = false)
    {
        using (var provisioning = CreateAuthenticatedClient())
            (await provisioning.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var seeded = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, name, permissions);
        if (owner)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenant_member_roles (id, tenant_member_id, tenant_role_id)
                SELECT gen_random_uuid(), id, @roleId FROM tenant_members
                 WHERE tenant_id = @tenantId AND subject_id = @subjectId;
                """;
            cmd.Parameters.AddWithValue("roleId", await AuthTestHelpers.GetTenantRoleIdAsync(conn, tenantId, RoleSeeds.Owner));
            cmd.Parameters.AddWithValue("tenantId", tenantId);
            cmd.Parameters.AddWithValue("subjectId", seeded.SubjectId);
            await cmd.ExecuteNonQueryAsync();
        }

        return seeded;
    }
}

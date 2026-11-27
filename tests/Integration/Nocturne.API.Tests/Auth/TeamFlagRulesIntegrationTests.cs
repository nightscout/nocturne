using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// A team's flag rules over HTTP on the apex: written by its managers, read by its staff, not found for anyone else and
/// for a staff member's write, each change kept in the team's history and never in a site's member history, and judged
/// for the team's staff on the overview's team and full reads but not for its managers.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class TeamFlagRulesIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private static readonly object TimeBelow = new { dataset = "glucose_statistics", field = "timeBelow", days = 14, comparison = "Above", value = 4 };

    private static object StaleReading(int hours) =>
        new { dataset = "current_status", field = "hoursSinceReading", comparison = "Below", value = hours };

    [Fact]
    public async Task A_teams_rules_are_written_by_its_managers_read_by_its_staff_and_not_found_for_anyone_else()
    {
        var (manager, _) = await SeedSubjectAsync("Rules Manager");
        var staff = await SeedBareSubjectAsync("Rules Nurse");
        var stranger = await SeedBareSubjectAsync("Rules Stranger");
        using var managerClient = await SessionAtApexAsync(manager);
        using var staffClient = await SessionAtApexAsync(staff);
        using var strangerClient = await SessionAtApexAsync(stranger);
        var teamId = await TeamWithStaffAsync(managerClient, staff);
        var rules = $"/api/v4/me/teams/{teamId}/flag-rules";

        var created = await managerClient.PostAsJsonAsync(rules, TimeBelow);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var listed = await staffClient.GetFromJsonAsync<JsonElement>(rules);
        listed.GetProperty("limit").GetInt32().Should().Be(FlagRuleLimits.PerTeam);
        listed.GetProperty("rules").EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("rule").GetProperty("id").GetGuid().Should().Be(id);
        (await staffClient.PostAsJsonAsync(rules, TimeBelow)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await staffClient.PutAsJsonAsync($"{rules}/{id}", TimeBelow)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await staffClient.DeleteAsync($"{rules}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await strangerClient.GetAsync(rules)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await strangerClient.PostAsJsonAsync(rules, TimeBelow)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await managerClient.PostAsync(rules, new StringContent(
            """{ "dataset": "glucose_statistics", "field": "timeBelow", "days": 91, "comparison": "Above", "value": 4 }""",
            MediaTypeHeaderValue.Parse("application/json")))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await managerClient.PutAsJsonAsync($"{rules}/{id}",
            new { dataset = "glucose_statistics", field = "timeBelow", days = 14, comparison = "Above", value = 9 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await managerClient.DeleteAsync($"{rules}/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await staffClient.GetFromJsonAsync<JsonElement>(rules)).GetProperty("rules").GetArrayLength().Should().Be(0);

        await using var conn = await OpenPostgresAsync();
        var history = await conn.QueryAsync("""
            SELECT event_type, actor_subject_id, coalesce(details->'old_flag_rule'->>'value', '-'), details->'flag_rule'->>'value'
              FROM auth_audit_log WHERE team_id = @team AND event_type LIKE 'team_flag_rule_%' ORDER BY id
            """, r => (r.GetString(0), r.GetGuid(1), r.GetString(2), r.GetString(3)), ("team", teamId));
        history.Should().Equal(
            ("team_flag_rule_created", manager, "-", "4"),
            ("team_flag_rule_changed", manager, "4", "9"),
            ("team_flag_rule_deleted", manager, "-", "9"));
    }

    [Fact]
    public async Task A_staff_members_team_read_gives_the_teams_reasons_and_a_full_read_gives_their_own_beside_them_once()
    {
        var (manager, _) = await SeedSubjectAsync("Overview Manager");
        var (owner, ownerToken) = await SeedSubjectAsync("Overview Owner", owner: true);
        var staff = await SeedBareSubjectAsync("Overview Nurse");
        using var managerClient = await SessionAtApexAsync(manager);
        using var ownerClient = await SessionAtApexAsync(owner);
        using var ownerOnSite = AuthTestHelpers.CreateBearerClient(Fixture, ownerToken);
        using var staffClient = await SessionAtApexAsync(staff);
        var teamId = await TeamWithStaffAsync(managerClient, staff);
        var teamName = (await managerClient.GetFromJsonAsync<JsonElement>($"/api/v4/me/teams/{teamId}")).GetProperty("name").GetString();
        await ApproveAsync(managerClient, ownerClient, teamId);
        var at = DateTimeOffset.UtcNow.AddMinutes(-30);
        (await AuthenticatedClient.PostAsJsonAsync("/api/v1/entries", new[]
        {
            new { type = "sgv", sgv = 120, direction = "Flat", date = at.ToUnixTimeMilliseconds(), dateString = at.UtcDateTime.ToString("o") },
        })).IsSuccessStatusCode.Should().BeTrue();
        (await managerClient.PostAsJsonAsync($"/api/v4/me/teams/{teamId}/flag-rules", StaleReading(100_000)))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await staffClient.PostAsJsonAsync("/api/v4/me/flag-rules", StaleReading(100_000))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await staffClient.PostAsJsonAsync("/api/v4/me/flag-rules", StaleReading(99_999))).StatusCode.Should().Be(HttpStatusCode.Created);

        var team = await staffClient.GetFromJsonAsync<JsonElement>("/api/v4/me/tenants/overview?rules=Team");
        var all = await staffClient.GetFromJsonAsync<JsonElement>("/api/v4/me/tenants/overview?rules=All");
        var unnamed = await staffClient.GetFromJsonAsync<JsonElement>("/api/v4/me/tenants/overview");
        var managed = await managerClient.GetFromJsonAsync<JsonElement>("/api/v4/me/tenants/overview?rules=Team");

        RuleReasons(team).Should().Equal(("Team", teamName, 100_000.0));
        RuleTeams(team).Should().Equal((teamId, teamName));
        RuleReasons(all).Should().BeEquivalentTo([("Team", teamName, 100_000.0), ("Yours", (string?)null, 99_999.0)],
            "the viewer's rule equal to the team's shows once, as the team's");
        RuleReasons(unnamed).Should().BeEmpty();
        RuleTeams(unnamed).Should().BeEmpty();
        RuleReasons(managed).Should().BeEmpty("a manager follows nobody through the team");
        RuleTeams(managed).Should().BeEmpty();
        (await ownerOnSite.GetStringAsync("/api/v4/member-invites/members/history")).Should().NotContain("flag_rule").And.NotContain("FlagRule");
    }

    private static IEnumerable<(string? Source, string? Team, double Value)> RuleReasons(JsonElement overview) =>
        overview.GetProperty("tenants").EnumerateArray()
            .SelectMany(t => t.GetProperty("attention").EnumerateArray())
            .Where(a => a.GetProperty("kind").GetString() == "FlagRule")
            .Select(a => a.GetProperty("rule"))
            .Select(r => (
                r.GetProperty("source").GetString(),
                r.GetProperty("team").ValueKind == JsonValueKind.Object ? r.GetProperty("team").GetProperty("name").GetString() : null,
                r.GetProperty("ruleValue").GetDouble()));

    private static IEnumerable<(Guid Id, string? Name)> RuleTeams(JsonElement overview) =>
        overview.GetProperty("ruleTeams").EnumerateArray().Select(t => (t.GetProperty("id").GetGuid(), t.GetProperty("name").GetString()));

    /// <summary>A new team run by the manager, with <paramref name="staff"/> joined through a staff invite; returns the team.</summary>
    private async Task<Guid> TeamWithStaffAsync(HttpClient managerClient, Guid staff)
    {
        var teamId = (await (await managerClient.PostAsJsonAsync("/api/v4/me/teams", new { name = $"Ward {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var invite = await managerClient.PostAsJsonAsync(
            $"/api/v4/me/teams/{teamId}/staff-invites", new { roleCeilingSlug = (string?)null, limitTo24Hours = false });
        invite.StatusCode.Should().Be(HttpStatusCode.Created);
        var token = (await invite.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();

        using var staffAtApex = await SessionAtApexAsync(staff);
        (await staffAtApex.PostAsync($"/api/v4/me/teams/staff-invites/{token}/accept", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        return teamId;
    }

    /// <summary>The site's owner approves the team's access link giving the caretaker role.</summary>
    private static async Task ApproveAsync(HttpClient managerClient, HttpClient ownerClient, Guid teamId)
    {
        var link = await managerClient.PostAsJsonAsync(
            $"/api/v4/me/teams/{teamId}/access-links", new { roleSlug = RoleSeeds.Caretaker, limitTo24Hours = false });
        link.StatusCode.Should().Be(HttpStatusCode.Created);
        var token = (await link.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var site = (await ownerClient.GetFromJsonAsync<JsonElement>($"/api/v4/me/teams/access-links/{token}"))
            .GetProperty("sites").EnumerateArray().Single();
        var role = site.GetProperty("roles").EnumerateArray()
            .Single(r => r.GetProperty("slug").GetString() == RoleSeeds.Caretaker).GetProperty("id").GetGuid();

        var approved = await ownerClient.PostAsJsonAsync($"/api/v4/me/teams/access-links/{token}/approve",
            new { tenantId = site.GetProperty("tenantId").GetGuid(), roleIds = new[] { role }, limitTo24Hours = false, accessEndsAt = (DateTimeOffset?)null });
        approved.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<HttpClient> SessionAtApexAsync(Guid subject)
    {
        var client = Fixture.CreateApexClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subject));
        return client;
    }

    /// <summary>A subject with no membership of any site and no credential of their own.</summary>
    private async Task<Guid> SeedBareSubjectAsync(string name)
    {
        var subject = Guid.CreateVersion7();
        await using var conn = await OpenPostgresAsync();
        await conn.ExecuteAsync("""
            INSERT INTO subjects (id, name, is_active, is_system_subject, created_at, updated_at, approval_status)
            VALUES (@id, @name, true, false, now(), now(), 'Approved');
            """, ("id", subject), ("name", name));
        return subject;
    }

    private async Task<(Guid Subject, string AccessToken)> SeedSubjectAsync(string name, bool owner = false)
    {
        using (var provisioning = CreateAuthenticatedClient())
            (await provisioning.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var seeded = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, name, [Scope.GlucoseRead]);
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

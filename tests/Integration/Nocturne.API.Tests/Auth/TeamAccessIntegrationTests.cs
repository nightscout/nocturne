using System.Net;
using System.Net.Http.Headers;
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
/// A team's access to a site over HTTP: a manager makes the link on the apex, an owner of the site
/// opens it and approves it there, the team reads the site's name and what it was given, and the site
/// lists the team, keeps its roles to data, stops the access and finds both in its member history.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class TeamAccessIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    [Fact]
    public async Task An_owner_gives_a_team_access_through_its_link_and_the_site_stops_it()
    {
        var (manager, _) = await SeedSubjectAsync("Clinic Manager");
        var (owner, ownerToken) = await SeedSubjectAsync("Site Owner", owner: true);
        var (stranger, _) = await SeedSubjectAsync("Not An Owner", [Scope.GlucoseRead]);
        var (_, viewerToken) = await SeedSubjectAsync("Viewer", [Scope.GlucoseRead]);
        var tenantId = await TenantIdAsync();
        using var managerClient = await SessionAtApexAsync(manager);
        using var ownerClient = await SessionAtApexAsync(owner);
        using var strangerClient = await SessionAtApexAsync(stranger);

        var teamId = (await (await managerClient.PostAsJsonAsync("/api/v4/me/teams", new { name = "Diabetes clinic" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var refusedLink = await managerClient.PostAsJsonAsync(
            $"/api/v4/me/teams/{teamId}/access-links", new { roleSlug = RoleSeeds.Admin, limitTo24Hours = false });
        await ShouldBeRefusedAsync(refusedLink, HttpStatusCode.BadRequest, "team_access_role_invalid");

        var created = await managerClient.PostAsJsonAsync(
            $"/api/v4/me/teams/{teamId}/access-links", new { roleSlug = RoleSeeds.Caretaker, limitTo24Hours = false, maxUses = 5 });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var token = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();

        var info = await ownerClient.GetFromJsonAsync<JsonElement>($"/api/v4/me/teams/access-links/{token}");
        info.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "teamId", "teamName", "createdByName", "suggestedRoleSlug", "suggestedRolePermissions",
            "suggestedLimitTo24Hours", "suggestedAccessEndsAt", "expiresAt", "sites");
        info.GetProperty("teamName").GetString().Should().Be("Diabetes clinic");
        var site = info.GetProperty("sites").EnumerateArray().Should().ContainSingle().Subject;
        site.GetProperty("tenantId").GetGuid().Should().Be(tenantId);
        site.GetProperty("alreadyGiven").GetBoolean().Should().BeFalse();
        var offered = site.GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("slug").GetString()).ToList();
        offered.Should().Contain([RoleSeeds.Caretaker, RoleSeeds.Viewer]).And.NotContain([RoleSeeds.Admin, RoleSeeds.Owner]);
        var caretaker = site.GetProperty("defaultRoleId").GetGuid();

        var strangerInfo = await strangerClient.GetFromJsonAsync<JsonElement>($"/api/v4/me/teams/access-links/{token}");
        strangerInfo.GetProperty("sites").GetArrayLength().Should().Be(0, "a person who owns no site is shown none");
        await ShouldBeRefusedAsync(
            await strangerClient.PostAsJsonAsync($"/api/v4/me/teams/access-links/{token}/approve", Approval(tenantId, caretaker)),
            HttpStatusCode.Forbidden, "team_access_not_owner");

        var approved = await ownerClient.PostAsJsonAsync($"/api/v4/me/teams/access-links/{token}/approve", Approval(tenantId, caretaker));
        approved.StatusCode.Should().Be(HttpStatusCode.OK);
        await ShouldBeRefusedAsync(
            await ownerClient.PostAsJsonAsync($"/api/v4/me/teams/access-links/{token}/approve", Approval(tenantId, caretaker)),
            HttpStatusCode.BadRequest, "team_access_already_given");

        var team = await managerClient.GetFromJsonAsync<JsonElement>($"/api/v4/me/teams/{teamId}");
        var given = team.GetProperty("sites").EnumerateArray().Should().ContainSingle().Subject;
        given.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "tenantId", "displayName", "roleNames", "limitTo24Hours", "accessEndsAt", "hasEnded", "givenAt",
            "teamManagesStaff", "pendingStaffCount", "declinedStaffCount");
        given.GetProperty("tenantId").GetGuid().Should().Be(tenantId);

        using var ownerOnSite = AuthTestHelpers.CreateBearerClient(Fixture, ownerToken);
        using var viewerOnSite = AuthTestHelpers.CreateBearerClient(Fixture, viewerToken);
        var listed = await ownerOnSite.GetFromJsonAsync<JsonElement>("/api/v4/member-invites/team-access");
        var entry = listed.EnumerateArray().Should().ContainSingle().Subject;
        entry.GetProperty("teamId").GetGuid().Should().Be(teamId);
        entry.GetProperty("approvedBySubjectId").GetGuid().Should().Be(owner);
        (await viewerOnSite.GetAsync("/api/v4/member-invites/team-access")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await ShouldBeRefusedAsync(
            await ownerOnSite.PutAsJsonAsync($"/api/v4/roles/{caretaker}",
                new { name = "Caretaker", description = (string?)null, permissions = new[] { Scope.GlucoseRead, Scope.MembersManage } }),
            HttpStatusCode.Conflict, "role_held_by_team");
        await ShouldBeRefusedAsync(await ownerOnSite.DeleteAsync($"/api/v4/roles/{caretaker}"), HttpStatusCode.BadRequest, "role_held_by_team");

        var (_, administratorToken) = await SeedSubjectAsync("Administrator Not Owner");
        using var administratorOnSite = AuthTestHelpers.CreateBearerClient(Fixture, administratorToken);
        await ShouldBeRefusedAsync(
            await administratorOnSite.PutAsJsonAsync($"/api/v4/roles/{caretaker}",
                new { name = "Caretaker", description = (string?)null, permissions = RoleSeeds.Permissions[RoleSeeds.Caretaker].Append(Scope.HeartRateReadWrite) }),
            HttpStatusCode.Conflict, "role_held_by_team_owner_only");

        (await viewerOnSite.DeleteAsync($"/api/v4/member-invites/team-access/{teamId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ownerOnSite.DeleteAsync($"/api/v4/member-invites/team-access/{teamId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ownerOnSite.DeleteAsync($"/api/v4/member-invites/team-access/{teamId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ownerOnSite.GetFromJsonAsync<JsonElement>("/api/v4/member-invites/team-access")).GetArrayLength().Should().Be(0);

        var history = await ownerOnSite.GetFromJsonAsync<JsonElement>("/api/v4/member-invites/members/history");
        history.GetProperty("data").EnumerateArray()
            .Where(e => e.TryGetProperty("team", out var t) && t.ValueKind == JsonValueKind.Object)
            .Select(e => e.GetProperty("kind").GetString())
            .Should().Equal("TeamAccessStopped", "TeamAccessGiven");
    }

    private static object Approval(Guid tenantId, Guid roleId) =>
        new { tenantId, roleIds = new[] { roleId }, limitTo24Hours = false, accessEndsAt = (DateTimeOffset?)null };

    private static async Task ShouldBeRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.Should().Be(status);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be(code);
    }

    private async Task<HttpClient> SessionAtApexAsync(Guid subject)
    {
        var client = Fixture.CreateApexClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subject));
        return client;
    }

    private async Task<Guid> TenantIdAsync()
    {
        await using var conn = await OpenPostgresAsync();
        return await AuthTestHelpers.GetTenantIdAsync(conn);
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

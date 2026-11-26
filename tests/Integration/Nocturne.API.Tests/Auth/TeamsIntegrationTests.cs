using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models.Identity;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// Teams over HTTP on the apex, which resolves no tenant: a person's session creates a team, a
/// stranger cannot see it, and the stranger joins it only by opening its invite link. On a tenant
/// host an access token of the same person is refused.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class TeamsIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    [Fact]
    public async Task A_team_is_created_hidden_from_a_stranger_and_joined_by_its_invite_link()
    {
        var (manager, managerToken) = await SeedSubjectAsync("Team Manager");
        var (stranger, _) = await SeedSubjectAsync("Team Stranger");
        using var managerClient = await SessionAtApexAsync(manager);
        using var strangerClient = await SessionAtApexAsync(stranger);

        var created = await managerClient.PostAsJsonAsync("/api/v4/me/teams", new { name = "Camp staff" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var teamId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var mine = await managerClient.GetFromJsonAsync<JsonElement>("/api/v4/me/teams");
        mine.GetProperty("canCreate").GetBoolean().Should().BeTrue();
        mine.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("id").GetGuid()).Should().Equal(teamId);

        (await strangerClient.GetAsync($"/api/v4/me/teams/{teamId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await strangerClient.DeleteAsync($"/api/v4/me/teams/{teamId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var invite = await managerClient.PostAsJsonAsync($"/api/v4/me/teams/{teamId}/invites", new { label = "Night staff" });
        invite.StatusCode.Should().Be(HttpStatusCode.Created);
        var token = (await invite.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();

        var info = await strangerClient.GetFromJsonAsync<JsonElement>($"/api/v4/me/teams/invites/{token}");
        info.GetProperty("teamName").GetString().Should().Be("Camp staff");
        info.GetProperty("alreadyMember").GetBoolean().Should().BeFalse();

        (await strangerClient.PostAsync($"/api/v4/me/teams/invites/{token}/accept", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var team = await strangerClient.GetFromJsonAsync<JsonElement>($"/api/v4/me/teams/{teamId}");
        team.GetProperty("managers").EnumerateArray().Select(m => m.GetProperty("subjectId").GetGuid())
            .Should().Equal(manager, stranger);
        team.GetProperty("isLastManager").GetBoolean().Should().BeFalse();

        using var accessToken = AuthTestHelpers.CreateBearerClient(Fixture, managerToken);
        (await accessToken.GetAsync("/api/v4/me/teams")).StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "on the tenant host the manager's access token authenticates, and a team is run from the person's own session");
    }

    [Fact]
    public async Task A_staff_invite_makes_staff_who_read_the_teams_name_and_cannot_run_it_and_who_can_leave()
    {
        var (manager, _) = await SeedSubjectAsync("Staff Manager");
        var (staff, _) = await SeedSubjectAsync("Staff Member");
        var (outsider, _) = await SeedSubjectAsync("Staff Outsider");
        using var managerClient = await SessionAtApexAsync(manager);
        using var staffClient = await SessionAtApexAsync(staff);
        using var outsiderClient = await SessionAtApexAsync(outsider);
        var teamId = await CreateTeamAsync(managerClient, "Camp staff");

        var invite = await managerClient.PostAsJsonAsync(
            $"/api/v4/me/teams/{teamId}/staff-invites", new { label = "Night shift", roleCeilingSlug = "viewer", limitTo24Hours = true, maxUses = 2 });
        invite.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await invite.Content.ReadFromJsonAsync<JsonElement>();
        var token = created.GetProperty("token").GetString();
        var inviteId = created.GetProperty("id").GetGuid();

        var info = await staffClient.GetFromJsonAsync<JsonElement>($"/api/v4/me/teams/staff-invites/{token}");
        info.GetProperty("teamName").GetString().Should().Be("Camp staff");
        info.GetProperty("narrowing").GetProperty("roleCeilingSlug").GetString().Should().Be("viewer");
        info.GetProperty("narrowing").GetProperty("limitTo24Hours").GetBoolean().Should().BeTrue();
        info.GetProperty("alreadyMember").GetBoolean().Should().BeFalse();
        RoleSlugs(info).Should().Equal(TeamSuggestableRoles.All.Select(r => r.Slug));
        (await staffClient.PostAsync($"/api/v4/me/teams/staff-invites/{token}/accept", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var asStaff = await staffClient.GetStringAsync($"/api/v4/me/teams/{teamId}");
        asStaff.Should().NotContain(manager.ToString(), "a staff member reads no manager's subject id");
        using (var team = JsonDocument.Parse(asStaff))
        {
            var root = team.RootElement;
            root.GetProperty("callerKind").GetString().Should().Be("staff");
            root.GetProperty("name").GetString().Should().Be("Camp staff");
            root.GetProperty("managers").GetArrayLength().Should().Be(0);
            root.GetProperty("managerNames").GetArrayLength().Should().Be(1);
            foreach (var hidden in new[] { "invites", "staffInvites", "staff", "accessLinks", "sites" })
                root.GetProperty(hidden).GetArrayLength().Should().Be(0, hidden);
            RoleSlugs(root).Should().Equal(TeamSuggestableRoles.All.Select(r => r.Slug), "a staff member names their role ceiling from the static seed roles");
            root.GetProperty("ownStaff").GetProperty("narrowing").GetProperty("roleCeilingSlug").GetString().Should().Be("viewer");
        }

        var asManager = await managerClient.GetFromJsonAsync<JsonElement>($"/api/v4/me/teams/{teamId}");
        asManager.GetProperty("staff").EnumerateArray().Select(m => m.GetProperty("subjectId").GetGuid()).Should().Equal(staff);
        asManager.GetProperty("staffInvites").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Equal(inviteId);

        var mine = await staffClient.GetFromJsonAsync<JsonElement>("/api/v4/me/teams");
        mine.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("kind").GetString()).Should().Equal("staff");

        (await staffClient.PutAsJsonAsync($"/api/v4/me/teams/{teamId}/name", new { name = "Mine" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await staffClient.PostAsJsonAsync($"/api/v4/me/teams/{teamId}/invites", new { label = "x" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await staffClient.PostAsJsonAsync($"/api/v4/me/teams/{teamId}/staff-invites", new { label = "x" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await staffClient.DeleteAsync($"/api/v4/me/teams/{teamId}/staff-invites/{inviteId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await staffClient.PutAsJsonAsync($"/api/v4/me/teams/{teamId}/staff/{staff}", new { limitTo24Hours = false })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await staffClient.DeleteAsync($"/api/v4/me/teams/{teamId}/managers/{manager}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var refused = await staffClient.DeleteAsync($"/api/v4/me/teams/{teamId}");
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("team_delete_refused");

        (await managerClient.PutAsJsonAsync($"/api/v4/me/teams/{teamId}/staff/{staff}", new { limitTo24Hours = false }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        var edited = await managerClient.GetFromJsonAsync<JsonElement>($"/api/v4/me/teams/{teamId}");
        edited.GetProperty("staff")[0].GetProperty("narrowing").GetProperty("roleCeilingSlug").ValueKind.Should().Be(JsonValueKind.Null);

        (await staffClient.DeleteAsync($"/api/v4/me/teams/{teamId}/staff/{staff}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await staffClient.GetAsync($"/api/v4/me/teams/{teamId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await outsiderClient.PostAsJsonAsync($"/api/v4/me/teams/{teamId}/staff-invites", new { label = "x" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await outsiderClient.DeleteAsync($"/api/v4/me/teams/{teamId}/staff-invites/{inviteId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await outsiderClient.PutAsJsonAsync($"/api/v4/me/teams/{teamId}/staff/{staff}", new { limitTo24Hours = false })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await outsiderClient.DeleteAsync($"/api/v4/me/teams/{teamId}/staff/{staff}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_manager_token_never_makes_staff_and_a_staff_token_never_makes_a_manager()
    {
        var (manager, _) = await SeedSubjectAsync("Token Manager");
        var (person, _) = await SeedSubjectAsync("Token Person");
        using var managerClient = await SessionAtApexAsync(manager);
        using var personClient = await SessionAtApexAsync(person);
        var teamId = await CreateTeamAsync(managerClient, "Token team");
        var managerToken = (await (await managerClient.PostAsJsonAsync($"/api/v4/me/teams/{teamId}/invites", new { label = "m" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var staffToken = (await (await managerClient.PostAsJsonAsync($"/api/v4/me/teams/{teamId}/staff-invites", new { label = "s" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();

        (await personClient.GetAsync($"/api/v4/me/teams/staff-invites/{managerToken}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await personClient.PostAsync($"/api/v4/me/teams/staff-invites/{managerToken}/accept", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await personClient.GetAsync($"/api/v4/me/teams/invites/{staffToken}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await personClient.PostAsync($"/api/v4/me/teams/invites/{staffToken}/accept", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await personClient.GetAsync($"/api/v4/me/teams/{teamId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static IEnumerable<string?> RoleSlugs(JsonElement element) =>
        element.GetProperty("suggestableRoles").EnumerateArray().Select(r => r.GetProperty("slug").GetString());

    private static async Task<Guid> CreateTeamAsync(HttpClient client, string name)
    {
        var created = await client.PostAsJsonAsync("/api/v4/me/teams", new { name });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<HttpClient> SessionAtApexAsync(Guid subject)
    {
        var client = Fixture.CreateApexClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subject));
        return client;
    }

    private async Task<(Guid Subject, string AccessToken)> SeedSubjectAsync(string name)
    {
        using (var provisioning = CreateAuthenticatedClient())
            (await provisioning.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        return await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, name);
    }
}

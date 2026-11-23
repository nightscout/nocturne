using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
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

        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        return await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, name);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Constants;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// An authenticated request resolves its membership in one statement before its controller runs, and
/// keeps nothing past the request: a removal, a revocation or a clamp applies on the next one.
/// </summary>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public class AuthenticatedRequestContextOpensIntegrationTests : ApiIntegrationTestBase
{
    private const string EntriesPath = "/api/v1/entries.json?count=10";

    public AuthenticatedRequestContextOpensIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task A_warm_session_cookie_request_opens_at_most_two_contexts_before_the_controller()
    {
        var (subjectId, _) = await SeedMemberAsync("Cookie Reader");
        var session = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);

        await AssertWarmOpensAsync(Cookie(session));
    }

    [Fact]
    public async Task A_warm_api_secret_request_opens_at_most_two_contexts_before_the_controller()
    {
        await AssertWarmOpensAsync(request => request.Headers.Add("api-secret", TestApiSecret));
    }

    [Fact]
    public async Task A_warm_direct_grant_request_opens_at_most_two_contexts_before_the_controller()
    {
        var (_, token) = await SeedMemberAsync("Grant Reader");

        await AssertWarmOpensAsync(Bearer(token));
    }

    [Fact]
    public async Task A_removed_member_is_refused_on_the_next_request()
    {
        var (subjectId, _) = await SeedMemberAsync("Removed Member");
        var session = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);
        using var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);

        (await SendAsync(client, Cookie(session))).Response.StatusCode.Should().Be(HttpStatusCode.OK);

        await ExecuteAsync("DELETE FROM tenant_members WHERE subject_id = @subject;", subjectId);

        (await SendAsync(client, Cookie(session))).Response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_revoked_grant_is_refused_on_the_next_request()
    {
        var (subjectId, token) = await SeedMemberAsync("Revoked Grant");
        using var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);

        (await SendAsync(client, Bearer(token))).Response.StatusCode.Should().Be(HttpStatusCode.OK);

        await ExecuteAsync("UPDATE oauth_grants SET revoked_at = now() WHERE subject_id = @subject;", subjectId);

        (await SendAsync(client, Bearer(token))).Response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_membership_of_another_tenant_admits_neither_a_session_nor_a_grant()
    {
        var (subjectId, token) = await SeedMemberAsync("Other Tenant Member");
        var session = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);
        using var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);
        (await SendAsync(client, Cookie(session))).Response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(client, Bearer(token))).Response.StatusCode.Should().Be(HttpStatusCode.OK);

        var otherTenant = Guid.CreateVersion7();
        await ExecuteAsync(
            $"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{otherTenant}', 'other-{otherTenant:N}', 'Other', true, now(), now());
            UPDATE tenant_members SET tenant_id = '{otherTenant}' WHERE subject_id = @subject;
            """,
            subjectId);

        (await SendAsync(client, Cookie(session))).Response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(client, Bearer(token))).Response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_clamped_member_stays_clamped_on_every_request()
    {
        var now = DateTimeOffset.UtcNow;
        var write = await AuthenticatedClient.PostAsJsonAsync("/api/v1/entries", new[]
        {
            Sgv(now.AddDays(-3), 101),
            Sgv(now.AddMinutes(-5), 202),
        });
        write.IsSuccessStatusCode.Should().BeTrue(await write.Content.ReadAsStringAsync());

        var (subjectId, _) = await SeedMemberAsync("Clamped Member");
        await ExecuteAsync(
            """
            UPDATE tenant_members SET limit_to_24_hours = true, direct_permissions = '["glucose.read"]'::jsonb
            WHERE subject_id = @subject;
            """,
            subjectId);
        var session = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);
        using var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var (response, _) = await SendAsync(client, Cookie(session));
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var entries = await response.Content.ReadFromJsonAsync<JsonArray>();
            entries!.Select(e => e!["sgv"]!.GetValue<int>()).Should().Equal(202);
        }
    }

    private async Task AssertWarmOpensAsync(Action<HttpRequestMessage> authenticate)
    {
        await MarkEveryCredentialJustUsedAsync();
        using var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);
        await SendAsync(client, authenticate);

        var (response, requestId) = await SendAsync(client, authenticate);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var opens = Fixture.Services.GetRequiredService<ContextOpenLog>().OpensOf(requestId);
        foreach (var open in opens)
            Log($"{(open.BeforeAction ? "before" : "after ")} {open.Kind,-14} {open.Caller}");
        opens.Should().Contain(o => !o.BeforeAction, "the entries read opens a context in the controller");
        opens.Should().Contain(
            o => o.BeforeAction && o.Kind == "tenant", "the credential and membership are read before the controller");
        opens.Where(o => o.BeforeAction).Should().HaveCountLessThanOrEqualTo(2);
    }

    /// <summary>
    /// Leaves no last-used stamp due. A seeded membership or grant has never been used, so the warm-up
    /// request would start the stamps, and any not yet committed when the measured request
    /// authenticates would start again and open contexts before its controller.
    /// </summary>
    private async Task MarkEveryCredentialJustUsedAsync()
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT set_config('app.current_tenant_id', @tenant, false);
            UPDATE tenant_members SET last_used_at = now();
            UPDATE oauth_grants SET last_used_at = now();
            """;
        cmd.Parameters.AddWithValue("tenant", Fixture.TenantId.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    private static Action<HttpRequestMessage> Cookie(string session) =>
        request => request.Headers.Add("Cookie", $".Nocturne.AccessToken={session}");

    private static Action<HttpRequestMessage> Bearer(string token) =>
        request => request.Headers.Add("Authorization", $"Bearer {token}");

    private static object Sgv(DateTimeOffset at, int sgv) => new
    {
        type = "sgv", sgv, direction = "Flat", date = at.ToUnixTimeMilliseconds(), dateString = at.UtcDateTime.ToString("o"),
    };

    private static async Task<(HttpResponseMessage Response, string RequestId)> SendAsync(
        HttpClient client, Action<HttpRequestMessage> authenticate)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, EntriesPath);
        authenticate(request);
        var id = ContextOpenLog.Tag(request);
        return (await client.SendAsync(request), id);
    }

    private async Task<(Guid SubjectId, string Token)> SeedMemberAsync(string name)
    {
        await using var conn = await OpenAsync();
        return await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, Fixture.TenantId, name);
    }

    private async Task ExecuteAsync(string sql, Guid subjectId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tenant, false);" + sql;
        cmd.Parameters.AddWithValue("tenant", Fixture.TenantId.ToString());
        cmd.Parameters.AddWithValue("subject", subjectId);
        (await cmd.ExecuteNonQueryAsync()).Should().BeGreaterThan(0);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        return conn;
    }
}

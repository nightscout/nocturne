using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// A person's saved views of the People page over HTTP on the apex, which resolves no tenant: created, listed in order,
/// changed, reordered and deleted; refused when the body or its fact is refused, a name is taken or the limit is reached;
/// and hidden from anyone else. The overview and review reads take a view's query.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class PeopleViewsIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private const string Views = "/api/v4/me/people-views";

    private static readonly object Query = new
    {
        search = "a",
        filters = new object[]
        {
            new { kind = "Fact", dataset = "glucose_statistics", field = "timeBelow", days = 14, comparison = "Above", value = 4 },
            new { kind = "Flagged" },
        },
        sort = new { column = "TimeInRange", direction = "Descending" },
    };

    [Fact]
    public async Task A_view_is_created_listed_changed_reordered_and_deleted()
    {
        var (viewer, _) = await SeedSubjectAsync("View Viewer", [Scope.FullAccess]);
        using var client = await SessionAtApexAsync(viewer);

        var created = await client.PostAsJsonAsync(Views, new { name = " Review ", query = Query, lens = "Review", reviewDays = 30 });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var view = await created.Content.ReadFromJsonAsync<JsonElement>();
        var first = view.GetProperty("id").GetGuid();
        view.GetProperty("name").GetString().Should().Be("Review");
        view.GetProperty("query").GetProperty("filters").GetArrayLength().Should().Be(2);
        var second = (await (await client.PostAsJsonAsync(Views, new { name = "Now", lens = "Now" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var listed = await client.GetFromJsonAsync<JsonElement>(Views);
        listed.GetProperty("limit").GetInt32().Should().Be(PeopleViewLimits.PerViewer);
        Ids(listed).Should().Equal(first, second);
        listed.GetProperty("views")[0].GetProperty("groupGone").GetBoolean().Should().BeFalse();

        var changed = await client.PutAsJsonAsync($"{Views}/{first}", new { name = "Week", lens = "Now" });
        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await changed.Content.ReadFromJsonAsync<JsonElement>();
        after.GetProperty("name").GetString().Should().Be("Week");
        after.TryGetProperty("reviewDays", out var days).Should().BeTrue();
        days.ValueKind.Should().Be(JsonValueKind.Null);

        var reordered = await client.PostAsJsonAsync($"{Views}/reorder", new { viewIds = new[] { second, first } });
        reordered.StatusCode.Should().Be(HttpStatusCode.OK);
        Ids(await client.GetFromJsonAsync<JsonElement>(Views)).Should().Equal(second, first);
        (await client.PostAsJsonAsync($"{Views}/reorder", new { viewIds = new[] { second } })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await client.DeleteAsync($"{Views}/{first}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        Ids(await client.GetFromJsonAsync<JsonElement>(Views)).Should().Equal(second);
    }

    public static TheoryData<string, string> Refusals => new()
    {
        { "no lens", """{ "name": "View" }""" },
        { "a blank name", """{ "name": "  ", "lens": "Now" }""" },
        { "a period for the now lens", """{ "name": "View", "lens": "Now", "reviewDays": 14 }""" },
        { "a review sort on the now lens", """{ "name": "View", "lens": "Now", "query": { "sort": { "column": "Gmi", "direction": "Ascending" } } }""" },
        { "a filter with another kind's field", """{ "name": "View", "lens": "Now", "query": { "filters": [ { "kind": "Flagged", "years": 3 } ] } }""" },
        { "a fact the reports engine refuses", """{ "name": "View", "lens": "Now", "query": { "filters": [ { "kind": "Fact", "dataset": "glucose_statistics", "field": "readings", "days": 14, "comparison": "Above", "value": 4 } ] } }""" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_view_the_api_refuses_is_a_bad_request_and_is_not_stored(string because, string body)
    {
        var (viewer, _) = await SeedSubjectAsync($"View Refused {Guid.NewGuid():N}", [Scope.FullAccess]);
        using var client = await SessionAtApexAsync(viewer);

        var response = await client.PostAsync(Views, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because);
        (await client.GetFromJsonAsync<JsonElement>(Views)).GetProperty("views").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_name_taken_and_the_view_past_the_limit_are_conflicts_and_another_persons_view_is_not_found()
    {
        var (owner, _) = await SeedSubjectAsync("View Owner", [Scope.FullAccess]);
        var (stranger, _) = await SeedSubjectAsync("View Stranger", [Scope.FullAccess]);
        using var ownerClient = await SessionAtApexAsync(owner);
        using var strangerClient = await SessionAtApexAsync(stranger);
        var id = (await (await ownerClient.PostAsJsonAsync(Views, new { name = "View 0", lens = "Now" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        (await ownerClient.PostAsJsonAsync(Views, new { name = "VIEW 0", lens = "Now" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        for (var i = 1; i < PeopleViewLimits.PerViewer; i++)
            (await ownerClient.PostAsJsonAsync(Views, new { name = $"View {i}", lens = "Now" })).StatusCode.Should().Be(HttpStatusCode.Created);
        var past = await ownerClient.PostAsJsonAsync(Views, new { name = "One more", lens = "Now" });

        past.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await past.Content.ReadAsStringAsync()).Should().Contain("people_view_limit_reached");
        (await strangerClient.GetFromJsonAsync<JsonElement>(Views)).GetProperty("views").GetArrayLength().Should().Be(0);
        (await strangerClient.PutAsJsonAsync($"{Views}/{id}", new { name = "Mine", lens = "Now" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await strangerClient.DeleteAsync($"{Views}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_name_the_unique_index_refuses_after_the_check_passed_is_a_conflict()
    {
        var (viewer, _) = await SeedSubjectAsync($"View Clash {Guid.NewGuid():N}", [Scope.FullAccess]);
        using var client = await SessionAtApexAsync(viewer);

        await using var conn = await OpenPostgresAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO people_views (id, subject_id, name, position, query, lens, review_days)
            VALUES ('{Guid.CreateVersion7()}', '{viewer}', 'Clash', 50, '{"{}"}', 'now', NULL)
            """, conn, tx))
            await insert.ExecuteNonQueryAsync();

        var created = client.PostAsJsonAsync(Views, new { name = "clash", lens = "Now" });
        var deadline = DateTime.UtcNow.AddSeconds(30);
        await using var watcher = await OpenPostgresAsync();
        while (!created.IsCompleted)
        {
            await using var waiting = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", watcher);
            if ((long)(await waiting.ExecuteScalarAsync())! > 0)
                break;
            DateTime.UtcNow.Should().BeBefore(deadline, "the create should wait on the uncommitted name");
            await Task.Delay(50);
        }

        await tx.CommitAsync();
        var response = await created;

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().Contain("people_view_name_taken");
    }

    [Fact]
    public async Task The_overview_and_review_reads_take_a_views_query_on_the_apex()
    {
        var (viewer, _) = await SeedSubjectAsync("View Reader", [Scope.FullAccess]);
        using var client = await SessionAtApexAsync(viewer);

        var none = await client.PostAsJsonAsync("/api/v4/me/tenants/overview", new { rules = "All", query = new { search = "no person has this name" } });
        var everyone = await client.PostAsJsonAsync("/api/v4/me/tenants/overview", new { });
        var review = await client.PostAsJsonAsync("/api/v4/me/tenants/overview/review", new { days = 14, rules = "Team", query = Query });
        var badPeriod = await client.PostAsJsonAsync("/api/v4/me/tenants/overview/review", new { days = 7 });
        var badFact = await client.PostAsJsonAsync("/api/v4/me/tenants/overview", new
        {
            query = new { filters = new[] { new { kind = "Fact", dataset = "no_such_dataset", field = "timeBelow", days = 14, comparison = "Above", value = 4 } } },
        });

        none.StatusCode.Should().Be(HttpStatusCode.OK);
        (await none.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tenants").GetArrayLength().Should().Be(0);
        everyone.StatusCode.Should().Be(HttpStatusCode.OK);
        (await everyone.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tenants").GetArrayLength().Should().BeGreaterThan(0);
        review.StatusCode.Should().Be(HttpStatusCode.OK, await review.Content.ReadAsStringAsync());
        (await review.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("peopleWithRulesPending", out _).Should().BeTrue();
        badPeriod.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        badFact.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static Guid[] Ids(JsonElement list) =>
        [.. list.GetProperty("views").EnumerateArray().Select(v => v.GetProperty("view").GetProperty("id").GetGuid())];

    private async Task<HttpClient> SessionAtApexAsync(Guid subject)
    {
        var client = Fixture.CreateApexClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subject));
        return client;
    }

    private async Task<(Guid Subject, string AccessToken)> SeedSubjectAsync(string name, IReadOnlyList<string> permissions)
    {
        using (var provisioning = CreateAuthenticatedClient())
            (await provisioning.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        return await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, name, permissions);
    }
}

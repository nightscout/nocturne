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
/// A person's own flag rules over HTTP on the apex, which resolves no tenant: created, listed with the count of people
/// each cannot be checked for, changed and deleted; refused when the reports engine refuses their fact; held to the
/// limit; and hidden from anyone else. The facts list needs no reports scope.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class FlagRulesIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private static readonly object TimeBelow = new { dataset = "glucose_statistics", field = "timeBelow", days = 14, comparison = "Above", value = 4 };

    [Fact]
    public async Task A_rule_is_created_listed_changed_and_deleted()
    {
        var (viewer, _) = await SeedSubjectAsync("Rule Viewer", [Scope.FullAccess]);
        using var client = await SessionAtApexAsync(viewer);

        var created = await client.PostAsJsonAsync("/api/v4/me/flag-rules", TimeBelow);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var rule = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = rule.GetProperty("id").GetGuid();
        rule.GetProperty("level").GetString().Should().Be("Warn", "a rule sent with no level is a warning");

        var listed = await client.GetFromJsonAsync<JsonElement>("/api/v4/me/flag-rules");
        listed.GetProperty("limit").GetInt32().Should().Be(FlagRuleLimits.PerViewer);
        var summary = listed.GetProperty("rules").EnumerateArray().Should().ContainSingle().Subject;
        summary.GetProperty("rule").GetProperty("id").GetGuid().Should().Be(id);
        summary.GetProperty("cannotCheckCount").GetInt32().Should().Be(0);

        var changed = await client.PutAsJsonAsync($"/api/v4/me/flag-rules/{id}",
            new { dataset = "current_status", field = "hoursSinceReading", comparison = "Above", value = 3, level = "Hazard" });
        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await changed.Content.ReadFromJsonAsync<JsonElement>();
        after.GetProperty("field").GetString().Should().Be("hoursSinceReading");
        after.GetProperty("days").ValueKind.Should().Be(JsonValueKind.Null);
        after.GetProperty("level").GetString().Should().Be("Hazard");

        (await client.DeleteAsync($"/api/v4/me/flag-rules/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<JsonElement>("/api/v4/me/flag-rules")).GetProperty("rules").GetArrayLength().Should().Be(0);
    }

    public static TheoryData<string, string> Refusals => new()
    {
        { "an unknown dataset", """{ "dataset": "no_such_dataset", "field": "timeBelow", "days": 14, "comparison": "Above", "value": 4 }""" },
        { "a field that is not a fact", """{ "dataset": "glucose_statistics", "field": "readings", "days": 14, "comparison": "Above", "value": 4 }""" },
        { "more than 90 days", """{ "dataset": "glucose_statistics", "field": "timeBelow", "days": 91, "comparison": "Above", "value": 4 }""" },
        { "days on a live fact", """{ "dataset": "current_status", "field": "hoursSinceReading", "days": 14, "comparison": "Above", "value": 2 }""" },
        { "no days on a period fact", """{ "dataset": "glucose_statistics", "field": "timeBelow", "comparison": "Above", "value": 4 }""" },
        { "a level outside the three", """{ "dataset": "glucose_statistics", "field": "timeBelow", "days": 14, "comparison": "Above", "value": 4, "level": "Urgent" }""" },
        { "no comparison", """{ "dataset": "glucose_statistics", "field": "timeBelow", "days": 14, "value": 4 }""" },
        { "no value", """{ "dataset": "glucose_statistics", "field": "timeBelow", "days": 14, "comparison": "Above" }""" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_rule_with_a_refused_fact_is_a_bad_request_and_is_not_stored(string because, string body)
    {
        var (viewer, _) = await SeedSubjectAsync("Rule Refused", [Scope.FullAccess]);
        using var client = await SessionAtApexAsync(viewer);

        var response = await client.PostAsync("/api/v4/me/flag-rules", new StringContent(body, MediaTypeHeaderValue.Parse("application/json")));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because);
        (await client.GetFromJsonAsync<JsonElement>("/api/v4/me/flag-rules")).GetProperty("rules").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task The_facts_and_presets_need_no_reports_scope()
    {
        var (viewer, _) = await SeedSubjectAsync("Rule Facts", [Scope.GlucoseRead]);
        using var client = await SessionAtApexAsync(viewer);

        var facts = await client.GetFromJsonAsync<JsonElement>("/api/v4/me/flag-rules/facts");

        facts.GetProperty("presets").GetArrayLength().Should().Be(8);
        facts.GetProperty("facts").EnumerateArray().Select(f => f.GetProperty("field").GetProperty("name").GetString())
            .Should().Contain(["timeBelow", "timeBelowOwnRange", "hoursSinceReading"]);
    }

    [Fact]
    public async Task The_list_counts_the_people_a_rule_cannot_check_and_names_none()
    {
        var (viewer, _) = await SeedSubjectAsync("Rule Counted", [Scope.GlucoseRead]);
        using var client = await SessionAtApexAsync(viewer);
        await client.PostAsJsonAsync("/api/v4/me/flag-rules", TimeBelow);
        await client.PostAsJsonAsync("/api/v4/me/flag-rules",
            new { dataset = "current_status", field = "hoursSinceReading", comparison = "Above", value = 2 });

        var missingScope = await client.GetStringAsync("/api/v4/me/flag-rules");
        await using (var conn = await OpenPostgresAsync())
            await conn.ExecuteAsync("UPDATE tenant_members SET limit_to_24_hours = true WHERE subject_id = @subject", ("subject", viewer));
        var limited = await client.GetFromJsonAsync<JsonElement>("/api/v4/me/flag-rules");

        Counts(JsonDocument.Parse(missingScope).RootElement).Should().Equal([1, 0], "the period fact needs reports.read and the live fact does not");
        Counts(limited).Should().Equal([1, 1], "a person read for only 24 hours cannot be checked by any rule");
        missingScope.Should().NotContain(ApiIntegrationTestFixture.TenantSlug).And.NotContain("displayName");
        limited.GetProperty("rules")[0].EnumerateObject().Select(p => p.Name).Should().Equal("rule", "cannotCheckCount");
    }

    [Fact]
    public async Task The_twenty_first_rule_is_refused_with_its_code()
    {
        var (viewer, _) = await SeedSubjectAsync("Rule Limit", [Scope.FullAccess]);
        using var client = await SessionAtApexAsync(viewer);
        for (var i = 0; i < FlagRuleLimits.PerViewer; i++)
            (await client.PostAsJsonAsync("/api/v4/me/flag-rules", TimeBelow)).StatusCode.Should().Be(HttpStatusCode.Created);

        var refused = await client.PostAsJsonAsync("/api/v4/me/flag-rules", TimeBelow);

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("flag_rule_limit_reached");
    }

    [Fact]
    public async Task Another_persons_rule_is_not_found()
    {
        var (owner, _) = await SeedSubjectAsync("Rule Owner", [Scope.FullAccess]);
        var (stranger, _) = await SeedSubjectAsync("Rule Stranger", [Scope.FullAccess]);
        using var ownerClient = await SessionAtApexAsync(owner);
        using var strangerClient = await SessionAtApexAsync(stranger);
        var id = (await (await ownerClient.PostAsJsonAsync("/api/v4/me/flag-rules", TimeBelow)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        (await strangerClient.GetFromJsonAsync<JsonElement>("/api/v4/me/flag-rules")).GetProperty("rules").GetArrayLength().Should().Be(0);
        (await strangerClient.PutAsJsonAsync($"/api/v4/me/flag-rules/{id}", TimeBelow)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await strangerClient.DeleteAsync($"/api/v4/me/flag-rules/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ownerClient.GetFromJsonAsync<JsonElement>("/api/v4/me/flag-rules")).GetProperty("rules").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task An_overview_read_that_names_no_rules_is_answered_with_the_fixed_reasons_only()
    {
        var (viewer, _) = await SeedSubjectAsync("Rule Overview", [Scope.FullAccess]);
        var at = DateTimeOffset.UtcNow.AddMinutes(-30);
        var write = await AuthenticatedClient.PostAsJsonAsync("/api/v1/entries", new[]
        {
            new { type = "sgv", sgv = 120, direction = "Flat", date = at.ToUnixTimeMilliseconds(), dateString = at.UtcDateTime.ToString("o") },
        });
        write.IsSuccessStatusCode.Should().BeTrue(await write.Content.ReadAsStringAsync());
        using var client = await SessionAtApexAsync(viewer);
        (await client.PostAsJsonAsync("/api/v4/me/flag-rules",
            new { dataset = "current_status", field = "hoursSinceReading", comparison = "Below", value = 100_000 }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var unnamed = await client.PostAsJsonAsync("/api/v4/me/tenants/overview", new { });
        var all = await (await client.PostAsJsonAsync("/api/v4/me/tenants/overview", new { rules = "All" }))
            .Content.ReadFromJsonAsync<JsonElement>();

        unnamed.StatusCode.Should().Be(HttpStatusCode.OK, "a caller that names no rule set is answered, not refused");
        ReasonKinds(await unnamed.Content.ReadFromJsonAsync<JsonElement>()).Should().NotContain("FlagRule");
        ReasonKinds(all).Should().Contain("FlagRule", "the same rule flags the person when the caller asks for their rules");
    }

    private static IEnumerable<string?> ReasonKinds(JsonElement overview) =>
        overview.GetProperty("tenants").EnumerateArray()
            .SelectMany(t => t.GetProperty("attention").EnumerateArray())
            .Select(a => a.GetProperty("kind").GetString());

    private static int[] Counts(JsonElement list) =>
        [.. list.GetProperty("rules").EnumerateArray().Select(r => r.GetProperty("cannotCheckCount").GetInt32())];

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

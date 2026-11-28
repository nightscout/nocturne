using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Reports;
using Nocturne.API.Services.Demo;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Demo;
using Nocturne.Core.Models.Identity;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// The demo clinic over HTTP: provisioned beside the demo host, signed in to as the clinic persona on the host's host,
/// and read through the people overview the persona's team gives it.
/// </summary>
[Collection(ApiIntegrationShards.One)]
[Trait("Category", "Integration")]
public class DemoClinicIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private const string HostSlug = "demo";

    private string BaseDomain => AuthTestHelpers.GetBaseDomain(Fixture.ApiClient);

    [Fact]
    public async Task The_persona_reads_every_person_of_the_clinic_and_both_groups()
    {
        await ProvisionAsync();
        using var persona = await PersonaClientAsync();

        var overview = await persona.PostAsJsonAsync("/api/v4/me/tenants/overview", new { });
        overview.StatusCode.Should().Be(HttpStatusCode.OK, await overview.Content.ReadAsStringAsync());
        var slugs = (await overview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tenants").EnumerateArray()
            .Select(t => t.GetProperty("slug").GetString()).ToList();
        var groups = await persona.GetFromJsonAsync<JsonElement>("/api/v4/me/tenants/overview/groups");

        slugs.Should().BeEquivalentTo([HostSlug, .. DemoClinic.People.Select(p => DemoClinic.SlugOf(HostSlug, p))]);
        slugs.Should().HaveCount(11);
        groups.EnumerateArray().Select(g => g.GetProperty("name").GetString())
            .Should().BeEquivalentTo([DemoClinic.Paediatrics, DemoClinic.MyList]);
    }

    [Fact]
    public async Task Only_the_personas_membership_of_John_is_limited_to_24_hours()
    {
        await ProvisionAsync();
        var personaId = await PersonaIdAsync();

        await using var conn = await OpenPostgresAsync();
        var limits = await conn.QueryAsync(
            """
            SELECT t.slug, m.limit_to_24_hours FROM tenant_members m JOIN tenants t ON t.id = m.tenant_id
            WHERE m.subject_id = @persona
            """,
            r => (Slug: r.GetString(0), Limited: r.GetBoolean(1)),
            ("persona", personaId));

        limits.Should().HaveCount(11);
        limits.Where(l => l.Limited).Select(l => l.Slug).Should().Equal(DemoClinic.SlugOf(HostSlug, DemoClinic.People[0]));
        DemoClinic.People[0].DisplayName.Should().Be("John Limitedaccess");
    }

    [Fact]
    public async Task A_second_provision_changes_nothing()
    {
        await ProvisionAsync();
        var personaId = await PersonaIdAsync();
        var first = await ClinicRowsAsync(personaId);

        await ProvisionAsync();

        (await PersonaIdAsync()).Should().Be(personaId);
        (await ClinicRowsAsync(personaId)).Should().BeEquivalentTo(first, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task Two_first_provisions_at_once_make_one_clinic()
    {
        var hostId = await AuthTestHelpers.SeedTenantAsync(Fixture, HostSlug, "Nocturne Demo");
        long personasBefore;
        await using (var conn = await OpenPostgresAsync())
        {
            personasBefore = await PersonaCountAsync(conn);
            await conn.ExecuteAsync(
                """
                UPDATE tenants SET is_demo = true WHERE id = @host;
                INSERT INTO tenant_demo_config (tenant_id, access_mode, backfill_days, interval_minutes, reset_interval_minutes, kind)
                VALUES (@host, 'open', 30, 5, 0, 'host');
                """,
                ("host", hostId));
        }

        await Task.WhenAll(ProvisionClinicInScopeAsync(), ProvisionClinicInScopeAsync());

        await using var check = await OpenPostgresAsync();
        (await check.ScalarAsync<long>("SELECT count(*) FROM teams")).Should().Be(1);
        (await check.ScalarAsync<long>("SELECT count(*) FROM team_members")).Should().Be(1);
        (await PersonaCountAsync(check)).Should().Be(personasBefore + 1);
        var personaId = await PersonaIdAsync();
        (await check.ScalarAsync<long>("SELECT count(*) FROM people_views WHERE subject_id = @p", ("p", personaId)))
            .Should().Be(DemoClinicSeeds.Views.Count);
        (await check.ScalarAsync<long>("SELECT count(*) FROM flag_rules WHERE subject_id = @p", ("p", personaId)))
            .Should().Be(DemoClinicSeeds.Rules.Count);
        (await check.ScalarAsync<long>("SELECT count(*) FROM tenant_members WHERE subject_id = @p", ("p", personaId)))
            .Should().Be(11);
    }

    [Fact]
    public async Task A_reset_of_the_host_leaves_the_clinic_as_it_was()
    {
        await ProvisionAsync();
        var personaId = await PersonaIdAsync();
        var before = await ClinicRowsAsync(personaId);

        using (var admin = AdminClient())
        {
            var reset = await admin.PostAsync("/api/v4/admin/demo/reset", null);
            reset.StatusCode.Should().Be(HttpStatusCode.OK, await reset.Content.ReadAsStringAsync());
        }

        var after = await ClinicRowsAsync(personaId);
        after["teams"].Should().Equal(before["teams"]);
        after["team_group_people"].Should().Equal(before["team_group_people"]);
        after["tenant_members"].Should().HaveCount(11);
        using var persona = await PersonaClientAsync();
        var overview = await persona.PostAsJsonAsync("/api/v4/me/tenants/overview", new { });
        (await overview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tenants").GetArrayLength().Should().Be(11);
    }

    [Fact]
    public async Task A_session_is_refused_on_a_persons_host_and_for_any_other_persona()
    {
        await ProvisionAsync();
        var john = DemoClinic.SlugOf(HostSlug, DemoClinic.People[0]);

        (await SessionAsync(john, persona: null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SessionAsync(john, DemoClinic.SessionPersona)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SessionAsync(HostSlug, "manager")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SessionAsync(HostSlug, DemoClinic.SessionPersona)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_real_tenant_on_a_persons_slug_is_left_as_it_is()
    {
        var gus = DemoClinic.SlugOf(HostSlug, DemoClinic.People.Single(p => p.Key == "gus"));
        var real = await AuthTestHelpers.SeedTenantAsync(Fixture, gus, "Real Gus");
        await using var conn = await OpenPostgresAsync();
        var before = await TenantRowAsync(conn, real);

        await ProvisionAsync();
        var personaId = await PersonaIdAsync();

        (await TenantRowAsync(conn, real)).Should().Be(before);
        (await conn.ScalarAsync<long>("SELECT count(*) FROM tenant_demo_config WHERE tenant_id = @t", ("t", real))).Should().Be(0);
        (await conn.ScalarAsync<long>("SELECT count(*) FROM team_tenant_access WHERE tenant_id = @t", ("t", real))).Should().Be(0);
        (await conn.ScalarAsync<long>("SELECT count(*) FROM tenant_members WHERE tenant_id = @t AND subject_id = @p",
            ("t", real), ("p", personaId))).Should().Be(0);

        using var persona = await PersonaClientAsync();
        var overview = await persona.PostAsJsonAsync("/api/v4/me/tenants/overview", new { });
        (await overview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tenants").GetArrayLength().Should().Be(10);
    }

    [Fact]
    public async Task The_persona_manages_nothing_and_belongs_only_to_demo_tenants()
    {
        await ProvisionAsync();
        var personaId = await PersonaIdAsync();

        await using var conn = await OpenPostgresAsync();
        (await conn.ScalarAsync<long>("SELECT count(*) FROM team_members WHERE subject_id = @p AND kind <> @staff",
            ("p", personaId), ("staff", TeamMemberKind.Staff))).Should().Be(0);
        (await conn.ScalarAsync<long>("SELECT count(*) FROM team_members WHERE subject_id = @p", ("p", personaId))).Should().Be(1);
        (await conn.ScalarAsync<long>(
            "SELECT count(*) FROM tenant_members m JOIN tenants t ON t.id = m.tenant_id WHERE m.subject_id = @p AND NOT t.is_demo",
            ("p", personaId))).Should().Be(0);
        (await conn.ScalarAsync<bool>("SELECT is_demo_subject FROM subjects WHERE id = @p", ("p", personaId))).Should().BeTrue();
    }

    [Fact]
    public async Task The_personas_views_and_rules_check_against_the_reports_engine()
    {
        await ProvisionAsync();
        var personaId = await PersonaIdAsync();

        using var scope = Fixture.Services.CreateScope();
        var catalogue = scope.ServiceProvider.GetRequiredService<IFlagRuleCatalogue>();
        var views = await scope.ServiceProvider.GetRequiredService<IPeopleViewRepository>().GetOfSubjectAsync(personaId, default);
        var rules = await scope.ServiceProvider.GetRequiredService<IFlagRuleRepository>().GetOfSubjectAsync(personaId, default);

        views.Should().HaveCount(3);
        rules.Should().HaveCount(3);
        foreach (var view in views)
            catalogue.CheckFacts(view.Query).Should().BeEmpty(view.Name);
        foreach (var rule in rules)
            catalogue.Check(rule.Dataset, rule.Field, rule.Days, rule.Comparison, rule.Value).Should().BeEmpty(rule.Field);
    }

    private static readonly object OwnView = new { name = "Visitor's view", lens = "Now" };

    private static readonly object StaleReading =
        new { dataset = "current_status", field = "hoursSinceReading", comparison = "Above", value = 3, level = "Hazard" };

    [Fact]
    public async Task With_team_writes_on_the_persona_writes_its_own_views_and_rules_and_nothing_of_the_team()
    {
        await ProvisionAsync();
        using var writes = AllowTeamWrites(true);
        using var persona = await PersonaClientAsync();
        var teamId = await OnlyTeamIdAsync(persona);
        var ruleId = await FirstRuleIdAsync(persona);

        var view = await persona.PostAsJsonAsync("/api/v4/me/people-views", OwnView);
        view.StatusCode.Should().Be(HttpStatusCode.Created, await view.Content.ReadAsStringAsync());
        var rule = await persona.PutAsJsonAsync($"/api/v4/me/flag-rules/{ruleId}", StaleReading);
        rule.StatusCode.Should().Be(HttpStatusCode.OK, await rule.Content.ReadAsStringAsync());

        (await persona.GetAsync($"/api/v4/me/teams/{teamId}/flag-rules")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await persona.PostAsJsonAsync($"/api/v4/me/teams/{teamId}/flag-rules", StaleReading)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await persona.PutAsJsonAsync($"/api/v4/me/teams/{teamId}/name", new { name = "Renamed" })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await persona.PostAsJsonAsync($"/api/v4/me/teams/{teamId}/access-links", new { })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await PermissionsAsync(persona)).GetProperty("refusedStaffWritesAsDemoSubject").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task With_team_writes_off_the_persona_reads_its_views_and_rules_and_writes_neither()
    {
        await ProvisionAsync();
        using var writes = AllowTeamWrites(false);
        using var persona = await PersonaClientAsync();
        var ruleId = await FirstRuleIdAsync(persona);

        var views = await persona.GetFromJsonAsync<JsonElement>("/api/v4/me/people-views");
        views.GetProperty("views").GetArrayLength().Should().Be(DemoClinicSeeds.Views.Count);
        (await persona.PostAsJsonAsync("/api/v4/me/people-views", OwnView)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await persona.PutAsJsonAsync($"/api/v4/me/flag-rules/{ruleId}", StaleReading)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await persona.DeleteAsync($"/api/v4/me/flag-rules/{ruleId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PermissionsAsync(persona)).GetProperty("refusedStaffWritesAsDemoSubject").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task With_team_writes_on_the_personal_visitor_is_in_no_team_and_writes_nothing()
    {
        await ProvisionAsync();
        using var writes = AllowTeamWrites(true);
        var session = await SessionAsync(HostSlug, persona: null);
        session.StatusCode.Should().Be(HttpStatusCode.OK, await session.Content.ReadAsStringAsync());
        var token = (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        using var visitor = AuthTestHelpers.CreateTenantBearerClient(Fixture, HostSlug, BaseDomain, token!);

        var teams = await visitor.GetFromJsonAsync<JsonElement>("/api/v4/me/teams");
        teams.GetProperty("items").GetArrayLength().Should().Be(0);
        teams.GetProperty("canCreate").GetBoolean().Should().BeFalse();
        (await visitor.PostAsJsonAsync("/api/v4/me/people-views", OwnView)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await visitor.PostAsJsonAsync("/api/v4/me/flag-rules", StaleReading)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await visitor.PostAsJsonAsync("/api/v4/me/teams", new { name = "Visitor team" })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await PermissionsAsync(visitor)).GetProperty("refusedStaffWritesAsDemoSubject").GetBoolean().Should().BeTrue();
    }

    /// <summary>Sets the demo's team writes for one test, and puts them back as they were.</summary>
    private Restore AllowTeamWrites(bool allowed)
    {
        var demoMode = Fixture.DemoModeService;
        var before = demoMode.AllowsTeamWrites;
        demoMode.AllowsTeamWrites = allowed;
        return new Restore(() => demoMode.AllowsTeamWrites = before);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    private static async Task<Guid> OnlyTeamIdAsync(HttpClient client)
    {
        var teams = await client.GetFromJsonAsync<JsonElement>("/api/v4/me/teams");
        return teams.GetProperty("items").EnumerateArray().Should().ContainSingle().Subject.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> FirstRuleIdAsync(HttpClient client)
    {
        var rules = await client.GetFromJsonAsync<JsonElement>("/api/v4/me/flag-rules");
        return rules.GetProperty("rules").EnumerateArray().First().GetProperty("rule").GetProperty("id").GetGuid();
    }

    private static Task<JsonElement> PermissionsAsync(HttpClient client) =>
        client.GetFromJsonAsync<JsonElement>("/api/v4/me/permissions");

    private static Task<long> PersonaCountAsync(NpgsqlConnection conn) =>
        conn.ScalarAsync<long>("SELECT count(*) FROM subjects WHERE is_demo_subject AND name = @name", ("name", DemoClinic.PersonaName));

    private async Task ProvisionClinicInScopeAsync()
    {
        await Task.Yield();
        using var scope = Fixture.Services.CreateScope();
        var host = await scope.ServiceProvider.GetRequiredService<ITenantRepository>().FindDemoTenantAsync(default);
        await scope.ServiceProvider.GetRequiredService<DemoTenantService>().ProvisionClinicAsync(host!);
    }

    private async Task ProvisionAsync()
    {
        using var client = AdminClient();
        var response = await client.PostAsync("/api/v4/admin/demo/provision", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private HttpClient AdminClient()
    {
        var client = Fixture.CreateApexClient();
        client.DefaultRequestHeaders.Add("X-Instance-Key", HashUtils.Sha256Hex(ApiIntegrationTestFixture.InstanceKey));
        client.DefaultRequestHeaders.Add("X-Instance-Service", "nocturne-demo");
        return client;
    }

    private async Task<HttpResponseMessage> SessionAsync(string slug, string? persona)
    {
        using var client = AuthTestHelpers.CreateTenantClient(Fixture, slug, BaseDomain);
        var query = persona is null ? "" : $"&as={Uri.EscapeDataString(persona)}";
        return await client.GetAsync($"/api/v4/demo/session?format=json{query}");
    }

    /// <summary>The persona's session, on the host's host, where the sign-in lands.</summary>
    private async Task<HttpClient> PersonaClientAsync()
    {
        var response = await SessionAsync(HostSlug, DemoClinic.SessionPersona);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        return AuthTestHelpers.CreateTenantBearerClient(Fixture, HostSlug, BaseDomain, token!);
    }

    private async Task<Guid> PersonaIdAsync()
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.ScalarAsync<Guid>(
            """
            SELECT m.subject_id FROM tenant_demo_config c
            JOIN tenants t ON t.id = c.tenant_id
            JOIN team_members m ON m.team_id = c.clinic_team_id
            WHERE t.slug = @host AND c.kind = 'host'
            """,
            ("host", HostSlug));
    }

    private static async Task<(string Slug, string Name, bool IsDemo, bool IsActive)> TenantRowAsync(NpgsqlConnection conn, Guid id) =>
        (await conn.QueryAsync(
            "SELECT slug, display_name, is_demo, is_active FROM tenants WHERE id = @id",
            r => (r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetBoolean(3)),
            ("id", id))).Single();

    /// <summary>The ids of every row the clinic is made of, by table, with the persona's 24-hour limits.</summary>
    private async Task<Dictionary<string, List<string>>> ClinicRowsAsync(Guid personaId)
    {
        await using var conn = await OpenPostgresAsync();
        var queries = new Dictionary<string, string>
        {
            ["tenants"] = "SELECT t.id::text || ':' || c.kind || ':' || coalesce(c.clinic_team_id::text, '') FROM tenants t JOIN tenant_demo_config c ON c.tenant_id = t.id",
            ["teams"] = "SELECT id::text FROM teams",
            ["team_members"] = "SELECT id::text FROM team_members",
            ["team_tenant_access"] = "SELECT id::text || ':' || limit_to_24_hours FROM team_tenant_access",
            ["team_tenant_access_roles"] = "SELECT team_tenant_access_id::text || ':' || tenant_role_id::text FROM team_tenant_access_roles",
            ["team_site_staff"] = "SELECT id::text || ':' || status FROM team_site_staff",
            ["team_groups"] = "SELECT id::text || ':' || name FROM team_groups",
            ["team_group_people"] = "SELECT team_group_id::text || ':' || tenant_id::text FROM team_group_people",
            ["team_group_staff"] = "SELECT team_group_id::text || ':' || team_member_id::text FROM team_group_staff",
            ["tenant_members"] = "SELECT id::text || ':' || limit_to_24_hours FROM tenant_members WHERE subject_id = @p",
            ["tenant_member_reasons"] = "SELECT r.id::text FROM tenant_member_reasons r JOIN tenant_members m ON m.id = r.tenant_member_id WHERE m.subject_id = @p",
            ["tenant_member_roles"] = "SELECT r.tenant_member_id::text || ':' || r.tenant_role_id::text FROM tenant_member_roles r JOIN tenant_members m ON m.id = r.tenant_member_id WHERE m.subject_id = @p",
            ["people_views"] = "SELECT id::text FROM people_views WHERE subject_id = @p",
            ["flag_rules"] = "SELECT id::text FROM flag_rules WHERE subject_id = @p",
        };

        var rows = new Dictionary<string, List<string>>();
        foreach (var (table, sql) in queries)
        {
            var ids = await conn.QueryAsync(sql, r => r.GetString(0), ("p", personaId));
            rows[table] = [.. ids.Order(StringComparer.Ordinal)];
        }

        rows.Values.Should().OnlyContain(ids => ids.Count > 0, "each table holds a part of the clinic");
        return rows;
    }
}

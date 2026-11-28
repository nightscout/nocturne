using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Reports;
using Nocturne.API.Services.Demo;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Demo;
using Nocturne.Core.Models.Identity;
using Nocturne.API.Services.Caching;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Extensions;
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
    public async Task A_visitor_reset_returns_the_personas_views_and_rules_to_their_seeds_and_keeps_its_sessions()
    {
        await ProvisionAsync();
        var personaId = await PersonaIdAsync();
        using var persona = await PersonaClientAsync();
        using (var scope = Fixture.Services.CreateScope())
        {
            var views = scope.ServiceProvider.GetRequiredService<IPeopleViewRepository>();
            var rules = scope.ServiceProvider.GetRequiredService<IFlagRuleRepository>();
            await views.CreateAsync(personaId, DemoClinicSeeds.Views[0] with { Name = "Visitor view" }, default);
            var seeded = (await views.GetOfSubjectAsync(personaId, default)).Single(v => v.Name == DemoClinicSeeds.Views[1].Name);
            await views.UpdateAsync(personaId, seeded.Id, DemoClinicSeeds.Views[1] with { Lens = PeopleLens.Review, ReviewDays = 14 }, default);
            await rules.DeleteAsync(personaId, (await rules.GetOfSubjectAsync(personaId, default))[0].Id, default);
        }
        var teamRule = await SeedTeamRuleAsync(await ClinicTeamIdAsync());
        var sessionsBefore = await SessionCountAsync(personaId);

        using (var admin = AdminClient())
        {
            var reset = await admin.PostAsync("/api/v4/admin/demo/visitor-reset", null);
            reset.StatusCode.Should().Be(HttpStatusCode.NoContent, await reset.Content.ReadAsStringAsync());
        }

        using (var scope = Fixture.Services.CreateScope())
        {
            var views = await scope.ServiceProvider.GetRequiredService<IPeopleViewRepository>().GetOfSubjectAsync(personaId, default);
            var rules = await scope.ServiceProvider.GetRequiredService<IFlagRuleRepository>().GetOfSubjectAsync(personaId, default);
            views.Select(v => (v.Name, v.Lens, v.ReviewDays))
                .Should().BeEquivalentTo(DemoClinicSeeds.Views.Select(v => (v.Name, v.Lens, v.ReviewDays)));
            rules.Select(r => (r.Dataset, r.Field, r.Days, r.Comparison, r.Value, r.Level))
                .Should().BeEquivalentTo(DemoClinicSeeds.Rules.Select(r => (r.Dataset, r.Field, r.Days, r.Comparison, r.Value, r.Level)));
        }
        (await PersonaIdAsync()).Should().Be(personaId);
        await using (var conn = await OpenPostgresAsync())
            (await conn.ScalarAsync<long>("SELECT count(*) FROM flag_rules WHERE id = @r", ("r", teamRule)))
                .Should().Be(1, "the team's own rules are not the persona's to lose");
        sessionsBefore.Should().BePositive();
        (await SessionCountAsync(personaId)).Should().Be(sessionsBefore);
        var overview = await persona.PostAsJsonAsync("/api/v4/me/tenants/overview", new { });
        overview.StatusCode.Should().Be(HttpStatusCode.OK, "a session issued before the visitor reset still works");
        (await overview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tenants").GetArrayLength().Should().Be(11);
    }

    [Fact]
    public async Task A_world_reset_keeps_nothing_of_the_clinic_and_leaves_what_a_fresh_provision_makes()
    {
        await ProvisionAsync();
        var fresh = await ClinicShapeAsync();
        var personaId = await PersonaIdAsync();
        using (await PersonaClientAsync())
        using (var scope = Fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IPeopleViewRepository>()
                .CreateAsync(personaId, DemoClinicSeeds.Views[0] with { Name = "Visitor view" }, default);
        var before = await ClinicRowsAsync(personaId);
        var personRolesBefore = await PersonRoleIdsAsync();

        using (var admin = AdminClient())
        {
            var reset = await admin.PostAsync("/api/v4/admin/demo/reset", null);
            reset.StatusCode.Should().Be(HttpStatusCode.OK, await reset.Content.ReadAsStringAsync());
        }

        var newPersonaId = await PersonaIdAsync();
        newPersonaId.Should().NotBe(personaId);
        var after = await ClinicRowsAsync(newPersonaId);
        foreach (var table in before.Keys.Where(t => t != "tenants"))
            after[table].Should().NotIntersectWith(before[table], "no {0} row from before the reset survives it", table);
        after["tenants"].Select(t => t.Split(':')[0]).Should().BeEquivalentTo(before["tenants"].Select(t => t.Split(':')[0]));
        (await PersonRoleIdsAsync()).Should().NotIntersectWith(personRolesBefore, "each person's site is wiped and re-seeded");
        await using (var conn = await OpenPostgresAsync())
        {
            (await conn.ScalarAsync<long>("SELECT count(*) FROM subjects WHERE id = @p", ("p", personaId))).Should().Be(0);
            (await SessionCountAsync(personaId)).Should().Be(0);
        }
        (await ClinicShapeAsync()).Should().BeEquivalentTo(fresh, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task A_world_reset_that_fails_at_a_persons_wipe_leaves_no_old_subject_after_the_next_provision()
    {
        await ProvisionAsync();
        var fresh = await ClinicShapeAsync();
        var personaId = await PersonaIdAsync();
        var demoMemberId = await DemoMemberIdAsync();
        using (await PersonaClientAsync())
            (await SessionAsync(HostSlug, persona: null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SessionCountAsync(personaId)).Should().BePositive();
        (await SessionCountAsync(demoMemberId)).Should().BePositive();

        var cache = new Mock<ITenantCache>();
        var wipes = 0;
        cache.Setup(c => c.RemoveTenantAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++wipes == 2 ? Task.FromException(new InvalidOperationException("A person's wipe failed")) : Task.CompletedTask);
        using (var scope = Fixture.Services.CreateScope())
        {
            var service = ActivatorUtilities.CreateInstance<DemoTenantService>(scope.ServiceProvider, cache.Object);
            await service.Invoking(s => s.ResetAsync()).Should().ThrowAsync<InvalidOperationException>();
        }

        await ProvisionAsync();

        await using (var conn = await OpenPostgresAsync())
        {
            foreach (var old in new[] { demoMemberId, personaId })
            {
                (await conn.ScalarAsync<long>("SELECT count(*) FROM subjects WHERE id = @s", ("s", old))).Should().Be(0);
                (await SessionCountAsync(old)).Should().Be(0);
            }
        }
        (await ClinicShapeAsync()).Should().BeEquivalentTo(fresh, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task A_world_reset_removes_the_clinic_team_whoever_else_has_joined_it()
    {
        await ProvisionAsync();
        var teamId = await ClinicTeamIdAsync();
        var otherMember = await AddTeamMemberAsync(teamId);
        var teamRule = await SeedTeamRuleAsync(teamId);

        using (var admin = AdminClient())
        {
            var reset = await admin.PostAsync("/api/v4/admin/demo/reset", null);
            reset.StatusCode.Should().Be(HttpStatusCode.OK, await reset.Content.ReadAsStringAsync());
        }

        await using var conn = await OpenPostgresAsync();
        foreach (var table in new[] { "teams WHERE id", "team_members WHERE team_id", "team_groups WHERE team_id", "team_tenant_access WHERE team_id" })
            (await conn.ScalarAsync<long>($"SELECT count(*) FROM {table} = @t", ("t", teamId))).Should().Be(0, table);
        (await conn.ScalarAsync<long>("SELECT count(*) FROM flag_rules WHERE id = @r", ("r", teamRule))).Should().Be(0);
        (await conn.ScalarAsync<long>("SELECT count(*) FROM subjects WHERE id = @s", ("s", otherMember)))
            .Should().Be(1, "an account that is not the demo's is left in place");
        (await ClinicTeamIdAsync()).Should().NotBe(teamId);
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("visitor-reset")]
    [InlineData("provision")]
    public async Task Each_clinic_write_waits_for_the_clinic_lock(string operation)
    {
        await ProvisionAsync();
        var personaId = await PersonaIdAsync();
        using (var views = Fixture.Services.CreateScope())
            await views.ServiceProvider.GetRequiredService<IPeopleViewRepository>()
                .CreateAsync(personaId, DemoClinicSeeds.Views[0] with { Name = "Visitor view" }, default);
        Guid hostId;
        await using (var conn = await OpenPostgresAsync())
            hostId = await conn.ScalarAsync<Guid>("SELECT id FROM tenants WHERE slug = @s", ("s", HostSlug));

        using var scope = Fixture.Services.CreateScope();
        await using var holder = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreatePlatformAsync();
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = holder.HoldWhileAsync(DemoTenantService.ClinicLockClass, hostId, async _ =>
        {
            acquired.TrySetResult();
            await release.Task;
        }, default);
        await acquired.Task;

        using var admin = AdminClient();
        var write = admin.PostAsync($"/api/v4/admin/demo/{operation}", null);
        await using (var conn = await OpenPostgresAsync())
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (await conn.ScalarAsync<long>(
                       "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND classid::bigint = @c",
                       ("c", (long)DemoTenantService.ClinicLockClass)) == 0)
            {
                DateTime.UtcNow.Should().BeBefore(deadline, "the {0} should be waiting on the clinic lock", operation);
                await Task.Delay(50);
            }
        }

        write.IsCompleted.Should().BeFalse();
        (await PersonaIdAsync()).Should().Be(personaId, "nothing is written while another holds the lock");
        await using (var conn = await OpenPostgresAsync())
            (await conn.ScalarAsync<long>(
                "SELECT count(*) FROM people_views WHERE subject_id = @p AND name = 'Visitor view'", ("p", personaId)))
                .Should().Be(1, "the visitor's view is untouched while another holds the lock");
        release.SetResult();
        await holding;
        using var response = await write;
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_world_reset_and_a_provision_at_once_leave_one_fresh_clinic()
    {
        await ProvisionAsync();
        var fresh = await ClinicShapeAsync();

        using var admin = AdminClient();
        var responses = await Task.WhenAll(
            admin.PostAsync("/api/v4/admin/demo/reset", null),
            admin.PostAsync("/api/v4/admin/demo/provision", null));

        responses.Should().OnlyContain(r => r.IsSuccessStatusCode);
        (await ClinicShapeAsync()).Should().BeEquivalentTo(fresh, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task A_world_reset_that_fails_between_wipe_and_reseed_is_repaired_by_the_next_provision()
    {
        await ProvisionAsync();
        var fresh = await ClinicShapeAsync();
        var failing = new Mock<ITenantService>();
        failing.Setup(t => t.SeedAfterResetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The re-seed failed"));

        using (var scope = Fixture.Services.CreateScope())
        {
            var service = ActivatorUtilities.CreateInstance<DemoTenantService>(scope.ServiceProvider, failing.Object);
            await service.Invoking(s => s.ResetAsync()).Should().ThrowAsync<InvalidOperationException>();
        }

        await using (var conn = await OpenPostgresAsync())
        {
            (await conn.ScalarAsync<long>("SELECT count(*) FROM teams")).Should().Be(0, "the reset failed after it tore the clinic down");
            (await conn.ScalarAsync<long>(
                "SELECT count(*) FROM tenant_roles r JOIN tenants t ON t.id = r.tenant_id WHERE t.slug = @s", ("s", HostSlug)))
                .Should().Be(0, "and before it re-seeded the host");
        }

        await ProvisionAsync();

        (await ClinicShapeAsync()).Should().BeEquivalentTo(fresh, options => options.WithStrictOrdering());
        (await SessionAsync(HostSlug, persona: null)).StatusCode.Should().Be(HttpStatusCode.OK);
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
    public async Task The_persona_writes_its_own_views_and_rules_and_nothing_of_the_team()
    {
        await ProvisionAsync();
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
    public async Task The_personal_visitor_is_in_no_team_and_writes_nothing()
    {
        await ProvisionAsync();
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

    [Fact]
    public async Task After_provision_and_seed_Lena_Tom_and_Rory_are_flagged_and_Katie_is_stale()
    {
        await ProvisionAsync();
        var originalDemoMode = Fixture.DemoModeService.IsEnabled;
        Fixture.DemoModeService.IsEnabled = true;
        try
        {
            await AssertSeededPeopleAreFlaggedAsync();
        }
        finally
        {
            Fixture.DemoModeService.IsEnabled = originalDemoMode;
        }
    }

    private async Task AssertSeededPeopleAreFlaggedAsync()
    {
        using (var admin = AdminClient())
        {
            foreach (var person in DemoClinic.People)
            {
                var seed = await admin.PostAsJsonAsync(
                    "/api/v4/admin/demo/seed-extras",
                    new { days = DemoClinic.PersonBackfillDays, includeGlucose = true, person = person.Key });
                seed.StatusCode.Should().Be(HttpStatusCode.OK, await seed.Content.ReadAsStringAsync());
            }
        }

        using var persona = await PersonaClientAsync();
        JsonElement overview = default;
        await Eventually.UntilAsync(
            "the persona's flag rules to be judged for every person",
            async () =>
            {
                var response = await persona.PostAsJsonAsync("/api/v4/me/tenants/overview", new { rules = "All" });
                overview = await response.Content.ReadFromJsonAsync<JsonElement>();
                return overview.GetProperty("peopleWithRulesPending").GetInt32() == 0;
            },
            patience: TimeSpan.FromMinutes(2),
            step: TimeSpan.FromSeconds(1),
            lastSeen: () => overview.ToString());

        JsonElement ItemOf(string key) => overview.GetProperty("tenants").EnumerateArray()
            .Single(t => t.GetProperty("slug").GetString() == DemoClinic.SlugOf(HostSlug, DemoClinic.People.Single(p => p.Key == key)));
        IEnumerable<string?> ReasonsOf(string key) => ItemOf(key).GetProperty("attention").EnumerateArray()
            .Select(a => a.GetProperty("kind").GetString());

        foreach (var flagged in new[] { "lena", "tom", "rory" })
        {
            ItemOf(flagged).GetProperty("latest").ValueKind.Should().NotBe(JsonValueKind.Null, $"{flagged} has readings: {ItemOf(flagged)}");
            ReasonsOf(flagged).Should().Contain("FlagRule", $"{flagged} meets a flag rule").And.NotContain("NoRecentReading");
        }

        ItemOf("katie").GetProperty("latest").ValueKind.Should().NotBe(JsonValueKind.Null, "Katie has readings, but old ones");
        ItemOf("katie").GetProperty("status").GetString().Should().Be("Stale");
        var quiet = ItemOf("katie").GetProperty("attention").EnumerateArray()
            .Single(a => a.GetProperty("kind").GetString() == "NoRecentReading");
        quiet.GetProperty("minutesSinceReading").GetInt32().Should().BeGreaterThan(120);
    }

    private static Task<long> PersonaCountAsync(NpgsqlConnection conn) =>
        conn.ScalarAsync<long>("SELECT count(*) FROM subjects WHERE is_demo_subject AND name = @name", ("name", DemoClinic.PersonaName));

    private async Task ProvisionClinicInScopeAsync()
    {
        await Task.Yield();
        using var scope = Fixture.Services.CreateScope();
        var host = await scope.ServiceProvider.GetRequiredService<ITenantRepository>().FindDemoTenantAsync(default);
        await scope.ServiceProvider.GetRequiredService<DemoTenantService>().ProvisionAsync(host!.Id);
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

    private async Task<Guid> DemoMemberIdAsync()
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.ScalarAsync<Guid>(
            "SELECT m.subject_id FROM tenant_members m JOIN tenants t ON t.id = m.tenant_id WHERE t.slug = @host AND m.username = 'demo'",
            ("host", HostSlug));
    }

    private async Task<Guid> ClinicTeamIdAsync()
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.ScalarAsync<Guid>(
            "SELECT c.clinic_team_id FROM tenant_demo_config c JOIN tenants t ON t.id = c.tenant_id WHERE t.slug = @host",
            ("host", HostSlug));
    }

    private async Task<Guid> AddTeamMemberAsync(Guid teamId)
    {
        var subjectId = Guid.CreateVersion7();
        using var scope = Fixture.Services.CreateScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().CreatePlatformAsync();
        db.Subjects.Add(new SubjectEntity { Id = subjectId, Name = "Another Staff Member", IsActive = true });
        db.TeamMembers.Add(new TeamMemberEntity { Id = Guid.CreateVersion7(), TeamId = teamId, SubjectId = subjectId, Kind = TeamMemberKind.Staff });
        await db.SaveChangesAsync();
        return subjectId;
    }

    /// <summary>A rule of the team's own, written past the policies on <c>flag_rules</c>, which let only a manager write one.</summary>
    private async Task<Guid> SeedTeamRuleAsync(Guid teamId)
    {
        var seed = DemoClinicSeeds.Rules[0];
        var ruleId = Guid.CreateVersion7();
        await using var conn = await OpenPostgresAsync();
        await conn.ExecuteAsync(
            """
            INSERT INTO flag_rules (id, team_id, dataset, field, days, comparison, value, level, created_at, updated_at)
            VALUES (@id, @team, @dataset, @field, @days, @comparison, 5, @level, now(), now())
            """,
            ("id", ruleId), ("team", teamId), ("dataset", seed.Dataset), ("field", seed.Field), ("days", seed.Days!.Value),
            ("comparison", FlagRuleEntity.Above), ("level", FlagRuleEntity.Warn));
        return ruleId;
    }

    private async Task<long> SessionCountAsync(Guid subjectId)
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.ScalarAsync<long>("SELECT count(*) FROM refresh_tokens WHERE subject_id = @s", ("s", subjectId));
    }

    private async Task<List<Guid>> PersonRoleIdsAsync()
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.QueryAsync(
            "SELECT r.id FROM tenant_roles r JOIN tenant_demo_config c ON c.tenant_id = r.tenant_id WHERE c.kind = 'person'",
            r => r.GetGuid(0));
    }

    /// <summary>
    /// The clinic as a provision leaves it, by name and slug rather than id, with the demo's subjects, so two
    /// provisions that made it afresh read the same.
    /// </summary>
    private async Task<Dictionary<string, List<string>>> ClinicShapeAsync()
    {
        var personaId = await PersonaIdAsync();
        await using var conn = await OpenPostgresAsync();
        var queries = new Dictionary<string, string>
        {
            ["tenants"] = "SELECT t.slug || ':' || c.kind || ':' || (c.clinic_team_id IS NOT NULL) || ':' || t.allow_public_docs FROM tenants t JOIN tenant_demo_config c ON c.tenant_id = t.id",
            ["teams"] = "SELECT name FROM teams",
            ["team_members"] = "SELECT kind FROM team_members",
            ["team_tenant_access"] = "SELECT t.slug || ':' || a.limit_to_24_hours FROM team_tenant_access a JOIN tenants t ON t.id = a.tenant_id",
            ["team_tenant_access_roles"] = "SELECT t.slug || ':' || r.slug FROM team_tenant_access_roles ar JOIN team_tenant_access a ON a.id = ar.team_tenant_access_id JOIN tenants t ON t.id = a.tenant_id JOIN tenant_roles r ON r.id = ar.tenant_role_id",
            ["team_site_staff"] = "SELECT status FROM team_site_staff",
            ["team_groups"] = "SELECT name FROM team_groups",
            ["team_group_people"] = "SELECT g.name || ':' || t.slug FROM team_group_people p JOIN team_groups g ON g.id = p.team_group_id JOIN tenants t ON t.id = p.tenant_id",
            ["team_group_staff"] = "SELECT g.name FROM team_group_staff s JOIN team_groups g ON g.id = s.team_group_id",
            ["tenant_members"] = "SELECT t.slug || ':' || m.limit_to_24_hours FROM tenant_members m JOIN tenants t ON t.id = m.tenant_id WHERE m.subject_id = @p",
            ["tenant_member_roles"] = "SELECT t.slug || ':' || r.slug FROM tenant_member_roles mr JOIN tenant_members m ON m.id = mr.tenant_member_id JOIN tenants t ON t.id = m.tenant_id JOIN tenant_roles r ON r.id = mr.tenant_role_id WHERE m.subject_id = @p",
            ["people_views"] = "SELECT name FROM people_views WHERE subject_id = @p",
            ["flag_rules"] = "SELECT field || ':' || value FROM flag_rules WHERE subject_id = @p",
            ["tenant_roles"] = "SELECT t.slug || ':' || r.slug FROM tenant_roles r JOIN tenants t ON t.id = r.tenant_id JOIN tenant_demo_config c ON c.tenant_id = t.id",
            ["demo_members"] = "SELECT t.slug || ':' || s.is_demo_subject FROM tenant_members m JOIN tenants t ON t.id = m.tenant_id JOIN subjects s ON s.id = m.subject_id WHERE m.username = 'demo'",
            ["public_members"] = "SELECT t.slug FROM tenant_members m JOIN tenants t ON t.id = m.tenant_id JOIN tenant_demo_config c ON c.tenant_id = t.id JOIN subjects s ON s.id = m.subject_id WHERE s.is_system_subject",
            ["demo_subjects"] = "SELECT name FROM subjects WHERE is_demo_subject",
        };

        var rows = new Dictionary<string, List<string>>();
        foreach (var (table, sql) in queries)
        {
            var values = await conn.QueryAsync(sql, r => r.GetString(0), ("p", personaId));
            rows[table] = [.. values.Order(StringComparer.Ordinal)];
        }

        return rows;
    }
}

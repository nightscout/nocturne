using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Reports;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// The overview of a staff member who follows 300 people, more than the overview reads at once. A cold read answers
/// with every person pending rather than waiting on figures, a warm read answers with none pending, and each item holds
/// its own person's latest reading.
/// </summary>
/// <remarks>
/// The timing measurement (medians of <see cref="Reads"/> reads, warm and cold, for the full and the team rule set, and
/// the contexts one read opens) runs only when <c>NOCTURNE_PEOPLE_OVERVIEW_TIMING=1</c>. Run it built in Release:
/// <c>dotnet test tests/Integration/Nocturne.API.Tests -c Release --filter "FullyQualifiedName~PeopleOverviewScaleTests" --logger "console;verbosity=detailed"</c>.
/// </remarks>
[Collection(ApiIntegrationShards.Four)]
[Trait("Category", "Integration")]
public class PeopleOverviewScaleTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private const int People = 300;
    private const int Reads = 20;
    private const int SitesCreatedAtOnce = 8;
    private const string TimingVariable = "NOCTURNE_PEOPLE_OVERVIEW_TIMING";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_cold_read_of_300_people_waits_on_no_figure_and_a_warm_read_gives_each_person_their_own_reading()
    {
        var watch = Stopwatch.StartNew();
        var owner = await SeedOwnerAsync();
        var staff = await SeedBareSubjectAsync("Crowd Nurse");
        using var staffClient = await SessionAtApexAsync(staff);
        var sites = await SeedSitesAsync(owner);
        await FollowDirectlyAsync(staff, sites);
        await SeedReadingsAsync(sites, readingsEach: 36);
        (await staffClient.PostAsJsonAsync("/api/v4/me/flag-rules",
            new { dataset = "glucose_statistics", field = "timeBelow", days = 14, comparison = "Above", value = 4 }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var seeded = watch.Elapsed;

        var cold = await ReadAsync(staffClient, "All");
        await Fixture.Services.GetRequiredService<FlagFactReads>().WhenIdleAsync();
        var warm = await ReadAsync(staffClient, "All");

        cold.GetProperty("peopleWithRulesPending").GetInt32().Should().Be(People, "a cold read answers before any figure is read");
        warm.GetProperty("peopleWithRulesPending").GetInt32().Should().Be(0);
        var expected = sites.Select((site, i) => (site, mgdl: LatestOf(i))).ToDictionary(s => s.site, s => s.mgdl);
        foreach (var overview in new[] { cold, warm })
        {
            overview.GetProperty("tenants").EnumerateArray()
                .Select(t => (Tenant: t.GetProperty("tenantId").GetGuid(), Mgdl: t.GetProperty("latest").GetProperty("mgdl").GetDouble()))
                .Should().BeEquivalentTo(expected.Select(e => (Tenant: e.Key, Mgdl: e.Value)), "each item holds its own person's reading");
        }

        Output.WriteLine($"seeded in {seeded.TotalSeconds:F1} s; the test took {watch.Elapsed.TotalSeconds:F1} s");
    }

    [TimingFact]
    public async Task The_overview_of_300_people_through_a_team_reads_within_budget_warm_and_cold()
    {
        var owner = await SeedOwnerAsync();
        var (manager, _) = await SeedSubjectAsync("Crowd Manager");
        var staff = await SeedBareSubjectAsync("Crowd Nurse");
        using var ownerClient = await SessionAtApexAsync(owner);
        using var managerClient = await SessionAtApexAsync(manager);
        using var staffClient = await SessionAtApexAsync(staff);

        var sites = await SeedSitesAsync(owner);
        var teamId = await TeamWithStaffAsync(managerClient, staff);
        await ApproveEveryoneAsync(managerClient, ownerClient, owner, teamId);
        await SeedReadingsAsync(sites, readingsEach: 288);

        (await staffClient.PostAsJsonAsync("/api/v4/me/flag-rules",
            new { dataset = "glucose_statistics", field = "timeBelow", days = 14, comparison = "Above", value = 4 }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await staffClient.PostAsJsonAsync("/api/v4/me/flag-rules",
            new { dataset = "current_status", field = "hoursSinceReading", comparison = "Above", value = 2 }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await managerClient.PostAsJsonAsync($"/api/v4/me/teams/{teamId}/flag-rules",
            new { dataset = "glucose_statistics", field = "timeAbove", days = 14, comparison = "Above", value = 25 }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var results = new List<(string Rules, string State, double MedianMs, double MaxMs, int Pending)>();
        foreach (var rules in new[] { "All", "Team" })
        {
            var cold = await TimeReadsAsync(staffClient, rules, cold: true);
            var warm = await TimeReadsAsync(staffClient, rules, cold: false);
            results.Add((rules, "cold", Median(cold.Ms), cold.Ms.Max(), cold.Pending.Min()));
            results.Add((rules, "warm", Median(warm.Ms), warm.Ms.Max(), warm.Pending.Max()));

            cold.People.Should().AllSatisfy(count => count.Should().Be(People));
            cold.Pending.Should().AllSatisfy(pending => pending.Should().BeGreaterThan(0),
                "a cold read answers before the figures are read rather than waiting on them");
            warm.Pending.Should().AllSatisfy(pending => pending.Should().Be(0));
        }

        await WriteRoundTripsAsync(staffClient);
        foreach (var (rules, state, median, max, pending) in results)
            Output.WriteLine($"overview rules={rules} {state}: median {median:F1} ms, max {max:F1} ms over {Reads} reads, pending {pending}");

        results.Should().AllSatisfy(r => r.MedianMs.Should().BeLessThan(Budget.TotalMilliseconds));
    }

    /// <summary>A fact that runs only when <see cref="TimingVariable"/> is <c>1</c>.</summary>
    private sealed class TimingFactAttribute : FactAttribute
    {
        public TimingFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(TimingVariable) != "1")
                Skip = $"A timing measurement: set {TimingVariable}=1 to run it.";
        }
    }

    /// <summary>The latest reading seeded for the <paramref name="index"/>th person, distinct for every person.</summary>
    private static double LatestOf(int index) => 40 + index;

    /// <summary>
    /// <see cref="Reads"/> reads of the overview. A cold read follows an empty cache; a warm read follows a read whose
    /// background figure reads have all finished.
    /// </summary>
    private async Task<(List<double> Ms, List<int> Pending, List<int> People)> TimeReadsAsync(
        HttpClient client, string rules, bool cold)
    {
        var reads = Fixture.Services.GetRequiredService<FlagFactReads>();
        var memory = (MemoryCache)Fixture.Services.GetRequiredService<IMemoryCache>();
        await ReadAsync(client, rules);
        await reads.WhenIdleAsync();

        var ms = new List<double>();
        var pending = new List<int>();
        var people = new List<int>();
        for (var i = 0; i < Reads; i++)
        {
            await reads.WhenIdleAsync();
            if (cold)
                memory.Clear();

            var watch = Stopwatch.StartNew();
            var overview = await ReadAsync(client, rules);
            watch.Stop();

            ms.Add(watch.Elapsed.TotalMilliseconds);
            pending.Add(overview.GetProperty("peopleWithRulesPending").GetInt32());
            people.Add(overview.GetProperty("tenants").GetArrayLength());
        }

        await reads.WhenIdleAsync();
        return (ms, pending, people);
    }

    /// <summary>
    /// The contexts one warm full read opens, by caller, and the transactions the database committed meanwhile, which
    /// show what each person costs the read. The transaction count takes in any other work the database did then.
    /// </summary>
    private async Task WriteRoundTripsAsync(HttpClient client)
    {
        await Fixture.Services.GetRequiredService<FlagFactReads>().WhenIdleAsync();
        await using var conn = await OpenPostgresAsync();
        const string Committed = "SELECT xact_commit FROM pg_stat_database WHERE datname = current_database()";
        await Task.Delay(TimeSpan.FromSeconds(2));
        var before = await conn.ScalarAsync<long>(Committed);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v4/me/tenants/overview")
        {
            Content = JsonContent.Create(new { rules = "All" }),
        };
        var id = ContextOpenLog.Tag(request);
        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);

        await Task.Delay(TimeSpan.FromSeconds(2));
        var after = await conn.ScalarAsync<long>(Committed);
        var opens = Fixture.Services.GetRequiredService<ContextOpenLog>().OpensOf(id);
        Output.WriteLine($"one warm full read: {opens.Count} contexts, {after - before - 1} transactions committed");
        foreach (var caller in opens.GroupBy(o => o.Caller).OrderByDescending(g => g.Count()))
            Output.WriteLine($"  {caller.Count(),5} {caller.Key}");
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string rules)
    {
        var response = await client.PostAsJsonAsync("/api/v4/me/tenants/overview", new { rules });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    /// <summary>The 300 sites, each owned by <paramref name="owner"/>, in the order they were seeded.</summary>
    private async Task<List<Guid>> SeedSitesAsync(Guid owner)
    {
        var run = Guid.NewGuid().ToString("N")[..6];
        var sites = new Guid[People];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, People),
            new ParallelOptions { MaxDegreeOfParallelism = SitesCreatedAtOnce },
            async (i, ct) =>
            {
                var slug = $"crowd{i:000}-{run}";
                using var scope = Fixture.Services.CreateScope();
                sites[i] = (await scope.ServiceProvider.GetRequiredService<ITenantService>()
                    .CreateWithOwnerAsync(slug, slug, owner, null, ct)).Id;
            });
        return [.. sites];
    }

    /// <summary><paramref name="staff"/> as a member of every one of <paramref name="sites"/>, with what a caretaker may do.</summary>
    private async Task FollowDirectlyAsync(Guid staff, List<Guid> sites)
    {
        await using var conn = await OpenPostgresAsync();
        await conn.ExecuteAsync("""
            INSERT INTO tenant_members (id, tenant_id, subject_id, direct_permissions, sys_created_at, sys_updated_at, limit_to_24_hours)
            SELECT gen_random_uuid(), site, @subject, @permissions::jsonb, now(), now(), false FROM unnest(@sites) AS site;
            """,
            ("subject", staff), ("permissions", JsonSerializer.Serialize(RoleSeeds.Permissions[RoleSeeds.Caretaker])), ("sites", sites.ToArray()));
    }

    /// <summary>
    /// Five-minute readings for each site, the newest ten minutes ago carrying <see cref="LatestOf"/> for that site.
    /// </summary>
    private async Task SeedReadingsAsync(List<Guid> sites, int readingsEach)
    {
        await using var conn = await OpenPostgresAsync();
        var newest = DateTime.UtcNow.AddMinutes(-10);
        for (var i = 0; i < sites.Count; i++)
        {
            await conn.PinTenantAsync(sites[i]);
            await conn.ExecuteAsync("""
                INSERT INTO sensor_glucose (id, tenant_id, "timestamp", mgdl)
                SELECT gen_random_uuid(), @site, @newest - make_interval(mins => 5 * g),
                       CASE WHEN g = 0 THEN @latest ELSE 60 + (g * 7) % 200 END
                FROM generate_series(0, @count - 1) AS g;
                """,
                ("site", sites[i]), ("newest", newest), ("latest", LatestOf(i)), ("count", readingsEach));
        }
    }

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

    /// <summary>
    /// The owner approves the team's caretaker access link for every one of their sites, each approval from a client
    /// address of its own so the approvals stay under the per-client rate limit.
    /// </summary>
    private async Task ApproveEveryoneAsync(HttpClient managerClient, HttpClient ownerClient, Guid owner, Guid teamId)
    {
        var link = await managerClient.PostAsJsonAsync(
            $"/api/v4/me/teams/{teamId}/access-links", new { roleSlug = RoleSeeds.Caretaker, limitTo24Hours = false, maxUses = (int?)null });
        link.StatusCode.Should().Be(HttpStatusCode.Created);
        var token = (await link.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var sites = (await ownerClient.GetFromJsonAsync<JsonElement>($"/api/v4/me/teams/access-links/{token}"))
            .GetProperty("sites").EnumerateArray().ToList();
        sites.Should().HaveCount(People);

        foreach (var site in sites)
        {
            var role = site.GetProperty("roles").EnumerateArray()
                .Single(r => r.GetProperty("slug").GetString() == RoleSeeds.Caretaker).GetProperty("id").GetGuid();
            using var approver = await SessionAtApexAsync(owner);
            var approved = await approver.PostAsJsonAsync($"/api/v4/me/teams/access-links/{token}/approve",
                new { tenantId = site.GetProperty("tenantId").GetGuid(), roleIds = new[] { role }, limitTo24Hours = false, accessEndsAt = (DateTimeOffset?)null });
            approved.StatusCode.Should().Be(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());
        }
    }

    private async Task<HttpClient> SessionAtApexAsync(Guid subject)
    {
        var client = Fixture.CreateApexClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subject));
        return client;
    }

    /// <summary>The owner of every site, with a passkey, without which their sites answer only with the setup gate.</summary>
    private async Task<Guid> SeedOwnerAsync()
    {
        var owner = await SeedBareSubjectAsync("Crowd Owner");
        await using var conn = await OpenPostgresAsync();
        await conn.ExecuteAsync("""
            INSERT INTO passkey_credentials (id, subject_id, credential_id, public_key, sign_count, transports, label, created_at)
            VALUES (@id, @subject, @credential, @credential, 0, '{}', 'crowd owner', now());
            """, ("id", Guid.CreateVersion7()), ("subject", owner), ("credential", Guid.NewGuid().ToByteArray()));
        return owner;
    }

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

    private async Task<(Guid Subject, string AccessToken)> SeedSubjectAsync(string name)
    {
        using (var provisioning = CreateAuthenticatedClient())
            (await provisioning.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var conn = await OpenPostgresAsync();
        return await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, await AuthTestHelpers.GetTenantIdAsync(conn), name, [Scope.GlucoseRead]);
    }
}

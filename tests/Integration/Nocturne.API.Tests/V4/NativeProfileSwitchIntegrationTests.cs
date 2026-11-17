using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.V4;

/// <summary>
/// Profile switches a person records through <c>POST /api/v4/profile/activations</c>: in force on v4
/// unless an AID system reports its pump, never on the v1 or v3 treatments feed, kept on the schedules
/// they were recorded with, ended and deleted only by a writer of therapy, and only in their own tenant.
/// </summary>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public class NativeProfileSwitchIntegrationTests : ApiIntegrationTestBase
{
    private const string Activations = "/api/v4/profile/activations";

    public NativeProfileSwitchIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private static object ProfileDocument(string name, double basal = 1.0) => new
    {
        _id = "5f8d0c1e8a7b4c3d9e5f1a01",
        defaultProfile = name,
        store = new Dictionary<string, object>
        {
            [name] = new
            {
                dia = 4,
                carbratio = new[] { new { time = "00:00", value = 10, timeAsSeconds = 0 } },
                sens = new[] { new { time = "00:00", value = 50, timeAsSeconds = 0 } },
                basal = new[] { new { time = "00:00", value = basal, timeAsSeconds = 0 } },
                target_low = new[] { new { time = "00:00", value = 80, timeAsSeconds = 0 } },
                target_high = new[] { new { time = "00:00", value = 120, timeAsSeconds = 0 } },
                units = "mg/dl",
                timezone = "UTC",
            },
        },
        startDate = DateTime.UtcNow.AddHours(-1).ToString("o"),
    };

    private static async Task StoreProfileAsync(HttpClient client, string name = "Rhys", double basal = 1.0)
    {
        var response = await client.PutAsJsonAsync("/api/v1/profile", ProfileDocument(name, basal));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonNode> SwitchAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync(Activations, body);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static async Task<JsonNode?> ActiveAsync(HttpClient client)
    {
        var response = await client.GetAsync($"{Activations}/active");
        if (response.StatusCode == HttpStatusCode.NoContent)
            return null;
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonNode> StatusAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonNode>($"{Activations}/status"))!;

    private async Task<double?> BasalNowAsync()
    {
        using var scope = Fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(
            new TenantContext(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, false));
        return await scope.ServiceProvider.GetRequiredService<IBasalRateResolver>()
            .GetBasalRateAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static object AapsEffectiveSwitch(string id) => new
    {
        _id = id,
        eventType = "Note",
        created_at = DateTime.UtcNow.AddMinutes(-30).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        enteredBy = "openaps://AndroidAPS",
        notes = "Pump",
        profileJson = """{"units":"mg/dl","dia":5,"timezone":"UTC","sens":[{"time":"00:00","value":40}],"carbratio":[{"time":"00:00","value":10}],"basal":[{"time":"00:00","value":0.7}],"target_low":[{"time":"00:00","value":100}],"target_high":[{"time":"00:00","value":110}]}""",
        originalProfileName = "Pump",
        originalCustomizedName = "Pump",
        originalTimeshift = 0,
        originalPercentage = 100,
        originalDuration = 0,
    };

    [Fact]
    public async Task A_recorded_switch_is_in_force_on_v4_and_absent_from_every_treatments_feed()
    {
        var client = CreateAuthenticatedClient();
        await StoreProfileAsync(client);

        var created = await SwitchAsync(client, new { baseProfileName = "Rhys", percentage = 110, timeshiftHours = 1, notes = "sick day" });
        created["origin"]!.GetValue<string>().Should().Be("User");
        created["phase"]!.GetValue<string>().Should().Be("Requested");
        created["dataSource"]!.GetValue<string>().Should().Be("manual");
        created["notes"]!.GetValue<string>().Should().Be("sick day");

        var active = (await ActiveAsync(client))!;
        active["id"]!.GetValue<string>().Should().Be(created["id"]!.GetValue<string>());
        active["percentage"]!.GetValue<int>().Should().Be(110);
        active["timeshiftHours"]!.GetValue<double>().Should().Be(1);

        (await client.GetFromJsonAsync<JsonArray>("/api/v1/treatments"))!.Should().BeEmpty(
            "AAPS imports every Profile Switch it reads on the treatments feed");
        (await client.GetFromJsonAsync<JsonNode>("/api/v3/treatments"))!["result"]!.AsArray().Should().BeEmpty();
        (await client.GetFromJsonAsync<JsonNode>("/api/v3/treatments/history/0"))!["result"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task A_switch_to_a_profile_not_in_the_store_or_out_of_range_is_refused()
    {
        var client = CreateAuthenticatedClient();
        await StoreProfileAsync(client);

        (await client.PostAsJsonAsync(Activations, new { baseProfileName = "Holiday" })).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync(Activations, new { baseProfileName = "Rhys", percentage = 500 })).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync(Activations, new { baseProfileName = "Rhys", timeshiftHours = 24 })).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await ActiveAsync(client)).Should().BeNull();
    }

    [Fact]
    public async Task Ending_a_switch_ends_it_now_and_deleting_it_removes_it()
    {
        var client = CreateAuthenticatedClient();
        await StoreProfileAsync(client);
        var created = await SwitchAsync(client, new { baseProfileName = "Rhys", percentage = 120, durationMinutes = 120 });
        var id = created["id"]!.GetValue<string>();

        var end = await client.PostAsync($"{Activations}/{id}/end", null);
        end.StatusCode.Should().Be(HttpStatusCode.OK, await end.Content.ReadAsStringAsync());
        var ended = JsonNode.Parse(await end.Content.ReadAsStringAsync())!;
        ended["endMills"]!.GetValue<long>().Should().BeLessThan(created["startMills"]!.GetValue<long>() + 120 * 60_000);
        (await ActiveAsync(client)).Should().BeNull();
        (await client.PostAsync($"{Activations}/{id}/end", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await client.DeleteAsync($"{Activations}/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<JsonArray>(Activations))!.Should().BeEmpty();
        (await client.DeleteAsync($"{Activations}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_profile_edit_leaves_the_switch_on_its_schedules_and_switching_again_takes_them_up()
    {
        var client = CreateAuthenticatedClient();
        await StoreProfileAsync(client);
        var id = (await SwitchAsync(client, new { baseProfileName = "Rhys", percentage = 150 }))["id"]!.GetValue<string>();
        (await BasalNowAsync()).Should().BeApproximately(1.5, 1e-9);
        (await StatusAsync(client))["recordedProfileChanged"]!.GetValue<bool>().Should().BeFalse();

        await StoreProfileAsync(client, basal: 2.0);

        (await BasalNowAsync()).Should().BeApproximately(1.5, 1e-9, "the switch runs the schedules it was recorded with");
        var status = await StatusAsync(client);
        status["recordedProfileChanged"]!.GetValue<bool>().Should().BeTrue();

        var renew = await client.PostAsync($"{Activations}/{id}/renew", null);
        renew.StatusCode.Should().Be(HttpStatusCode.Created, await renew.Content.ReadAsStringAsync());
        var renewed = JsonNode.Parse(await renew.Content.ReadAsStringAsync())!;
        renewed["percentage"]!.GetValue<int>().Should().Be(150);
        renewed["isTemporary"]!.GetValue<bool>().Should().BeFalse();
        (await BasalNowAsync()).Should().BeApproximately(3.0, 1e-9);
        (await StatusAsync(client))["recordedProfileChanged"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task While_an_aid_system_reports_its_pump_a_recorded_switch_is_kept_but_does_not_apply()
    {
        var client = CreateAuthenticatedClient();
        await StoreProfileAsync(client);
        var post = await client.PostAsJsonAsync("/api/v1/treatments", new[] { AapsEffectiveSwitch("6500000000000000000000e1") });
        post.StatusCode.Should().Be(HttpStatusCode.OK, await post.Content.ReadAsStringAsync());

        var recorded = await SwitchAsync(client, new { baseProfileName = "Rhys", percentage = 150 });

        var status = await StatusAsync(client);
        status["recordedSwitchesApply"]!.GetValue<bool>().Should().BeFalse();
        status["recorded"]!["id"]!.GetValue<string>().Should().Be(recorded["id"]!.GetValue<string>());
        status["reported"]!["phase"]!.GetValue<string>().Should().Be("Effective");
        status["active"]!["id"]!.GetValue<string>().Should().Be(status["reported"]!["id"]!.GetValue<string>());
        (await BasalNowAsync()).Should().BeApproximately(0.7, 1e-9, "Nocturne follows the profile the pump reports");
    }

    [Fact]
    public async Task A_careportal_switch_is_the_uploaders_to_end_or_delete()
    {
        var client = CreateAuthenticatedClient();
        await StoreProfileAsync(client);
        var post = await client.PostAsJsonAsync("/api/v1/treatments", new[]
        {
            new
            {
                _id = "6500000000000000000000f2",
                eventType = "Profile Switch",
                created_at = DateTime.UtcNow.AddMinutes(-10).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                enteredBy = "Rhys",
                profile = "Rhys",
                percentage = 100,
                duration = 0,
            },
        });
        post.StatusCode.Should().Be(HttpStatusCode.OK, await post.Content.ReadAsStringAsync());
        var active = (await ActiveAsync(client))!;
        active["origin"]!.GetValue<string>().Should().Be("User");
        var id = active["id"]!.GetValue<string>();

        var end = await client.PostAsync($"{Activations}/{id}/end", null);
        end.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await end.Content.ReadAsStringAsync()).Should().Contain("uploaded as a treatment");
        (await client.DeleteAsync($"{Activations}/{id}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.GetFromJsonAsync<JsonArray>("/api/v1/treatments"))!.Should().ContainSingle();
    }

    [Fact]
    public async Task An_uploaders_switch_is_its_own_to_end_or_delete()
    {
        var client = CreateAuthenticatedClient();
        var post = await client.PostAsJsonAsync("/api/v1/treatments", new[]
        {
            new
            {
                _id = "6500000000000000000000f1",
                eventType = "Profile Switch",
                created_at = DateTime.UtcNow.AddMinutes(-10).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                enteredBy = "openaps://AndroidAPS",
                profile = "Rhys",
                originalProfileName = "Rhys",
                percentage = 100,
                duration = 0,
            },
        });
        post.StatusCode.Should().Be(HttpStatusCode.OK, await post.Content.ReadAsStringAsync());
        var id = (await ActiveAsync(client))!["id"]!.GetValue<string>();

        (await client.PostAsync($"{Activations}/{id}/end", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.DeleteAsync($"{Activations}/{id}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ActiveAsync(client))!["id"]!.GetValue<string>().Should().Be(id);
    }

    [Fact]
    public async Task A_credential_that_only_reads_therapy_cannot_switch_end_or_delete()
    {
        var owner = CreateAuthenticatedClient();
        await StoreProfileAsync(owner);
        var id = (await SwitchAsync(owner, new { baseProfileName = "Rhys" }))["id"]!.GetValue<string>();

        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (subjectId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Therapy Reader");
        var sessionToken = await AuthTestHelpers.IssueSessionTokenAsync(Fixture, subjectId);
        using var subject = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, sessionToken);
        var grant = await subject.PostAsJsonAsync("/api/auth/direct-grants", new { label = "therapy-reader", scopes = new[] { "therapy.read" } });
        grant.StatusCode.Should().Be(HttpStatusCode.OK, await grant.Content.ReadAsStringAsync());
        var token = JsonSerializer.Deserialize<JsonElement>(await grant.Content.ReadAsStringAsync()).GetProperty("token").GetString()!;
        using var reader = AuthTestHelpers.CreateBearerClient(Fixture, token);

        (await reader.GetAsync($"{Activations}/active")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await reader.PostAsJsonAsync(Activations, new { baseProfileName = "Rhys" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reader.PostAsync($"{Activations}/{id}/end", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reader.DeleteAsync($"{Activations}/{id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ActiveAsync(owner))!["id"]!.GetValue<string>().Should().Be(id);
    }

    [Fact]
    public async Task Another_tenant_neither_sees_nor_changes_a_switch()
    {
        var owner = CreateAuthenticatedClient();
        await StoreProfileAsync(owner);
        var id = (await SwitchAsync(owner, new { baseProfileName = "Rhys" }))["id"]!.GetValue<string>();

        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        var slug = $"tenant-b-{Guid.NewGuid():N}"[..20];
        var tenantB = await AuthTestHelpers.SeedTenantAsync(Fixture, slug, "Tenant B");
        var (_, tokenB) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantB, "Tenant B User");
        using var clientB = AuthTestHelpers.CreateTenantBearerClient(Fixture, slug, AuthTestHelpers.GetBaseDomain(ApiClient), tokenB);

        (await ActiveAsync(clientB)).Should().BeNull();
        (await clientB.PostAsync($"{Activations}/{id}/end", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await clientB.DeleteAsync($"{Activations}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ActiveAsync(owner))!["id"]!.GetValue<string>().Should().Be(id);
    }
}

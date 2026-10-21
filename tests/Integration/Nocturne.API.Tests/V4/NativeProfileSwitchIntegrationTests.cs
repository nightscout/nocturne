using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.V4;

/// <summary>
/// Profile switches a person records through <c>POST /api/v4/profile/activations</c>: in force on v4,
/// never on the v1 or v3 treatments feed an uploader reads, ended and deleted only by a writer of
/// therapy, and only in their own tenant.
/// </summary>
[Trait("Category", "Integration")]
public class NativeProfileSwitchIntegrationTests : ApiIntegrationTestBase
{
    private const string Activations = "/api/v4/profile/activations";

    public NativeProfileSwitchIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private static object ProfileDocument(string name) => new
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
                basal = new[] { new { time = "00:00", value = 1.0, timeAsSeconds = 0 } },
                target_low = new[] { new { time = "00:00", value = 80, timeAsSeconds = 0 } },
                target_high = new[] { new { time = "00:00", value = 120, timeAsSeconds = 0 } },
                units = "mg/dl",
                timezone = "UTC",
            },
        },
        startDate = DateTime.UtcNow.AddHours(-1).ToString("o"),
    };

    private static async Task StoreProfileAsync(HttpClient client, string name = "Rhys")
    {
        var response = await client.PutAsJsonAsync("/api/v1/profile", ProfileDocument(name));
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
        (await client.GetFromJsonAsync<JsonArray>(Activations))!.Should().BeEmpty();
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
        var (_, accessToken) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Therapy Reader");
        using var subject = AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, accessToken);
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

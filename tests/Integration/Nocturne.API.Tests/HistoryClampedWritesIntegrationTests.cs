using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// A credential limited to 24 hours reads only the last day, but still records what it is entitled
/// to write when that is dated earlier: a lab result from last quarter, a weight from last week, a
/// sensor change logged late, which completes the run it replaces at that earlier time.
/// </summary>
[Trait("Category", "Integration")]
public class HistoryClampedWritesIntegrationTests : ApiIntegrationTestBase
{
    public HistoryClampedWritesIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task A_clamped_credential_records_a_lab_result_from_last_quarter()
    {
        using var clamped = await CreateHistoryClampedClientAsync();

        var response = await clamped.PostAsJsonAsync("/api/v4/lab-results/hba1c", new
        {
            measuredAt = DateTime.UtcNow.AddDays(-90), valuePercent = 6.8,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var all = await clamped.GetFromJsonAsync<JsonArray>("/api/v4/lab-results/hba1c");
        all.Should().BeEmpty("the result is still outside the credential's window");
    }

    [Fact]
    public async Task A_clamped_credential_records_last_weeks_weight()
    {
        using var clamped = await CreateHistoryClampedClientAsync();

        var response = await clamped.PostAsJsonAsync("/api/v4/body-weight", new
        {
            mills = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeMilliseconds(), weightKg = 70.5,
        });

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_clamped_credential_starts_and_completes_backdated_tracker_runs()
    {
        using var clamped = await CreateHistoryClampedClientAsync();
        var definition = await clamped.PostAsJsonAsync("/api/v4/trackers/definitions", new
        {
            name = "Sensor", lifespanHours = 240,
        });
        definition.StatusCode.Should().Be(HttpStatusCode.Created, await definition.Content.ReadAsStringAsync());
        var definitionId = (await definition.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>();

        var first = await StartAsync(clamped, definitionId, DateTime.UtcNow.AddDays(-5));
        var second = await StartAsync(clamped, definitionId, DateTime.UtcNow.AddDays(-2));

        var active = await clamped.GetFromJsonAsync<JsonArray>("/api/v4/trackers/instances");
        active!.Select(i => i!["id"]!.GetValue<string>()).Should().Equal(second);
        active.Should().NotContain(i => i!["id"]!.GetValue<string>() == first);

        var complete = await clamped.PutAsJsonAsync($"/api/v4/trackers/instances/{second}/complete", new
        {
            reason = "Completed", completedAt = DateTime.UtcNow.AddHours(-30),
        });
        complete.StatusCode.Should().Be(HttpStatusCode.OK, await complete.Content.ReadAsStringAsync());
    }

    private static async Task<string> StartAsync(HttpClient client, string definitionId, DateTime startedAt)
    {
        var response = await client.PostAsJsonAsync("/api/v4/trackers/instances", new { definitionId, startedAt });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>();
    }

    private async Task<HttpClient> CreateHistoryClampedClientAsync()
    {
        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        var (subjectId, token) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(
            conn, Fixture.TenantId, "Clamped Writer");

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT set_config('app.current_tenant_id', @tenant, false);
                UPDATE oauth_grants SET limit_to_24_hours = true WHERE subject_id = @subject;
                """;
            cmd.Parameters.AddWithValue("tenant", Fixture.TenantId.ToString());
            cmd.Parameters.AddWithValue("subject", subjectId);
            await cmd.ExecuteNonQueryAsync();
        }

        return AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, token);
    }
}

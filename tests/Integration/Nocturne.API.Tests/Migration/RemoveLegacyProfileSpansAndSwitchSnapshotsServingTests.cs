using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Infrastructure.Data;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Migration;

/// <summary>
/// A switch converted by <c>AddProfileActivations</c> keeps its Profile span's id, so removing the
/// span (and the <c>@@@@@</c> rows decomposed beside it) by <c>RemoveLegacyProfileSpansAndSwitchSnapshots</c>
/// must not reach any client as a delete. These lay the cutover's leftovers beside a live switch, run the
/// migration's statements on the API's database, and read the switch back on v1, v3 and v3 history.
/// </summary>
[Trait("Category", "Integration")]
public class RemoveLegacyProfileSpansAndSwitchSnapshotsServingTests : ApiIntegrationTestBase
{
    private const string MigrationUnderTest = "20261021131616_RemoveLegacyProfileSpansAndSwitchSnapshots";

    private const string LegacyId = "6500000000000000000000f1";

    public RemoveLegacyProfileSpansAndSwitchSnapshotsServingTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private static async Task<JsonNode> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static List<(string Identifier, bool? IsValid)> Delivered(JsonNode history) =>
        history["result"]!.AsArray()
            .Select(t => (t!["identifier"]!.GetValue<string>(), t["isValid"]?.GetValue<bool>()))
            .ToList();

    private static string UpSql(string connectionString)
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.UseNocturneMigrations())
            .Options;
        using var context = new NocturneDbContext(options);
        var assembly = context.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations[MigrationUnderTest], context.Database.ProviderName!);
        return string.Join(";\n", migration.UpOperations.OfType<SqlOperation>().Select(o => o.Sql));
    }

    private static async Task ExecuteAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task A_converted_switch_is_still_served_and_no_delete_is_delivered_for_it_or_its_snapshot()
    {
        var client = CreateAuthenticatedClient();
        var mills = DateTimeOffset.UtcNow.AddMinutes(-60).ToUnixTimeMilliseconds();
        var created = DateTimeOffset.FromUnixTimeMilliseconds(mills).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var post = await client.PostAsJsonAsync("/api/v1/treatments", new[]
        {
            new
            {
                _id = LegacyId, eventType = "Profile Switch", created_at = created, enteredBy = "openaps://AndroidAPS",
                profile = "Rhys", duration = 0, percentage = 100, timeshift = 0,
            },
        });
        post.StatusCode.Should().Be(HttpStatusCode.OK, await post.Content.ReadAsStringAsync());

        var historyBefore = Delivered(await GetAsync(client, "/api/v3/treatments/history/0"));
        historyBefore.Should().ContainSingle();
        var profileHistoryBefore = Delivered(await GetAsync(client, "/api/v3/profile/history/0"));

        var connectionString = (await GetPostgresConnectionStringAsync())!;
        await using (var conn = new NpgsqlConnection(connectionString))
        {
            await conn.OpenAsync();
            var tenant = Fixture.TenantId;
            await ExecuteAsync(conn, $$"""
                SELECT set_config('app.current_tenant_id', '{{tenant}}', false);
                INSERT INTO state_spans
                  (id, tenant_id, category, state, start_timestamp, original_id, source, metadata, created_at, updated_at)
                SELECT a.id, a.tenant_id, 'Profile', 'Active', a.start_timestamp, a.legacy_id, 'openaps://AndroidAPS',
                       '{"profileName":"Rhys","utcOffset":0}', now(), now()
                FROM profile_activations a
                WHERE a.tenant_id = '{{tenant}}' AND a.legacy_id = '{{LegacyId}}';
                INSERT INTO linked_records (id, tenant_id, canonical_id, record_type, record_id, source_timestamp, data_source, is_primary)
                SELECT gen_random_uuid(), a.tenant_id, gen_random_uuid(), 'statespan', a.id, {{mills}}, 'openaps://AndroidAPS', true
                FROM profile_activations a
                WHERE a.tenant_id = '{{tenant}}' AND a.legacy_id = '{{LegacyId}}';
                INSERT INTO therapy_settings
                  (id, tenant_id, timestamp, legacy_id, profile_name, dia, carbs_hr, delay, is_default, is_externally_managed)
                VALUES (gen_random_uuid(), '{{tenant}}', now(), '{{LegacyId}}:Rhys@@@@@{{mills}}', 'Rhys@@@@@{{mills}}', 4, 20, 20, false, false);
                INSERT INTO basal_schedules (id, tenant_id, timestamp, legacy_id, profile_name, entries_json)
                VALUES (gen_random_uuid(), '{{tenant}}', now(), '{{LegacyId}}:Rhys@@@@@{{mills}}', 'Rhys@@@@@{{mills}}', '[]');
                SELECT set_config('app.current_tenant_id', '', false);
                """);

            await ExecuteAsync(conn, UpSql(connectionString));

            await using var count = conn.CreateCommand();
            count.CommandText = $"""
                SELECT (SELECT count(*) FROM state_spans WHERE tenant_id = '{tenant}' AND category = 'Profile')
                     + (SELECT count(*) FROM therapy_settings WHERE tenant_id = '{tenant}' AND profile_name LIKE '%@@@@@%')
                     + (SELECT count(*) FROM basal_schedules WHERE tenant_id = '{tenant}' AND profile_name LIKE '%@@@@@%');
                """;
            ((long)(await count.ExecuteScalarAsync())!).Should().Be(0);
        }

        var identifier = historyBefore.Single().Identifier;
        (await GetAsync(client, "/api/v1/treatments")).AsArray()
            .Should().ContainSingle(t => t!["_id"]!.GetValue<string>() == identifier
                && t["eventType"]!.GetValue<string>() == "Profile Switch");
        (await GetAsync(client, "/api/v3/treatments"))["result"]!.AsArray()
            .Should().ContainSingle(t => t!["identifier"]!.GetValue<string>() == identifier);
        Delivered(await GetAsync(client, "/api/v3/treatments/history/0")).Should().Equal(historyBefore);
        Delivered(await GetAsync(client, "/api/v3/profile/history/0")).Should().Equal(profileHistoryBefore);
    }
}

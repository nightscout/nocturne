using System.Text;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// A byte-identical re-send of a device status, a treatment batch or a profile leaves every row it
/// decomposed into as it was: no row version changes and no update stamp moves.
/// </summary>
[Collection(ApiIntegrationShards.Four)]
[Trait("Category", "Integration")]
public class UnchangedResendIntegrationTests : ApiIntegrationTestBase
{
    private static readonly string[] Tables =
    [
        "aps_snapshots", "pump_snapshots", "uploader_snapshots", "boluses", "temp_basals", "carb_intakes",
        "therapy_settings", "basal_schedules", "carb_ratio_schedules", "sensitivity_schedules", "target_range_schedules",
    ];

    public UnchangedResendIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task ByteIdenticalResend_RewritesNoRow()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var posts = Posts(DateTime.UtcNow.AddMinutes(-30), tag);
        foreach (var (path, body) in posts)
            await PostAsync(path, body);
        var before = await RowVersionsAsync(tag);

        foreach (var (path, body) in posts)
            await PostAsync(path, body);

        before.Keys.Select(k => k.Split('/')[0]).Distinct().Should().BeEquivalentTo(Tables);
        (await RowVersionsAsync(tag)).Should().Equal(before);
    }

    private static (string Path, string Body)[] Posts(DateTime at, string tag)
    {
        string Iso(int minutes) => at.AddMinutes(minutes).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var mills = new DateTimeOffset(at).ToUnixTimeMilliseconds();
        string Id(int i) => $"6a1b2c3d4e5f{tag}{i:x4}";
        return
        [
            ("/api/v1/devicestatus", $$$"""
                [{"_id":"{{{Id(1)}}}","device":"openaps://{{{tag}}}","created_at":"{{{Iso(0)}}}","mills":{{{mills}}},
                  "openaps":{"iob":{"iob":1.5,"basaliob":0.5,"time":"{{{Iso(0)}}}"},
                    "suggested":{"bg":120,"eventualBG":110,"COB":10,"timestamp":"{{{Iso(0)}}}","predBGs":{"IOB":[120,118]}},
                    "enacted":{"bg":120,"rate":0.5,"duration":30,"received":true,"timestamp":"{{{Iso(0)}}}"}},
                  "pump":{"clock":"{{{Iso(0)}}}","reservoir":50.5,"battery":{"percent":70}},
                  "uploader":{"battery":80}}]
                """),
            ("/api/v1/treatments", $$$"""
                [{"_id":"{{{Id(2)}}}","eventType":"Correction Bolus","insulin":1.25,"created_at":"{{{Iso(1)}}}","enteredBy":"{{{tag}}}"},
                 {"_id":"{{{Id(3)}}}","eventType":"Temp Basal","rate":0.6,"absolute":0.6,"duration":30,"created_at":"{{{Iso(2)}}}","enteredBy":"{{{tag}}}"},
                 {"_id":"{{{Id(4)}}}","eventType":"Meal Bolus","insulin":2,"carbs":20,"created_at":"{{{Iso(3)}}}","enteredBy":"{{{tag}}}"}]
                """),
            ("/api/v1/profile", $$$"""
                {"_id":"{{{Id(5)}}}","defaultProfile":"Default","startDate":"{{{Iso(0)}}}","mills":{{{mills}}},
                 "store":{"Default":{"dia":3,"units":"mg/dl","timezone":"UTC",
                   "carbratio":[{"time":"00:00","value":10,"timeAsSeconds":0}],"sens":[{"time":"00:00","value":50,"timeAsSeconds":0}],
                   "basal":[{"time":"00:00","value":1.0,"timeAsSeconds":0}],
                   "target_low":[{"time":"00:00","value":80,"timeAsSeconds":0}],"target_high":[{"time":"00:00","value":120,"timeAsSeconds":0}] } } }
                """),
        ];
    }

    private async Task PostAsync(string path, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await AuthenticatedClient.PostAsync(path, content);
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Row version and update stamp of every row in <see cref="Tables"/> created by the posts tagged <paramref name="tag"/>.</summary>
    private async Task<Dictionary<string, string>> RowVersionsAsync(string tag)
    {
        var versions = new Dictionary<string, string>();
        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        await using (var set = conn.CreateCommand())
        {
            set.CommandText = "SELECT set_config('app.current_tenant_id', @tenant, false)";
            set.Parameters.AddWithValue("tenant", Fixture.TenantId.ToString());
            await set.ExecuteScalarAsync();
        }

        foreach (var table in Tables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT id::text, xmin::text, sys_updated_at::text FROM {table} WHERE legacy_id LIKE @tag";
            cmd.Parameters.AddWithValue("tag", $"6a1b2c3d4e5f{tag}%");
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                versions[$"{table}/{reader.GetString(0)}"] = $"{reader.GetString(1)}@{reader.GetString(2)}";
        }

        return versions;
    }
}

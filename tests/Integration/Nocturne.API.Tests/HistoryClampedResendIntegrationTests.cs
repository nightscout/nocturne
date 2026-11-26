using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// An uploader on a credential limited to 24 hours re-sends a record stored before its window. The
/// stored rows it reaches stay in their correlation group, so the re-send adds no second row to the
/// group and the parts of the group with no legacy id of their own are written under the group's id.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class HistoryClampedResendIntegrationTests : ApiIntegrationTestBase
{
    public HistoryClampedResendIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task A_resent_status_stored_before_the_window_keeps_its_group_and_adds_no_extras()
    {
        var at = ThreeDaysAgo();
        var status = Status(at, withExtras: true);
        await PostAsync(AuthenticatedClient, "/api/v1/devicestatus", status);
        var group = (await ColumnAsync<Guid>(SnapshotGroup, Id(status))).Should().ContainSingle().Subject;
        using var clamped = await CreateHistoryClampedClientAsync("Clamped Uploader");

        await PostAsync(clamped, "/api/v1/devicestatus", status);

        (await ColumnAsync<Guid>(SnapshotGroup, Id(status))).Should().Equal([group]);
        (await ColumnAsync<Guid>(ExtrasAt, at)).Should().Equal([group], "the stored extras row holds the re-send off");
        (await ColumnAsync<Guid>(ReportsAt, at)).Should().Equal([group]);
    }

    [Fact]
    public async Task A_resent_status_stored_before_the_window_without_extras_gains_them_in_its_group()
    {
        var at = ThreeDaysAgo();
        var stored = Status(at, withExtras: false);
        await PostAsync(AuthenticatedClient, "/api/v1/devicestatus", stored);
        var group = (await ColumnAsync<Guid>(SnapshotGroup, Id(stored))).Should().ContainSingle().Subject;
        using var clamped = await CreateHistoryClampedClientAsync("Clamped Uploader");

        await PostAsync(clamped, "/api/v1/devicestatus", Status(at, withExtras: true, id: Id(stored)));

        (await ColumnAsync<Guid>(SnapshotGroup, Id(stored))).Should().Equal([group]);
        (await ColumnAsync<Guid>(ExtrasAt, at)).Should().Equal([group]);
        (await ColumnAsync<Guid>(ReportsAt, at)).Should().Equal([group]);
    }

    [Fact]
    public async Task A_resent_treatment_stored_before_the_window_keeps_its_group()
    {
        var at = ThreeDaysAgo();
        var id = NewLegacyId();
        object Treatment(double insulin) => new
        {
            _id = id, eventType = "Meal Bolus", insulin, carbs = 20.0, created_at = Iso(at), enteredBy = "loop",
        };
        await PostAsync(AuthenticatedClient, "/api/v1/treatments", new[] { Treatment(2.0) });
        var group = (await ColumnAsync<Guid>("SELECT correlation_id FROM boluses WHERE legacy_id = @key AND deleted_at IS NULL", id))
            .Should().ContainSingle().Subject;
        (await ColumnAsync<Guid>("SELECT correlation_id FROM carb_intakes WHERE legacy_id = @key AND deleted_at IS NULL", id))
            .Should().Equal([group]);
        using var clamped = await CreateHistoryClampedClientAsync("Clamped Uploader");

        await PostAsync(clamped, "/api/v1/treatments", new[] { Treatment(2.5) });

        (await ColumnAsync<Guid>("SELECT correlation_id FROM boluses WHERE legacy_id = @key AND deleted_at IS NULL", id))
            .Should().Equal([group]);
        (await ColumnAsync<Guid>("SELECT correlation_id FROM carb_intakes WHERE legacy_id = @key AND deleted_at IS NULL", id))
            .Should().Equal([group]);
        (await ColumnAsync<double>("SELECT insulin FROM boluses WHERE legacy_id = @key AND deleted_at IS NULL", id))
            .Should().Equal([2.5]);
    }

    [Fact]
    public async Task A_meal_whose_carbs_the_user_deleted_before_the_window_is_refused_whole()
    {
        var at = ThreeDaysAgo();
        var syncIdentifier = Guid.NewGuid().ToString();
        await ExecuteAsync(
            """
            INSERT INTO carb_intakes (id, tenant_id, data_source, sync_identifier, carbs, "timestamp", deleted_at, deleted_by_user)
            VALUES (@id, @tenant, 'aaps', @key, 30, @at, @at, true)
            """,
            syncIdentifier, ("id", Guid.CreateVersion7()), ("at", at));
        using var clamped = await CreateHistoryClampedClientAsync("Clamped Uploader");

        var response = await clamped.PostAsJsonAsync("/api/v4/nutrition/meals", new
        {
            timestamp = new DateTimeOffset(at), insulin = 3.0, carbs = 30.0, dataSource = "aaps", syncIdentifier,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await ColumnAsync<Guid>("SELECT id FROM boluses WHERE sync_identifier = @key", syncIdentifier))
            .Should().BeEmpty("the refusal comes before either half is written");
    }

    private const string SnapshotGroup =
        "SELECT correlation_id FROM uploader_snapshots WHERE legacy_id = @key AND deleted_at IS NULL";

    private const string ExtrasAt =
        "SELECT correlation_id FROM device_status_extras WHERE \"timestamp\" = @key AND deleted_at IS NULL";

    private const string ReportsAt =
        "SELECT correlation_id FROM sensor_state_reports WHERE \"timestamp\" = @key AND deleted_at IS NULL";

    private static Dictionary<string, object> Status(DateTime at, bool withExtras, string? id = null)
    {
        var status = new Dictionary<string, object>
        {
            ["_id"] = id ?? NewLegacyId(),
            ["created_at"] = Iso(at),
            ["mills"] = new DateTimeOffset(at).ToUnixTimeMilliseconds(),
            ["device"] = "xdripjs://rig",
            ["uploader"] = new { battery = 80 },
        };
        if (withExtras)
            status["xdripjs"] = new { state = 6, stateString = "OK" };
        return status;
    }

    private static string Id(Dictionary<string, object> status) => (string)status["_id"];

    private static string NewLegacyId() => Guid.NewGuid().ToString("N")[..24];

    private static DateTime ThreeDaysAgo()
    {
        var now = DateTime.UtcNow.AddDays(-3);
        return new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }

    private static string Iso(DateTime at) => at.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static async Task PostAsync(HttpClient client, string path, object body)
    {
        var response = await client.PostAsJsonAsync(path, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task<List<T>> ColumnAsync<T>(string sql, object key)
    {
        await using var conn = await TenantConnectionAsync();
        return await conn.QueryAsync(sql, reader => reader.GetFieldValue<T>(0), ("key", key));
    }

    private async Task ExecuteAsync(string sql, object key, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TenantConnectionAsync();
        await conn.ExecuteAsync(sql, [("key", key), ("tenant", Fixture.TenantId), .. parameters]);
    }

    private async Task<NpgsqlConnection> TenantConnectionAsync()
    {
        var conn = await OpenPostgresAsync();
        await conn.PinTenantAsync(Fixture.TenantId);
        return conn;
    }
}

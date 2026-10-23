using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// Temp basals stored before their origin's evidence was recorded, in two tenants, judged by
/// <see cref="TempBasalOriginCorroborator"/> as the background job runs it: as the runtime role,
/// pinned to one tenant, on a real PostgreSQL. A first run is cancelled after its first page, then
/// resumed, then run again.
/// </summary>
public class TempBasalOriginCorroboratorFixture : IAsyncLifetime
{
    internal static readonly Guid TenantA = Guid.Parse("a1111111-1111-7111-8111-111111111111");
    internal static readonly Guid TenantB = Guid.Parse("b2222222-2222-7222-8222-222222222222");

    /// <summary>A Trio temp whose snapshot enacted its rate.</summary>
    internal static readonly Guid Enacted = Guid.Parse("70000000-0000-7000-8000-000000000001");

    /// <summary>A Trio temp whose snapshot nearby proposed another rate.</summary>
    internal static readonly Guid ByHand = Guid.Parse("70000000-0000-7000-8000-000000000002");

    /// <summary>A Trio temp whose only snapshot nearby, proposing its rate, is another device's.</summary>
    internal static readonly Guid OtherDevice = Guid.Parse("70000000-0000-7000-8000-000000000003");

    /// <summary>An AAPS temp mirrored through Nightscout with no device, beside its phone's snapshot.</summary>
    internal static readonly Guid Deviceless = Guid.Parse("70000000-0000-7000-8000-00000000000c");

    /// <summary>A temp Tidepool mirrored, stored before evidence was recorded, beside a Trio snapshot.</summary>
    internal static readonly Guid Tidepool = Guid.Parse("70000000-0000-7000-8000-00000000000d");

    /// <summary>A temp with no device beside the snapshots of two loops, one proposing its rate.</summary>
    internal static readonly Guid TwoLoops = Guid.Parse("70000000-0000-7000-8000-00000000000e");

    /// <summary>A temp with no device beside one loop's snapshot proposing another rate.</summary>
    internal static readonly Guid DevicelessByHand = Guid.Parse("70000000-0000-7000-8000-00000000000f");

    /// <summary>A temp with no device its upload flagged as manual, beside a snapshot proposing its rate.</summary>
    internal static readonly Guid DevicelessFlagged = Guid.Parse("70000000-0000-7000-8000-000000000010");

    /// <summary>A Glooko Manual temp written before the connector kept the flag, beside a snapshot proposing its rate.</summary>
    internal static readonly Guid GlookoManual = Guid.Parse("70000000-0000-7000-8000-000000000011");

    internal static readonly Guid LoopPhoneSnapshot = Guid.Parse("50000000-0000-7000-8000-000000000004");
    internal static readonly Guid PhoneSnapshot = Guid.Parse("50000000-0000-7000-8000-000000000008");
    internal static readonly Guid TrioMirroredSnapshot = Guid.Parse("50000000-0000-7000-8000-000000000009");

    /// <summary>A temp its upload flagged as manual.</summary>
    internal static readonly Guid Flagged = Guid.Parse("70000000-0000-7000-8000-000000000004");

    /// <summary>A suspend a connector wrote.</summary>
    internal static readonly Guid Suspend = Guid.Parse("70000000-0000-7000-8000-000000000005");

    /// <summary>A default written moments ago, still settling.</summary>
    internal static readonly Guid Unsettled = Guid.Parse("70000000-0000-7000-8000-000000000006");

    /// <summary>A CareLink Algorithm temp the connector wrote before it kept the flag.</summary>
    internal static readonly Guid Connector = Guid.Parse("70000000-0000-7000-8000-000000000008");

    /// <summary>A Loop temp whose snapshot recommended its rate only in <c>loop_json</c>.</summary>
    internal static readonly Guid LoopRecommended = Guid.Parse("70000000-0000-7000-8000-000000000009");

    /// <summary>A Loop temp whose enactment, dated only in <c>loop_json</c>, is in a later snapshot.</summary>
    internal static readonly Guid LoopEnacted = Guid.Parse("70000000-0000-7000-8000-00000000000a");

    /// <summary>A Trio temp whose snapshot arrives after it was judged.</summary>
    internal static readonly Guid Late = Guid.Parse("70000000-0000-7000-8000-00000000000b");

    internal static readonly Guid LoopRecommendedSnapshot = Guid.Parse("50000000-0000-7000-8000-000000000005");
    internal static readonly Guid LoopEnactedSnapshot = Guid.Parse("50000000-0000-7000-8000-000000000006");
    internal static readonly Guid LateSnapshot = Guid.Parse("50000000-0000-7000-8000-000000000007");

    /// <summary>Tenant B's Trio temp, whose snapshot enacted its rate.</summary>
    internal static readonly Guid OtherTenant = Guid.Parse("70000000-0000-7000-8000-000000000007");

    internal static readonly Guid SnapshotA = Guid.Parse("50000000-0000-7000-8000-000000000001");
    internal static readonly Guid SnapshotB = Guid.Parse("50000000-0000-7000-8000-000000000002");

    private TestDatabase _database = null!;

    internal int CrossTenantWritten { get; private set; }
    internal int JudgedBeforeCancel { get; private set; }
    internal int FirstWritten { get; private set; }
    internal int RerunWritten { get; private set; }
    internal Dictionary<Guid, Row> AfterFirst { get; private set; } = [];
    internal Dictionary<Guid, Row> AfterRerun { get; private set; } = [];
    internal Row? LateBeforeSnapshot { get; private set; }
    internal int LateReopened { get; private set; }
    internal Row? LateAfterSnapshot { get; private set; }

    internal sealed record Row(string Origin, string? Evidence, Guid? SnapshotId, DateTime UpdatedAt);

    public async Task InitializeAsync()
    {
        _database = await SharedPostgres.CreateMigratedDatabaseAsync("temp_basal_origin");
        await SeedAsync();

        CrossTenantWritten = await CorroborateAsync(pinnedTo: TenantA, tenant: TenantB);

        using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            var interrupted = async () => await CorroborateAsync(
                TenantA, TenantA, pageSize: 1, pauseAfterWrite: TimeSpan.FromMinutes(1), ct: cancel.Token);
            await interrupted.Should().ThrowAsync<OperationCanceledException>();
        }

        JudgedBeforeCancel = (await ReadAsync()).Values.Count(r => r.Evidence is not null and not "Uploader");
        FirstWritten = await CorroborateAsync(TenantA, TenantA) + await CorroborateAsync(TenantB, TenantB);
        AfterFirst = await ReadAsync();
        RerunWritten = await CorroborateAsync(TenantA, TenantA) + await CorroborateAsync(TenantB, TenantB);
        AfterRerun = await ReadAsync();

        LateBeforeSnapshot = AfterRerun[Late];
        var beforeSnapshot = DateTime.UtcNow.AddSeconds(-1);
        await SeedLateSnapshotAsync();
        LateReopened = await ReopenAsync(TenantA, beforeSnapshot);
        await CorroborateAsync(TenantA, TenantA);
        LateAfterSnapshot = (await ReadAsync())[Late];
    }

    private async Task<int> ReopenAsync(Guid tenant, DateTime since)
    {
        await using var db = Context(tenant);
        return (await TempBasalOriginCorroborator.ReopenNearNewSnapshotsAsync(db, tenant, since, 500, CancellationToken.None)).Reopened;
    }

    private NocturneDbContext Context(Guid pinnedTo) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_database.AppConnectionString)
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options) { TenantId = pinnedTo };

    private async Task SeedLateSnapshotAsync()
    {
        await using var conn = new NpgsqlConnection(_database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            SELECT set_config('app.current_tenant_id', '{{TenantA}}', false);
            INSERT INTO aps_snapshots (id, tenant_id, timestamp, device, aps_system, enacted, enacted_rate)
            VALUES ('{{LateSnapshot}}', '{{TenantA}}', '2026-08-01T20:01:00Z', 'Trio', 'Trio', true, 1.4);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> CorroborateAsync(
        Guid pinnedTo, Guid tenant, int pageSize = 500, TimeSpan pauseAfterWrite = default, CancellationToken ct = default)
    {
        await using var db = Context(pinnedTo);
        return await TempBasalOriginCorroborator.CorroborateTenantAsync(
            db, tenant, DateTime.UtcNow.AddMinutes(-30), pageSize, pauseAfterWrite, TimeSpan.Zero, ct);
    }

    private async Task<Dictionary<Guid, Row>> ReadAsync()
    {
        var rows = new Dictionary<Guid, Row>();
        await using var conn = new NpgsqlConnection(_database.MigratorConnectionString);
        await conn.OpenAsync();
        foreach (var tenant in new[] { TenantA, TenantB })
        {
            await using (var pin = conn.CreateCommand())
            {
                pin.CommandText = "SELECT set_config('app.current_tenant_id', @tenant, false);";
                pin.Parameters.AddWithValue("tenant", tenant.ToString());
                await pin.ExecuteNonQueryAsync();
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, origin, origin_evidence, aps_snapshot_id, sys_updated_at FROM temp_basals;";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows[reader.GetGuid(0)] = new Row(
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetGuid(3),
                    reader.GetDateTime(4));
            }
        }

        return rows;
    }

    private async Task SeedAsync()
    {
        await using var conn = new NpgsqlConnection(_database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES
              ('{{TenantA}}', 'tenant-a', 'Tenant A', true, now(), now()),
              ('{{TenantB}}', 'tenant-b', 'Tenant B', true, now(), now());

            SELECT set_config('app.current_tenant_id', '{{TenantA}}', false);
            INSERT INTO aps_snapshots (id, tenant_id, timestamp, device, aps_system, enacted, enacted_rate, suggested_json, enacted_json)
            VALUES
              ('{{SnapshotA}}', '{{TenantA}}', '2026-08-01T12:01:00Z', 'Trio', 'Trio', true, 1.2,
               '{"rate": 1.2}', '{"rate": 1.2, "timestamp": "2026-08-01T12:01:00Z"}'),
              ('50000000-0000-7000-8000-000000000003', '{{TenantA}}', '2026-08-01T13:02:00Z', 'Trio', 'Trio', true, 0.8,
               '{"rate": 0.8}', '{"rate": 0.8}'),
              ('50000000-0000-7000-8000-00000000000a', '{{TenantA}}', '2026-08-01T13:01:00Z', 'openaps://SM-G991B', 'OpenAps', true, 2.0,
               NULL, NULL),
              ('{{LoopPhoneSnapshot}}', '{{TenantA}}', '2026-08-01T14:00:00Z', 'loop://phone', 'Loop', true, 1.0,
               NULL, NULL),
              ('{{PhoneSnapshot}}', '{{TenantA}}', '2026-08-01T22:01:00Z', 'openaps://SM-G991B', 'OpenAps', true, 1.1,
               '{"rate": 1.1}', NULL),
              ('{{TrioMirroredSnapshot}}', '{{TenantA}}', '2026-08-01T23:02:00Z', 'Trio', 'Trio', true, 0.5,
               '{"rate": 0.85}', NULL),
              ('50000000-0000-7000-8000-00000000000b', '{{TenantA}}', '2026-08-02T01:01:00Z', 'openaps://SM-G991B', 'OpenAps', true, 1.0,
               NULL, NULL),
              ('50000000-0000-7000-8000-00000000000c', '{{TenantA}}', '2026-08-02T01:02:00Z', 'Trio', 'Trio', true, 0.3,
               NULL, NULL),
              ('50000000-0000-7000-8000-00000000000d', '{{TenantA}}', '2026-08-02T02:01:00Z', 'openaps://SM-G991B', 'OpenAps', true, 1.0,
               NULL, NULL),
              ('50000000-0000-7000-8000-00000000000e', '{{TenantA}}', '2026-08-02T03:01:00Z', 'openaps://SM-G991B', 'OpenAps', true, 0.6,
               NULL, NULL),
              ('50000000-0000-7000-8000-00000000000f', '{{TenantA}}', '2026-08-02T04:01:00Z', 'openaps://SM-G991B', 'OpenAps', true, 0.9,
               NULL, NULL);
            INSERT INTO aps_snapshots (id, tenant_id, timestamp, device, aps_system, enacted, enacted_rate, loop_json)
            VALUES
              ('{{LoopRecommendedSnapshot}}', '{{TenantA}}', '2026-08-01T18:01:00Z', 'loop://iPhone', 'Loop', false, NULL,
               '{"name": "Loop", "automaticDoseRecommendation": {"tempBasal": {"rate": 0.9, "duration": 30} } }'),
              ('{{LoopEnactedSnapshot}}', '{{TenantA}}', '2026-08-01T19:09:00Z', 'loop://iPhone', 'Loop', true, 0.7,
               '{"name": "Loop", "enacted": {"rate": 0.7, "duration": 30, "received": true, "timestamp": "2026-08-01T19:01:00Z"} }');
            INSERT INTO temp_basals (id, tenant_id, start_timestamp, rate, device, app, origin, origin_evidence, additional_properties, legacy_id, sys_updated_at)
            VALUES
              ('{{Enacted}}', '{{TenantA}}', '2026-08-01T12:00:00Z', 1.2, 'Trio', 'Trio', 'Manual', NULL, NULL, 'ns-Enacted', '2026-08-02T00:00:00Z'),
              ('{{ByHand}}', '{{TenantA}}', '2026-08-01T13:00:00Z', 2.0, 'Trio', 'Trio', 'Manual', NULL, NULL, 'ns-ByHand', '2026-08-02T00:00:00Z'),
              ('{{OtherDevice}}', '{{TenantA}}', '2026-08-01T14:00:00Z', 1.0, 'Trio', 'Trio', 'Manual', NULL, NULL, 'ns-OtherDevice', '2026-08-02T00:00:00Z'),
              ('{{Flagged}}', '{{TenantA}}', '2026-08-01T15:00:00Z', 1.0, 'Trio', 'Trio', 'Manual', NULL, '{"automatic": false}', 'ns-Flagged', '2026-08-02T00:00:00Z'),
              ('{{Suspend}}', '{{TenantA}}', '2026-08-01T16:00:00Z', 0, 'Glooko', 'Glooko', 'Suspended', NULL, NULL, 'ns-Suspend', '2026-08-02T00:00:00Z'),
              ('{{Unsettled}}', '{{TenantA}}', '2026-08-01T17:00:00Z', 1.0, 'Trio', 'Trio', 'Algorithm', 'Uploader', NULL, 'ns-Unsettled', now()),
              ('{{LoopRecommended}}', '{{TenantA}}', '2026-08-01T18:00:00Z', 0.9, 'loop://iPhone', 'loop://iPhone', 'Algorithm', NULL, NULL, 'ns-LoopRecommended', '2026-08-02T00:00:00Z'),
              ('{{LoopEnacted}}', '{{TenantA}}', '2026-08-01T19:00:00Z', 0.7, 'loop://iPhone', 'loop://iPhone', 'Algorithm', NULL, NULL, 'ns-LoopEnacted', '2026-08-02T00:00:00Z'),
              ('{{Late}}', '{{TenantA}}', '2026-08-01T20:00:00Z', 1.4, 'Trio', 'Trio', 'Manual', NULL, NULL, 'ns-Late', '2026-08-02T00:00:00Z'),
              ('{{Deviceless}}', '{{TenantA}}', '2026-08-01T22:00:00Z', 1.1, NULL, NULL, 'Manual', 'Uploader', NULL, 'ns-Deviceless', '2026-08-02T00:00:00Z'),
              ('{{TwoLoops}}', '{{TenantA}}', '2026-08-02T01:00:00Z', 1.0, NULL, NULL, 'Manual', 'Uploader', NULL, 'ns-TwoLoops', '2026-08-02T00:00:00Z'),
              ('{{DevicelessByHand}}', '{{TenantA}}', '2026-08-02T02:00:00Z', 2.5, NULL, NULL, 'Manual', 'Uploader', NULL, 'ns-DevicelessByHand', '2026-08-02T00:00:00Z'),
              ('{{DevicelessFlagged}}', '{{TenantA}}', '2026-08-02T03:00:00Z', 0.6, NULL, NULL, 'Manual', NULL, '{"automatic": false}', 'ns-DevicelessFlagged', '2026-08-02T00:00:00Z');
            INSERT INTO temp_basals (id, tenant_id, start_timestamp, rate, device, app, data_source, origin, sys_updated_at)
            VALUES
              ('{{Connector}}', '{{TenantA}}', '2026-08-01T21:00:00Z', 0.6, NULL, NULL, 'carelink-connector', 'Algorithm', '2026-08-02T00:00:00Z'),
              ('{{GlookoManual}}', '{{TenantA}}', '2026-08-02T04:00:00Z', 0.9, NULL, NULL, 'glooko-connector', 'Manual', '2026-08-02T00:00:00Z');
            INSERT INTO temp_basals (id, tenant_id, start_timestamp, rate, device, app, data_source, origin, legacy_id, sys_updated_at)
            VALUES ('{{Tidepool}}', '{{TenantA}}', '2026-08-01T23:00:00Z', 0.85, 'tidepool', 'tidepool', 'nightscout-connector', 'Manual', 'ns-Tidepool', '2026-08-02T00:00:00Z');

            SELECT set_config('app.current_tenant_id', '{{TenantB}}', false);
            INSERT INTO aps_snapshots (id, tenant_id, timestamp, device, aps_system, enacted, enacted_rate)
            VALUES ('{{SnapshotB}}', '{{TenantB}}', '2026-08-01T12:00:00Z', 'Trio', 'Trio', true, 1.2);
            INSERT INTO temp_basals (id, tenant_id, start_timestamp, rate, device, app, origin, legacy_id, sys_updated_at)
            VALUES ('{{OtherTenant}}', '{{TenantB}}', '2026-08-01T12:00:00Z', 1.2, 'Trio', 'Trio', 'Manual', 'ns-OtherTenant', '2026-08-02T00:00:00Z');

            SELECT set_config('app.current_tenant_id', '', false);
            """;
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <inheritdoc cref="TempBasalOriginCorroboratorFixture"/>
[Trait("Category", "Integration")]
public class TempBasalOriginCorroboratorTests(TempBasalOriginCorroboratorFixture fixture)
    : IClassFixture<TempBasalOriginCorroboratorFixture>
{
    private Dictionary<Guid, TempBasalOriginCorroboratorFixture.Row> Rows => fixture.AfterFirst;

    [Fact]
    public void A_context_pinned_to_one_tenant_judges_none_of_anothers()
    {
        fixture.CrossTenantWritten.Should().Be(0);
    }

    [Fact]
    public void An_interrupted_run_keeps_its_committed_page_and_the_next_run_finishes()
    {
        fixture.JudgedBeforeCancel.Should().Be(1);
        Rows.Values.Count(r => r.Evidence is null).Should().Be(0);
    }

    [Fact]
    public void A_temp_its_snapshot_enacted_is_the_loops_and_linked()
    {
        Rows[TempBasalOriginCorroboratorFixture.Enacted].Should().BeEquivalentTo(new
        {
            Origin = "Algorithm", Evidence = "Snapshot", SnapshotId = (Guid?)TempBasalOriginCorroboratorFixture.SnapshotA,
        });
    }

    [Fact]
    public void A_temp_no_snapshot_of_its_own_device_nearby_proposed_is_manual_though_another_loops_did()
    {
        Rows[TempBasalOriginCorroboratorFixture.ByHand].Should().BeEquivalentTo(new
        {
            Origin = "Manual", Evidence = "Snapshot", SnapshotId = (Guid?)null,
        });
    }

    [Fact]
    public void A_temp_whose_device_uploaded_nothing_nearby_is_judged_by_the_one_loop_that_did()
    {
        Rows[TempBasalOriginCorroboratorFixture.OtherDevice].Should().BeEquivalentTo(new
        {
            Origin = "Algorithm", Evidence = "OtherDevice", SnapshotId = (Guid?)TempBasalOriginCorroboratorFixture.LoopPhoneSnapshot,
        });
    }

    [Fact]
    public void An_aaps_temp_with_no_device_is_linked_to_its_phones_snapshot()
    {
        Rows[TempBasalOriginCorroboratorFixture.Deviceless].Should().BeEquivalentTo(new
        {
            Origin = "Algorithm", Evidence = "OtherDevice", SnapshotId = (Guid?)TempBasalOriginCorroboratorFixture.PhoneSnapshot,
        });
    }

    [Fact]
    public void A_tidepool_mirrored_temp_stored_without_evidence_is_linked_to_the_trio_snapshot()
    {
        Rows[TempBasalOriginCorroboratorFixture.Tidepool].Should().BeEquivalentTo(new
        {
            Origin = "Algorithm", Evidence = "OtherDevice", SnapshotId = (Guid?)TempBasalOriginCorroboratorFixture.TrioMirroredSnapshot,
        });
    }

    [Fact]
    public void With_two_loops_nearby_a_temp_of_neither_keeps_its_default()
    {
        Rows[TempBasalOriginCorroboratorFixture.TwoLoops].Should().BeEquivalentTo(new
        {
            Origin = "Manual", Evidence = "Uncorroborated", SnapshotId = (Guid?)null,
        });
    }

    [Fact]
    public void A_temp_with_no_device_the_one_loop_nearby_proposed_another_rate_for_is_manual()
    {
        Rows[TempBasalOriginCorroboratorFixture.DevicelessByHand].Should().BeEquivalentTo(new
        {
            Origin = "Manual", Evidence = "OtherDevice", SnapshotId = (Guid?)null,
        });
    }

    [Theory]
    [InlineData("70000000-0000-7000-8000-000000000010")]
    [InlineData("70000000-0000-7000-8000-000000000011")]
    public void A_stated_manual_temp_beside_a_loop_proposing_its_rate_keeps_its_origin(string id)
    {
        Rows[Guid.Parse(id)].Should().BeEquivalentTo(new { Origin = "Manual", Evidence = "Reported", SnapshotId = (Guid?)null });
    }

    [Theory]
    [InlineData("70000000-0000-7000-8000-000000000004", "Manual")]
    [InlineData("70000000-0000-7000-8000-000000000005", "Suspended")]
    public void A_stated_origin_is_marked_reported_and_kept(string id, string origin)
    {
        Rows[Guid.Parse(id)].Should().BeEquivalentTo(new { Origin = origin, Evidence = "Reported" });
    }

    [Fact]
    public void A_row_still_settling_is_left_for_a_later_run()
    {
        Rows[TempBasalOriginCorroboratorFixture.Unsettled].Evidence.Should().Be("Uploader");
    }

    [Fact]
    public void Each_tenant_is_judged_against_its_own_snapshots()
    {
        Rows[TempBasalOriginCorroboratorFixture.OtherTenant].SnapshotId.Should().Be(TempBasalOriginCorroboratorFixture.SnapshotB);
    }

    [Fact]
    public void A_rerun_writes_nothing_and_no_judged_row_is_stamped_as_updated()
    {
        fixture.RerunWritten.Should().Be(0);
        fixture.AfterRerun.Should().BeEquivalentTo(fixture.AfterFirst);
        Rows.Where(r => r.Key != TempBasalOriginCorroboratorFixture.Unsettled)
            .Should().OnlyContain(r => r.Value.UpdatedAt == new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void A_connector_temp_stored_without_the_flag_keeps_its_origin()
    {
        Rows[TempBasalOriginCorroboratorFixture.Connector].Should().BeEquivalentTo(new { Origin = "Algorithm", Evidence = "Reported" });
    }

    [Theory]
    [InlineData("70000000-0000-7000-8000-000000000009", "50000000-0000-7000-8000-000000000005")]
    [InlineData("70000000-0000-7000-8000-00000000000a", "50000000-0000-7000-8000-000000000006")]
    public void A_loop_temp_is_linked_through_its_snapshots_loop_status(string id, string snapshot)
    {
        Rows[Guid.Parse(id)].Should().BeEquivalentTo(new
        {
            Origin = "Algorithm", Evidence = "Snapshot", SnapshotId = (Guid?)Guid.Parse(snapshot),
        });
    }

    [Fact]
    public void A_temp_judged_before_its_snapshot_arrived_is_judged_again_once_it_does()
    {
        fixture.LateBeforeSnapshot!.Evidence.Should().Be("Uncorroborated");
        fixture.LateReopened.Should().Be(1);
        fixture.LateAfterSnapshot.Should().BeEquivalentTo(new
        {
            Origin = "Algorithm", Evidence = "Snapshot", SnapshotId = (Guid?)TempBasalOriginCorroboratorFixture.LateSnapshot,
        });
    }
}

/// <summary>
/// A temp basal an upload rewrites between the job's read and its write keeps the upload's
/// version: the write waits on the upload's row lock and then finds the row changed.
/// </summary>
[Trait("Category", "Integration")]
public class TempBasalOriginCorroboratorConcurrencyTests(TempBasalOriginCorroboratorConcurrencyFixture fixture)
    : IClassFixture<TempBasalOriginCorroboratorConcurrencyFixture>
{
    private static readonly Guid Tenant = TempBasalOriginCorroboratorConcurrencyFixture.Tenant;
    private static readonly Guid TempBasal = TempBasalOriginCorroboratorConcurrencyFixture.TempBasal;

    [Fact]
    public async Task A_row_rewritten_between_the_read_and_the_write_is_left_as_the_upload_wrote_it()
    {
        await using var upload = await OpenPinnedAsync();
        await using var transaction = await upload.BeginTransactionAsync();
        await using (var rewrite = upload.CreateCommand())
        {
            rewrite.Transaction = transaction;
            rewrite.CommandText = $"UPDATE temp_basals SET rate = 1.5, sys_updated_at = now() WHERE id = '{TempBasal}';";
            await rewrite.ExecuteNonQueryAsync();
        }

        var corroborate = Task.Run(async () =>
        {
            await using var db = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
                .UseNpgsql(fixture.Database.AppConnectionString)
                .AddInterceptors(new TenantConnectionInterceptor())
                .Options) { TenantId = Tenant };
            return await TempBasalOriginCorroborator.CorroborateTenantAsync(
                db, Tenant, DateTime.UtcNow.AddMinutes(-30), 500, TimeSpan.Zero, TimeSpan.Zero, CancellationToken.None);
        });

        await PostgresLockWaits.UntilBlockedAsync(fixture.Database.SuperuserConnectionString, corroborate);
        await transaction.CommitAsync();

        (await corroborate).Should().Be(0);
        await using var read = await OpenPinnedAsync();
        await using var cmd = read.CreateCommand();
        cmd.CommandText = $"SELECT origin_evidence IS NULL AND aps_snapshot_id IS NULL FROM temp_basals WHERE id = '{TempBasal}';";
        ((bool)(await cmd.ExecuteScalarAsync())!).Should().BeTrue();
    }

    private Task<NpgsqlConnection> OpenPinnedAsync() => fixture.OpenPinnedAsync();
}

/// <summary>A migrated database holding one Trio temp basal and the snapshot that enacted it.</summary>
public class TempBasalOriginCorroboratorConcurrencyFixture : IAsyncLifetime
{
    internal static readonly Guid Tenant = Guid.Parse("a1111111-1111-7111-8111-111111111111");
    internal static readonly Guid TempBasal = Guid.Parse("70000000-0000-7000-8000-000000000001");

    internal TestDatabase Database { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Database = await SharedPostgres.CreateMigratedDatabaseAsync("temp_basal_origin_race");
        await using var conn = await OpenPinnedAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{{Tenant}}', 'tenant-a', 'Tenant A', true, now(), now());
            INSERT INTO aps_snapshots (id, tenant_id, timestamp, device, aps_system, enacted, enacted_rate)
            VALUES ('50000000-0000-7000-8000-000000000001', '{{Tenant}}', '2026-08-01T12:01:00Z', 'Trio', 'Trio', true, 1.2);
            INSERT INTO temp_basals (id, tenant_id, start_timestamp, rate, device, app, origin, legacy_id, sys_updated_at)
            VALUES ('{{TempBasal}}', '{{Tenant}}', '2026-08-01T12:00:00Z', 1.2, 'Trio', 'Trio', 'Manual', 'ns-TempBasal', '2026-08-02T00:00:00Z');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    internal async Task<NpgsqlConnection> OpenPinnedAsync()
    {
        var conn = new NpgsqlConnection(Database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var pin = conn.CreateCommand();
        pin.CommandText = $"SELECT set_config('app.current_tenant_id', '{Tenant}', false);";
        await pin.ExecuteNonQueryAsync();
        return conn;
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// The set-based reads a device status batch makes, against PostgreSQL as <c>nocturne_app</c>: what
/// each returns, and that each is one statement whatever it is asked for.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class DeviceStatusBatchReadsTests(RlsCompletenessFixture fx)
{
    private static readonly DateTime Start = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int minute) => Start.AddMinutes(minute);

    [Fact]
    public async Task GetMarksAsync_ReadsEachDevicesOwnFollowingMarksAndLeavesOutTheExcluded()
    {
        await using var store = await OpenAsync();
        var aps = store.Aps;
        var phone = await SeedApsAsync(aps, "phone", [1, 2, 3, 20, 21, 22, 23, 24]);
        var watch = await SeedApsAsync(aps, "watch", [4, 5, 6, 7, 8]);
        store.Recorder.Clear();

        var marks = await aps.GetMarksAsync(
            ["phone", null], after: At(0), through: At(3), following: 2, excluded: [phone[At(2)]]);

        store.Recorder.Count("SELECT").Should().Be(1);
        marks.Select(m => (m.Device, m.Timestamp)).Should().BeEquivalentTo(
        [
            ("phone", At(1)), ("phone", At(3)),
            ("phone", At(20)), ("phone", At(21)),
            ("watch", At(4)), ("watch", At(5)),
        ], "the phone takes its own two after the window, and every device's first two follow for the null device");
        watch.Should().HaveCount(5);
    }

    [Fact]
    public async Task FindByIdentitiesAsync_MatchesWholeIdentitiesOnly()
    {
        await using var store = await OpenAsync();
        var devices = new DeviceRepository(store.Factory);
        await devices.CreateAsync(Device(DeviceCategory.InsulinPump, "Omnipod", "A1"), WriteOrigin.Live);
        await devices.CreateAsync(Device(DeviceCategory.InsulinPump, "Medtronic", "B2"), WriteOrigin.Live);
        store.Recorder.Clear();

        var crossed = await devices.FindByIdentitiesAsync(
            [(DeviceCategory.InsulinPump, "Omnipod", "B2"), (DeviceCategory.InsulinPump, "Medtronic", "A1")]);
        var exact = await devices.FindByIdentitiesAsync([(DeviceCategory.InsulinPump, "Omnipod", "A1")]);

        crossed.Should().BeEmpty("each column matching some identity is not a match");
        exact.Should().ContainSingle().Which.Serial.Should().Be("A1");
        store.Recorder.Count("SELECT").Should().Be(2, "one statement per lookup");
    }

    [Fact]
    public async Task CreateAsync_ADeviceAConcurrentWriterStoredFirst_ReturnsTheStoredOne()
    {
        await using var store = await OpenAsync();
        var devices = new DeviceRepository(store.Factory);
        var first = await devices.CreateAsync(Device(DeviceCategory.CGM, "Dexcom", "G7"), WriteOrigin.Live);

        var second = await devices.CreateAsync(Device(DeviceCategory.CGM, "Dexcom", "G7"), WriteOrigin.Live);

        second.Id.Should().Be(first.Id);
    }

    [Fact]
    public async Task FindStoredAsync_MatchesTheSameExtrasAtTheSameInstant()
    {
        await using var store = await OpenAsync();
        var extras = new DeviceStatusExtrasRepository(store.Factory, new SystemAuditContext(), NullLogger<DeviceStatusExtrasRepository>.Instance);
        await extras.BulkCreateAsync(
            [Extras(At(1), new() { ["a"] = 1, ["b"] = new Dictionary<string, object?> { ["c"] = 2 } })], WriteOrigin.Live);
        store.Recorder.Clear();

        var found = await extras.FindStoredAsync(
        [
            Extras(At(1), new() { ["b"] = new Dictionary<string, object?> { ["c"] = 2 }, ["a"] = 1 }),
            Extras(At(2), new() { ["a"] = 1, ["b"] = new Dictionary<string, object?> { ["c"] = 2 } }),
            Extras(At(1), new() { ["a"] = 2 }),
        ]);

        found.Select(f => f is not null).Should().Equal(true, false, false);
        store.Recorder.Count("SELECT").Should().Be(1);
    }

    [Fact]
    public async Task UpdateStateSpansAsync_MatchesTheOriginalIdBeforeTheUuid()
    {
        await using var store = await OpenAsync();
        var spans = Spans(store.Factory);
        await spans.BulkUpsertAsync([Span("tt", At(0), At(1), "by-uuid")]);
        var uuid = (await store.Context.StateSpans.AsNoTracking().SingleAsync(s => s.OriginalId == "by-uuid")).Id.ToString();
        await spans.BulkUpsertAsync([Span("tt", At(2), At(3), uuid)]);

        var written = await spans.UpdateStateSpansAsync([new StateSpan
        {
            Id = uuid, Category = StateSpanCategory.TemporaryTarget, State = "Custom",
            StartTimestamp = At(2), EndTimestamp = At(9), OriginalId = uuid,
        }]);

        written.Should().ContainSingle().Which!.EndTimestamp.Should().Be(At(9));
        var rows = await store.Context.StateSpans.AsNoTracking().ToDictionaryAsync(s => s.OriginalId!);
        rows[uuid].EndTimestamp.Should().Be(At(9), "the span whose original id is the uuid is the one updated");
        rows["by-uuid"].EndTimestamp.Should().Be(At(1));
    }

    [Fact]
    public async Task GetStateSpansAroundAsync_ReadsTheWindowOpenSpansAndOnlyTheFirstFollowingPerSource()
    {
        await using var store = await OpenAsync();
        var spans = Spans(store.Factory);
        await spans.BulkUpsertAsync(
        [
            Span("before", At(1), At(2), "ended-before", "phone"),
            Span("overlapping", At(5), At(15), "overlapping", "phone"),
            Span("open", At(100), null, "open-later", "pump"),
            .. Enumerable.Range(0, 20).Select(i => Span($"later-{i}", At(30 + i), At(30 + i).AddSeconds(30), $"phone-later-{i}", "phone")),
            .. Enumerable.Range(0, 5).Select(i => Span($"other-{i}", At(60 + i), At(60 + i).AddSeconds(30), $"watch-later-{i}", "watch")),
        ]);
        store.Recorder.Clear();

        var around = await spans.GetStateSpansAroundAsync(
            StateSpanCategory.TemporaryTarget, from: At(10), through: At(20), followingSources: ["phone"], following: 3);

        store.Recorder.Count("SELECT").Should().Be(1);
        around.Select(s => s.OriginalId).Should().BeEquivalentTo(
            ["overlapping", "open-later", "phone-later-0", "phone-later-1", "phone-later-2"],
            "a history of later spans is read only as far as the first three of each observing source");
    }

    private static Device Device(DeviceCategory category, string type, string serial) => new()
    {
        Id = Guid.CreateVersion7(), Category = category, Type = type, Serial = serial,
        FirstSeenTimestamp = Start, LastSeenTimestamp = Start,
    };

    private static DeviceStatusExtras Extras(DateTime at, Dictionary<string, object?> extras) => new()
    {
        CorrelationId = Guid.CreateVersion7(), Timestamp = at, Extras = extras,
    };

    private static StateSpan Span(string name, DateTime start, DateTime? end, string originalId, string? source = null) => new()
    {
        Category = StateSpanCategory.TemporaryTarget,
        State = "Custom",
        StartTimestamp = start,
        EndTimestamp = end,
        Source = source,
        OriginalId = originalId,
        Metadata = new() { ["name"] = name },
    };

    private static StateSpanRepository Spans(INocturneDbContextFactory factory) =>
        new(factory, new Mock<IDeduplicationService>().Object, new SystemAuditContext(), NullLogger<StateSpanRepository>.Instance);

    private static async Task<Dictionary<DateTime, Guid>> SeedApsAsync(ApsSnapshotRepository aps, string device, int[] minutes)
    {
        var written = await aps.BulkCreateAsync(
            [.. minutes.Select(m => new ApsSnapshot { Timestamp = At(m), Device = device, AidAlgorithm = AidAlgorithm.Loop })],
            WriteOrigin.Backfill);
        return written.ToDictionary(a => a.Timestamp, a => a.Id);
    }

    private async Task<Store> OpenAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var migrator = await fx.OpenMigratorConnectionAsync())
        await using (var cmd = migrator.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@tid, @slug, 'device-status-batch-reads', true, now(), now())
                """;
            cmd.Parameters.AddWithValue("tid", tenant);
            cmd.Parameters.AddWithValue("slug", $"dsbr-{tenant:N}");
            await cmd.ExecuteNonQueryAsync();
        }

        var conn = await fx.OpenAppConnectionAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tid, false), set_config('app.is_share', 'false', false)";
            cmd.Parameters.AddWithValue("tid", tenant.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        var recorder = new StatementRecorder();
        var context = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn)
            .AddInterceptors(recorder)
            .Options) { TenantId = tenant };
        return new Store(conn, context, recorder);
    }

    private sealed class Store(NpgsqlConnection connection, NocturneDbContext context, StatementRecorder recorder) : IAsyncDisposable
    {
        public NocturneDbContext Context { get; } = context;
        public StatementRecorder Recorder { get; } = recorder;
        public INocturneDbContextFactory Factory { get; } = new TestNocturneDbContextFactory(context);
        public ApsSnapshotRepository Aps =>
            new(Factory, new SystemAuditContext(), NullLogger<ApsSnapshotRepository>.Instance);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}

using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// A matched record the bulk upserts sort into one of three outcomes: unchanged (nothing written,
/// reported in <see cref="BulkWrite{TRecord}.Unchanged"/>, no cache clear, no broadcast), updated
/// without a material change (saved silently), or materially changed (saved and broadcast).
/// </summary>
[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public class UnchangedResendGoldenTests(V4GoldenFixture fx)
{
    private static readonly DateTime T0 = new(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Correlation = Guid.CreateVersion7();

    private void ClearCaptures()
    {
        fx.Capture.Clear();
        fx.CacheInvalidations.Clear();
    }

    [Fact]
    public async Task SyncKeyedResend_WritesNothing_AndIsReportedUnchanged()
    {
        using var scope = await fx.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        SensorGlucose Reading(params (string Key, object? Value)[] props) => new()
        {
            Timestamp = T0, Mgdl = 120, DataSource = "dexcom-connector", SyncIdentifier = "s-1",
            Direction = GlucoseDirection.Flat, UtcOffset = 600,
            AdditionalProperties = props.ToDictionary(p => p.Key, p => p.Value),
        };
        await repo.BulkCreateAsync([Reading(("a", 1), ("b", "x"))], WriteOrigin.Live);
        ClearCaptures();

        using var commands = new CommandCapture();
        var written = await repo.BulkCreateAsync([Reading(("b", "x"), ("a", 1))], WriteOrigin.Live);

        commands.Writes("sensor_glucose").Should().BeEmpty();
        written.Should().ContainSingle();
        written.Unchanged.Should().ContainSingle();
        written.Updated.Should().BeEmpty();
        written.Created.Should().BeEmpty();
        fx.CacheInvalidations.Snapshot().Should().BeEmpty();
        fx.Capture.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task LegacyKeyedResend_WritesNothing_AndIsReportedUnchanged()
    {
        using var scope = await fx.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = scope.ServiceProvider.GetRequiredService<IApsSnapshotRepository>();
        ApsSnapshot Snapshot() => new()
        {
            Timestamp = T0, DataSource = "loop", LegacyId = "aps-1", CorrelationId = Correlation,
            AidAlgorithm = AidAlgorithm.Loop, Iob = 1.2, SuggestedJson = """{"b": 1, "a": [1, 2]}""",
        };
        await repo.BulkUpsertAsync([Snapshot()], WriteOrigin.Live);
        ClearCaptures();

        using var commands = new CommandCapture();
        var written = await repo.BulkUpsertAsync([Snapshot()], WriteOrigin.Live);

        commands.Writes("aps_snapshots").Should().BeEmpty();
        written.Unchanged.Should().ContainSingle();
        written.Updated.Should().BeEmpty();
        fx.CacheInvalidations.Snapshot().Should().BeEmpty();
        fx.Capture.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task LegacyUpsertBatchResend_IsUnchanged()
    {
        using var scope = await fx.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = scope.ServiceProvider.GetRequiredService<IBasalScheduleRepository>();
        BasalSchedule Schedule() => new()
        {
            Timestamp = T0, DataSource = "loop", LegacyId = "p1:Default", CorrelationId = Correlation,
            Entries = [new ScheduleEntry { Time = "00:00", Value = 0.8, TimeAsSeconds = 0 }],
        };
        await repo.BulkUpsertByLegacyIdAsync([Schedule()], WriteOrigin.Live);
        ClearCaptures();

        using var commands = new CommandCapture();
        var batch = await repo.BulkUpsertByLegacyIdAsync([Schedule()], WriteOrigin.Live);

        commands.Writes("basal_schedules").Should().BeEmpty();
        batch.Outcomes["p1:Default"].Outcome.Should().Be(UpsertOutcome.Unchanged);
        fx.CacheInvalidations.Snapshot().Should().BeEmpty();
        fx.Capture.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task MixedBatch_SortsEachRecord_AndAnnouncesOnlyTheWrittenOnes()
    {
        using var scope = await fx.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();
        SensorGlucose Reading(string legacyId, DateTime at, double mgdl) => new()
        {
            Timestamp = at, Mgdl = mgdl, DataSource = "xdrip", LegacyId = legacyId, CorrelationId = Correlation,
        };
        await repo.BulkUpsertAsync(
            [Reading("same", T0, 120), Reading("moved", T0.AddMinutes(5), 130)], WriteOrigin.Live);
        ClearCaptures();
        fx.EntryCapture.Clear();

        var written = await repo.BulkUpsertAsync(
            [Reading("same", T0, 120), Reading("moved", T0.AddMinutes(6), 130), Reading("new", T0.AddMinutes(10), 140)],
            WriteOrigin.Live);

        written.Unchanged.Should().ContainSingle().Which.LegacyId.Should().Be("same");
        written.Updated.Should().ContainSingle().Which.LegacyId.Should().Be("moved");
        written.Created.Should().ContainSingle().Which.LegacyId.Should().Be("new");
        written.Should().HaveCount(3);
        fx.Capture.Snapshot().Should().BeEquivalentTo(
        [
            new BroadcastEntry("created", typeof(SensorGlucose), 1, []),
            new BroadcastEntry("updated", typeof(SensorGlucose), 1, []),
        ]);
        fx.EntryCapture.Written().Should().ContainSingle().Which.Select(e => e.Mills).Should().BeEquivalentTo(
            new[] { T0.AddMinutes(6), T0.AddMinutes(10), T0.AddMinutes(5) }
                .Select(t => new DateTimeOffset(t).ToUnixTimeMilliseconds()),
            "the moved reading reports its prior instant as vacated, and the unchanged one reports nothing");
        fx.CacheInvalidations.Snapshot().Should().NotBeEmpty().And.OnlyContain(t => t == typeof(SensorGlucose));
    }

    [Fact]
    public async Task ChangedValueInAJsonColumn_IsAMaterialUpdate()
    {
        using var scope = await fx.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = scope.ServiceProvider.GetRequiredService<IBolusRepository>();
        await repo.BulkUpsertAsync([Bolus(props: new() { ["a"] = 1 })], WriteOrigin.Live);
        ClearCaptures();

        var written = await repo.BulkUpsertAsync([Bolus(props: new() { ["a"] = 2 })], WriteOrigin.Live);

        written.Updated.Should().ContainSingle();
        written.Unchanged.Should().BeEmpty();
        fx.Capture.Snapshot().Should().ContainSingle(e => e.Kind == "updated");
    }

    [Fact]
    public async Task ChangedTimestamp_IsAMaterialUpdate()
    {
        using var scope = await fx.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = scope.ServiceProvider.GetRequiredService<IBolusRepository>();
        await repo.BulkUpsertAsync([Bolus()], WriteOrigin.Live);
        ClearCaptures();

        var written = await repo.BulkUpsertAsync([Bolus(at: T0.AddSeconds(1))], WriteOrigin.Live);

        written.Updated.Should().ContainSingle().Which.Timestamp.Should().Be(T0.AddSeconds(1));
        fx.Capture.Snapshot().Should().ContainSingle(e => e.Kind == "updated");
    }

    [Fact]
    public async Task ChangedPatientDevice_IsAMaterialUpdate()
    {
        using var scope = await fx.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = scope.ServiceProvider.GetRequiredService<IBolusRepository>();
        var device = await scope.ServiceProvider.GetRequiredService<IPatientDeviceRepository>().CreateAsync(
            new PatientDevice { DeviceCategory = DeviceCategory.InsulinPump, Manufacturer = "Insulet", Model = "Dash" },
            WriteOrigin.Live, CancellationToken.None);
        await repo.BulkUpsertAsync([Bolus()], WriteOrigin.Live);
        ClearCaptures();

        var resend = Bolus();
        resend.PatientDeviceId = device.Id;
        var written = await repo.BulkUpsertAsync([resend], WriteOrigin.Live);

        written.Updated.Should().ContainSingle().Which.PatientDeviceId.Should().Be(device.Id);
        fx.Capture.Snapshot().Should().ContainSingle(e => e.Kind == "updated");
    }

    [Fact]
    public async Task ChangedCorrelationId_IsSavedWithoutABroadcast()
    {
        using var scope = await fx.BeginTenantScopeAsync(Guid.NewGuid());
        var repo = scope.ServiceProvider.GetRequiredService<IBolusRepository>();
        await repo.BulkUpsertAsync([Bolus()], WriteOrigin.Live);
        ClearCaptures();

        var converged = Guid.CreateVersion7();
        var resend = Bolus();
        resend.CorrelationId = converged;
        var written = await repo.BulkUpsertAsync([resend], WriteOrigin.Live);

        written.Updated.Should().ContainSingle().Which.CorrelationId.Should().Be(converged);
        fx.Capture.Snapshot().Should().BeEmpty();
        fx.CacheInvalidations.Snapshot().Should().Contain(typeof(Bolus));
    }

    [Fact]
    public async Task FingerprintOnlyDifference_IsSavedWithoutABroadcastOrStamp()
    {
        var tenant = Guid.NewGuid();
        using var scope = await fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IBolusRepository>();
        using (UpstreamFingerprintScope.Open(new Dictionary<(string?, string), string?> { [("aaps", "b-1")] = "f1" }))
            await repo.BulkUpsertAsync([Bolus()], WriteOrigin.Live);
        var stampBefore = await StoredBolusAsync(tenant, b => b.SysUpdatedAt);
        ClearCaptures();

        BulkWrite<Bolus> written;
        using (UpstreamFingerprintScope.Open(new Dictionary<(string?, string), string?> { [("aaps", "b-1")] = "f2" }))
            written = await repo.BulkUpsertAsync([Bolus()], WriteOrigin.Live);

        written.Updated.Should().ContainSingle();
        written.Unchanged.Should().BeEmpty();
        fx.Capture.Snapshot().Should().BeEmpty();
        (await StoredBolusAsync(tenant, b => b.UpstreamFingerprint)).Should().Be("f2");
        (await StoredBolusAsync(tenant, b => b.SysUpdatedAt)).Should().Be(stampBefore);
    }

    [Fact]
    public async Task UserDeletedRow_StaysDeleted()
    {
        var tenant = Guid.NewGuid();
        using var scope = await fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IBolusRepository>();
        await repo.BulkUpsertAsync([Bolus()], WriteOrigin.Live);
        scope.ServiceProvider.GetRequiredService<TestAuditContext>().AttributeToUser(Guid.NewGuid());
        await repo.DeleteByLegacyIdAsync("b-1", WriteOrigin.Live);
        ClearCaptures();

        var written = await repo.BulkUpsertAsync([Bolus()], WriteOrigin.Live);

        written.Should().BeEmpty();
        written.SkippedDeleted.Should().Be(1);
        (await fx.QueryAsync(tenant, ctx => ctx.Boluses.AsNoTracking().CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task LegacyIdRepeatedInTheBatch_ComparesTheLastAgainstTheStoredRow()
    {
        var tenant = Guid.NewGuid();
        using var scope = await fx.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IBolusRepository>();
        await repo.BulkUpsertAsync([Bolus(insulin: 2.5)], WriteOrigin.Live);
        ClearCaptures();

        var unchanged = await repo.BulkUpsertAsync([Bolus(insulin: 3.0), Bolus(insulin: 2.5)], WriteOrigin.Live);
        var updated = await repo.BulkUpsertAsync([Bolus(insulin: 2.5), Bolus(insulin: 3.0)], WriteOrigin.Live);

        unchanged.Unchanged.Should().ContainSingle();
        unchanged.Updated.Should().BeEmpty();
        updated.Updated.Should().ContainSingle().Which.Insulin.Should().Be(3.0);
        (await StoredBolusAsync(tenant, b => b.Insulin)).Should().Be(3.0);
    }

    private static Bolus Bolus(double insulin = 2.5, DateTime? at = null, Dictionary<string, object?>? props = null) => new()
    {
        Timestamp = at ?? T0, Insulin = insulin, DataSource = "aaps", LegacyId = "b-1",
        CorrelationId = Correlation, AdditionalProperties = props,
    };

    private Task<T> StoredBolusAsync<T>(Guid tenant, System.Linq.Expressions.Expression<Func<Entities.V4.BolusEntity, T>> select) =>
        fx.QueryAsync(tenant, ctx => ctx.Boluses.AsNoTracking().Where(b => b.LegacyId == "b-1").Select(select).SingleAsync());

    /// <summary>Records the SQL EF runs on the current async flow.</summary>
    private sealed class CommandCapture : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private static readonly AsyncLocal<List<string>?> Sink = new();
        private readonly List<string> _commands = [];
        private readonly List<IDisposable> _subscriptions = [];
        private readonly IDisposable _listeners;

        public CommandCapture()
        {
            Sink.Value = _commands;
            _listeners = DiagnosticListener.AllListeners.Subscribe(this);
        }

        /// <summary>The INSERT and UPDATE statements run, against <paramref name="table"/> or any table.</summary>
        public List<string> Writes(string? table = null) => _commands
            .Where(c => Regex.IsMatch(c, $@"\b(UPDATE|INSERT INTO)\s+""?{table ?? @"\w+"}\b"))
            .ToList();

        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == DbLoggerCategory.Name)
                lock (_subscriptions)
                    _subscriptions.Add(value.Subscribe(this));
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key == RelationalEventId.CommandExecuting.Name && value.Value is CommandEventData data)
                Sink.Value?.Add(data.Command.CommandText);
        }

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void Dispose()
        {
            Sink.Value = null;
            _listeners.Dispose();
            lock (_subscriptions)
                _subscriptions.ForEach(s => s.Dispose());
        }
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

public class DeviceStatusDecomposerOverrideSpanTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime T0 = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly StateSpanRepository _spans;
    private readonly DeviceStatusDecomposer _decomposer;

    public DeviceStatusDecomposerOverrideSpanTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId);
        _context = _db.CreateContext();
        _spans = new StateSpanRepository(
            _context, Mock.Of<IDeduplicationService>(), new SystemAuditContext(),
            NullLogger<StateSpanRepository>.Instance);

        _decomposer = new DeviceStatusDecomposer(
            MicrosecondPrecision<IApsSnapshotRepository>.Wrap(new ApsSnapshotRepository(
                new TestTenantDbContextFactory(_context), new SystemAuditContext(),
                NullLogger<ApsSnapshotRepository>.Instance)),
            Mock.Of<IPumpSnapshotRepository>(),
            Mock.Of<IUploaderSnapshotRepository>(),
            Mock.Of<IDeviceStatusExtrasRepository>(),
            new StateSpanService(
                MicrosecondPrecision<IStateSpanRepository>.Wrap(_spans), NullLogger<StateSpanService>.Instance),
            Mock.Of<IDeviceService>(),
            Mock.Of<IAuditContext>(),
            NullLogger<DeviceStatusDecomposer>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    // Whole milliseconds, since Mills carries no finer precision.
    private static DateTime At(int slot) => T0.AddMinutes(5 * slot).AddSeconds(12).AddMilliseconds(345);

    private static DeviceStatus Snapshot(
        int slot, string? overrideName, double? duration = null, double multiplier = 0.8) => new()
    {
        Id = $"ds-{slot}",
        Mills = new DateTimeOffset(At(slot)).ToUnixTimeMilliseconds(),
        CreatedAt = At(slot).AddSeconds(13).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        Device = "loop://iPhone",
        Loop = new LoopStatus(),
        Override = overrideName is null
            ? new OverrideStatus { Active = false }
            : new OverrideStatus
            {
                Active = true,
                Name = overrideName,
                Multiplier = multiplier,
                Duration = duration,
                CurrentCorrectionRange = new CorrectionRange { MinValue = 100 + slot, MaxValue = 120 },
            },
    };

    private static List<DeviceStatus> ExerciseThenSleep() =>
    [
        Snapshot(0, "Exercise"), Snapshot(1, "Exercise"), Snapshot(2, "Exercise"), Snapshot(3, "Exercise"),
        Snapshot(4, null), Snapshot(5, null),
        Snapshot(6, "Sleep"), Snapshot(7, "Sleep"),
    ];

    private static List<DeviceStatus> Ordered(List<DeviceStatus> statuses, bool newestFirst)
    {
        if (newestFirst)
            statuses.Reverse();
        return statuses;
    }

    private async Task DecomposeEachAsync(IEnumerable<DeviceStatus> statuses)
    {
        foreach (var ds in statuses)
            await _decomposer.DecomposeAsync(ds, WriteOrigin.Live);
    }

    private async Task<List<StateSpan>> OverrideSpansAsync() =>
        (await _spans.GetStateSpansAsync(category: StateSpanCategory.Override, descending: false)).ToList();

    private Task<StateSpan> TreatmentOverrideAsync(int slot, string reason, double factor) =>
        _spans.UpsertStateSpanAsync(new StateSpan
        {
            Category = StateSpanCategory.Override,
            State = OverrideState.Custom.ToString(),
            StartTimestamp = At(slot),
            Source = "Loop",
            OriginalId = $"treatment-{slot}",
            Metadata = new Dictionary<string, object>
            {
                [StateSpanMetadataExtensions.CollectionKey] = StateSpanMetadataExtensions.TreatmentsCollection,
                ["reason"] = reason,
                ["insulinNeedsScaleFactor"] = factor,
                ["durationType"] = "indefinite",
                ["enteredBy"] = "Loop",
            },
        });

    [Fact]
    public async Task DecomposeAsync_IndefiniteOverride_IsOneOpenSpan()
    {
        await DecomposeEachAsync(Enumerable.Range(0, 6).Select(i => Snapshot(i, "Exercise")));

        var span = (await OverrideSpansAsync()).Should().ContainSingle().Subject;
        span.StartTimestamp.Should().Be(At(0));
        span.EndTimestamp.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecomposeAsync_InactiveThenDifferentOverride_EndsAndOpensSpans(bool newestFirst)
    {
        await DecomposeEachAsync(Ordered(ExerciseThenSleep(), newestFirst));

        var spans = await OverrideSpansAsync();
        spans.Should().HaveCount(2);
        spans[0].StartTimestamp.Should().Be(At(0));
        spans[0].EndTimestamp.Should().Be(At(4));
        spans[1].StartTimestamp.Should().Be(At(6));
        spans[1].EndTimestamp.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecomposeAsync_OverrideAThenB_EndsAWhereBStarts(bool newestFirst)
    {
        await DecomposeEachAsync(Ordered(
            [Snapshot(0, "Exercise"), Snapshot(1, "Exercise"), Snapshot(2, "Sleep"), Snapshot(3, "Sleep")],
            newestFirst));

        var spans = await OverrideSpansAsync();
        spans.Should().HaveCount(2);
        spans[0].EndTimestamp.Should().Be(At(2));
        spans[1].StartTimestamp.Should().Be(At(2));
        spans[1].EndTimestamp.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecomposeAsync_UploadGapWithoutContraryObservation_StaysOneSpan(bool newestFirst)
    {
        await DecomposeEachAsync(Ordered(
            [Snapshot(0, "Exercise"), Snapshot(1, "Exercise"), Snapshot(2000, "Exercise")], newestFirst));

        var span = (await OverrideSpansAsync()).Should().ContainSingle().Subject;
        span.StartTimestamp.Should().Be(At(0));
        span.EndTimestamp.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecomposeAsync_UploadGapWithInactiveSnapshot_EndsSpanThere(bool newestFirst)
    {
        await DecomposeEachAsync(Ordered(
            [Snapshot(0, "Exercise"), Snapshot(100, null), Snapshot(200, "Exercise")], newestFirst));

        var spans = await OverrideSpansAsync();
        spans.Select(s => (s.StartTimestamp, s.EndTimestamp)).Should().Equal(
            (At(0), At(100)),
            (At(200), (DateTime?)null));
    }

    [Fact]
    public async Task DecomposeAsync_Repeated_ChangesNothing()
    {
        await DecomposeEachAsync(ExerciseThenSleep());
        var first = await OverrideSpansAsync();

        await DecomposeEachAsync(ExerciseThenSleep());
        await DecomposeEachAsync(Ordered(ExerciseThenSleep(), newestFirst: true));

        (await OverrideSpansAsync()).Select(s => (s.StartTimestamp, s.EndTimestamp))
            .Should().Equal(first.Select(s => (s.StartTimestamp, s.EndTimestamp)));
    }

    [Fact]
    public async Task DecomposeBatchAsync_AnyOrder_YieldsOneSpanPerPeriod()
    {
        var inOrder = ExerciseThenSleep();
        var shuffled = new[] { 3, 6, 0, 5, 7, 1, 4, 2 }.Select(i => inOrder[i]).ToList();

        await _decomposer.DecomposeBatchAsync(shuffled, source: null, WriteOrigin.Live);
        await _decomposer.DecomposeBatchAsync(shuffled, source: null, WriteOrigin.Live);

        var spans = await OverrideSpansAsync();
        spans.Should().HaveCount(2);
        spans[0].StartTimestamp.Should().Be(At(0));
        spans[0].EndTimestamp.Should().Be(At(4));
        spans[1].StartTimestamp.Should().Be(At(6));
    }

    [Fact]
    public async Task DecomposeBatchAsync_OlderPageBehindNewer_ExtendsNewerSpanBackward()
    {
        var statuses = Enumerable.Range(0, 8).Select(i => Snapshot(i, "Exercise")).ToList();

        await _decomposer.DecomposeBatchAsync(statuses[4..], source: null, WriteOrigin.Live);
        await _decomposer.DecomposeBatchAsync(statuses[..4], source: null, WriteOrigin.Live);

        var span = (await OverrideSpansAsync()).Should().ContainSingle().Subject;
        span.StartTimestamp.Should().Be(At(0));
        span.EndTimestamp.Should().BeNull();
    }

    [Fact]
    public async Task DecomposeAsync_OverrideWithDuration_EndsAtDeclaredEndOrCancel()
    {
        // Loop reports the remaining duration, so each snapshot declares the same end.
        await DecomposeEachAsync(
        [
            Snapshot(0, "Pre-Meal", duration: 3600), Snapshot(1, "Pre-Meal", duration: 3300),
            Snapshot(2, "Pre-Meal", duration: 3000),
        ]);

        var span = (await OverrideSpansAsync()).Should().ContainSingle().Subject;
        span.EndTimestamp.Should().Be(At(0).AddHours(1));

        await _decomposer.DecomposeAsync(Snapshot(3, null), WriteOrigin.Live);

        (await OverrideSpansAsync()).Should().ContainSingle().Which.EndTimestamp.Should().Be(At(3));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecomposeAsync_SnapshotsOfTreatmentOverride_LeaveTreatmentOpen(bool newestFirst)
    {
        await TreatmentOverrideAsync(0, "N Night", 0.9);

        await DecomposeEachAsync(Ordered(
            Enumerable.Range(1, 5).Select(i => Snapshot(i, "Night", multiplier: 0.9)).ToList(), newestFirst));

        var spans = await OverrideSpansAsync();
        spans.Single(s => s.Source == "Loop").EndTimestamp.Should().BeNull();
        spans.Single(s => s.Source == "loop://iPhone").StartTimestamp.Should().Be(At(1));
    }

    [Fact]
    public async Task DecomposeBatchAsync_TreatmentAndSnapshotsOfSameOverride_BothStayOpen()
    {
        await TreatmentOverrideAsync(0, "N Night", 0.9);
        var statuses = Enumerable.Range(1, 12).Select(i => Snapshot(i, "Night", multiplier: 0.9)).ToList();

        await _decomposer.DecomposeBatchAsync(statuses[6..], source: null, WriteOrigin.Live);
        await _decomposer.DecomposeBatchAsync(statuses[..6], source: null, WriteOrigin.Live);

        var spans = await OverrideSpansAsync();
        spans.Should().HaveCount(2).And.OnlyContain(s => s.EndTimestamp == null);
        spans.Single(s => s.Source == "loop://iPhone").StartTimestamp.Should().Be(At(1));
    }

    [Fact]
    public async Task DecomposeAsync_NewestFirstWithStaleOpenSpans_ExtendsNextSpan()
    {
        // A store written before spans were closed holds many open ones from the uploader.
        for (var i = 0; i < 12; i++)
        {
            _context.StateSpans.Add(StateSpanMapper.ToEntity(new StateSpan
            {
                Category = StateSpanCategory.Override,
                State = OverrideState.Custom.ToString(),
                StartTimestamp = At(-100 + i),
                Source = "loop://iPhone",
                OriginalId = $"stale-{i}",
                Metadata = new Dictionary<string, object> { ["name"] = "Other" },
            }));
        }
        await _context.SaveChangesAsync();

        await DecomposeEachAsync(Ordered(
            Enumerable.Range(1, 5).Select(i => Snapshot(i, "Night")).ToList(), newestFirst: true));

        var night = (await OverrideSpansAsync()).Where(s => s.Metadata.TryReadString("name") == "Night");
        night.Should().ContainSingle().Which.StartTimestamp.Should().Be(At(1));
    }

    [Fact]
    public async Task DecomposeAsync_InactiveSnapshotOverManyStaleOpenSpans_EndsEveryOne()
    {
        for (var i = 0; i < 25; i++)
        {
            _context.StateSpans.Add(StateSpanMapper.ToEntity(new StateSpan
            {
                Category = StateSpanCategory.Override,
                State = OverrideState.Custom.ToString(),
                StartTimestamp = At(-100 + i),
                Source = "loop://iPhone",
                OriginalId = $"stale-{i}",
                Metadata = new Dictionary<string, object> { ["name"] = "Night" },
            }));
        }
        await _context.SaveChangesAsync();

        await _decomposer.DecomposeAsync(Snapshot(1, null), WriteOrigin.Live);

        (await OverrideSpansAsync()).Should().HaveCount(25).And.OnlyContain(s => s.EndTimestamp == At(1));
    }

    [Fact]
    public async Task DecomposeAsync_ActiveOverrideWithNoFields_IsOneOpenSpan()
    {
        var statuses = Enumerable.Range(1, 3).Select(i => Snapshot(i, "Night")).ToList();
        foreach (var ds in statuses)
            ds.Override = new OverrideStatus { Active = true };

        await DecomposeEachAsync(statuses);

        var span = (await OverrideSpansAsync()).Should().ContainSingle().Subject;
        span.StartTimestamp.Should().Be(At(1));
        span.EndTimestamp.Should().BeNull();
    }

    [Fact]
    public async Task DecomposeAsync_DifferentOverrideSnapshot_EndsTreatmentOverride()
    {
        await TreatmentOverrideAsync(0, "N Night", 0.9);

        await DecomposeEachAsync([Snapshot(1, "Night", multiplier: 0.9), Snapshot(5, "Exercise")]);

        (await OverrideSpansAsync()).Single(s => s.Source == "Loop").EndTimestamp.Should().Be(At(5));
    }

    [Fact]
    public async Task DecomposeAsync_SameOverrideFromAnotherUploader_EndsItsSpan()
    {
        var oldPhone = Snapshot(0, "Night");
        oldPhone.Device = "loop://Old iPhone";
        await _decomposer.DecomposeAsync(oldPhone, WriteOrigin.Live);

        await _decomposer.DecomposeAsync(Snapshot(5, "Night"), WriteOrigin.Live);

        var spans = await OverrideSpansAsync();
        spans.Single(s => s.Source == "loop://Old iPhone").EndTimestamp.Should().Be(At(5));
        spans.Single(s => s.Source == "loop://iPhone").EndTimestamp.Should().BeNull();
    }

    [Fact]
    public async Task DecomposeAsync_InactiveSnapshot_EndsTreatmentOverride()
    {
        await TreatmentOverrideAsync(0, "N Night", 0.9);

        await DecomposeEachAsync([Snapshot(1, "Night", multiplier: 0.9), Snapshot(5, null)]);

        var spans = await OverrideSpansAsync();
        spans.Should().OnlyContain(s => s.EndTimestamp == At(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecomposeAsync_MultiplierEdits_SplitSnapshotSpansButNotTreatment(bool newestFirst)
    {
        await TreatmentOverrideAsync(0, "N Night", 0.9);

        await DecomposeEachAsync(Ordered([
            Snapshot(1, "Night", multiplier: 0.9), Snapshot(2, "Night", multiplier: 0.9),
            Snapshot(3, "Night", multiplier: 0.6), Snapshot(4, "Night", multiplier: 0.9),
        ], newestFirst));

        var spans = await OverrideSpansAsync();
        spans.Single(s => s.Source == "Loop").EndTimestamp.Should().BeNull();
        spans.Where(s => s.Source == "loop://iPhone")
            .Select(s => (s.StartTimestamp, s.EndTimestamp))
            .Should().Equal((At(1), At(3)), (At(3), At(4)), (At(4), (DateTime?)null));
    }
}

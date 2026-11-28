using System.Globalization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// The device-status pump-mode folds through the real store, whose clock stamps when each snapshot and
/// span arrived (<see cref="PumpModeSpanClock"/>). A span or transition dated beyond the clock bound when
/// it arrived is ahead; a pump whose clock is consistently fast still ends its own spans.
/// </summary>
[Trait("Category", "Unit")]
public class DeviceStatusDecomposerPumpModeClockTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime T = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DayAhead = T.AddDays(1);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(T.AddDays(-1)));
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly IAuditContext _audit = new SystemAuditContext();
    private readonly StateSpanRepository _spans;
    private readonly PumpSnapshotRepository _pumps;
    private readonly DeviceStatusDecomposer _decomposer;

    public DeviceStatusDecomposerPumpModeClockTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId).WithClock(_clock);
        _context = _db.CreateContext();
        var contexts = new TestNocturneDbContextFactory(_context);
        _spans = new StateSpanRepository(contexts, Mock.Of<IDuplicateLinker>(), _audit, NullLogger<StateSpanRepository>.Instance);
        _pumps = new PumpSnapshotRepository(contexts, _audit, NullLogger<PumpSnapshotRepository>.Instance);
        _decomposer = DeviceStatusDecomposerDoubles.Stored(
            contexts, _audit, stateSpans: new StateSpanService(_spans, NullLogger<StateSpanService>.Instance), time: _clock);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A suspension dated a day ahead of the clock does not stop a real one from opening its span: the
    /// real suspension ends it at its own start, and a later resume ends the real one.
    /// </summary>
    [Fact]
    public async Task ASuspensionDatedADayAhead_EndsAtItsOwnStart_AndARealSuspensionOpensAndResumes()
    {
        await FoldAsync(T.AddHours(-1), Suspension("before", T.AddHours(-1), suspended: false));
        await FoldAsync(T.AddMinutes(-30), Suspension("ahead", DayAhead, suspended: true));

        await FoldAsync(T, Suspension("real-suspend", T, suspended: true));
        await FoldAsync(T.AddHours(1), Suspension("real-resume", T.AddHours(1), suspended: false));

        (await SpansAsync(PumpModeState.Suspended)).Should().BeEquivalentTo(
            [(DayAhead, (DateTime?)DayAhead), (T, (DateTime?)T.AddHours(1))]);
    }

    /// <summary>
    /// The store ended the real suspension at the start of one dated a day ahead. The resume ends that
    /// one at its own start, after which it ends nothing, so the real one ends at the resume.
    /// </summary>
    [Fact]
    public async Task AResume_EndsTheRealSuspensionAndOneDatedADayAheadAtItsOwnStart()
    {
        await FoldAsync(T.AddHours(-2), Suspension("before", T.AddHours(-2), suspended: false));
        await FoldAsync(T.AddHours(-1), Suspension("suspend", T.AddHours(-1), suspended: true));
        await StoreSpanAsync(T.AddMinutes(-30), "ahead", PumpModeState.Suspended, DayAhead);

        await FoldAsync(T, Suspension("resume", T, suspended: false));

        (await SpansAsync(PumpModeState.Suspended)).Should().BeEquivalentTo(
            [(T.AddHours(-1), (DateTime?)T), (DayAhead, (DateTime?)DayAhead)]);
    }

    /// <summary>
    /// A resume dated a day ahead ends the real suspension when it arrived, not at a future time.
    /// </summary>
    [Fact]
    public async Task AResumeDatedADayAhead_EndsTheRealSuspensionWhenItArrived()
    {
        await FoldAsync(T.AddHours(-2), Suspension("before", T.AddHours(-2), suspended: false));
        await FoldAsync(T.AddHours(-1), Suspension("suspend", T.AddHours(-1), suspended: true));

        await FoldAsync(T, Suspension("resume-ahead", DayAhead, suspended: false));

        (await SpansAsync(PumpModeState.Suspended)).Should().Equal([(T.AddHours(-1), (DateTime?)T)]);
    }

    /// <summary>
    /// A suspension seen by two snapshots, then a resume whose pump clock is a day ahead, arriving below the
    /// millisecond.
    /// The resume ends the suspension at its arrival as the store keeps it, and decomposed again it finds that
    /// end, so it backfills no suspension from the second snapshot.
    /// </summary>
    [Fact]
    public async Task AResumeDatedADayAheadArrivingBelowTheMillisecond_DecomposedAgain_BackfillsNoSuspension()
    {
        var arrived = T.AddTicks(1_234_567);
        await FoldAsync(T.AddHours(-2), Suspension("before", T.AddHours(-2), suspended: false));
        await FoldAsync(T.AddHours(-1), Suspension("suspend", T.AddHours(-1), suspended: true));
        await FoldAsync(T.AddMinutes(-30), Suspension("still", T.AddMinutes(-30), suspended: true));
        await FoldAsync(arrived, Suspension("resume-ahead", T, suspended: false, clock: DayAhead));
        var ended = (T.AddHours(-1), (DateTime?)RecordTime.AtMillisecond(arrived));

        (await SpansAsync(PumpModeState.Suspended)).Should().Equal([ended]);

        await FoldAsync(T.AddHours(2), Suspension("resume-ahead", T, suspended: false, clock: DayAhead));

        (await SpansAsync(PumpModeState.Suspended)).Should().Equal([ended]);
    }

    /// <summary>
    /// A suspension seen by two snapshots, then a resume sent a day ahead, arriving below the millisecond,
    /// which ends the suspension at its arrival as the store keeps it. Decomposed again on its own, it finds
    /// that end, a day before its own time, so it backfills no suspension from the second snapshot and
    /// writes no span.
    /// </summary>
    [Fact]
    public async Task AResumeSentADayAhead_DecomposedAgainOnItsOwn_BackfillsNothingAndChangesNothing()
    {
        var arrived = T.AddTicks(1_234_567);
        await FoldAsync(T.AddHours(-2), Suspension("before", T.AddHours(-2), suspended: false));
        await FoldAsync(T.AddHours(-1), Suspension("suspend", T.AddHours(-1), suspended: true));
        await FoldAsync(T.AddMinutes(-30), Suspension("still", T.AddMinutes(-30), suspended: true));
        await FoldAsync(arrived, Suspension("resume-ahead", DayAhead, suspended: false));
        var ended = (T.AddHours(-1), (DateTime?)RecordTime.AtMillisecond(arrived));

        (await SpansAsync(PumpModeState.Suspended)).Should().Equal([ended]);

        var again = await FoldAsync(T.AddHours(2), Suspension("resume-ahead", DayAhead, suspended: false));

        (await SpansAsync(PumpModeState.Suspended)).Should().Equal([ended]);
        again.CreatedRecords.Concat(again.UpdatedRecords).Concat(again.UnchangedRecords).OfType<StateSpan>()
            .Should().BeEmpty();
    }

    /// <summary>A resume dated a day ahead backfills a suspension that ends when it arrived, not at a future time.</summary>
    [Fact]
    public async Task AResumeDatedADayAhead_BackfillsASuspensionEndingWhenItArrived()
    {
        await StoreSnapshotAsync(T.AddHours(-1), "suspended", T.AddHours(-1), clock: null);

        await FoldAsync(T, Suspension("resume-ahead", DayAhead, suspended: false));

        (await SpansAsync(PumpModeState.Suspended)).Should().Equal([(T.AddHours(-1), (DateTime?)T)]);
    }

    /// <summary>
    /// A pump clock set an hour forward during a suspension (a daylight saving change on the pump) dates
    /// the resume ahead. The suspension ends when the resume arrived.
    /// </summary>
    [Fact]
    public async Task APumpClockSetForwardDuringASuspension_EndsItWhenTheResumeArrived()
    {
        await FoldAsync(T.AddHours(-1), Suspension("before", T.AddHours(-1), suspended: false, clock: T.AddHours(-1)));
        await FoldAsync(T, Suspension("suspend", T, suspended: true, clock: T));
        await FoldAsync(T.AddMinutes(90), Suspension("resume", T.AddMinutes(90), suspended: false, clock: T.AddMinutes(150)));

        (await SpansAsync(PumpModeState.Suspended)).Should().Equal([(T, (DateTime?)T.AddMinutes(90))]);
    }

    /// <summary>
    /// A pump about the clock tolerance fast: its suspend arrived 4 minutes ahead, within the bound, and its
    /// resume 6 minutes ahead, beyond it. Their leads agree within the tolerance, so they read one clock and
    /// the resume ends the suspension at its own time.
    /// </summary>
    [Fact]
    public async Task APumpNearTheClockTolerance_ItsResumeEndsTheSuspensionAtItsOwnTime()
    {
        await FoldAsync(T.AddHours(-1), Suspension("before", T.AddHours(-1), suspended: false, clock: T.AddHours(-1).AddMinutes(4)));
        await FoldAsync(T, Suspension("suspend", T, suspended: true, clock: T.AddMinutes(4)));
        await FoldAsync(T.AddMinutes(90), Suspension("resume", T.AddMinutes(90), suspended: false, clock: T.AddMinutes(96)));

        (await SpansAsync(PumpModeState.Suspended)).Should().Equal([(T.AddMinutes(4), (DateTime?)T.AddMinutes(96))]);
    }

    /// <summary>
    /// A pump 10 minutes fast suspends, then sends a resume dated a day ahead. The two leads disagree, so
    /// the resume is ahead of the suspension and ends it when the resume arrived, not a day past the present.
    /// </summary>
    [Fact]
    public async Task AFastPump_AResumeDatedADayAhead_EndsTheSuspensionWhenItArrived()
    {
        var fast = TimeSpan.FromMinutes(10);
        await FoldAsync(T.AddHours(-1), Suspension("before", T.AddHours(-1), suspended: false, clock: T.AddHours(-1) + fast));
        await FoldAsync(T, Suspension("suspend", T, suspended: true, clock: T + fast));

        await FoldAsync(T.AddMinutes(20), Suspension("resume-ahead", T.AddMinutes(20), suspended: false, clock: DayAhead.AddMinutes(30)));

        (await SpansAsync(PumpModeState.Suspended)).Should().Equal([(T + fast, (DateTime?)T.AddMinutes(20))]);
    }

    /// <summary>
    /// A suspension dated a day ahead is stored, then a pump 10 minutes fast suspends and resumes. The real
    /// suspend is ahead of the bound too, but not of the day-ahead span, so it ends that span at its own
    /// start and opens its own, which its resume ends.
    /// </summary>
    [Fact]
    public async Task AFastPump_ASuspensionDatedADayAhead_EndsAtItsOwnStart_AndTheRealSuspensionOpensAndResumes()
    {
        var fast = TimeSpan.FromMinutes(10);
        await FoldAsync(T.AddHours(-1), Suspension("before", T.AddHours(-1), suspended: false, clock: T.AddHours(-1) + fast));
        await FoldAsync(T.AddMinutes(-30), Suspension("ahead", DayAhead, suspended: true));

        await FoldAsync(T, Suspension("suspend", T, suspended: true, clock: T + fast));
        await FoldAsync(T.AddMinutes(90), Suspension("resume", T.AddMinutes(90), suspended: false, clock: T.AddMinutes(90) + fast));

        (await SpansAsync(PumpModeState.Suspended)).Should().BeEquivalentTo(
            [(DayAhead, (DateTime?)DayAhead), (T + fast, (DateTime?)(T.AddMinutes(90) + fast))]);
    }

    /// <summary>
    /// A resume backfills no suspension from a prior snapshot whose pump clock was a day ahead: the
    /// span would only end at its own start.
    /// </summary>
    [Fact]
    public async Task AResume_BackfillsNoSuspensionFromAPriorDatedADayAhead()
    {
        await StoreSnapshotAsync(T.AddHours(-1), "suspended-ahead", T.AddHours(-1), clock: DayAhead);

        await FoldAsync(T, Suspension("resume", T, suspended: false));

        (await SpansAsync(PumpModeState.Suspended)).Should().BeEmpty();
    }

    /// <summary>
    /// A mode dated a day ahead ends the real mode when it arrived, not at a future time. The next real
    /// transition ends the ahead span at its own start, so it does not block the mode it opens.
    /// </summary>
    [Fact]
    public async Task APumpModeTransitionDatedADayAhead_EndsTheRealModeWhenItArrived()
    {
        await FoldAsync(T.AddHours(-1), Mode("auto", T.AddHours(-1), PumpModeState.Automatic));

        await FoldAsync(T, Mode("manual-ahead", DayAhead, PumpModeState.Manual));
        (await ModeSpansAsync()).Should().BeEquivalentTo(
        [
            ("Automatic", T.AddHours(-1), (DateTime?)T),
            ("Manual", DayAhead, (DateTime?)null),
        ]);

        await FoldAsync(T.AddMinutes(10), Mode("manual", T.AddMinutes(10), PumpModeState.Manual));
        (await ModeSpansAsync()).Should().BeEquivalentTo(
        [
            ("Automatic", T.AddHours(-1), (DateTime?)T),
            ("Manual", DayAhead, (DateTime?)DayAhead),
            ("Manual", T.AddMinutes(10), (DateTime?)null),
        ]);
    }

    /// <summary>
    /// A pump clock set an hour forward while in Automatic dates the switch to Manual ahead. Automatic ends
    /// when the switch arrived, and Manual opens at the pump's time.
    /// </summary>
    [Fact]
    public async Task APumpClockSetForwardDuringAMode_EndsItWhenTheNextModeArrived()
    {
        await FoldAsync(T.AddHours(-1), Mode("auto", T.AddHours(-1), PumpModeState.Automatic, clock: T.AddHours(-1)));
        await FoldAsync(T, Mode("manual", T, PumpModeState.Manual, clock: T.AddHours(1)));

        (await ModeSpansAsync()).Should().BeEquivalentTo(
        [
            ("Automatic", T.AddHours(-1), (DateTime?)T),
            ("Manual", T.AddHours(1), (DateTime?)null),
        ]);
    }

    /// <summary>
    /// A real transition ends a span of the mode it leaves at the transition, and one of that mode
    /// dated a day ahead at that one's own start.
    /// </summary>
    [Fact]
    public async Task APumpModeTransition_EndsTheModeItLeavesDatedADayAheadAtItsOwnStart()
    {
        await FoldAsync(T.AddHours(-1), Mode("auto", T.AddHours(-1), PumpModeState.Automatic));
        await StoreSpanAsync(T.AddMinutes(-30), "auto-ahead", PumpModeState.Automatic, DayAhead);

        await FoldAsync(T, Mode("manual", T, PumpModeState.Manual));

        (await ModeSpansAsync()).Should().BeEquivalentTo(
        [
            ("Automatic", T.AddHours(-1), (DateTime?)T),
            ("Automatic", DayAhead, (DateTime?)DayAhead),
            ("Manual", T, (DateTime?)null),
        ]);
    }

    /// <summary>
    /// A suspension that starts after a resume but within the clock bound belongs to a later
    /// transition, which the older resume does not end.
    /// </summary>
    [Fact]
    public async Task AnOutOfOrderResume_DoesNotEndALaterSuspensionWithinTheBound()
    {
        await FoldAsync(T.AddHours(-2), Suspension("before", T.AddHours(-2), suspended: false));
        await FoldAsync(T.AddHours(-1), Suspension("suspend", T.AddHours(-1), suspended: true));
        await StoreSpanAsync(T, "later", PumpModeState.Suspended, T.AddMinutes(2));

        await FoldAsync(T, Suspension("resume", T, suspended: false));

        (await _spans.GetStateSpansAsync(category: StateSpanCategory.PumpMode))
            .Should().ContainSingle(s => s.OriginalId == "later").Which.EndTimestamp.Should().BeNull();
    }

    /// <summary>
    /// A suspension that starts after a new one but within the clock bound is still open, so the new
    /// one opens no second span.
    /// </summary>
    [Fact]
    public async Task ASuspensionWithinTheBound_StillBlocksASecondOpenSpan()
    {
        await FoldAsync(T.AddHours(-1), Suspension("before", T.AddHours(-1), suspended: false));
        await StoreSpanAsync(T, "later", PumpModeState.Suspended, T.AddMinutes(2));

        await FoldAsync(T, Suspension("suspend", T, suspended: true));

        (await SpansAsync(PumpModeState.Suspended)).Should().Equal([(T.AddMinutes(2), (DateTime?)null)]);
    }

    /// <summary>
    /// A mode span that starts after a transition but within the clock bound belongs to a later one,
    /// which the older transition does not end.
    /// </summary>
    [Fact]
    public async Task AnOutOfOrderPumpModeTransition_DoesNotEndALaterModeWithinTheBound()
    {
        await StoreSnapshotAsync(T.AddHours(-1), "auto", T.AddHours(-1), clock: null, mode: PumpModeState.Automatic);
        await StoreSpanAsync(T, "auto-later", PumpModeState.Automatic, T.AddMinutes(2));

        await FoldAsync(T, Mode("manual", T, PumpModeState.Manual));

        (await _spans.GetStateSpansAsync(category: StateSpanCategory.PumpMode))
            .Should().ContainSingle(s => s.OriginalId == "auto-later").Which.EndTimestamp.Should().BeNull();
    }

    /// <summary>
    /// A pump whose clock runs fast (ten minutes, or an hour when it missed a daylight saving change)
    /// reports its suspend and its resume equally ahead, so its resume ends the suspension it opened.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(60)]
    public async Task AFastPump_ItsResumeEndsTheSuspensionItOpened(int minutesFast)
    {
        var fast = TimeSpan.FromMinutes(minutesFast);
        await FoldAsync(T.AddHours(-1), Suspension("before", T.AddHours(-1), suspended: false, clock: T.AddHours(-1) + fast));
        await FoldAsync(T, Suspension("suspend", T, suspended: true, clock: T + fast));
        await FoldAsync(T.AddMinutes(90), Suspension("resume", T.AddMinutes(90), suspended: false, clock: T.AddMinutes(90) + fast));

        (await SpansAsync(PumpModeState.Suspended)).Should().Equal([(T + fast, (DateTime?)(T.AddMinutes(90) + fast))]);
    }

    /// <summary>
    /// A pump 6 or 60 minutes fast suspends, and an older suspended status of that pump, dated before the
    /// suspension arrived, arrives three hours late. It is an older transition, so it opens no span and leaves
    /// the suspension open, and the real resume ends it.
    /// </summary>
    [Theory]
    [InlineData(6, 30)]
    [InlineData(60, 90)]
    public async Task AFastPump_AnOlderSuspendArrivingLate_LeavesTheSuspensionForItsOwnResume(int minutesFast, int minutesOld)
    {
        var fast = TimeSpan.FromMinutes(minutesFast);
        var old = T.AddMinutes(-minutesOld);
        await FoldAsync(T.AddHours(-2), Suspension("before", T.AddHours(-2), suspended: false, clock: T.AddHours(-2) + fast));
        await FoldAsync(T, Suspension("suspend", T, suspended: true, clock: T + fast));

        await FoldAsync(T.AddHours(3), Suspension("suspend-late", old, suspended: true, clock: old + fast));
        (await SpansAsync(PumpModeState.Suspended)).Should().Equal([(T + fast, (DateTime?)null)]);

        await FoldAsync(T.AddHours(4), Suspension("resume", T.AddHours(4), suspended: false, clock: T.AddHours(4) + fast));
        (await SpansAsync(PumpModeState.Suspended)).Should().Equal([(T + fast, (DateTime?)(T.AddHours(4) + fast))]);
    }

    /// <summary>
    /// A pump an hour fast suspends, and a suspended status of that pump half an hour older arrives three
    /// hours late. Dated after the suspension arrived, it cannot be told from a lagged newest status, so it
    /// overtakes the suspension, which ends at its own start.
    /// </summary>
    [Fact]
    public async Task AFastPump_ASuspendArrivingLate_DatedAfterTheSuspensionArrived_EndsItAtItsOwnStart()
    {
        var fast = TimeSpan.FromHours(1);
        var old = T.AddMinutes(-30);
        await FoldAsync(T.AddHours(-2), Suspension("before", T.AddHours(-2), suspended: false, clock: T.AddHours(-2) + fast));
        await FoldAsync(T, Suspension("suspend", T, suspended: true, clock: T + fast));

        await FoldAsync(T.AddHours(3), Suspension("suspend-late", old, suspended: true, clock: old + fast));

        (await SpansAsync(PumpModeState.Suspended)).Should().ContainSingle(s => s.Start == T + fast)
            .Which.End.Should().Be(T + fast);
    }

    /// <summary>
    /// A source that sends each status some minutes after its time suspends and resumes after a suspension
    /// dated a day ahead was stored. Each status arrived late but is newer than the ahead span, so the suspend
    /// ends that span at its own start and opens the real suspension, which the resume ends.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    public async Task ALaggedSource_ASuspensionDatedADayAhead_EndsAtItsOwnStart_AndTheRealSuspensionResumes(int minutesLate)
    {
        var lag = TimeSpan.FromMinutes(minutesLate);
        await FoldAsync(T.AddHours(-1) + lag, Suspension("before", T.AddHours(-1), suspended: false));
        await FoldAsync(T.AddMinutes(-30), Suspension("ahead", DayAhead, suspended: true));

        await FoldAsync(T + lag, Suspension("suspend", T, suspended: true));
        await FoldAsync(T.AddHours(1) + lag, Suspension("resume", T.AddHours(1), suspended: false));

        (await SpansAsync(PumpModeState.Suspended)).Should().BeEquivalentTo(
            [(DayAhead, (DateTime?)DayAhead), (T, (DateTime?)T.AddHours(1))]);
    }

    /// <summary>
    /// A connector that sends each mode some minutes after its time switches to Manual after a Manual span
    /// dated a day ahead was stored. The switch arrived late but is newer than the ahead span, so it ends that
    /// span at its own start and opens Manual, which the next switch ends.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    public async Task ALaggedSource_AModeDatedADayAhead_EndsAtItsOwnStart_AndTheRealModeOpens(int minutesLate)
    {
        var lag = TimeSpan.FromMinutes(minutesLate);
        await FoldAsync(T.AddHours(-1) + lag, Mode("auto", T.AddHours(-1), PumpModeState.Automatic));
        await StoreSpanAsync(T.AddMinutes(-30), "manual-ahead", PumpModeState.Manual, DayAhead);

        await FoldAsync(T + lag, Mode("manual", T, PumpModeState.Manual));
        await FoldAsync(T.AddHours(1) + lag, Mode("auto-again", T.AddHours(1), PumpModeState.Automatic));

        (await ModeSpansAsync()).Should().BeEquivalentTo(
        [
            ("Automatic", T.AddHours(-1), (DateTime?)T),
            ("Manual", DayAhead, (DateTime?)DayAhead),
            ("Manual", T, (DateTime?)T.AddHours(1)),
            ("Automatic", T.AddHours(1), (DateTime?)null),
        ]);
    }

    /// <summary>
    /// A pump whose clock runs fast ends each mode at the next transition, and Automatic and Manual are
    /// never open together.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(60)]
    public async Task AFastPump_ItsModeTransitionsEndEachOther_AndNeverLeaveBothModesOpen(int minutesFast)
    {
        var fast = TimeSpan.FromMinutes(minutesFast);
        (DateTime Real, PumpModeState Mode)[] transitions =
            [(T, PumpModeState.Automatic), (T.AddMinutes(90), PumpModeState.Manual), (T.AddMinutes(180), PumpModeState.Automatic)];

        foreach (var (real, mode) in transitions)
        {
            await FoldAsync(real, Mode($"mode-{real:HHmm}", real, mode, clock: real + fast));
            (await ModeSpansAsync()).Count(s => s.End is null).Should().Be(1, "one mode is active at {0}", real);
        }

        (await ModeSpansAsync()).Should().BeEquivalentTo(
        [
            ("Automatic", T + fast, (DateTime?)(T.AddMinutes(90) + fast)),
            ("Manual", T.AddMinutes(90) + fast, (DateTime?)(T.AddMinutes(180) + fast)),
            ("Automatic", T.AddMinutes(180) + fast, (DateTime?)null),
        ]);
    }

    /// <summary>
    /// A snapshot that said Manual at a time a day ahead is judged as it arrived: decomposed again after
    /// the clock has passed that time, it gives the same spans.
    /// </summary>
    [Fact]
    public async Task ATransitionDatedADayAhead_DecomposedAgainAfterTheClockPassesIt_GivesTheSameSpans()
    {
        await FoldAsync(T.AddHours(-1), Mode("auto", T.AddHours(-1), PumpModeState.Automatic));
        await FoldAsync(T, Mode("manual-ahead", T, PumpModeState.Manual, clock: DayAhead));
        var first = await ModeSpansAsync();

        await FoldAsync(DayAhead.AddHours(1), Mode("manual-ahead", T, PumpModeState.Manual, clock: DayAhead));

        (await ModeSpansAsync()).Should().BeEquivalentTo(first);
        first.Should().BeEquivalentTo(
        [
            ("Automatic", T.AddHours(-1), (DateTime?)T),
            ("Manual", DayAhead, (DateTime?)null),
        ]);
    }

    private static string Iso(DateTime at) => at.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>A status sent at <paramref name="sentAt"/>, whose pump reads <paramref name="clock"/> when one is given.</summary>
    private static DeviceStatus Suspension(string id, DateTime sentAt, bool suspended, DateTime? clock = null) => new()
    {
        Id = id,
        Mills = new DateTimeOffset(sentAt).ToUnixTimeMilliseconds(),
        Device = "openaps://test",
        Pump = new PumpStatus
        {
            Manufacturer = "Insulet",
            Model = "Omnipod",
            Status = new PumpStatusDetails { Suspended = suspended },
            Clock = clock is { } at ? Iso(at) : null,
        },
    };

    private static DeviceStatus Mode(string id, DateTime sentAt, PumpModeState mode, DateTime? clock = null) => new()
    {
        Id = id,
        Mills = new DateTimeOffset(sentAt).ToUnixTimeMilliseconds(),
        Device = "carelink-connector",
        Pump = new PumpStatus
        {
            Manufacturer = "Medtronic",
            Model = "780G",
            PumpMode = mode.ToString(),
            Clock = clock is { } at ? Iso(at) : null,
        },
    };

    /// <summary>Decomposes <paramref name="status"/> as it arrives at <paramref name="arrivedAt"/>.</summary>
    private async Task<V4Models.DecompositionResult> FoldAsync(DateTime arrivedAt, DeviceStatus status)
    {
        _clock.AdjustTime(new DateTimeOffset(arrivedAt));
        return await _decomposer.DecomposeBatchAsync([status], null, WriteOrigin.Live);
    }

    /// <summary>Stores a pump snapshot arriving at <paramref name="arrivedAt"/> without folding it.</summary>
    private async Task StoreSnapshotAsync(
        DateTime arrivedAt, string id, DateTime timestamp, DateTime? clock, PumpModeState? mode = null)
    {
        _clock.AdjustTime(new DateTimeOffset(arrivedAt));
        await _pumps.BulkUpsertAsync(
        [
            new V4Models.PumpSnapshot
            {
                LegacyId = id,
                Timestamp = timestamp,
                Device = "openaps://test",
                Suspended = mode is null ? true : null,
                PumpMode = mode?.ToString(),
                Clock = clock is { } at ? Iso(at) : null,
            },
        ], WriteOrigin.Live);
    }

    /// <summary>Stores an open pump-mode span arriving at <paramref name="arrivedAt"/>, as another source writes one.</summary>
    private async Task StoreSpanAsync(DateTime arrivedAt, string id, PumpModeState state, DateTime start)
    {
        _clock.AdjustTime(new DateTimeOffset(arrivedAt));
        await _spans.UpsertStateSpanAsync(new StateSpan
        {
            Category = StateSpanCategory.PumpMode,
            State = state.ToString(),
            StartTimestamp = start,
            Source = "other",
            OriginalId = id,
        });
    }

    private async Task<List<(DateTime Start, DateTime? End)>> SpansAsync(PumpModeState state) =>
        [.. (await _spans.GetStateSpansAsync(category: StateSpanCategory.PumpMode, descending: false))
            .Where(s => s.State == state.ToString())
            .Select(s => (s.StartTimestamp, s.EndTimestamp))];

    private async Task<List<(string? State, DateTime Start, DateTime? End)>> ModeSpansAsync() =>
        [.. (await _spans.GetStateSpansAsync(category: StateSpanCategory.PumpMode, descending: false))
            .Where(s => s.State != PumpModeState.Suspended.ToString())
            .Select(s => (s.State, s.StartTimestamp, s.EndTimestamp))];
}

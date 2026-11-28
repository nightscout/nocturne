using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// Pump Suspend and Pump Resume treatments through the treatment fold and the real store, whose clock
/// stamps when each event and span arrived (<see cref="PumpModeSpanClock"/>). With a suspension dated a
/// day ahead, in either order, the real suspension runs from its suspend to its resume and the ahead one
/// ends at its own start.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentDecomposerPumpSuspensionStoreTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Ahead = Now.AddDays(1);

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly TestNocturneDbContextFactory _factory;
    private readonly AuditContext _audit = new();
    private readonly IDuplicateLinker _dedup = Mock.Of<IDuplicateLinker>();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Now));
    private readonly Mock<ILogger<TreatmentDecomposer>> _logger = new();
    private readonly StateSpanRepository _spans;
    private readonly TreatmentDecomposer _decomposer;

    public TreatmentDecomposerPumpSuspensionStoreTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TenantId).WithClock(_clock);
        _context = _db.CreateContext();
        _factory = new TestNocturneDbContextFactory(_context);
        _spans = new StateSpanRepository(_factory, _dedup, _audit, NullLogger<StateSpanRepository>.Instance);
        _decomposer = TreatmentDecomposerDoubles.Stored(
            _factory, _dedup, _audit,
            stateSpans: new StateSpanService(_spans, NullLogger<StateSpanService>.Instance),
            time: _clock,
            logger: _logger.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TheAheadSuspensionFirst_ThenARealSuspensionAndItsResume()
    {
        await DecomposeAsync(PumpEvent("ahead", "Pump Suspend", Ahead));
        await DecomposeAsync(PumpEvent("suspend", "Pump Suspend", Now));
        _clock.SetUtcNow(new DateTimeOffset(Now.AddHours(1)));
        await DecomposeAsync(PumpEvent("resume", "Pump Resume", Now.AddHours(1)));

        (await SuspensionsAsync()).Should().BeEquivalentTo(
        [
            ("pump-suspended-tx:ahead", Ahead, (DateTime?)Ahead),
            ("pump-suspended-tx:suspend", Now, (DateTime?)Now.AddHours(1)),
        ]);
    }

    /// <summary>
    /// The ahead suspension arrives while the real one is open, and the store ends the real one at its
    /// start; the resume ends the ahead one at its own start, which releases the real one to end at the
    /// resume.
    /// </summary>
    [Fact]
    public async Task ARealSuspension_ThenTheAheadSuspension_ThenTheResume()
    {
        await DecomposeAsync(PumpEvent("suspend", "Pump Suspend", Now));
        await DecomposeAsync(PumpEvent("ahead", "Pump Suspend", Ahead));
        _clock.SetUtcNow(new DateTimeOffset(Now.AddHours(1)));
        await DecomposeAsync(PumpEvent("resume", "Pump Resume", Now.AddHours(1)));

        (await SuspensionsAsync()).Should().BeEquivalentTo(
        [
            ("pump-suspended-tx:ahead", Ahead, (DateTime?)Ahead),
            ("pump-suspended-tx:suspend", Now, (DateTime?)Now.AddHours(1)),
        ]);
    }

    /// <summary>A suspension dated a day ahead does not stop a suspend and its resume in one batch.</summary>
    [Fact]
    public async Task TheAheadSuspensionFirst_ThenARealSuspensionAndItsResumeInOneBatch()
    {
        await DecomposeAsync(PumpEvent("ahead", "Pump Suspend", Ahead));
        _clock.SetUtcNow(new DateTimeOffset(Now.AddHours(1)));
        await DecomposeAsync(PumpEvent("suspend", "Pump Suspend", Now), PumpEvent("resume", "Pump Resume", Now.AddHours(1)));

        (await SuspensionsAsync()).Should().BeEquivalentTo(
        [
            ("pump-suspended-tx:ahead", Ahead, (DateTime?)Ahead),
            ("pump-suspended-tx:suspend", Now, (DateTime?)Now.AddHours(1)),
        ]);
    }

    /// <summary>
    /// A resume dated a day ahead ends the real suspension when it arrived, not at a future time, and a
    /// later resume finds it ended.
    /// </summary>
    [Fact]
    public async Task AResumeDatedADayAhead_EndsTheRealSuspensionWhenItArrived()
    {
        await DecomposeAsync(PumpEvent("suspend", "Pump Suspend", Now));
        _clock.SetUtcNow(new DateTimeOffset(Now.AddHours(1)));
        await DecomposeAsync(PumpEvent("resume-ahead", "Pump Resume", Ahead));

        (await SuspensionsAsync()).Should().Equal([("pump-suspended-tx:suspend", Now, (DateTime?)Now.AddHours(1))]);

        _clock.SetUtcNow(new DateTimeOffset(Now.AddHours(2)));
        await DecomposeAsync(PumpEvent("resume", "Pump Resume", Now.AddHours(2)));

        (await SuspensionsAsync()).Should().Equal([("pump-suspended-tx:suspend", Now, (DateTime?)Now.AddHours(1))]);
    }

    /// <summary>
    /// A resume dated a day ahead that arrived below the millisecond ends the suspension at its arrival as
    /// the store keeps it. The suspend and the resume uploaded again together find that end and change nothing.
    /// </summary>
    [Fact]
    public async Task AResumeDatedADayAheadArrivingBelowTheMillisecond_EndsTheSuspensionOnceAcrossARefold()
    {
        var arrived = Now.AddHours(1).AddTicks(1_234_567);
        await DecomposeAsync(PumpEvent("suspend", "Pump Suspend", Now));
        _clock.SetUtcNow(new DateTimeOffset(arrived));
        await DecomposeAsync(PumpEvent("resume-ahead", "Pump Resume", Ahead));
        var ended = (DateTime?)RecordTime.AtMillisecond(arrived);

        (await SuspensionsAsync()).Should().Equal([("pump-suspended-tx:suspend", Now, ended)]);

        _clock.SetUtcNow(new DateTimeOffset(Now.AddHours(2)));
        await DecomposeAsync(PumpEvent("suspend", "Pump Suspend", Now), PumpEvent("resume-ahead", "Pump Resume", Ahead));

        (await SuspensionsAsync()).Should().Equal([("pump-suspended-tx:suspend", Now, ended)]);
        _logger.Verify(
            l => l.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    /// <summary>
    /// A suspend and a resume dated a day ahead, arriving below the millisecond in one batch, end the
    /// suspension at the resume's arrival. The resume uploaded again on its own finds that end, a day before
    /// its own time, and writes nothing.
    /// </summary>
    [Fact]
    public async Task AResumeDatedADayAhead_UploadedAgainOnItsOwn_WritesNothingAndWarnsOfNothing()
    {
        var arrived = Now.AddHours(1).AddTicks(1_234_567);
        _clock.SetUtcNow(new DateTimeOffset(arrived));
        await DecomposeAsync(PumpEvent("suspend", "Pump Suspend", Now), PumpEvent("resume-ahead", "Pump Resume", Ahead));
        var ended = (DateTime?)RecordTime.AtMillisecond(arrived);

        (await SuspensionsAsync()).Should().Equal([("pump-suspended-tx:suspend", Now, ended)]);

        _clock.SetUtcNow(new DateTimeOffset(Now.AddHours(2)));
        var again = await DecomposeAsync(PumpEvent("resume-ahead", "Pump Resume", Ahead));

        (await SuspensionsAsync()).Should().Equal([("pump-suspended-tx:suspend", Now, ended)]);
        WrittenSpans(again).Should().BeEmpty();
        VerifyWarnings(Times.Never());
    }

    /// <summary>
    /// A resume dated a day ahead ends the suspension at its arrival. Sent again under a new legacy id with
    /// the same sync key, it updates its stored device event, which keeps that arrival, so it finds the end
    /// and writes nothing.
    /// </summary>
    [Fact]
    public async Task AResumeDatedADayAhead_SentAgainUnderANewIdWithItsSyncKey_WritesNothingAndWarnsOfNothing()
    {
        await DecomposeAsync(PumpEvent("suspend", "Pump Suspend", Now));
        _clock.SetUtcNow(new DateTimeOffset(Now.AddHours(1)));
        await DecomposeAsync(Synced(PumpEvent("resume-ahead", "Pump Resume", Ahead)));

        (await SuspensionsAsync()).Should().Equal([("pump-suspended-tx:suspend", Now, (DateTime?)Now.AddHours(1))]);

        _clock.SetUtcNow(new DateTimeOffset(Now.AddHours(2)));
        var again = await DecomposeAsync(Synced(PumpEvent("resume-ahead-again", "Pump Resume", Ahead)));

        (await SuspensionsAsync()).Should().Equal([("pump-suspended-tx:suspend", Now, (DateTime?)Now.AddHours(1))]);
        WrittenSpans(again).Should().BeEmpty();
        VerifyWarnings(Times.Never());

        static Treatment Synced(Treatment treatment)
        {
            treatment.DataSource = "pump-uploader";
            treatment.SyncIdentifier = "resume-sync";
            return treatment;
        }
    }

    /// <summary>A resume on the server's clock uploaded again on its own writes nothing and warns of nothing.</summary>
    [Fact]
    public async Task AResume_UploadedAgainOnItsOwn_WritesNothingAndWarnsOfNothing()
    {
        await DecomposeAsync(PumpEvent("suspend", "Pump Suspend", Now));
        _clock.SetUtcNow(new DateTimeOffset(Now.AddHours(1)));
        await DecomposeAsync(PumpEvent("resume", "Pump Resume", Now.AddHours(1)));

        _clock.SetUtcNow(new DateTimeOffset(Now.AddHours(2)));
        var again = await DecomposeAsync(PumpEvent("resume", "Pump Resume", Now.AddHours(1)));

        (await SuspensionsAsync()).Should().Equal([("pump-suspended-tx:suspend", Now, (DateTime?)Now.AddHours(1))]);
        WrittenSpans(again).Should().BeEmpty();
        VerifyWarnings(Times.Never());
    }

    /// <summary>A resume dated a day ahead with no suspension stored ends nothing and is warned of once.</summary>
    [Fact]
    public async Task AResumeDatedADayAhead_WithNoSuspensionStored_IsWarnedOfOnce()
    {
        var result = await DecomposeAsync(PumpEvent("resume-ahead", "Pump Resume", Ahead));

        (await SuspensionsAsync()).Should().BeEmpty();
        WrittenSpans(result).Should().BeEmpty();
        VerifyWarnings(Times.Once());
    }

    /// <summary>
    /// A resume dated a day ahead ends a suspension, then the pump suspends again on the server's clock. The
    /// resume uploaded again on its own does not end the later suspension, and warns of nothing.
    /// </summary>
    [Fact]
    public async Task AResumeDatedADayAhead_UploadedAgainOnItsOwn_LeavesALaterSuspensionOpen()
    {
        await DecomposeAsync(PumpEvent("suspend", "Pump Suspend", Now));
        _clock.SetUtcNow(new DateTimeOffset(Now.AddHours(1)));
        await DecomposeAsync(PumpEvent("resume-ahead", "Pump Resume", Ahead));
        _clock.SetUtcNow(new DateTimeOffset(Now.AddMinutes(90)));
        await DecomposeAsync(PumpEvent("later", "Pump Suspend", Now.AddMinutes(90)));

        _clock.SetUtcNow(new DateTimeOffset(Now.AddHours(2)));
        var again = await DecomposeAsync(PumpEvent("resume-ahead", "Pump Resume", Ahead));

        (await SuspensionsAsync()).Should().BeEquivalentTo(
        [
            ("pump-suspended-tx:suspend", Now, (DateTime?)Now.AddHours(1)),
            ("pump-suspended-tx:later", Now.AddMinutes(90), (DateTime?)null),
        ]);
        WrittenSpans(again).Should().BeEmpty();
        VerifyWarnings(Times.Never());
    }

    /// <summary>
    /// A pump clock set an hour forward during a suspension dates the resume ahead. The suspension ends when
    /// the resume arrived.
    /// </summary>
    [Fact]
    public async Task APumpClockSetForwardDuringASuspension_EndsItWhenTheResumeArrived()
    {
        await DecomposeAsync(PumpEvent("suspend", "Pump Suspend", Now));
        _clock.SetUtcNow(new DateTimeOffset(Now.AddMinutes(90)));
        await DecomposeAsync(PumpEvent("resume", "Pump Resume", Now.AddMinutes(150)));

        (await SuspensionsAsync()).Should().Equal([("pump-suspended-tx:suspend", Now, (DateTime?)Now.AddMinutes(90))]);
    }

    /// <summary>
    /// A suspension dated a day ahead is stored, then a pump 10 minutes fast suspends and resumes. The real
    /// suspend is ahead of the bound too, but not of the day-ahead span, so it ends that span at its own
    /// start, and the resume ends the real suspension at its own time.
    /// </summary>
    [Fact]
    public async Task AFastPump_ASuspensionDatedADayAhead_EndsAtItsOwnStart_AndTheRealSuspensionOpensAndResumes()
    {
        var fast = TimeSpan.FromMinutes(10);
        await DecomposeAsync(PumpEvent("ahead", "Pump Suspend", Ahead));
        _clock.SetUtcNow(new DateTimeOffset(Now.AddMinutes(30)));
        await DecomposeAsync(PumpEvent("suspend", "Pump Suspend", Now.AddMinutes(30) + fast));
        _clock.SetUtcNow(new DateTimeOffset(Now.AddMinutes(120)));
        await DecomposeAsync(PumpEvent("resume", "Pump Resume", Now.AddMinutes(120) + fast));

        (await SuspensionsAsync()).Should().BeEquivalentTo(
        [
            ("pump-suspended-tx:ahead", Ahead, (DateTime?)Ahead),
            ("pump-suspended-tx:suspend", Now.AddMinutes(30) + fast, (DateTime?)(Now.AddMinutes(120) + fast)),
        ]);
    }

    /// <summary>
    /// A resume uploaded three hours late, dated before a suspension that is open, has a lead far below the
    /// suspension's. That does not put the suspension ahead of it: the suspension was not dated beyond the
    /// bound as it arrived, so it belongs to a later transition and stays open. A treatment gives no order
    /// (<see cref="PumpModeSpanClock"/>), so the resume of a pump clock set back during the suspension, dated
    /// before it too, leaves it open the same way.
    /// </summary>
    [Fact]
    public async Task AResumeUploadedLate_OrOfAPumpClockSetBack_DoesNotEndALaterSuspension()
    {
        await DecomposeAsync(PumpEvent("suspend", "Pump Suspend", Now));
        _clock.SetUtcNow(new DateTimeOffset(Now.AddHours(2)));
        await DecomposeAsync(PumpEvent("resume-late", "Pump Resume", Now.AddHours(-1)));

        (await SuspensionsAsync()).Should().Equal([("pump-suspended-tx:suspend", Now, (DateTime?)null)]);
    }

    /// <summary>
    /// A pump 6 or 60 minutes fast suspends, and an older resume of that pump, dated before the suspension
    /// arrived, arrives three hours late. It is an older transition, so it leaves the suspension open, and the
    /// real resume ends it.
    /// </summary>
    [Theory]
    [InlineData(6, 30)]
    [InlineData(60, 90)]
    public async Task AFastPump_AnOlderResumeArrivingLate_LeavesTheSuspensionForItsOwnResume(int minutesFast, int minutesOld)
    {
        var fast = TimeSpan.FromMinutes(minutesFast);
        await DecomposeAsync(PumpEvent("suspend", "Pump Suspend", Now + fast));
        _clock.SetUtcNow(new DateTimeOffset(Now.AddHours(3)));
        await DecomposeAsync(PumpEvent("resume-late", "Pump Resume", Now.AddMinutes(-minutesOld) + fast));

        (await SuspensionsAsync()).Should().Equal([("pump-suspended-tx:suspend", Now + fast, (DateTime?)null)]);

        _clock.SetUtcNow(new DateTimeOffset(Now.AddHours(4)));
        await DecomposeAsync(PumpEvent("resume", "Pump Resume", Now.AddHours(4) + fast));

        (await SuspensionsAsync()).Should().Equal(
            [("pump-suspended-tx:suspend", Now + fast, (DateTime?)(Now.AddHours(4) + fast))]);
    }

    /// <summary>
    /// A pump an hour fast suspends, and a resume of that pump half an hour older arrives three hours late.
    /// Dated after the suspension arrived, it cannot be told from a lagged newest resume, since a treatment
    /// gives no order (<see cref="PumpModeSpanClock"/>), so it overtakes the suspension, which ends at its own
    /// start.
    /// </summary>
    [Fact]
    public async Task AFastPump_AResumeArrivingLate_DatedAfterTheSuspensionArrived_WithNoOrder_EndsItAtItsOwnStart()
    {
        var fast = TimeSpan.FromHours(1);
        await DecomposeAsync(PumpEvent("suspend", "Pump Suspend", Now + fast));
        _clock.SetUtcNow(new DateTimeOffset(Now.AddHours(3)));
        await DecomposeAsync(PumpEvent("resume-late", "Pump Resume", Now.AddMinutes(-30) + fast));

        (await SuspensionsAsync()).Should().Equal([("pump-suspended-tx:suspend", Now + fast, (DateTime?)(Now + fast))]);
    }

    /// <summary>
    /// A source that sends each event some minutes after its time, as a polling connector does, suspends and
    /// resumes after a suspension dated a day ahead was stored. Each transition arrived late but is newer than
    /// the ahead span, so the suspend ends that span at its own start and its resume ends the real suspension.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    public async Task ALaggedSource_ASuspensionDatedADayAhead_EndsAtItsOwnStart_AndTheRealSuspensionResumes(int minutesLate)
    {
        var lag = TimeSpan.FromMinutes(minutesLate);
        await DecomposeAsync(PumpEvent("ahead", "Pump Suspend", Ahead));
        _clock.SetUtcNow(new DateTimeOffset(Now.AddHours(1) + lag));
        await DecomposeAsync(PumpEvent("suspend", "Pump Suspend", Now.AddHours(1)));
        _clock.SetUtcNow(new DateTimeOffset(Now.AddHours(2) + lag));
        await DecomposeAsync(PumpEvent("resume", "Pump Resume", Now.AddHours(2)));

        (await SuspensionsAsync()).Should().BeEquivalentTo(
        [
            ("pump-suspended-tx:ahead", Ahead, (DateTime?)Ahead),
            ("pump-suspended-tx:suspend", Now.AddHours(1), (DateTime?)Now.AddHours(2)),
        ]);
    }

    /// <summary>
    /// A resume dated ahead that arrived before a real suspension came before it: decomposed again later,
    /// it does not end that suspension.
    /// </summary>
    [Fact]
    public async Task AResumeDatedAheadThatArrivedFirst_DoesNotEndALaterSuspensionWhenDecomposedAgain()
    {
        await DecomposeAsync(PumpEvent("resume-ahead", "Pump Resume", Ahead));
        _clock.SetUtcNow(new DateTimeOffset(Now.AddMinutes(1)));
        await DecomposeAsync(PumpEvent("suspend", "Pump Suspend", Now.AddMinutes(1)));
        _clock.SetUtcNow(new DateTimeOffset(Now.AddHours(2)));
        await DecomposeAsync(PumpEvent("resume-ahead", "Pump Resume", Ahead));

        (await SuspensionsAsync()).Should().Equal([("pump-suspended-tx:suspend", Now.AddMinutes(1), (DateTime?)null)]);
    }

    /// <summary>
    /// A suspension that starts after a resume but within the clock bound belongs to a later
    /// transition, which the older resume does not end.
    /// </summary>
    [Fact]
    public async Task AnOutOfOrderResume_DoesNotEndALaterSuspensionWithinTheBound()
    {
        var resumedAt = Now.AddHours(1);
        await DecomposeAsync(PumpEvent("suspend", "Pump Suspend", Now));
        _clock.SetUtcNow(new DateTimeOffset(resumedAt));
        await DecomposeAsync(PumpEvent("later", "Pump Suspend", resumedAt.AddMinutes(2)));
        await DecomposeAsync(PumpEvent("resume", "Pump Resume", resumedAt));

        (await SuspensionsAsync()).Should().ContainSingle(s => s.Id == "pump-suspended-tx:later")
            .Which.End.Should().BeNull();
    }

    /// <summary>
    /// A pump whose clock runs fast (ten minutes, or an hour when it missed a daylight saving change)
    /// dates its suspend and its resume equally ahead, so its resume ends the suspension it opened.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(60)]
    public async Task AFastPump_ItsResumeEndsTheSuspensionItOpened(int minutesFast)
    {
        var fast = TimeSpan.FromMinutes(minutesFast);
        await DecomposeAsync(PumpEvent("suspend", "Pump Suspend", Now + fast));
        _clock.SetUtcNow(new DateTimeOffset(Now.AddMinutes(90)));
        await DecomposeAsync(PumpEvent("resume", "Pump Resume", Now.AddMinutes(90) + fast));

        (await SuspensionsAsync()).Should().Equal(
            [("pump-suspended-tx:suspend", Now + fast, (DateTime?)(Now.AddMinutes(90) + fast))]);
    }

    /// <summary>
    /// A suspend and a resume at one instant in one batch leave a suspension that ends where it starts,
    /// which ends nothing in the store. The earlier open suspension its insert would have ended is ended
    /// by the resume.
    /// </summary>
    [Fact]
    public async Task ASuspendAndResumeAtOneInstant_EndTheEarlierOpenSuspensionAtThatInstant()
    {
        _clock.AdjustTime(new DateTimeOffset(Now.AddHours(-1)));
        await DecomposeAsync(PumpEvent("earlier", "Pump Suspend", Now.AddHours(-1)));
        _clock.AdjustTime(new DateTimeOffset(Now));
        await DecomposeAsync(PumpEvent("suspend", "Pump Suspend", Now), PumpEvent("resume", "Pump Resume", Now));

        (await SuspensionsAsync()).Should().BeEquivalentTo(
        [
            ("pump-suspended-tx:earlier", Now.AddHours(-1), (DateTime?)Now),
            ("pump-suspended-tx:suspend", Now, (DateTime?)Now),
        ]);
    }

    private static Treatment PumpEvent(string id, string eventType, DateTime at) =>
        new() { Id = id, EventType = eventType, Mills = new DateTimeOffset(at).ToUnixTimeMilliseconds() };

    private Task<TreatmentDecomposition> DecomposeAsync(params Treatment[] treatments) =>
        _decomposer.DecomposeBatchAsync([.. treatments], WriteOrigin.Live, default, new TreatmentDecomposition());

    private static IEnumerable<StateSpan> WrittenSpans(TreatmentDecomposition result) =>
        result.CreatedRecords.Concat(result.UpdatedRecords).Concat(result.UnchangedRecords).OfType<StateSpan>();

    private void VerifyWarnings(Times times) =>
        _logger.Verify(
            l => l.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);

    private async Task<List<(string? Id, DateTime Start, DateTime? End)>> SuspensionsAsync() =>
        [.. (await _spans.GetStateSpansAsync(category: StateSpanCategory.PumpMode))
            .Where(s => s.State == PumpModeState.Suspended.ToString())
            .Select(s => (s.OriginalId, s.StartTimestamp, s.EndTimestamp))];
}

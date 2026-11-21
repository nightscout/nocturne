using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// One notification per tenant and fact, raised beyond the clock tolerance and archived only once
/// the fact is observed at or before now (docs/alerts/engine-semantics.md §5.4).
/// </summary>
public class ClockAnomalyNoticeTests
{
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly DateTime T0 = new(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);
    private const string Owner = "owner-subject";

    private readonly Mock<IInAppNotificationService> _notifications = new();
    private readonly Mock<ICanonicalGlucoseService> _canonical = new();
    private readonly List<InAppNotificationDto> _active = [];
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(T0, TimeSpan.Zero));
    private readonly ClockAnomalyNotice _notice;
    private int _created;
    private int _archived;
    private int _reads;

    public ClockAnomalyNoticeTests()
    {
        _notifications
            .Setup(n => n.GetActiveNotificationsAsync(Owner, It.IsAny<CancellationToken>()))
            .Callback(() => _reads++)
            .ReturnsAsync(() => _active.ToList());
        _notifications
            .Setup(n => n.CreateNotificationUnlessActiveAsync(
                Owner, ClockAnomalyNotice.NotificationType, It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<NotificationCategory?>(), ClockAnomalyNotice.NotificationSource, It.IsAny<string?>(),
                It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string type, string _, string sourceId, NotificationCategory? _, string? _,
                string? _, Dictionary<string, object>? metadata, CancellationToken _) =>
            {
                if (_active.Any(n => n.SourceId == sourceId))
                    return false;
                _created++;
                _active.Add(new InAppNotificationDto { Type = type, SourceId = sourceId, Metadata = metadata });
                return true;
            });
        _notifications
            .Setup(n => n.ArchiveBySourceAsync(Owner, ClockAnomalyNotice.NotificationType, It.IsAny<string>(),
                NotificationArchiveReason.ConditionMet, It.IsAny<CancellationToken>()))
            .Callback((string _, string _, string sourceId, NotificationArchiveReason _, CancellationToken _) =>
            {
                _archived++;
                _active.RemoveAll(n => n.SourceId == sourceId);
            })
            .ReturnsAsync(true);
        _notifications
            .Setup(n => n.ReplaceMetadataBySourceAsync(Owner, ClockAnomalyNotice.NotificationType, It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, string sourceId, Dictionary<string, object> metadata, CancellationToken _) =>
            {
                foreach (var notification in _active.Where(n => n.SourceId == sourceId))
                    notification.Metadata = metadata;
            })
            .ReturnsAsync(true);

        var owner = new Mock<ITenantOwnerResolver>();
        owner.Setup(o => o.GetOwnerSubjectIdAsync(Tenant, It.IsAny<CancellationToken>())).ReturnsAsync(Owner);

        _notice = new ClockAnomalyNotice(
            _notifications.Object, owner.Object, Mock.Of<ITenantAccessor>(a => a.TenantId == Tenant),
            _canonical.Object, new ClockAnomalyNoticeLog(_clock), _clock, NullLogger<ClockAnomalyNotice>.Instance);
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private static AlertReadingSelection NewestAt(DateTime at)
    {
        var reading = new SensorGlucose { Timestamp = at, Mgdl = 120 };
        return new AlertReadingSelection(reading, reading);
    }

    private static SensorContext Context(DateTime? siteChange = null, DateTime? loopCycle = null) => new()
    {
        LatestValue = null,
        LatestTimestamp = null,
        TrendRate = null,
        LastReadingAt = null,
        LastSiteChangeAt = siteChange,
        LastApsCycleAt = loopCycle,
    };

    private IEnumerable<string?> Standing => _active.Select(n => n.SourceId);

    [Fact]
    public async Task A_stream_six_minutes_fast_raises_once_and_keeps_it()
    {
        for (var reading = 0; reading < 12; reading++)
        {
            await _notice.ReportReadingAsync(NewestAt(Now.AddMinutes(6)), Now, CancellationToken.None);
            for (var minute = 0; minute < 5; minute++)
            {
                _clock.Advance(TimeSpan.FromMinutes(1));
                await _notice.ReportReadingAsync(NewestAt(Now.AddMinutes(5 - minute)), Now, CancellationToken.None);
            }
        }

        _created.Should().Be(1);
        _archived.Should().Be(0);
        Standing.Should().Equal(ClockAnomalyNotice.ReadingFact);
    }

    [Fact]
    public async Task It_clears_once_no_stored_reading_is_ahead()
    {
        await _notice.ReportReadingAsync(NewestAt(Now.AddHours(1)), Now, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(57));
        await _notice.ReportReadingAsync(NewestAt(T0.AddHours(1)), Now, CancellationToken.None);
        Standing.Should().Equal(new[] { ClockAnomalyNotice.ReadingFact }, "the reading is still ahead, inside the tolerance");

        _clock.Advance(TimeSpan.FromMinutes(3));
        await _notice.ReportReadingAsync(NewestAt(T0.AddHours(1)), Now, CancellationToken.None);

        Standing.Should().BeEmpty();
        _created.Should().Be(1);
    }

    [Fact]
    public async Task A_sweep_refresh_clears_it_when_time_passes_with_no_new_reading()
    {
        await _notice.ReportReadingAsync(NewestAt(Now.AddMinutes(30)), Now, CancellationToken.None);
        _canonical.Setup(c => c.GetLatestAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SensorGlucose { Timestamp = T0.AddMinutes(30), Mgdl = 120 });
        _clock.Advance(TimeSpan.FromMinutes(31));

        await _notice.RefreshAsync(Now, CancellationToken.None);

        Standing.Should().BeEmpty();
    }

    [Fact]
    public async Task Every_rule_reading_one_fact_shares_one_notification()
    {
        IReadOnlyCollection<ClockAnomaly> ahead = [new("last_site_change_at", TimeSpan.FromHours(1))];

        await _notice.ReportAsync(Context(T0.AddHours(1)), ahead, CancellationToken.None);
        await _notice.ReportAsync(Context(T0.AddHours(1)), ahead, CancellationToken.None);

        Standing.Should().Equal("last_site_change_at");
        _created.Should().Be(1);
    }

    [Fact]
    public async Task An_engine_fact_clears_when_the_context_shows_it_no_longer_ahead()
    {
        IReadOnlyCollection<ClockAnomaly> ahead = [new("last_site_change_at", TimeSpan.FromMinutes(6))];
        await _notice.ReportAsync(Context(T0.AddMinutes(6)), ahead, CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(3));
        await _notice.ReportAsync(Context(T0.AddMinutes(6)), [], CancellationToken.None);
        Standing.Should().Equal("last_site_change_at");

        _clock.Advance(TimeSpan.FromMinutes(3));
        await _notice.ReportAsync(Context(T0.AddMinutes(6)), [], CancellationToken.None);
        Standing.Should().BeEmpty();
    }

    [Fact]
    public async Task An_engine_fact_no_pass_observes_again_clears_once_its_instant_has_passed()
    {
        IReadOnlyCollection<ClockAnomaly> ahead = [new("last_site_change_at", TimeSpan.FromHours(1))];
        await _notice.ReportAsync(Context(T0.AddHours(1)), ahead, CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(30));
        await _notice.ReportAsync(Context(), [], CancellationToken.None);
        await _notice.RefreshAsync(Now, CancellationToken.None);
        Standing.Should().Equal("last_site_change_at");

        _clock.Advance(TimeSpan.FromMinutes(30));
        await _notice.RefreshAsync(Now, CancellationToken.None);
        Standing.Should().BeEmpty("the instant it reported is no longer ahead");
    }

    /// <summary>
    /// A loop phone 6 minutes fast: every cycle is reported ahead, and the reading-only reconciles in
    /// between (after each reading, and every sweep) cannot observe the cycle.
    /// </summary>
    [Fact]
    public async Task A_loop_clock_six_minutes_fast_raises_once_and_keeps_it()
    {
        for (var cycle = 0; cycle < 12; cycle++)
        {
            var cycleAt = Now.AddMinutes(6);
            await _notice.ReportAsync(Context(loopCycle: cycleAt),
                [new ClockAnomaly("last_aps_cycle_at", cycleAt - Now)], CancellationToken.None);
            for (var minute = 0; minute < 5; minute++)
            {
                _clock.Advance(TimeSpan.FromMinutes(1));
                await _notice.RefreshAsync(Now, CancellationToken.None);
                await _notice.ReportReadingAsync(NewestAt(Now), Now, CancellationToken.None);
                await _notice.ReportAsync(Context(loopCycle: cycleAt), [], CancellationToken.None);
            }
        }

        _created.Should().Be(1);
        _archived.Should().Be(0);
        Standing.Should().Equal("last_aps_cycle_at");
    }

    [Fact]
    public async Task A_fact_no_longer_reported_clears_after_its_latest_instant()
    {
        await _notice.ReportAsync(Context(), [new ClockAnomaly("last_aps_cycle_at", TimeSpan.FromMinutes(6))], CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await _notice.ReportAsync(Context(), [new ClockAnomaly("last_aps_cycle_at", TimeSpan.FromMinutes(6))], CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(5));
        await _notice.RefreshAsync(Now, CancellationToken.None);
        Standing.Should().Equal(new[] { "last_aps_cycle_at" }, "the latest instant, 11 minutes in, is still ahead");

        _clock.Advance(TimeSpan.FromMinutes(1));
        await _notice.RefreshAsync(Now, CancellationToken.None);
        Standing.Should().BeEmpty();
        _created.Should().Be(1);
    }

    [Fact]
    public async Task An_instant_from_the_host_clock_is_logged_and_not_notified()
    {
        IReadOnlyCollection<ClockAnomaly> ahead =
        [
            new("enabled_at", TimeSpan.FromMinutes(10)),
            new("sustained", TimeSpan.FromMinutes(10)),
            new("hysteresis_started_at", TimeSpan.FromMinutes(10)),
            new("active_alerts", TimeSpan.FromMinutes(10)),
        ];

        await _notice.ReportAsync(Context(), ahead, CancellationToken.None);

        _created.Should().Be(0);
    }

    [Fact]
    public async Task With_nothing_ahead_and_nothing_standing_it_reads_notifications_once_per_freshness_window()
    {
        await _notice.ReportReadingAsync(NewestAt(Now), Now, CancellationToken.None);
        await _notice.ReportReadingAsync(NewestAt(Now), Now, CancellationToken.None);
        await _notice.ReportAsync(Context(T0), [], CancellationToken.None);
        _reads.Should().Be(1);

        _clock.Advance(ClockAnomalyNoticeLog.Freshness);
        await _notice.ReportReadingAsync(NewestAt(Now), Now, CancellationToken.None);
        _reads.Should().Be(2);
    }
}

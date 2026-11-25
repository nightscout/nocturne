using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
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
            new ClockAnomalyNoticeLog(_clock), _clock, NullLogger<ClockAnomalyNotice>.Instance);
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>What <see cref="AlertSweepJob"/>'s clock-anomaly pass does with the run's reading selection.</summary>
    private async Task SweepRefreshAsync(AlertReadingSelection? selection = null)
    {
        if (_notice.NeedsReadingReport(Now))
            await _notice.ReportReadingAsync(selection ?? new AlertReadingSelection(null, null), Now, CancellationToken.None);
    }

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
        _clock.Advance(TimeSpan.FromMinutes(31));

        await SweepRefreshAsync(NewestAt(T0.AddMinutes(30)));

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
        await SweepRefreshAsync();
        Standing.Should().Equal("last_site_change_at");

        _clock.Advance(TimeSpan.FromMinutes(30));
        await SweepRefreshAsync();
        Standing.Should().BeEmpty("the instant it reported is no longer ahead");
    }

    private const string RetiredPumpFact = "active_pump_state";
    private const string PumpModesFact = "active_pump_modes";

    private void StandingUnderTheRetiredPumpFact(DateTime aheadUntil) =>
        _active.Add(new InAppNotificationDto
        {
            Type = ClockAnomalyNotice.NotificationType,
            SourceId = RetiredPumpFact,
            Metadata = new Dictionary<string, object>
            {
                [ClockAnomalyNotice.AheadUntilKey] = aheadUntil.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            },
        });

    private SensorContext PumpModesStartingAt(DateTime? newest) =>
        Context() with { PumpModesRead = new SpansRead(Now, newest) };

    [Fact]
    public async Task A_notice_raised_under_the_retired_pump_fact_stands_for_the_same_span_and_is_not_raised_again()
    {
        StandingUnderTheRetiredPumpFact(T0.AddMinutes(30));
        IReadOnlyCollection<ClockAnomaly> ahead = [new(PumpModesFact, TimeSpan.FromMinutes(30))];

        await _notice.ReportAsync(PumpModesStartingAt(T0.AddMinutes(30)), ahead, CancellationToken.None);
        await _notice.ReportAsync(PumpModesStartingAt(T0.AddMinutes(30)), ahead, CancellationToken.None);

        _created.Should().Be(0);
        Standing.Should().Equal(RetiredPumpFact);
    }

    [Fact]
    public async Task A_notice_under_the_retired_pump_fact_is_archived_when_its_span_is_no_longer_ahead()
    {
        StandingUnderTheRetiredPumpFact(T0.AddMinutes(30));
        _clock.Advance(TimeSpan.FromMinutes(31));

        await _notice.ReportAsync(PumpModesStartingAt(T0.AddMinutes(30)), [], CancellationToken.None);

        Standing.Should().BeEmpty();
        _created.Should().Be(0);
    }

    [Fact]
    public async Task A_notice_under_the_retired_pump_fact_no_pass_observes_again_clears_once_its_instant_has_passed()
    {
        StandingUnderTheRetiredPumpFact(T0.AddMinutes(30));
        await _notice.ReportAsync(Context(), [], CancellationToken.None);
        Standing.Should().Equal(new[] { RetiredPumpFact }, "its instant is still ahead");

        _clock.Advance(TimeSpan.FromMinutes(31));
        await _notice.ReportAsync(Context(), [], CancellationToken.None);

        Standing.Should().BeEmpty();
    }

    [Fact]
    public async Task The_pump_modes_are_observed_at_their_newest_start()
    {
        IReadOnlyCollection<ClockAnomaly> ahead = [new(PumpModesFact, TimeSpan.FromMinutes(30))];
        var context = PumpModesStartingAt(T0.AddMinutes(30));
        await _notice.ReportAsync(context, ahead, CancellationToken.None);
        Standing.Should().Equal(PumpModesFact);

        _clock.Advance(TimeSpan.FromMinutes(31));
        await _notice.ReportAsync(context, [], CancellationToken.None);

        Standing.Should().BeEmpty("the newest start is no longer ahead");
    }

    [Fact]
    public async Task A_pump_mode_span_beyond_the_tolerance_raises_the_notice_with_no_engine_anomaly()
    {
        await _notice.ReportAsync(PumpModesStartingAt(Now.AddMinutes(20)), [], CancellationToken.None);

        Standing.Should().Equal(PumpModesFact);
        _created.Should().Be(1);
        _active.Single().Type.Should().Be(ClockAnomalyNotice.NotificationType);
    }

    [Fact]
    public async Task A_pump_mode_span_within_the_tolerance_raises_nothing()
    {
        await _notice.ReportAsync(PumpModesStartingAt(Now.AddMinutes(3)), [], CancellationToken.None);

        Standing.Should().BeEmpty();
        _created.Should().Be(0);
    }

    [Fact]
    public async Task The_pump_mode_notice_is_raised_once_across_passes_and_kept_while_the_span_is_ahead()
    {
        for (var pass = 0; pass < 6; pass++)
        {
            await _notice.ReportAsync(PumpModesStartingAt(T0.AddMinutes(20)), [], CancellationToken.None);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        _created.Should().Be(1);
        _archived.Should().Be(0);
        Standing.Should().Equal(PumpModesFact);
    }

    [Fact]
    public async Task The_pump_mode_notice_is_kept_once_the_span_comes_within_the_tolerance_and_archived_when_time_passes_it()
    {
        await _notice.ReportAsync(PumpModesStartingAt(T0.AddMinutes(20)), [], CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(17));
        await _notice.ReportAsync(PumpModesStartingAt(T0.AddMinutes(20)), [], CancellationToken.None);
        Standing.Should().Equal(new[] { PumpModesFact }, "the span is still ahead, inside the tolerance");

        _clock.Advance(TimeSpan.FromMinutes(3));
        await _notice.ReportAsync(PumpModesStartingAt(T0.AddMinutes(20)), [], CancellationToken.None);
        Standing.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-60)]
    public async Task The_pump_mode_notice_is_archived_at_once_when_the_span_is_removed_or_ended(int? newestStartMinutes)
    {
        await _notice.ReportAsync(PumpModesStartingAt(T0.AddMinutes(20)), [], CancellationToken.None);
        Standing.Should().Equal(PumpModesFact);

        await _notice.ReportAsync(
            PumpModesStartingAt(newestStartMinutes is { } m ? Now.AddMinutes(m) : null), [], CancellationToken.None);

        Standing.Should().BeEmpty("no stored open pump-mode span is ahead of now, though the span's start is 20 minutes away");
    }

    [Theory]
    [InlineData(10, 20, 20)]
    [InlineData(30, 20, 30)]
    public async Task The_larger_of_the_engines_and_the_hosts_distance_ahead_is_the_one_stored(
        int engineMinutes, int hostMinutes, int storedMinutes)
    {
        IReadOnlyCollection<ClockAnomaly> engine = [new(PumpModesFact, TimeSpan.FromMinutes(engineMinutes))];

        await _notice.ReportAsync(PumpModesStartingAt(Now.AddMinutes(hostMinutes)), engine, CancellationToken.None);

        _active.Single().Metadata![ClockAnomalyNotice.AheadUntilKey].Should()
            .Be(Now.AddMinutes(storedMinutes).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_span_the_enrichment_judged_beyond_the_bound_is_reported_though_the_clock_has_moved_within_it()
    {
        var enrichedAt = Now;
        var start = enrichedAt.AddMinutes(5).AddSeconds(5);
        _clock.Advance(TimeSpan.FromSeconds(10));

        await _notice.ReportAsync(
            Context() with { PumpModesRead = new SpansRead(enrichedAt, start) }, [], CancellationToken.None);

        Standing.Should().Equal(PumpModesFact);
    }

    [Fact]
    public async Task A_span_the_enrichment_sent_is_not_reported_though_the_clock_is_behind_the_bound_it_used()
    {
        var enrichedAt = Now.AddMinutes(1);

        await _notice.ReportAsync(
            Context() with { PumpModesRead = new SpansRead(enrichedAt, Now.AddMinutes(6)) },
            [], CancellationToken.None);

        Standing.Should().BeEmpty();
    }

    [Fact]
    public async Task A_pass_that_reads_no_pump_modes_leaves_the_pump_mode_notice_to_its_instant()
    {
        await _notice.ReportAsync(PumpModesStartingAt(T0.AddMinutes(20)), [], CancellationToken.None);

        await _notice.ReportAsync(Context(), [], CancellationToken.None);
        Standing.Should().Equal(new[] { PumpModesFact }, "the pass did not read pump modes, so it cannot say the span is gone");

        _clock.Advance(TimeSpan.FromMinutes(21));
        await _notice.ReportAsync(Context(), [], CancellationToken.None);
        Standing.Should().BeEmpty();
    }

    [Fact]
    public async Task A_later_pass_reporting_a_span_further_ahead_moves_the_notice_forward()
    {
        await _notice.ReportAsync(PumpModesStartingAt(T0.AddMinutes(20)), [], CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _notice.ReportAsync(PumpModesStartingAt(T0.AddMinutes(45)), [], CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(30));
        await _notice.ReportAsync(Context(), [], CancellationToken.None);

        Standing.Should().Equal(new[] { PumpModesFact }, "the notice stands for the latest start, 45 minutes in");
        _created.Should().Be(1);
    }

    private const string OverrideFact = "active_override";

    private SensorContext OverridesStartingAt(DateTime? newest) =>
        Context() with { OverridesRead = new SpansRead(Now, newest) };

    [Fact]
    public async Task A_device_status_override_beyond_the_tolerance_raises_its_own_notice()
    {
        await _notice.ReportAsync(OverridesStartingAt(Now.AddMinutes(20)), [], CancellationToken.None);

        Standing.Should().Equal(OverrideFact);
        _active.Single().Metadata![ClockAnomalyNotice.AheadUntilKey].Should()
            .Be(Now.AddMinutes(20).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(null)]
    public async Task An_override_read_with_nothing_beyond_the_tolerance_raises_nothing(int? newestStartMinutes)
    {
        await _notice.ReportAsync(
            OverridesStartingAt(newestStartMinutes is { } m ? Now.AddMinutes(m) : null), [], CancellationToken.None);

        Standing.Should().BeEmpty();
        _created.Should().Be(0);
    }

    [Fact]
    public async Task An_override_and_a_pump_mode_span_ahead_raise_one_notice_each()
    {
        await _notice.ReportAsync(
            Context() with { OverridesRead = new SpansRead(Now, Now.AddMinutes(20)), PumpModesRead = new SpansRead(Now, Now.AddMinutes(30)) },
            [], CancellationToken.None);

        Standing.Should().BeEquivalentTo([OverrideFact, PumpModesFact]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-60)]
    public async Task The_override_notice_is_archived_at_once_when_no_device_status_override_is_ahead(int? newestStartMinutes)
    {
        await _notice.ReportAsync(OverridesStartingAt(T0.AddMinutes(20)), [], CancellationToken.None);
        Standing.Should().Equal(OverrideFact);

        await _notice.ReportAsync(
            OverridesStartingAt(newestStartMinutes is { } m ? Now.AddMinutes(m) : null), [], CancellationToken.None);

        Standing.Should().BeEmpty("no device-status override is ahead of now, though the one reported starts 20 minutes away");
    }

    [Fact]
    public async Task The_override_notice_is_kept_once_the_override_comes_within_the_tolerance_and_archived_when_time_passes_it()
    {
        await _notice.ReportAsync(OverridesStartingAt(T0.AddMinutes(20)), [], CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(17));
        await _notice.ReportAsync(OverridesStartingAt(T0.AddMinutes(20)), [], CancellationToken.None);
        Standing.Should().Equal(new[] { OverrideFact }, "the override is still ahead, inside the tolerance");

        _clock.Advance(TimeSpan.FromMinutes(3));
        await _notice.ReportAsync(OverridesStartingAt(T0.AddMinutes(20)), [], CancellationToken.None);
        Standing.Should().BeEmpty();
    }

    [Fact]
    public async Task A_pass_that_no_longer_reads_overrides_leaves_the_override_notice_to_its_instant()
    {
        await _notice.ReportAsync(OverridesStartingAt(T0.AddMinutes(20)), [], CancellationToken.None);

        await _notice.ReportAsync(Context(), [], CancellationToken.None);
        Standing.Should().Equal(new[] { OverrideFact }, "the pass did not read overrides, so it cannot say the override is gone");

        _clock.Advance(TimeSpan.FromMinutes(21));
        await _notice.ReportAsync(Context(), [], CancellationToken.None);
        Standing.Should().BeEmpty();
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
                await SweepRefreshAsync();
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
        await SweepRefreshAsync();
        Standing.Should().Equal(new[] { "last_aps_cycle_at" }, "the latest instant, 11 minutes in, is still ahead");

        _clock.Advance(TimeSpan.FromMinutes(1));
        await SweepRefreshAsync();
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

    [Fact]
    public void A_tenant_whose_notices_are_not_known_needs_the_reading_reported()
    {
        _notice.NeedsReadingReport(Now).Should().BeTrue();
    }

    [Fact]
    public async Task With_nothing_ahead_and_nothing_standing_the_reading_needs_no_report_within_the_freshness_window()
    {
        await _notice.ReportReadingAsync(NewestAt(Now), Now, CancellationToken.None);
        _notice.NeedsReadingReport(Now).Should().BeFalse();

        _clock.Advance(ClockAnomalyNoticeLog.Freshness);
        _notice.NeedsReadingReport(Now).Should().BeTrue("the last read of the notices is no longer trusted");
    }

    [Fact]
    public async Task A_standing_reading_notice_needs_the_reading_reported_so_time_can_archive_it()
    {
        await _notice.ReportReadingAsync(NewestAt(Now.AddHours(1)), Now, CancellationToken.None);

        _notice.NeedsReadingReport(Now).Should().BeTrue();
    }

    [Fact]
    public async Task A_standing_engine_fact_needs_the_reading_reported_only_once_its_instant_has_passed()
    {
        IReadOnlyCollection<ClockAnomaly> ahead = [new("last_site_change_at", TimeSpan.FromHours(1))];
        await _notice.ReportAsync(Context(T0.AddHours(1)), ahead, CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(58));
        await _notice.ReportAsync(Context(), [], CancellationToken.None);
        _notice.NeedsReadingReport(Now).Should().BeFalse();

        _clock.Advance(TimeSpan.FromMinutes(2));
        _notice.NeedsReadingReport(Now).Should().BeTrue();
    }
}

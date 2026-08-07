using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// Pins the two properties the paged glucose fetch has to hold: the replay output is identical
/// however the window's readings are split into pages, and a window wider than the configured
/// maximum is a client error rather than a truncated answer.
/// </summary>
[Trait("Category", "Unit")]
public class AlertReplayServicePagingTests
{
    private readonly Mock<IAlertRepository> _alertRepository = new();
    private readonly Mock<ISensorGlucoseRepository> _glucoseRepository = new();
    private readonly Mock<ITenantAccessor> _tenantAccessor = new();
    private readonly Mock<IIobCalculator> _iobCalculator = new();
    private readonly Mock<ICobCalculator> _cobCalculator = new();
    private readonly Mock<ITreatmentService> _treatmentService = new();
    private readonly Mock<IBolusRepository> _bolusRepository = new();
    private readonly Mock<ICarbIntakeRepository> _carbIntakeRepository = new();
    private readonly Mock<IDeviceEventRepository> _deviceEventRepository = new();
    private readonly Mock<IPumpSnapshotRepository> _pumpSnapshotRepository = new();
    private readonly Mock<IApsSnapshotRepository> _apsSnapshotRepository = new();
    private readonly Mock<ITempBasalRepository> _tempBasalRepository = new();
    private readonly Mock<IUploaderSnapshotRepository> _uploaderSnapshotRepository = new();
    private readonly Mock<IStateSpanService> _stateSpanService = new();
    private readonly Mock<Nocturne.API.Services.Devices.IReservoirEstimationService> _reservoirEstimation = new();

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly DateTime _dayStart = new(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Registered devices backing the canonical selector; tests append to this.</summary>
    private readonly List<PatientDevice> _devices = [];

    /// <summary>
    /// Row count of every batch handed to the canonical selector. The selector is the only place
    /// the service materialises readings, so the largest entry here is the resident reading set.
    /// </summary>
    private readonly List<int> _selectedBatchSizes = [];

    public AlertReplayServicePagingTests()
    {
        _tenantAccessor.Setup(t => t.TenantId).Returns(_tenantId);
        _alertRepository
            .Setup(r => r.GetDndWindowsAsOfAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DndWindowSnapshot>());
        _alertRepository
            .Setup(r => r.GetUnexpiredDndWindowsAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DndWindowSnapshot>());
    }

    private AlertReplayService CreateSut(int pageSize, TimeSpan? maxWindow = null)
    {
        var enricherDeps = new SensorContextEnricherDependencies(
            _iobCalculator.Object,
            _cobCalculator.Object,
            _treatmentService.Object,
            _bolusRepository.Object,
            _carbIntakeRepository.Object,
            _deviceEventRepository.Object,
            _pumpSnapshotRepository.Object,
            _apsSnapshotRepository.Object,
            _tempBasalRepository.Object,
            _uploaderSnapshotRepository.Object,
            _stateSpanService.Object,
            _alertRepository.Object,
            new Mock<ITargetRangeScheduleRepository>().Object,
            new Mock<Nocturne.Core.Contracts.Profiles.Resolvers.IActiveProfileResolver>().Object,
            new Mock<Nocturne.Core.Contracts.Profiles.Resolvers.ITherapySettingsResolver>().Object,
            new Mock<Nocturne.Core.Contracts.Sleep.ISleepService>().Object,
            new Mock<Nocturne.Infrastructure.Data.Abstractions.ITrackerRepository>().Object,
            _reservoirEstimation.Object,
            Options.Create(new AlertEvaluationOptions()));
        var enricher = new SensorContextEnricher(
            enricherDeps,
            new ServiceCollection().BuildServiceProvider(),
            TimeProvider.System,
            NullLogger<SensorContextEnricher>.Instance);

        var options = new AlertEvaluationOptions { ReplayGlucosePageSize = pageSize };
        if (maxWindow is { } max) options.MaxReplayWindow = max;

        return new AlertReplayService(
            _alertRepository.Object,
            _glucoseRepository.Object,
            CanonicalSelector(),
            enricher,
            _tenantAccessor.Object,
            Options.Create(options),
            NullLogger<AlertReplayService>.Instance);
    }

    /// <summary>
    /// The real bucket-winner selection over <see cref="_devices"/> — a pass-through double would
    /// hide the bucket-split hazard the page-boundary carry exists to prevent.
    /// </summary>
    private ICanonicalGlucoseService CanonicalSelector()
    {
        var mock = new Mock<ICanonicalGlucoseService>();
        mock.Setup(s => s.SelectAsync(It.IsAny<IReadOnlyList<SensorGlucose>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SensorGlucose> readings, CancellationToken _) =>
            {
                _selectedBatchSizes.Add(readings.Count);
                return CanonicalGlucoseStream.Select(readings, _devices);
            });
        return mock.Object;
    }

    /// <summary>
    /// Backs the glucose repository with an in-memory series that honours the keyset cursor and
    /// the row limit exactly as <c>SensorGlucoseRepository</c> does — ordering by
    /// <c>(Timestamp, Id)</c> ascending and seeking strictly past the cursor.
    /// </summary>
    private void SetupSeries(IReadOnlyList<SensorGlucose> all)
    {
        _glucoseRepository
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, false,
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns((DateTime? from, DateTime? to, string? _, string? _,
                int limit, int _, bool _, bool _,
                DateTime? afterTimestamp, Guid? afterId, CancellationToken _, Guid? _) =>
            {
                IEnumerable<SensorGlucose> rows = all
                    .Where(r => (from is null || r.Timestamp >= from.Value)
                             && (to is null || r.Timestamp <= to.Value))
                    .OrderBy(r => r.Timestamp)
                    .ThenBy(r => r.Id);

                if (afterTimestamp is { } ts && afterId is { } id)
                {
                    rows = rows.Where(r => r.Timestamp > ts
                        || (r.Timestamp == ts && r.Id.CompareTo(id) > 0));
                }

                return Task.FromResult<IEnumerable<SensorGlucose>>(rows.Take(limit).ToList());
            });
    }

    private static long _idCounter;

    private static SensorGlucose Reading(DateTime at, double mgdl, Guid? patientDeviceId = null,
        string dataSource = "test", string device = "cgm")
        => new()
        {
            // A counter-derived id, not CreateVersion7: v7 ids minted within the same millisecond
            // order by their random bits, so the keyset tiebreaker would be non-deterministic for
            // same-timestamp readings.
            Id = new Guid((uint)Interlocked.Increment(ref _idCounter), 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0),
            Timestamp = at,
            Mgdl = mgdl,
            PatientDeviceId = patientDeviceId,
            DataSource = dataSource,
            Device = device,
        };

    private static AlertRuleSnapshot ThresholdBelow(Guid id, decimal value) =>
        new(id, Guid.NewGuid(), $"below-{value}", AlertConditionType.Threshold,
            $$"""{"direction":"below","value":{{value}}}""", AlertRuleSeverity.Warning, "{}", 0,
            AutoResolveEnabled: false, AutoResolveParams: null);

    private static (
        IReadOnlyList<(DateTime At, Guid RuleId, AlertReplayEventKind Kind)> Events,
        IReadOnlyList<(Guid RuleId, int LeafId, long AtMs, bool Value)> Leaves,
        IReadOnlyList<(string Key, long AtMs, decimal Value)> Facts) Shape(AlertReplayResult result)
        => (
            result.Events.Select(e => (e.At, e.RuleId, e.Kind)).ToList(),
            result.LeafTransitionsByRule
                .OrderBy(kvp => kvp.Key)
                .SelectMany(kvp => kvp.Value.SelectMany(l =>
                    l.Points.Select(p => (kvp.Key, l.LeafId, p.AtMs, p.Value))))
                .ToList(),
            result.FactTimelines
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .SelectMany(kvp => kvp.Value.Select(p => (kvp.Key, p.AtMs, p.Value)))
                .ToList());

    /// <summary>
    /// A sparse series: the tick loop must keep the last reading it saw as "current" for the many
    /// ticks that follow it, including across a page boundary. A cursor rebuilt per page would see
    /// no reading at-or-before those ticks and clear the excursion, producing extra fired events.
    /// </summary>
    [Fact]
    public async Task PageSize_DoesNotChangeReplayOutput_SparseSeries()
    {
        var ruleId = Guid.NewGuid();
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { ThresholdBelow(ruleId, 70m) });

        // Readings two hours apart so most ticks carry a reading fetched on an earlier page.
        SetupSeries(
        [
            Reading(_dayStart.AddHours(2), 60),
            Reading(_dayStart.AddHours(4), 60),
            Reading(_dayStart.AddHours(6), 120),
            Reading(_dayStart.AddHours(8), 55),
        ]);

        var reference = Shape(await CreateSut(pageSize: 1000)
            .ReplayAsync(new DateOnly(2026, 4, 28), "UTC", null, null, CancellationToken.None));

        // A single fired event that survives across the whole low run is the property at risk.
        reference.Events.Should().HaveCount(2);

        foreach (var pageSize in new[] { 1, 2, 3, 5 })
        {
            var paged = Shape(await CreateSut(pageSize)
                .ReplayAsync(new DateOnly(2026, 4, 28), "UTC", null, null, CancellationToken.None));

            paged.Events.Should().Equal(reference.Events, "page size {0} must not change events", pageSize);
            paged.Leaves.Should().Equal(reference.Leaves, "page size {0} must not change the leaf log", pageSize);
            paged.Facts.Should().Equal(reference.Facts, "page size {0} must not change fact timelines", pageSize);
        }
    }

    /// <summary>
    /// Two concurrent CGMs writing into the same aligned 5-minute bucket. Canonical selection gives
    /// the whole bucket to the ranked device, so the 60 mg/dL reading from the unregistered stream
    /// never reaches the evaluator. Selecting page-locally without holding back the split bucket
    /// lets both halves win their own page and the low surfaces — hence the boundary carry.
    /// </summary>
    /// <remarks>
    /// Page sizes start at the contested bucket's row count: a bucket wider than a page is flushed
    /// per page by design (see <see cref="AlertEvaluationOptions.ReplayGlucosePageSize"/>), so
    /// whole-bucket resolution is only guaranteed once a bucket fits in a page.
    /// </remarks>
    [Fact]
    public async Task PageSize_DoesNotChangeReplayOutput_WhenABucketStraddlesAPageBoundary()
    {
        var rankedDevice = new PatientDevice
        {
            Id = Guid.CreateVersion7(),
            DeviceCategory = DeviceCategory.CGM,
            Rank = 0,
            CreatedAt = _dayStart,
        };
        _devices.Add(rankedDevice);

        var ruleId = Guid.NewGuid();
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { ThresholdBelow(ruleId, 70m) });

        // Bucket [04:00, 04:05): ranked device at 04:00 (200), a second unregistered stream at
        // 04:01 (60). Two readings ahead of it offset the paging so a page boundary lands inside
        // the contested bucket at page size 3; two behind it keep the window from ending there.
        SetupSeries(
        [
            Reading(_dayStart.AddHours(3).AddMinutes(50), 200, patientDeviceId: rankedDevice.Id),
            Reading(_dayStart.AddHours(3).AddMinutes(55), 200, patientDeviceId: rankedDevice.Id),
            Reading(_dayStart.AddHours(4), 200, patientDeviceId: rankedDevice.Id),
            Reading(_dayStart.AddHours(4).AddMinutes(1), 60, dataSource: "other", device: "cgm-b"),
            Reading(_dayStart.AddHours(4).AddMinutes(10), 200, patientDeviceId: rankedDevice.Id),
            Reading(_dayStart.AddHours(4).AddMinutes(20), 200, patientDeviceId: rankedDevice.Id),
        ]);

        var reference = Shape(await CreateSut(pageSize: 1000)
            .ReplayAsync(new DateOnly(2026, 4, 28), "UTC", null, null, CancellationToken.None));

        // The loser stream's low is invisible to the whole-window selection, so nothing fires —
        // anchored by a non-empty fact timeline so an empty event list can't come from a
        // reference run that never ticked.
        reference.Events.Should().BeEmpty();
        reference.Facts.Should().NotBeEmpty();

        // The contested bucket holds two readings, so page sizes from two upward. Page size 3
        // splits it across a boundary, which is the case the hold-back exists for.
        foreach (var pageSize in new[] { 2, 3, 4, 5 })
        {
            var paged = Shape(await CreateSut(pageSize)
                .ReplayAsync(new DateOnly(2026, 4, 28), "UTC", null, null, CancellationToken.None));

            paged.Events.Should().Equal(reference.Events, "page size {0} must not change events", pageSize);
            paged.Leaves.Should().Equal(reference.Leaves, "page size {0} must not change the leaf log", pageSize);
            paged.Facts.Should().Equal(reference.Facts, "page size {0} must not change fact timelines", pageSize);
        }
    }

    /// <summary>
    /// The fetch must page rather than ask for the whole window in one go — an unpaged read is
    /// what let a multi-month replay allocate the entire series.
    /// </summary>
    [Fact]
    public async Task GlucoseFetch_RequestsBoundedPages()
    {
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { ThresholdBelow(Guid.NewGuid(), 70m) });
        SetupSeries([Reading(_dayStart.AddHours(4), 60)]);

        await CreateSut(pageSize: 250)
            .ReplayAsync(new DateOnly(2026, 4, 28), "UTC", null, null, CancellationToken.None);

        _glucoseRepository.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
            250, It.IsAny<int>(), false, false,
            It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _glucoseRepository.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
            int.MaxValue, It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A bucket holding more rows than one page must not accumulate in the hold-back buffer —
    /// the resident set stays within a page. Every reading here is one stream, so per-page
    /// selection is identity and the flushed result still matches the single-page reference.
    /// </summary>
    [Fact]
    public async Task ABucketWiderThanAPage_IsFlushedRatherThanHeldBack()
    {
        var ruleId = Guid.NewGuid();
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { ThresholdBelow(ruleId, 70m) });

        // Six readings inside the single bucket [04:00, 04:05), then one well clear of it.
        SetupSeries(
        [
            .. Enumerable.Range(0, 6).Select(i => Reading(_dayStart.AddHours(4).AddSeconds(i * 30), 60)),
            Reading(_dayStart.AddHours(5), 120),
        ]);

        var reference = Shape(await CreateSut(pageSize: 1000)
            .ReplayAsync(new DateOnly(2026, 4, 28), "UTC", null, null, CancellationToken.None));
        reference.Events.Should().ContainSingle();

        _selectedBatchSizes.Clear();
        const int pageSize = 2;
        var paged = Shape(await CreateSut(pageSize)
            .ReplayAsync(new DateOnly(2026, 4, 28), "UTC", null, null, CancellationToken.None));

        paged.Events.Should().Equal(reference.Events);
        paged.Leaves.Should().Equal(reference.Leaves);
        paged.Facts.Should().Equal(reference.Facts);
        _selectedBatchSizes.Should().NotBeEmpty();
        _selectedBatchSizes.Max().Should().BeLessThanOrEqualTo(pageSize,
            "an over-wide bucket must be flushed per page, not accumulated");
    }

    /// <summary>
    /// A fetch that ignores the keyset cursor hands back the same full page forever, and the
    /// paging loop must fail rather than spin.
    /// </summary>
    /// <remarks>
    /// The replay runs under an expiring token so a regression that removes the guard surfaces as
    /// the wrong exception type within seconds. <c>[Fact(Timeout)]</c> is not enough on its own:
    /// xUnit reports the timeout but does not abandon the runaway loop, which wedges the run.
    /// </remarks>
    [Fact(Timeout = 30000)]
    public async Task AFetchThatIgnoresTheKeysetCursor_Fails_RatherThanSpinning()
    {
        using var spinGuard = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        _alertRepository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { ThresholdBelow(Guid.NewGuid(), 70m) });

        // Deliberately cursor-blind: always the same two rows, which is a full page at pageSize 2.
        var stuck = new[]
        {
            Reading(_dayStart.AddHours(4), 60),
            Reading(_dayStart.AddHours(4).AddMinutes(10), 60),
        };
        _glucoseRepository
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), It.IsAny<int>(), false, false,
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stuck);

        await CreateSut(pageSize: 2)
            .Invoking(s => s.ReplayAsync(new DateOnly(2026, 4, 28), "UTC", null, null, spinGuard.Token))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*did not advance past the keyset cursor*");
    }

    /// <summary>
    /// The shipped default has to clear the widest window a first-party caller asks for — a
    /// DST-stretched calendar day — while still rejecting something far beyond it.
    /// </summary>
    [Fact]
    public async Task TheDefaultWindowCap_ClearsACalendarDayAndRejectsFarBeyondIt()
    {
        var shipped = new AlertEvaluationOptions().MaxReplayWindow;
        shipped.Should().BeGreaterThanOrEqualTo(TimeSpan.FromHours(25));
        // The ceiling matters as much as the floor: each replayed tick costs a round of as-of
        // enrichment queries, so a default far past the documented ~2x need re-opens the
        // unbounded-work hole the cap exists to close.
        shipped.Should().BeLessThanOrEqualTo(TimeSpan.FromHours(72));

        _alertRepository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlertRuleSnapshot>());
        SetupSeries([]);
        var sut = CreateSut(pageSize: 1000, maxWindow: shipped);

        // A DST-stretched calendar day must still run under the shipped default.
        var accepted = await sut.ReplayAsync(
            null, null, _dayStart, _dayStart.AddHours(25), CancellationToken.None);
        accepted.WindowEnd.Should().Be(_dayStart.AddHours(25));

        await sut.Invoking(s => s.ReplayAsync(
                null, null, _dayStart, _dayStart + shipped + TimeSpan.FromMinutes(5), CancellationToken.None))
            .Should().ThrowAsync<ReplayWindowTooLargeException>();
    }

    [Fact]
    public async Task WindowExactlyAtTheMaximum_IsReplayed()
    {
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(_tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { ThresholdBelow(Guid.NewGuid(), 70m) });
        SetupSeries([Reading(_dayStart.AddMinutes(30), 60)]);

        var max = TimeSpan.FromHours(2);
        var sut = CreateSut(pageSize: 1000, maxWindow: max);

        var result = await sut.ReplayAsync(null, null, _dayStart, _dayStart + max, CancellationToken.None);

        result.WindowStart.Should().Be(_dayStart);
        result.WindowEnd.Should().Be(_dayStart + max);
        result.Events.Should().ContainSingle();
    }

    [Fact]
    public async Task WindowOneTickOverTheMaximum_IsRejected()
    {
        var max = TimeSpan.FromHours(2);
        var sut = CreateSut(pageSize: 1000, maxWindow: max);

        var act = () => sut.ReplayAsync(
            null, null, _dayStart, _dayStart + max + TimeSpan.FromMinutes(5), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<ReplayWindowTooLargeException>();
        thrown.Which.Maximum.Should().Be(max);
        thrown.Which.Requested.Should().Be(max + TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// The cap is input validation, so it must reject before any tenant data is touched — an
    /// over-wide window is the same error whatever the rule set or reading count is.
    /// </summary>
    [Fact]
    public async Task OverWideWindow_IsRejectedWithoutFetchingRulesOrReadings()
    {
        var sut = CreateSut(pageSize: 1000, maxWindow: TimeSpan.FromHours(2));

        await sut.Invoking(s => s.ReplayAsync(
                null, null, _dayStart, _dayStart.AddDays(90), CancellationToken.None))
            .Should().ThrowAsync<ReplayWindowTooLargeException>();

        _alertRepository.Verify(
            r => r.GetEnabledRulesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _glucoseRepository.Verify(
            r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DryRunReplay_IsSubjectToTheSameWindowCap()
    {
        var sut = CreateSut(pageSize: 1000, maxWindow: TimeSpan.FromHours(2));
        var ruleOverride = new ReplayRuleOverride(
            Id: null, Name: "draft", ConditionType: AlertConditionType.Threshold,
            ConditionParams: """{"direction":"below","value":70}""",
            Severity: AlertRuleSeverity.Warning, AllowThroughDnd: false,
            AutoResolveEnabled: false, AutoResolveParams: null);

        await sut.Invoking(s => s.ReplayDryRunAsync(
                null, null, _dayStart, _dayStart.AddDays(30), ruleOverride, CancellationToken.None))
            .Should().ThrowAsync<ReplayWindowTooLargeException>();
    }

    [Fact]
    public async Task Controller_MapsAnOverWideWindowToBadRequest()
    {
        var replayService = new Mock<IAlertReplayService>();
        replayService
            .Setup(s => s.ReplayAsync(It.IsAny<DateOnly?>(), It.IsAny<string?>(),
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ReplayWindowTooLargeException(TimeSpan.FromDays(90), TimeSpan.FromDays(7)));
        var controller = new AlertReplayController(replayService.Object);

        var response = await controller.Replay(
            new AlertReplayRequest(null, null, _dayStart, _dayStart.AddDays(90)), CancellationToken.None);

        response.Result.Should().BeOfType<BadRequestObjectResult>();
    }
}

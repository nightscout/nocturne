using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Clinical.Treatments;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Services.Alerts;
using Nocturne.Alerts.Corpus;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// Pins the properties the paged glucose fetch has to hold: the replay output is identical
/// however the window's readings are split into pages, it reads the canonical stream, and a window
/// wider than the configured maximum is a client error rather than a truncated answer.
/// </summary>
[Trait("Category", "Unit")]
public class AlertReplayServicePagingTests
{
    private readonly Mock<IAlertRepository> _alertRepository = new();
    private readonly Mock<IDndWindowRepository> _dndWindows = new();
    private readonly Mock<ITenantAlertSettingsRepository> _alertSettings = new();
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

    public AlertReplayServicePagingTests()
    {
        _tenantAccessor.Setup(t => t.TenantId).Returns(_tenantId);
        SetupHidden([]);
        _dndWindows
            .Setup(r => r.GetReceivedByAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DndWindowSnapshot>());
        _dndWindows
            .Setup(r => r.GetUnexpiredSnapshotsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DndWindowSnapshot>());
    }

    private AlertReplayService CreateSut(int pageSize, TimeSpan? maxWindow = null)
    {
        var enricherDeps = new SensorContextEnricherDependencies(
            new IobCobReader(_iobCalculator.Object, _cobCalculator.Object, _bolusRepository.Object,
                _tempBasalRepository.Object, _carbIntakeRepository.Object, Mock.Of<ITherapyTimelineResolver>(), Mock.Of<IStateSpanRepository>(), Mock.Of<IApsSnapshotRepository>(), Mock.Of<IPumpSnapshotRepository>(),
                TestTenantCache.Create(partition: null), NullLogger<IobCobReader>.Instance),
            _iobCalculator.Object,
            _cobCalculator.Object,
            _treatmentService.Object,
            _deviceEventRepository.Object,
            _pumpSnapshotRepository.Object,
            _apsSnapshotRepository.Object,
            _tempBasalRepository.Object,
            _uploaderSnapshotRepository.Object,
            _stateSpanService.Object,
            _alertRepository.Object,
            _alertSettings.Object,
            _dndWindows.Object,
            new Mock<ITargetRangeScheduleRepository>().Object,
            new Mock<Nocturne.Core.Contracts.Profiles.Resolvers.IActiveProfileResolver>().Object,
            new Mock<Nocturne.Core.Contracts.Profiles.Resolvers.IProfileActivationResolver>().Object,
            new Mock<Nocturne.Core.Contracts.Profiles.Resolvers.ITherapySettingsResolver>().Object,
            new Mock<Nocturne.Core.Contracts.Sleep.ISleepService>().Object,
            new Mock<Nocturne.Core.Contracts.Repositories.ITrackerRepository>().Object,
            _reservoirEstimation.Object,
            Mock.Of<Nocturne.Core.Contracts.V4.Repositories.ISensorStateReportRepository>(),
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
            _dndWindows.Object,
            _glucoseRepository.Object,
            enricher,
            _tenantAccessor.Object,
            Options.Create(options),
            new RustAlertReplayEngine(new AlertEngineErrors(new Nocturne.API.Tests.Services.BackgroundServices.TestMeterFactory(), TimeProvider.System)),
            new ImportedHistory(_glucoseRepository.Object, new Mock<Nocturne.Core.Contracts.Auth.IOAuthGrantRepository>().Object, TestTenantCache.Create()),
            TimeProvider.System);
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
                It.IsAny<int>(), It.IsAny<int>(), false,
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), null, It.IsAny<GlucoseStreams>()))
            .Returns((DateTime? from, DateTime? to, string? _, string? _,
                int limit, int _, bool _,
                DateTime? afterTimestamp, Guid? afterId, CancellationToken _, Guid? _, GlucoseStreams _) =>
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

    /// <summary>
    /// Backs the hidden-row read with an in-memory series, keyset-paged as
    /// <see cref="SetupSeries"/> pages the canonical one.
    /// </summary>
    private void SetupHidden(IReadOnlyList<(SensorGlucose Reading, Guid? DuplicateOf)> all)
    {
        _glucoseRepository
            .Setup(r => r.GetHiddenAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns((DateTime from, DateTime to, int limit, DateTime? afterTimestamp, Guid? afterId, CancellationToken _) =>
            {
                var rows = all
                    .Where(r => r.Reading.Timestamp >= from && r.Reading.Timestamp <= to)
                    .Where(r => afterTimestamp is not { } ts || afterId is not { } id
                        || r.Reading.Timestamp > ts || (r.Reading.Timestamp == ts && r.Reading.Id.CompareTo(id) > 0))
                    .OrderBy(r => r.Reading.Timestamp)
                    .ThenBy(r => r.Reading.Id)
                    .Take(limit)
                    .ToList();
                return Task.FromResult<IReadOnlyList<(SensorGlucose Reading, Guid? DuplicateOf)>>(rows);
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
            CreatedAt = at,
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
    [NativeFact]
    public async Task PageSize_DoesNotChangeReplayOutput_SparseSeries()
    {
        var ruleId = Guid.NewGuid();
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
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
    /// The fetch must page rather than ask for the whole window in one go — an unpaged read is
    /// what let a multi-month replay allocate the entire series — and read only the canonical
    /// stream, so a losing CGM's low never reaches the evaluator.
    /// </summary>
    [NativeFact]
    public async Task GlucoseFetch_RequestsBoundedPagesOfTheCanonicalStream()
    {
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { ThresholdBelow(Guid.NewGuid(), 70m) });
        SetupSeries([Reading(_dayStart.AddHours(4), 60)]);

        await CreateSut(pageSize: 250)
            .ReplayAsync(new DateOnly(2026, 4, 28), "UTC", null, null, CancellationToken.None);

        _glucoseRepository.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
            250, It.IsAny<int>(), false,
            It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), null, GlucoseStreams.Canonical), Times.AtLeastOnce);
        _glucoseRepository.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), null,
            It.Is<GlucoseStreams>(streams => streams != GlucoseStreams.Canonical)), Times.Never);
        _glucoseRepository.Verify(r => r.GetAsync(
            It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
            int.MaxValue, It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), null, It.IsAny<GlucoseStreams>()), Times.Never);
    }

    /// <summary>
    /// A fetch that ignores the keyset cursor hands back the same full page forever, and the
    /// paging loop must fail rather than spin.
    /// </summary>
    /// <remarks>
    /// The replay runs under an expiring token so a regression that removes the guard surfaces as
    /// the wrong exception type within the hang guard. <c>[Fact(Timeout)]</c> is not enough on its own:
    /// xUnit reports the timeout but does not abandon the runaway loop, which wedges the run.
    /// </remarks>
    [Fact(Timeout = 120000)]
    public async Task AFetchThatIgnoresTheKeysetCursor_Fails_RatherThanSpinning()
    {
        using var spinGuard = new CancellationTokenSource(Eventually.DefaultPatience);

        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
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
                It.IsAny<int>(), It.IsAny<int>(), false,
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), null, It.IsAny<GlucoseStreams>()))
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
    [NativeFact]
    public async Task TheDefaultWindowCap_ClearsACalendarDayAndRejectsFarBeyondIt()
    {
        var shipped = new AlertEvaluationOptions().MaxReplayWindow;
        shipped.Should().BeGreaterThanOrEqualTo(TimeSpan.FromHours(25));
        // The ceiling matters as much as the floor: each replayed tick costs a round of as-of
        // enrichment queries, so a default far past the documented ~2x need re-opens the
        // unbounded-work hole the cap exists to close.
        shipped.Should().BeLessThanOrEqualTo(TimeSpan.FromHours(72));

        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
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

    [NativeFact]
    public async Task WindowExactlyAtTheMaximum_IsReplayed()
    {
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { ThresholdBelow(Guid.NewGuid(), 70m) });
        SetupSeries([Reading(_dayStart.AddMinutes(30), 60)]);

        var max = TimeSpan.FromHours(2);
        var sut = CreateSut(pageSize: 1000, maxWindow: max);

        var result = await sut.ReplayAsync(null, null, _dayStart, _dayStart + max, CancellationToken.None);

        result.WindowStart.Should().Be(_dayStart);
        result.WindowEnd.Should().Be(_dayStart + max);
        result.Events.Should().ContainSingle();
    }

    [NativeFact]
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
    [NativeFact]
    public async Task OverWideWindow_IsRejectedWithoutFetchingRulesOrReadings()
    {
        var sut = CreateSut(pageSize: 1000, maxWindow: TimeSpan.FromHours(2));

        await sut.Invoking(s => s.ReplayAsync(
                null, null, _dayStart, _dayStart.AddDays(90), CancellationToken.None))
            .Should().ThrowAsync<ReplayWindowTooLargeException>();

        _alertRepository.Verify(
            r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _glucoseRepository.Verify(
            r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), null, It.IsAny<GlucoseStreams>()), Times.Never);
    }

    [NativeFact]
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

    [NativeFact]
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

        var problem = response.Result.Should().BeOfType<ObjectResult>().Which;
        problem.StatusCode.Should().Be(400);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Controller_MapsTheStepLimitToAProblemNamingTheRule()
    {
        var replayService = new Mock<IAlertReplayService>();
        var ruleId = Guid.NewGuid();
        replayService
            .Setup(s => s.ReplayAsync(It.IsAny<DateOnly?>(), It.IsAny<string?>(),
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ReplayStepLimitException(ruleId, "Night low", new InvalidOperationException()));
        var controller = new AlertReplayController(replayService.Object);

        var response = await controller.Replay(
            new AlertReplayRequest(null, null, _dayStart, _dayStart.AddDays(1)), CancellationToken.None);

        var problem = response.Result.Should().BeOfType<ObjectResult>().Which;
        problem.StatusCode.Should().Be(422);
        problem.Value.Should().BeOfType<ProblemDetails>().Which.Detail.Should().Contain("\"Night low\"");
    }

    private static AlertRuleSnapshot Rule(Guid id, AlertConditionType type, string json) =>
        new(id, Guid.NewGuid(), type.ToString(), type, json, AlertRuleSeverity.Warning, "{}", 0,
            AutoResolveEnabled: false, AutoResolveParams: null);

    /// <summary>
    /// Readings that store no rate (Dexcom, Glooko) are judged by the rate derived from the
    /// previous reading of their stream, as live evaluation judges them, whatever the page size.
    /// </summary>
    [NativeFact]
    public async Task RateAndTrendRulesFireOverAFallWithNoStoredRate()
    {
        var rateRule = Guid.NewGuid();
        var trendRule = Guid.NewGuid();
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                Rule(rateRule, AlertConditionType.RateOfChange, """{"direction":"falling","rate":3}"""),
                Rule(trendRule, AlertConditionType.Trend, """{"bucket":"falling_fast"}"""),
            });
        var series = Enumerable.Range(0, 21)
            .Select(i => Reading(_dayStart.AddHours(2).AddMinutes(i), i <= 10 ? 150 : 150 - 4 * (i - 10)))
            .ToList();
        // A reading of a second uploader between each pair: paired with one, a 150 mg/dL reading
        // would read as a steep fall from 250 and fire before the real fall starts.
        series.AddRange(Enumerable.Range(0, 21)
            .Select(i => Reading(_dayStart.AddHours(2).AddMinutes(i).AddSeconds(30), 250, dataSource: "other")));
        SetupSeries(series);

        // Each reading of the fall fires, and each flat reading of the second uploader after it
        // closes the excursion again, as live evaluation of each arriving reading does.
        var falling = Enumerable.Range(11, 10).Select(i => _dayStart.AddHours(2).AddMinutes(i)).ToList();
        foreach (var pageSize in new[] { 1, 3, 1000 })
        {
            var result = await CreateSut(pageSize)
                .ReplayAsync(new DateOnly(2026, 4, 28), "UTC", null, null, CancellationToken.None);

            result.Events.Select(e => (e.RuleId, e.At, e.Kind)).Should().BeEquivalentTo(
                falling.SelectMany(at => new[]
                {
                    (rateRule, at, AlertReplayEventKind.Fired),
                    (trendRule, at, AlertReplayEventKind.Fired),
                }),
                "page size {0}", pageSize);
        }
    }

    /// <summary>
    /// The window's first reading pairs with the reading before the window, as it did live, but
    /// that earlier reading is never the current reading of a tick inside the window.
    /// </summary>
    [NativeFact]
    public async Task TheFirstReadingPairsWithAReadingBeforeTheWindowWithoutBecomingCurrent()
    {
        var rateRule = Guid.NewGuid();
        var lowRule = Guid.NewGuid();
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                Rule(rateRule, AlertConditionType.RateOfChange, """{"direction":"falling","rate":3}"""),
                ThresholdBelow(lowRule, 200m),
            });
        SetupSeries(
        [
            Reading(_dayStart.AddMinutes(-2), 125),
            Reading(_dayStart.AddMinutes(3), 100),
        ]);

        var result = await CreateSut(pageSize: 1000)
            .ReplayAsync(new DateOnly(2026, 4, 28), "UTC", null, null, CancellationToken.None);

        result.Events.Select(e => (e.RuleId, e.At)).Should().BeEquivalentTo(
        [
            (rateRule, _dayStart.AddMinutes(3)),
            (lowRule, _dayStart.AddMinutes(3)),
        ]);
    }

    private static AlertRuleSnapshot FallingFasterThan(Guid id, int rate) =>
        Rule(id, AlertConditionType.RateOfChange, $$"""{"direction":"falling","rate":{{rate}}}""");

    private async Task<IReadOnlyList<DateTime>> FiredAtAsync(Guid ruleId, int pageSize = 1000)
    {
        var result = await CreateSut(pageSize)
            .ReplayAsync(new DateOnly(2026, 4, 28), "UTC", null, null, CancellationToken.None);
        return result.Events.Where(e => e.RuleId == ruleId && e.Kind == AlertReplayEventKind.Fired)
            .Select(e => e.At).ToList();
    }

    [NativeTheory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task A_pair_at_most_fifteen_minutes_apart_derives(int secondsOver, bool fires)
    {
        var rule = Guid.NewGuid();
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { FallingFasterThan(rule, 1) });
        var first = _dayStart.AddHours(2);
        SetupSeries(
        [
            Reading(first, 130),
            Reading(first + ReadingTrend.MaxGap + TimeSpan.FromSeconds(secondsOver), 100),
        ]);

        (await FiredAtAsync(rule)).Should().HaveCount(fires ? 1 : 0);
    }

    [NativeTheory]
    [InlineData(30, true)]
    [InlineData(29, false)]
    public async Task A_pair_at_least_thirty_seconds_apart_derives(int seconds, bool fires)
    {
        var rule = Guid.NewGuid();
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { FallingFasterThan(rule, 1) });
        var first = _dayStart.AddHours(2);
        SetupSeries([Reading(first, 101), Reading(first.AddSeconds(seconds), 100)]);

        (await FiredAtAsync(rule)).Should().HaveCount(fires ? 1 : 0);
    }

    /// <summary>
    /// A Dexcom Share connector and xDrip upload one G7. Whichever copy lands first is shown, so
    /// the shown readings alternate between the two streams and each one's previous reading of its
    /// own stream is a hidden copy. Values fall 20 mg/dL every 5 minutes.
    /// </summary>
    [NativeFact]
    public async Task Two_uploaders_taking_turns_derive_from_the_hidden_copy_of_their_own_stream()
    {
        var rule = Guid.NewGuid();
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { FallingFasterThan(rule, 3) });
        var (shown, hidden) = TakingTurns(patientDeviceId: null);
        SetupSeries(shown);
        SetupHidden(hidden);

        foreach (var pageSize in new[] { 1, 3, 1000 })
            (await FiredAtAsync(rule, pageSize)).Should().Equal(new[] { _dayStart.AddHours(2).AddMinutes(4).AddSeconds(3) },
                "the xdrip reading at 02:04:03 pairs with the hidden xdrip copy at 01:59:03 (page size {0})", pageSize);
    }

    /// <summary>
    /// The same two uploaders with one registered device, so both copies are one stream. The
    /// other uploader's copy of the reading itself, three seconds older, is not its previous one.
    /// </summary>
    [NativeFact]
    public async Task A_registered_device_derives_past_the_other_uploaders_copy_of_the_same_reading()
    {
        var rule = Guid.NewGuid();
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { FallingFasterThan(rule, 3) });
        var (shown, hidden) = TakingTurns(patientDeviceId: Guid.NewGuid());
        SetupSeries(shown);
        SetupHidden(hidden);

        (await FiredAtAsync(rule)).Should().Equal(new[] { _dayStart.AddHours(2).AddMinutes(4).AddSeconds(3) },
            "the xdrip reading at 02:04:03 pairs with the xdrip copy at 01:59:03, not the dexcom copy at 02:04:00");
    }

    /// <summary>
    /// Two apps upload one registered G7 and deduplication links none of their copies: the xdrip
    /// copy of each reading lands 40 seconds after the dexcom one, so both are shown and each is
    /// evaluated in turn. Over a 20 mg/dL fall every 5 minutes with equal copies, paired
    /// with the dexcom copy the rate would be 0. Over flat glucose with the xdrip copy 3 mg/dL low,
    /// it would be -4.5 mg/dL/min.
    /// </summary>
    [NativeTheory]
    [InlineData(20, 0, true)]
    [InlineData(0, -3, false)]
    public async Task Another_uploaders_unlinked_copy_is_never_the_pair(int fallPerFiveMinutes, int xdripOffset, bool fires)
    {
        var rule = Guid.NewGuid();
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { FallingFasterThan(rule, 3) });
        var device = Guid.NewGuid();
        var series = new List<SensorGlucose>();
        for (var i = 0; i < 4; i++)
        {
            var at = _dayStart.AddHours(2).AddMinutes(5 * i - 1);
            var mgdl = 200 - fallPerFiveMinutes * i;
            series.Add(Reading(at, mgdl, device, dataSource: "dexcom-connector"));
            series.Add(Reading(at.AddSeconds(40), mgdl + xdripOffset, device, dataSource: "xdrip"));
        }
        SetupSeries(series);

        foreach (var pageSize in new[] { 1, 3, 1000 })
            (await FiredAtAsync(rule, pageSize)).Should().Equal(
                fires ? new[] { _dayStart.AddHours(2).AddMinutes(4) } : [],
                "each copy pairs with its own uploader's copy 5 minutes before it (page size {0})", pageSize);
    }

    /// <summary>
    /// The window's first shown reading, an xdrip copy at 00:01:03, has its own uploader's previous
    /// reading only as a hidden copy at 23:57:03 the day before, under a dexcom copy that is shown.
    /// </summary>
    [NativeFact]
    public async Task The_first_reading_pairs_with_a_hidden_copy_from_before_the_window()
    {
        var rule = Guid.NewGuid();
        _alertRepository.Setup(r => r.GetEnabledRulesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { FallingFasterThan(rule, 3) });
        var before = _dayStart.AddMinutes(-3);
        var after = _dayStart.AddMinutes(1);
        var dexcomBefore = Reading(before, 130, dataSource: "dexcom-connector");
        var xdripBefore = Reading(before.AddSeconds(3), 130, dataSource: "xdrip");
        var xdripAfter = Reading(after.AddSeconds(3), 110, dataSource: "xdrip");
        var dexcomAfter = Reading(after, 110, dataSource: "dexcom-connector");
        SetupSeries([dexcomBefore, xdripAfter]);
        SetupHidden([(xdripBefore, dexcomBefore.Id), (dexcomAfter, xdripAfter.Id)]);

        (await FiredAtAsync(rule)).Should().Equal(after.AddSeconds(3));
    }

    /// <summary>
    /// Readings at 01:59 to 02:19 falling 20 mg/dL every 5 minutes, each with a dexcom copy on the
    /// minute and an xdrip copy three seconds later. The dexcom copy is shown at 01:59, 02:09 and
    /// 02:19, the xdrip copy at 02:04 and 02:14; the other copy is hidden as a duplicate of it.
    /// </summary>
    private (List<SensorGlucose> Shown, List<(SensorGlucose Reading, Guid? DuplicateOf)> Hidden) TakingTurns(
        Guid? patientDeviceId)
    {
        var shown = new List<SensorGlucose>();
        var hidden = new List<(SensorGlucose, Guid?)>();
        for (var i = 0; i < 5; i++)
        {
            var at = _dayStart.AddHours(2).AddMinutes(5 * i - 1);
            var mgdl = 200 - 20 * i;
            var dexcom = Reading(at, mgdl, patientDeviceId, dataSource: "dexcom-connector");
            var xdrip = Reading(at.AddSeconds(3), mgdl, patientDeviceId, dataSource: "xdrip");
            var (primary, copy) = i % 2 == 0 ? (dexcom, xdrip) : (xdrip, dexcom);
            shown.Add(primary);
            hidden.Add((copy, primary.Id));
        }
        return (shown, hidden);
    }
}

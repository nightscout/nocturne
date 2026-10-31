using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Profiles;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Tests.Services.BackgroundServices;

/// <summary>
/// Detection reads the tenant's enable flag, bedtime and wake time from
/// <see cref="IUISettingsService"/>, so a read it cannot trust leaves it with no window to analyse.
/// </summary>
[Trait("Category", "Unit")]
public class CompressionLowDetectionServiceTests
{
    private static readonly Guid TenantId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateOnly NightOf = new(2026, 4, 17);

    private readonly Mock<ICompressionLowRepository> _repository = new();
    private readonly Mock<IEntryService> _entryService = new();

    [Fact]
    public async Task DetectForNightAsync_doesNotDetectOnDefaultSettingsWhenTheSettingsReadFails()
    {
        var sut = Detector();
        await using var scope = (await BrokenSettingsProviderAsync()).CreateAsyncScope();

        var created = await sut.DetectForNightAsync(NightOf, scope.ServiceProvider, CancellationToken.None);

        created.Should().Be(0);
        _repository.Verify(
            r =>
                r.ActiveSuggestionsExistForNightAsync(
                    It.IsAny<DateOnly>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
        _entryService.Verify(
            e =>
                e.GetEntriesAsync(
                    It.IsAny<string?>(),
                    It.IsAny<int?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    private static readonly double[] CompressionLow =
        [110, 110, 110, 100, 85, 65, 60, 80, 105, 110, 110, 110];

    private const long FiveMinutes = 5 * 60 * 1000;
    private const long NightStart = 1_000_000_000;

    private static List<Entry> Readings(
        string dataSource,
        string device,
        long offsetMills,
        IEnumerable<double> values,
        Func<int, bool>? present = null) =>
        values
            .Select((value, index) => (value, index))
            .Where(r => present?.Invoke(r.index) ?? true)
            .Select(r => new Entry
            {
                DataSource = dataSource,
                Device = device,
                Mills = NightStart + offsetMills + r.index * FiveMinutes,
                Sgv = r.value,
                Mgdl = r.value,
            })
            .ToList();

    private static List<Entry> Primary(Func<int, bool>? present = null) =>
        Readings("xdrip", "sensor-a", 0, CompressionLow, present);

    private static List<Entry> Canonical(params List<Entry>[] streams) =>
        streams.SelectMany(stream => stream).OrderBy(e => e.Mills).ToList();

    private static CompressionLowDetectionService Detector() =>
        new(
            Mock.Of<IServiceProvider>(),
            ActiveTenantSnapshotTestDoubles.Unread(),
            TimeProvider.System,
            NullLogger<CompressionLowDetectionService>.Instance
        );

    [Fact]
    public void DetectVShapeCandidates_recoversOnThePrimarySensorWhenAnotherSensorFillsItsGap()
    {
        var primary = Primary(index => index != 7);
        var fill = Readings("dexcom", "sensor-b", FiveMinutes / 2, Enumerable.Repeat(110d, CompressionLow.Length), index => index == 7);

        var candidate = Detector().DetectVShapeCandidates(Canonical(primary, fill)).Should().ContainSingle().Subject;

        candidate.LowestGlucose.Should().Be(60);
        candidate.RecoveryMinutes.Should().Be(10);
        candidate.EndMills.Should().Be(primary[^4].Mills);
    }

    [Fact]
    public void DetectVShapeCandidates_separatesTwoSensorsThatShareADeviceByDataSource()
    {
        var primary = Readings("xdrip", "connector", 0, CompressionLow, index => index != 7);
        var fill = Readings("dexcom", "connector", FiveMinutes / 2, Enumerable.Repeat(110d, CompressionLow.Length), index => index == 7);

        var candidate = Detector().DetectVShapeCandidates(Canonical(primary, fill)).Should().ContainSingle().Subject;

        candidate.RecoveryMinutes.Should().Be(10);
        candidate.EndMills.Should().Be(primary[^4].Mills);
    }

    [Fact]
    public void DetectVShapeCandidates_findsNothingFromAConstantOffsetBetweenSensors()
    {
        var high = Readings("xdrip", "sensor-a", 0, Enumerable.Repeat(120d, CompressionLow.Length), index => index % 3 != 1);
        var offset = Readings("dexcom", "sensor-b", FiveMinutes / 2, Enumerable.Repeat(60d, CompressionLow.Length), index => index % 3 == 1);

        Detector().DetectVShapeCandidates(Canonical(high, offset)).Should().BeEmpty();
    }

    [Fact]
    public void DetectVShapeCandidates_detectsASingleSensorNightWhetherOrNotItNamesTheDataSource()
    {
        var named = Detector().DetectVShapeCandidates(Primary());
        var unnamed = Detector().DetectVShapeCandidates(
            Primary().Select(e => { e.DataSource = null; return e; }).ToList());

        named.Should().ContainSingle().Which.LowestGlucose.Should().Be(60);
        unnamed.Should().Equal(named);
    }

    [Theory]
    [InlineData(1, 9, 5)]
    [InlineData(9, 17, 13)]
    public async Task DetectForNightAsync_filesADipUnderTheOneNightWhoseWindowHoldsIt(
        int bedtimeHour, int wakeTimeHour, int dipHour)
    {
        var night = new DateOnly(2026, 10, 20);
        var dipStart = night.ToDateTime(new TimeOnly(dipHour, 0));
        var firstReading = night.AddDays(-1).ToDateTime(TimeOnly.MinValue);
        var readings = Enumerable.Range(0, 3 * 24 * 12)
            .Select(i => firstReading.AddMinutes(5 * i))
            .Select(time =>
            {
                var step = (int)(time - dipStart).TotalMinutes / 5;
                var glucose = time >= dipStart && step < CompressionLow.Length ? CompressionLow[step] : 110d;
                var mills = new DateTimeOffset(time, TimeSpan.Zero).ToUnixTimeMilliseconds();
                return new Entry { Mills = mills, Sgv = glucose, Mgdl = glucose };
            })
            .ToList();
        var saved = new List<CompressionLowSuggestion>();
        _repository
            .Setup(r => r.ActiveSuggestionsExistForNightAsync(It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _repository
            .Setup(r => r.CreateManyAsync(It.IsAny<IReadOnlyCollection<CompressionLowSuggestion>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyCollection<CompressionLowSuggestion> batch, CancellationToken _) => saved.AddRange(batch))
            .Returns(Task.CompletedTask);
        _entryService
            .Setup(e => e.GetEntriesAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string? find, int? _, int? _, CancellationToken _) =>
            {
                var bounds = System.Text.RegularExpressions.Regex.Matches(find!, @"\d+")
                    .Select(m => long.Parse(m.Value))
                    .ToList();
                return readings.Where(e => e.Mills >= bounds[0] && e.Mills <= bounds[1]).ToList();
            });
        var settings = new Mock<IUISettingsService>();
        settings
            .Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UISettingsConfiguration
            {
                DataQuality = new DataQualitySettings
                {
                    SleepSchedule = new SleepScheduleSettings
                    {
                        Timezone = "UTC",
                        BedtimeHour = bedtimeHour,
                        WakeTimeHour = wakeTimeHour,
                    },
                },
            });
        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(a => a.TenantId).Returns(TenantId);
        var services = new ServiceCollection();
        services.AddScoped(_ => settings.Object);
        services.AddScoped(_ => _repository.Object);
        services.AddScoped(_ => _entryService.Object);
        services.AddScoped(_ => Mock.Of<ITherapySettingsResolver>());
        services.AddScoped<ICompressionLowNightWindowResolver, CompressionLowNightWindowResolver>();
        services.AddLogging();
        services.AddScoped(_ => TestIobCobReader.Empty());
        services.AddScoped(_ => Mock.Of<ISensitivityResolver>());
        services.AddScoped(_ => Mock.Of<IInAppNotificationService>());
        services.AddScoped(_ => tenantAccessor.Object);
        services.AddScoped(_ => Mock.Of<Nocturne.Core.Contracts.Identity.ITenantOwnerResolver>());
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var sut = Detector();

        foreach (var day in new[] { night.AddDays(-1), night, night.AddDays(1) })
            await sut.DetectForNightAsync(day, scope.ServiceProvider, CancellationToken.None);

        saved.Should().ContainSingle().Which.NightOf.Should().Be(night);
    }

    private const double SharpDrop = 5.0;
    private const int FastRecovery = 5;
    private const int SlowRecovery = CompressionLowDetectionService.MaxRecoveryMinutes;

    private static CompressionLowDetectionService.VShapeCandidate Candidate(
        int nadirHour,
        double dropRate,
        int recoveryMinutes,
        int nadirMinute = 0)
    {
        var nadir = new DateTime(2026, 4, 17, nadirHour, nadirMinute, 0, DateTimeKind.Utc);
        var start = new DateTimeOffset(nadir).AddMinutes(-20).ToUnixTimeMilliseconds();
        return new CompressionLowDetectionService.VShapeCandidate(
            StartMills: start,
            EndMills: start + (20 + recoveryMinutes) * 60_000L,
            LowestGlucose: 60,
            PreDropGlucose: 110,
            MaxDropRate: dropRate,
            RecoveryMinutes: recoveryMinutes,
            NadirTime: nadir);
    }

    private sealed record OnBoard(double Iob = 0, double Cob = 0, double Isf = 50, bool TempBasalOnly = false);

    private static async Task<double> ScoreInWindowAsync(
        int bedtimeHour,
        int wakeTimeHour,
        CompressionLowDetectionService.VShapeCandidate candidate,
        OnBoard? onBoard = null)
    {
        onBoard ??= new OnBoard();

        var inputs = new IobCobInputs(
            [],
            onBoard.TempBasalOnly ? [new TempBasal()] : [],
            []);
        var reader = new Mock<IIobCobReader>();
        reader
            .Setup(r => r.ReadTotalAsync(candidate.StartMills, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IobCobReading(
                new IobResult { Iob = onBoard.Iob, ReportedByDevice = !onBoard.TempBasalOnly },
                new CobResult { Cob = onBoard.Cob, ReportedByDevice = true },
                inputs)
            { InsulinInWindow = onBoard.TempBasalOnly });
        var sensitivity = new Mock<ISensitivityResolver>();
        sensitivity
            .Setup(r => r.GetSensitivityAsync(candidate.StartMills, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(onBoard.Isf);

        return await Detector().ScoreCandidateAsync(
            candidate, TimeZoneInfo.Utc, bedtimeHour, wakeTimeHour, reader.Object, sensitivity.Object, CancellationToken.None);
    }

    private static Task<double> ScoreAsync(
        CompressionLowDetectionService.VShapeCandidate candidate,
        OnBoard? onBoard = null) =>
        ScoreInWindowAsync(23, 7, candidate, onBoard);

    private static async Task<double> PenaltyAsync(OnBoard onBoard)
    {
        var candidate = Candidate(3, SharpDrop, FastRecovery);
        return await ScoreAsync(candidate) - await ScoreAsync(candidate, onBoard);
    }

    private static bool Accepted(double confidence) =>
        confidence >= CompressionLowConfidence.MinThreshold;

    [Fact]
    public async Task ScoreCandidate_acceptsATextbookNightTimeVNearTheTopOfTheScale()
    {
        var confidence = await ScoreAsync(Candidate(3, SharpDrop, FastRecovery));

        Accepted(confidence).Should().BeTrue();
        confidence.Should().BeGreaterThan(0.95);
    }

    [Theory]
    [InlineData(23, 0)]
    [InlineData(6, 59)]
    public async Task ScoreCandidate_rejectsAnEdgeOfNightVWithASlowRecoveryAndNoTreatments(int hour, int minute)
    {
        Accepted(await ScoreAsync(Candidate(hour, SharpDrop, SlowRecovery, minute))).Should().BeFalse();
    }

    [Fact]
    public async Task ScoreCandidate_rejectsAMinimumSlopeSlowRecoveryVEvenAtTheBestTimeOfNight()
    {
        var confidence = await ScoreAsync(
            Candidate(3, CompressionLowDetectionService.MinDropRateMgDlPerMin, SlowRecovery));

        Accepted(confidence).Should().BeFalse();
        confidence.Should().BeApproximately(0.35, 0.01);
    }

    [Fact]
    public async Task ScoreCandidate_rejectsATextbookVAfterALargeBolusAlone()
    {
        Accepted(await ScoreAsync(Candidate(3, SharpDrop, FastRecovery), new OnBoard(Iob: 3))).Should().BeFalse();
    }

    [Fact]
    public async Task ScoreCandidate_rejectsATextbookVAfterAMealBolus()
    {
        Accepted(await ScoreAsync(Candidate(3, SharpDrop, FastRecovery), new OnBoard(Iob: 3, Cob: 40))).Should().BeFalse();
    }

    [Fact]
    public async Task ScoreCandidate_penalisesInsulinOnBoardFromTempBasalsAlone()
    {
        var penalty = await PenaltyAsync(new OnBoard(Iob: 1.5, TempBasalOnly: true));

        penalty.Should().BeGreaterThan(0.3);
    }

    [Fact]
    public async Task ScoreCandidate_penalisesInsulinOnBoardHoweverLongAgoTheBolusWas()
    {
        var penalty = await PenaltyAsync(new OnBoard(Iob: 1.2));

        penalty.Should().BeGreaterThan(0.3);
    }

    [Fact]
    public async Task ScoreCandidate_growsTheInsulinPenaltyPastTwoUnits()
    {
        var two = await PenaltyAsync(new OnBoard(Iob: 2, Isf: 10));
        var four = await PenaltyAsync(new OnBoard(Iob: 4, Isf: 10));

        four.Should().BeGreaterThan(two);
        two.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ScoreCandidate_stopsTheInsulinPenaltyOnceTheInsulinExplainsTheWholeDrop()
    {
        var explained = await PenaltyAsync(new OnBoard(Iob: 1, Isf: 50));
        var more = await PenaltyAsync(new OnBoard(Iob: 6, Isf: 50));

        explained.Should().BeApproximately(CompressionLowDetectionService.InsulinPenaltyWeight, 0.001);
        more.Should().BeApproximately(explained, 0.001);
    }

    [Fact]
    public async Task ScoreCandidate_growsTheCarbPenaltyWithCarbsOnBoardUpToItsWeight()
    {
        var half = await PenaltyAsync(new OnBoard(Cob: CompressionLowDetectionService.CarbPenaltyFullCobGrams / 2));
        var full = await PenaltyAsync(new OnBoard(Cob: CompressionLowDetectionService.CarbPenaltyFullCobGrams));
        var more = await PenaltyAsync(new OnBoard(Cob: CompressionLowDetectionService.CarbPenaltyFullCobGrams * 2));

        half.Should().BeGreaterThan(0).And.BeLessThan(full);
        full.Should().BeApproximately(CompressionLowDetectionService.CarbPenaltyWeight, 0.001);
        more.Should().BeApproximately(full, 0.001);
    }

    [Fact]
    public async Task ScoreCandidate_doesNotPenaliseWithNoInsulinAndNoCarbs()
    {
        (await PenaltyAsync(new OnBoard())).Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task ScoreCandidate_doesNotPenaliseInsulinWhenThereIsNoSensitivity(double isf)
    {
        (await PenaltyAsync(new OnBoard(Iob: 3, Isf: isf))).Should().Be(0);
    }

    [Fact]
    public async Task ScoreCandidate_doesNotPenaliseNegativeInsulinOnBoard()
    {
        (await PenaltyAsync(new OnBoard(Iob: -1))).Should().Be(0);
    }

    [Fact]
    public async Task ScoreCandidate_scoresAMinimumSlopeDropBelowASharpOneAtTheSameTimeAndRecovery()
    {
        var weak = await ScoreAsync(Candidate(3, CompressionLowDetectionService.MinDropRateMgDlPerMin, FastRecovery));
        var sharp = await ScoreAsync(Candidate(3, SharpDrop, FastRecovery));

        weak.Should().BeLessThan(sharp);
    }

    [Fact]
    public async Task ScoreCandidate_letsEachTermAloneMoveACandidateAcrossTheThreshold()
    {
        const double midRate = 3.5;
        const int midRecovery = SlowRecovery / 2;

        Accepted(await ScoreAsync(Candidate(0, midRate, midRecovery, 30))).Should().BeFalse();

        Accepted(await ScoreAsync(Candidate(0, SharpDrop, midRecovery, 30))).Should().BeTrue("sharpness");
        Accepted(await ScoreAsync(Candidate(3, midRate, midRecovery))).Should().BeTrue("time of night");
        Accepted(await ScoreAsync(Candidate(0, midRate, FastRecovery, 30))).Should().BeTrue("recovery");
    }

    [Fact]
    public async Task ScoreCandidate_scoresADaySleepersVByWhereItFallsInTheirWindow()
    {
        Accepted(await ScoreInWindowAsync(9, 17, Candidate(13, SharpDrop, FastRecovery))).Should().BeTrue();
        Accepted(await ScoreInWindowAsync(9, 17, Candidate(9, SharpDrop, SlowRecovery, 30))).Should().BeFalse();
    }

    [Fact]
    public async Task ScoreCandidate_acceptsATextbookVMidWindowForAnEarlyBedtime()
    {
        Accepted(await ScoreInWindowAsync(21, 7, Candidate(1, SharpDrop, FastRecovery))).Should().BeTrue();
    }

    [Theory]
    [InlineData(23, 7, 3, 0, 1.0)]
    [InlineData(23, 7, 23, 0, 0.0)]
    [InlineData(23, 7, 7, 0, 0.0)]
    [InlineData(23, 7, 12, 0, 0.0)]
    [InlineData(22, 6, 23, 0, 0.5)]
    [InlineData(9, 17, 13, 0, 1.0)]
    [InlineData(9, 17, 3, 0, 0.0)]
    [InlineData(9, 17, 11, 0, 1.0)]
    [InlineData(9, 17, 10, 0, 0.5)]
    [InlineData(9, 17, 9, 30, 0.25)]
    public void TimeOfNightFraction_followsTheNadirThroughTheConfiguredWindow(
        int bedtimeHour, int wakeTimeHour, int hour, int minute, double expected)
    {
        CompressionLowDetectionService
            .TimeOfNightFraction(new TimeSpan(hour, minute, 0), bedtimeHour, wakeTimeHour)
            .Should().BeApproximately(expected, 0.001);
    }

    private static readonly DateTimeOffset BeforeEastWake = new(2026, 10, 20, 0, 50, 0, TimeSpan.Zero);

    private const string SixHoursEast = "Etc/GMT-6";
    private const string SixHoursWest = "Etc/GMT+6";

    [Fact]
    public async Task ScanReadyNightsAsync_doesNotScanANightWhoseWindowIsStillOpen()
    {
        using var fixture = await ScheduleFixture.CreateAsync(BeforeEastWake, SixHoursEast);

        await fixture.Sut.ScanReadyNightsAsync(CancellationToken.None);

        fixture.Scans.Should().Equal(new DateOnly(2026, 10, 18));
    }

    [Fact]
    public async Task ScanReadyNightsAsync_scansTheWholeNightOnceItsWindowHasEnded()
    {
        using var fixture = await ScheduleFixture.CreateAsync(BeforeEastWake, SixHoursEast);
        await fixture.Sut.ScanReadyNightsAsync(CancellationToken.None);

        fixture.Clock.Advance(TimeSpan.FromMinutes(24));
        await fixture.Sut.ScanReadyNightsAsync(CancellationToken.None);
        fixture.Scans.Should().Equal(new DateOnly(2026, 10, 18));

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Sut.ScanReadyNightsAsync(CancellationToken.None);
        await fixture.Sut.ScanReadyNightsAsync(CancellationToken.None);
        fixture.Scans.Should().Equal(new DateOnly(2026, 10, 18), new DateOnly(2026, 10, 19));
    }

    [Fact]
    public async Task ScanReadyNightsAsync_scansEachTenantOncePerNightAfterItsOwnWindowEnds()
    {
        using var fixture = await ScheduleFixture.CreateAsync(BeforeEastWake, SixHoursEast, SixHoursWest);
        var east = fixture.TenantIds[0];
        var west = fixture.TenantIds[1];

        await fixture.Sut.ScanReadyNightsAsync(CancellationToken.None);
        fixture.Clock.Advance(TimeSpan.FromMinutes(25));
        await fixture.Sut.ScanReadyNightsAsync(CancellationToken.None);
        fixture.Clock.Advance(TimeSpan.FromHours(6));
        await fixture.Sut.ScanReadyNightsAsync(CancellationToken.None);
        fixture.Clock.Advance(TimeSpan.FromHours(6));
        await fixture.Sut.ScanReadyNightsAsync(CancellationToken.None);
        await fixture.Sut.ScanReadyNightsAsync(CancellationToken.None);

        fixture.TenantScans.Should().BeEquivalentTo(
            [
                (east, new DateOnly(2026, 10, 18)),
                (west, new DateOnly(2026, 10, 18)),
                (east, new DateOnly(2026, 10, 19)),
                (west, new DateOnly(2026, 10, 19)),
            ],
            options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task ScanReadyNightsAsync_sleepsUntilTheEarliestNightEndsAcrossTenants()
    {
        using var fixture = await ScheduleFixture.CreateAsync(BeforeEastWake, SixHoursWest, SixHoursEast);

        var untilEast = await fixture.Sut.ScanReadyNightsAsync(CancellationToken.None);

        untilEast.Should().Be(TimeSpan.FromMinutes(25));
    }

    [Fact]
    public async Task ScanReadyNightsAsync_boundsTheSleepByTheFloorAndTheCeiling()
    {
        using var fixture = await ScheduleFixture.CreateAsync(BeforeEastWake, SixHoursEast);

        fixture.Clock.Advance(TimeSpan.FromMinutes(24) + TimeSpan.FromSeconds(50));
        (await fixture.Sut.ScanReadyNightsAsync(CancellationToken.None))
            .Should().Be(CompressionLowDetectionService.MinSleep);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        (await fixture.Sut.ScanReadyNightsAsync(CancellationToken.None))
            .Should().Be(CompressionLowDetectionService.MaxSleep);
    }

    [Fact]
    public async Task ScanReadyNightsAsync_scansADaySleepersNightOnceItsOwnWindowHasEnded()
    {
        var beforeWake = new DateTimeOffset(2026, 10, 20, 8, 50, 0, TimeSpan.Zero);
        using var fixture = await ScheduleFixture.CreateAsync(beforeWake, "UTC", bedtimeHour: 1, wakeTimeHour: 9);

        await fixture.Sut.ScanReadyNightsAsync(CancellationToken.None);
        fixture.Scans.Should().Equal(new DateOnly(2026, 10, 19));

        fixture.Clock.Advance(TimeSpan.FromMinutes(36));
        await fixture.Sut.ScanReadyNightsAsync(CancellationToken.None);
        fixture.Scans.Should().Equal(new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 20));
    }

    [Fact]
    public async Task ScanReadyNightsAsync_sleepsUntilTheNextDaySleepersWindowEnds()
    {
        var afterWake = new DateTimeOffset(2026, 10, 20, 9, 30, 0, TimeSpan.Zero);
        using var fixture = await ScheduleFixture.CreateAsync(afterWake, "UTC", bedtimeHour: 1, wakeTimeHour: 9);

        var delay = await fixture.Sut.ScanReadyNightsAsync(CancellationToken.None);

        delay.Should().Be(CompressionLowDetectionService.MaxSleep);
        fixture.Scans.Should().Equal(new DateOnly(2026, 10, 20));
    }

    [Fact]
    public async Task ScanReadyNightsAsync_doesNotRescanANightWhoseSuggestionsWereAllDismissed()
    {
        using var fixture = await ScheduleFixture.CreateAsync(BeforeEastWake, SixHoursEast);
        fixture.NightsWithSuggestions.Add(new DateOnly(2026, 10, 18));

        await fixture.Sut.ScanReadyNightsAsync(CancellationToken.None);

        fixture.Scans.Should().BeEmpty();
    }

    private sealed class ScheduleFixture : IDisposable
    {
        private readonly SqliteTestDatabase _database;
        private readonly ServiceProvider _provider;

        public FakeTimeProvider Clock { get; }
        public CompressionLowDetectionService Sut { get; }
        public IReadOnlyList<Guid> TenantIds { get; }
        public HashSet<DateOnly> NightsWithSuggestions { get; }
        public List<(Guid Tenant, DateOnly Night)> TenantScans { get; }
        public List<DateOnly> Scans => TenantScans.Select(s => s.Night).ToList();

        private ScheduleFixture(
            SqliteTestDatabase database,
            ServiceProvider provider,
            FakeTimeProvider clock,
            CompressionLowDetectionService sut,
            IReadOnlyList<Guid> tenantIds,
            HashSet<DateOnly> nightsWithSuggestions,
            List<(Guid Tenant, DateOnly Night)> scans)
        {
            _database = database;
            _provider = provider;
            Clock = clock;
            Sut = sut;
            TenantIds = tenantIds;
            NightsWithSuggestions = nightsWithSuggestions;
            TenantScans = scans;
        }

        public static Task<ScheduleFixture> CreateAsync(DateTimeOffset now, params string[] tenantTimezones) =>
            CreateAsync(now, tenantTimezones, 23, 7);

        public static Task<ScheduleFixture> CreateAsync(
            DateTimeOffset now, string timezone, int bedtimeHour, int wakeTimeHour) =>
            CreateAsync(now, [timezone], bedtimeHour, wakeTimeHour);

        private static Task<ScheduleFixture> CreateAsync(
            DateTimeOffset now, string[] tenantTimezones, int bedtimeHour, int wakeTimeHour)
        {
            var clock = new FakeTimeProvider(now);
            var tenantIds = tenantTimezones.Select(_ => Guid.NewGuid()).ToList();
            var database = TestDbContextFactory.CreateSqlite();
            for (var i = 0; i < tenantIds.Count; i++)
                database.SeedTenant(tenantIds[i], $"tenant-{i}");

            var settingsByTenant = tenantIds
                .Zip(tenantTimezones)
                .ToDictionary(
                    pair => pair.First,
                    pair => new UISettingsConfiguration
                    {
                        DataQuality = new DataQualitySettings
                        {
                            SleepSchedule = new SleepScheduleSettings
                            {
                                Timezone = pair.Second,
                                BedtimeHour = bedtimeHour,
                                WakeTimeHour = wakeTimeHour,
                            },
                        },
                    });
            var nightsWithSuggestions = new HashSet<DateOnly>();
            var scans = new List<(Guid Tenant, DateOnly Night)>();

            var services = new ServiceCollection();
            database.AddToServices(services);
            services.AddSingleton(sp => new ActiveTenantSnapshot(
                new TenantRepository(sp.GetRequiredService<INocturneDbContextFactory>()),
                clock,
                NullLogger<ActiveTenantSnapshot>.Instance));
            services.AddScoped<ITenantAccessor, RecordingTenantAccessor>();
            services.AddScoped(sp =>
            {
                var tenantId = sp.GetRequiredService<ITenantAccessor>().TenantId;
                var settings = new Mock<IUISettingsService>();
                settings
                    .Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(settingsByTenant[tenantId]);
                return settings.Object;
            });
            services.AddScoped(sp =>
            {
                var tenantId = sp.GetRequiredService<ITenantAccessor>().TenantId;
                var repository = new Mock<ICompressionLowRepository>();
                repository
                    .Setup(r => r.AnySuggestionExistsForNightAsync(It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((DateOnly night, CancellationToken _) => nightsWithSuggestions.Contains(night));
                repository
                    .Setup(r => r.ActiveSuggestionsExistForNightAsync(It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
                    .Callback((DateOnly night, CancellationToken _) => scans.Add((tenantId, night)))
                    .ReturnsAsync(false);
                return repository.Object;
            });
            services.AddScoped(_ =>
            {
                var entries = new Mock<IEntryService>();
                entries
                    .Setup(e => e.GetEntriesAsync(
                        It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Array.Empty<Entry>());
                return entries.Object;
            });
            services.AddScoped(_ => TestIobCobReader.Empty());
            services.AddScoped(_ => Mock.Of<ISensitivityResolver>());
            services.AddScoped(_ => Mock.Of<IInAppNotificationService>());
            services.AddScoped(_ => Mock.Of<ITherapySettingsResolver>());
            services.AddScoped<ICompressionLowNightWindowResolver, CompressionLowNightWindowResolver>();
            services.AddLogging();
            var provider = services.BuildServiceProvider();

            var sut = new CompressionLowDetectionService(
                provider,
                provider.GetRequiredService<ActiveTenantSnapshot>(),
                clock,
                NullLogger<CompressionLowDetectionService>.Instance);

            return Task.FromResult(
                new ScheduleFixture(database, provider, clock, sut, tenantIds, nightsWithSuggestions, scans));
        }

        public void Dispose()
        {
            _provider.Dispose();
            _database.Dispose();
        }
    }

    private sealed class RecordingTenantAccessor : ITenantAccessor
    {
        public TenantContext? Context { get; private set; }
        public Guid TenantId => Context?.TenantId ?? Guid.Empty;
        public bool IsResolved => Context is not null;

        public void SetTenant(TenantContext context) => Context = context;
    }

    /// <summary>
    /// A provider whose <see cref="IUISettingsService"/> is the real one over a database that can no
    /// longer be read, which is what a failed read looks like from inside the scope.
    /// </summary>
    private async Task<ServiceProvider> BrokenSettingsProviderAsync()
    {
        _entryService
            .Setup(e =>
                e.GetEntriesAsync(
                    It.IsAny<string?>(),
                    It.IsAny<int?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(Array.Empty<Entry>());

        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var context = new NocturneDbContext(options) { TenantId = TenantId };
        var settingsService = new UISettingsService(
            context.AsFactory(),
            TimeProvider.System,
            NullLogger<UISettingsService>.Instance
        );
        await context.DisposeAsync();

        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.SetupGet(a => a.TenantId).Returns(TenantId);

        var services = new ServiceCollection();
        services.AddScoped<IUISettingsService>(_ => settingsService);
        services.AddScoped(_ => _repository.Object);
        services.AddScoped(_ => _entryService.Object);
        services.AddScoped(_ => TestIobCobReader.Empty());
        services.AddScoped(_ => Mock.Of<ISensitivityResolver>());
        services.AddScoped(_ => Mock.Of<IInAppNotificationService>());
        services.AddScoped(_ => Mock.Of<ITherapySettingsResolver>());
        services.AddScoped<ICompressionLowNightWindowResolver, CompressionLowNightWindowResolver>();
        services.AddLogging();
        services.AddScoped(_ => tenantAccessor.Object);

        return services.BuildServiceProvider();
    }
}

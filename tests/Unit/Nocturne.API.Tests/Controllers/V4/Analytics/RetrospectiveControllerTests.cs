using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Clinical.Treatments;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Services.Devices;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Queries;
using Nocturne.Core.Models.V4;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Analytics;

[Trait("Category", "Unit")]
public class RetrospectiveControllerTests
{
    private readonly Mock<IIobCalculator> _iobCalculatorMock = new();
    private readonly Mock<ICobCalculator> _cobCalculatorMock = new();
    private readonly Mock<IEntryService> _entryServiceMock = new();
    private readonly Mock<IBolusRepository> _bolusRepoMock = new();
    private readonly Mock<ICarbIntakeRepository> _carbIntakeRepoMock = new();
    private readonly Mock<ITempBasalRepository> _tempBasalRepoMock = new();
    private readonly Mock<IStateSpanRepository> _stateSpans = MockStateSpans.Empty();
    private readonly Mock<ITherapyTimelineResolver> _therapyTimelineResolverMock = new();
    private readonly Mock<ILogger<RetrospectiveController>> _loggerMock = new();

    public RetrospectiveControllerTests()
    {
        _therapyTimelineResolverMock
            .Setup(r => r.BuildAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long from, long to, string? _, CancellationToken _) =>
                new TherapyTimeline([new TherapySegment(from, to, FlatSchedule(0.8))]));
    }

    private static TherapySnapshot FlatSchedule(double basalRate) => new(
        3.0, TherapySnapshot.DefaultCarbsPerHour, null, null, 0,
        [new ScheduleEntry { TimeAsSeconds = 0, Value = 95.0 }], null,
        [new ScheduleEntry { TimeAsSeconds = 0, Value = basalRate }]);

    private void SetupOverride(DateTime start, DateTime end, double factor) =>
        _stateSpans
            .Setup(r => r.GetByCategories(
                It.IsAny<IEnumerable<StateSpanCategory>>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<StateSpanCategory, List<StateSpan>>
            {
                [StateSpanCategory.Override] =
                [
                    new StateSpan
                    {
                        Category = StateSpanCategory.Override,
                        StartTimestamp = start,
                        EndTimestamp = end,
                        Metadata = new Dictionary<string, object> { ["insulinNeedsScaleFactor"] = factor },
                    },
                ],
            });

    private void VerifyOverridesReadOnce(CancellationToken token) =>
        _stateSpans.Verify(
            r => r.GetByCategories(
                It.Is<IEnumerable<StateSpanCategory>>(c => c.SequenceEqual(new[] { StateSpanCategory.Override })),
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), token),
            Times.Once);

    private static DeviceStatusProjectionService CreateProjectionService()
    {
        return new DeviceStatusProjectionService(
            Mock.Of<IDeviceStatusGroupRepository>(r => r.GetAsync(
                    It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(),
                    It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())
                == Task.FromResult<IReadOnlyList<DeviceStatusGroup>>(Array.Empty<DeviceStatusGroup>())),
            new Mock<IApsSnapshotRepository>().Object,
            new Mock<IPumpSnapshotRepository>().Object,
            new Mock<IUploaderSnapshotRepository>().Object,
            new Mock<IStateSpanRepository>().Object,
            new Mock<IDeviceStatusExtrasRepository>().Object,
            new Mock<ILogger<DeviceStatusProjectionService>>().Object);
    }

    private RetrospectiveController CreateController()
    {
        var controller = new RetrospectiveController(
            _iobCalculatorMock.Object,
            _cobCalculatorMock.Object,
            _entryServiceMock.Object,
            _bolusRepoMock.Object,
            _carbIntakeRepoMock.Object,
            _tempBasalRepoMock.Object,
            CreateProjectionService(),
            _therapyTimelineResolverMock.Object,
            _stateSpans.Object,
            Mock.Of<IApsSnapshotRepository>(),
            Mock.Of<IPumpSnapshotRepository>(),
            new IobCobReader(_iobCalculatorMock.Object, _cobCalculatorMock.Object, _bolusRepoMock.Object,
                _tempBasalRepoMock.Object, _carbIntakeRepoMock.Object, TestTenantCache.Create(partition: null), NullLogger<IobCobReader>.Instance),
            _loggerMock.Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = new HashSet<string> { Scope.FullAccess };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return controller;
    }

    [Fact]
    public async Task GetBasalTimeline_TempStartedTwoHoursBeforeTheDay_AppearsForItsInDayPart()
    {
        var dayStart = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var straddling = new TempBasal
        {
            StartTimestamp = dayStart.AddHours(-2),
            EndTimestamp = dayStart.AddHours(1),
            Origin = TempBasalOrigin.Algorithm,
            Rate = 0.1,
        };
        _tempBasalRepoMock
            .Setup(s => s.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime? from, DateTime? to, string? _, string? _, int _, int _, bool _, CancellationToken _) =>
                new[] { straddling }.Where(t => t.StartTimestamp >= from && t.StartTimestamp <= to).ToList());

        var result = await CreateController().GetBasalTimeline("2024-01-15", intervalMinutes: 30);

        var data = ((BasalTimelineResponse)((OkObjectResult)result.Result!).Value!).Data!;
        data.Where(p => p.Time < new DateTimeOffset(dayStart.AddHours(1)).ToUnixTimeMilliseconds())
            .Should().OnlyContain(p => p.IsTemp && p.Rate == 0.1);
        data.Where(p => p.Time >= new DateTimeOffset(dayStart.AddHours(1)).ToUnixTimeMilliseconds())
            .Should().OnlyContain(p => !p.IsTemp && p.Rate == 0.8);
    }

    [Fact]
    public async Task GetBasalTimeline_TempSupersededBeforeTheDay_DoesNotClaimItsStoredSpan()
    {
        var dayStart = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        TempBasal Temp(double fromHours, double toHours, double rate) => new()
        {
            Device = "loop",
            StartTimestamp = dayStart.AddHours(fromHours),
            EndTimestamp = dayStart.AddHours(toHours),
            Origin = TempBasalOrigin.Algorithm,
            Rate = rate,
        };
        var stored = new[] { Temp(-4, 4, 0.0), Temp(-3.5, -3, 0.5), Temp(1, 2, 2.0) };
        _tempBasalRepoMock
            .Setup(s => s.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime? from, DateTime? to, string? _, string? _, int _, int _, bool _, CancellationToken _) =>
                stored.Where(t => t.StartTimestamp >= from && t.StartTimestamp <= to).ToList());

        var result = await CreateController().GetBasalTimeline("2024-01-15", intervalMinutes: 60);

        var data = ((BasalTimelineResponse)((OkObjectResult)result.Result!).Value!).Data!;
        data.Single(p => p.Hour == 0).Should().Match<BasalDataPoint>(p => !p.IsTemp && p.Rate == 0.8);
        data.Single(p => p.Hour == 1).Should().Match<BasalDataPoint>(p => p.IsTemp && p.Rate == 2.0);
    }

    [Fact]
    public async Task GetBasalTimeline_ScalesTheScheduledRateByAnActiveOverride()
    {
        var dayStart = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        SetupOverride(dayStart.AddHours(6), dayStart.AddHours(8), 0.5);
        _tempBasalRepoMock
            .Setup(s => s.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TempBasal>());
        using var cts = new CancellationTokenSource();

        var result = await CreateController().GetBasalTimeline("2024-01-15", intervalMinutes: 60, cts.Token);

        var data = ((BasalTimelineResponse)((OkObjectResult)result.Result!).Value!).Data!;
        data.Where(p => p.Hour is 6 or 7).Should().OnlyContain(p => p.Rate == 0.4 && !p.IsTemp);
        data.Where(p => p.Hour is not (6 or 7)).Should().OnlyContain(p => p.Rate == 0.8);
        VerifyOverridesReadOnce(cts.Token);
    }

    private void SetupTimelineDependencies()
    {
        _entryServiceMock
            .Setup(s => s.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Entry>());

        _bolusRepoMock
            .Setup(s => s.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<BolusKind?>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Bolus>());

        _tempBasalRepoMock
            .Setup(s => s.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TempBasal>());

        _carbIntakeRepoMock
            .Setup(s => s.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CarbIntake>());
    }

    [Fact]
    public async Task GetRetrospectiveTimeline_ShowsTheOverrideScaledRateAsDeliveredBasal()
    {
        SetupTimelineDependencies();
        var dayStart = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        SetupOverride(dayStart.AddHours(6), dayStart.AddHours(8), 0.5);
        using var cts = new CancellationTokenSource();

        var result = await CreateController().GetRetrospectiveTimeline("2024-01-15", 60, cts.Token);

        var data = ((RetrospectiveTimelineResponse)((OkObjectResult)result.Result!).Value!).Data!;
        data.Where(p => p.Hour is 6 or 7).Should().OnlyContain(p => p.BasalRate == 0.4 && !p.IsTemp);
        data.Where(p => p.Hour is not (6 or 7)).Should().OnlyContain(p => p.BasalRate == 0.8);
        VerifyOverridesReadOnce(cts.Token);
    }

    [Fact]
    public async Task GetRetrospectiveTimeline_ReadsEveryEntryOfTheDayByTime()
    {
        SetupTimelineDependencies();
        EntryQuery? captured = null;
        _entryServiceMock
            .Setup(s => s.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>()))
            .Callback<EntryQuery, CancellationToken>((query, _) => captured = query)
            .ReturnsAsync(new List<Entry>());
        var controller = CreateController();

        await controller.GetRetrospectiveTimeline("2024-01-15");

        captured.Should().NotBeNull();
        captured!.Type.Should().BeNull();
        captured.Count.Should().Be(5000);
        var find = FindQuery.Parse(captured.Find);
        find.FromMills.Should().Be(new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
        find.ToMills.Should().Be(new DateTimeOffset(2024, 1, 16, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() - 1);
    }

    [Fact]
    public async Task GetRetrospectiveData_QueriesSgvAroundTheTime()
    {
        // Arrange
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        _entryServiceMock
            .Setup(s => s.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Entry>());

        _bolusRepoMock
            .Setup(s => s.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<BolusKind?>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Bolus>());

        _tempBasalRepoMock
            .Setup(s => s.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TempBasal>());

        _carbIntakeRepoMock
            .Setup(s => s.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CarbIntake>());

        _iobCalculatorMock
            .Setup(s => s.CalculateTotalAsync(
                It.IsAny<List<Bolus>>(),
                It.IsAny<List<TempBasal>?>(),
                It.IsAny<long>(),
                It.IsAny<long?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IobResult());

        _cobCalculatorMock
            .Setup(s => s.CalculateTotalAsync(
                It.IsAny<List<CarbIntake>>(),
                It.IsAny<List<Bolus>?>(),
                It.IsAny<List<TempBasal>?>(),
                It.IsAny<long>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CobResult());

        var controller = CreateController();

        // Act
        var result = await controller.GetRetrospectiveData(time);

        // Assert
        result.Result.Should().BeOfType<OkObjectResult>();

        var fromMills = time - (30 * 60 * 1000);
        var toMills = time + (5 * 60 * 1000);
        var expectedFind = $"{{\"mills\":{{\"$gte\":{fromMills},\"$lte\":{toMills}}}}}";

        _entryServiceMock.Verify(
            s => s.QueryEntriesAsync(
                It.Is<EntryQuery>(q => q.Type == "sgv" && q.Count == 50 && q.Skip == 0 && q.Find == expectedFind && !q.ReverseResults),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetRetrospectiveData_ShowsTheOverrideScaledRateAsBasal()
    {
        SetupTimelineDependencies();
        _iobCalculatorMock
            .Setup(s => s.CalculateTotalAsync(
                It.IsAny<List<Bolus>>(), It.IsAny<List<TempBasal>?>(), It.IsAny<long>(), It.IsAny<long?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IobResult());
        _cobCalculatorMock
            .Setup(s => s.CalculateTotalAsync(
                It.IsAny<List<CarbIntake>>(), It.IsAny<List<Bolus>?>(), It.IsAny<List<TempBasal>?>(),
                It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CobResult());
        var at = new DateTime(2024, 1, 15, 7, 0, 0, DateTimeKind.Utc);
        SetupOverride(at.AddHours(-1), at.AddHours(1), 0.5);
        using var cts = new CancellationTokenSource();

        var result = await CreateController().GetRetrospectiveData(new DateTimeOffset(at).ToUnixTimeMilliseconds(), cts.Token);

        var basal = ((RetrospectiveDataResponse)((OkObjectResult)result.Result!).Value!).Basal!;
        basal.Rate.Should().Be(0.4);
        basal.IsTemp.Should().BeFalse();
        VerifyOverridesReadOnce(cts.Token);
    }
}

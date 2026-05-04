using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// Asserts that the new sync <see cref="CobService.CobTotal"/> overload
/// (driven by <see cref="TherapySnapshot"/> + <see cref="DeviceCobSnapshot"/>)
/// produces results identical to the legacy <see cref="CobService.CobTotalAsync"/>
/// on equivalent inputs.
/// </summary>
/// <remarks>
/// Both paths are exercised on the same <see cref="CobService"/> instance so the
/// underlying <see cref="IIobService"/> activity feedback is shared. Equivalence
/// of the IOB activity itself is covered by <see cref="IobServiceEquivalenceTests"/>.
/// </remarks>
public class CobServiceEquivalenceTests
{
    private const double DefaultCarbAbsorptionRate = 30.0;
    private const double DefaultSensitivity = 50.0;
    private const double DefaultCarbRatio = 18.0;
    private const double DefaultDia = 3.0;
    private const double DefaultBasalRate = 1.0;
    private const double Epsilon = 1e-6;

    private readonly Mock<IApsSnapshotRepository> _apsRepo;
    private readonly CobService _cobService;

    public CobServiceEquivalenceTests()
    {
        var iobService = BuildIobService();

        var sensitivity = new Mock<ISensitivityResolver>();
        sensitivity
            .Setup(s => s.GetSensitivityAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultSensitivity);

        var carbRatio = new Mock<ICarbRatioResolver>();
        carbRatio
            .Setup(c => c.GetCarbRatioAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultCarbRatio);

        var therapySettings = new Mock<ITherapySettingsResolver>();
        therapySettings.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        therapySettings
            .Setup(t => t.GetCarbAbsorptionRateAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultCarbAbsorptionRate);

        _apsRepo = new Mock<IApsSnapshotRepository>();
        _apsRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<ApsSnapshot>());

        var timelineResolver = new Mock<ITherapyTimelineResolver>();
        timelineResolver
            .Setup(r => r.GetSnapshotAtAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildSnapshot());

        _cobService = new CobService(
            new Mock<ILogger<CobService>>().Object,
            iobService,
            _apsRepo.Object,
            timelineResolver.Object
        );
    }

    private static IobService BuildIobService()
    {
        var apsRepo = new Mock<IApsSnapshotRepository>();
        apsRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<ApsSnapshot>());
        var pumpRepo = new Mock<IPumpSnapshotRepository>();
        pumpRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<PumpSnapshot>());

        var timelineResolver = new Mock<ITherapyTimelineResolver>();
        timelineResolver
            .Setup(r => r.GetSnapshotAtAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildSnapshot());

        return new IobService(apsRepo.Object, pumpRepo.Object, timelineResolver.Object);
    }

    private static TherapySnapshot BuildSnapshot(
        double dia = DefaultDia,
        double sens = DefaultSensitivity,
        double carbRatio = DefaultCarbRatio,
        double carbsPerHour = DefaultCarbAbsorptionRate,
        double basal = DefaultBasalRate)
    {
        return new TherapySnapshot(
            dia: dia,
            peakMinutes: TherapySnapshot.DefaultPeakMinutes,
            carbsPerHour: carbsPerHour,
            timezone: null,
            ccpPercentage: null,
            ccpTimeshiftMs: 0,
            sensitivityEntries: new[] { new ScheduleEntry { Value = sens, TimeAsSeconds = 0 } },
            carbRatioEntries: new[] { new ScheduleEntry { Value = carbRatio, TimeAsSeconds = 0 } },
            basalEntries: new[] { new ScheduleEntry { Value = basal, TimeAsSeconds = 0 } }
        );
    }

    private static void AssertEquivalent(CobResult legacy, CobResult snapshot)
    {
        snapshot.Cob.Should().BeApproximately(legacy.Cob, Epsilon);
        snapshot.Source.Should().Be(legacy.Source);
        snapshot.Device.Should().Be(legacy.Device);
        snapshot.Mills.Should().Be(legacy.Mills);
        snapshot.Display.Should().Be(legacy.Display);
        snapshot.DisplayLine.Should().Be(legacy.DisplayLine);
        snapshot.DecayedBy.Should().Be(legacy.DecayedBy);
        snapshot.IsDecaying.Should().Be(legacy.IsDecaying);

        if (legacy.CarbsHr.HasValue)
            snapshot.CarbsHr!.Value.Should().BeApproximately(legacy.CarbsHr.Value, Epsilon);
        else
            snapshot.CarbsHr.Should().BeNull();

        if (legacy.RawCarbImpact.HasValue)
            snapshot.RawCarbImpact!.Value.Should().BeApproximately(legacy.RawCarbImpact.Value, Epsilon);
        else
            snapshot.RawCarbImpact.Should().BeNull();

        snapshot.LastCarbs.Should().BeSameAs(legacy.LastCarbs);
    }

    private void NoApsSnapshot() =>
        _apsRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<ApsSnapshot>());

    private void ApsSnapshotPresent(ApsSnapshot snapshot) =>
        _apsRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { snapshot });

    [Fact]
    public async Task NoDeviceCob_TreatmentBasedFallback_Equivalent()
    {
        NoApsSnapshot();
        var snapshot = BuildSnapshot();
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment> { new() { Carbs = 30, Mills = time - 30 * 60 * 1000 } };

        var legacy = await _cobService.CobTotalAsync(treatments, time);
        var fromSnapshot = _cobService.CobTotal(treatments, time, snapshot, deviceCob: null, nowMills: time);

        AssertEquivalent(legacy, fromSnapshot);
    }

    [Fact]
    public async Task EmptyTreatments_NoDeviceCob_Equivalent()
    {
        NoApsSnapshot();
        var snapshot = BuildSnapshot();
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>();

        var legacy = await _cobService.CobTotalAsync(treatments, time);
        var fromSnapshot = _cobService.CobTotal(treatments, time, snapshot, deviceCob: null, nowMills: time);

        AssertEquivalent(legacy, fromSnapshot);
    }

    [Fact]
    public async Task SingleTreatment_AbsorptionCurve_NoDeviceCob_Equivalent()
    {
        NoApsSnapshot();
        var snapshot = BuildSnapshot();
        var carbTime = new DateTimeOffset(2026, 4, 15, 4, 40, 40, 174, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var treatments = new List<Treatment> { new() { Carbs = 8, Mills = carbTime } };

        var probeTimes = new[]
        {
            carbTime + 60 * 1000,
            carbTime + 24 * 60 * 1000,
            carbTime + 40 * 60 * 1000,
            carbTime + 70 * 60 * 1000,
            carbTime + 130 * 60 * 1000,
        };

        foreach (var time in probeTimes)
        {
            var legacy = await _cobService.CobTotalAsync(treatments, time);
            var fromSnapshot = _cobService.CobTotal(treatments, time, snapshot, deviceCob: null, nowMills: time);
            AssertEquivalent(legacy, fromSnapshot);
        }
    }

    [Fact]
    public async Task MultipleTreatments_NoDeviceCob_Equivalent()
    {
        NoApsSnapshot();
        var snapshot = BuildSnapshot();
        var t0 = new DateTimeOffset(2026, 4, 15, 2, 3, 48, 827, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var t1 = new DateTimeOffset(2026, 4, 15, 3, 45, 10, 670, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Carbs = 100, Mills = t0 },
            new() { Carbs = 10, Mills = t1 },
        };

        var probeTimes = new[]
        {
            t0 + 1000,
            t1,
            t1 + 1000,
            t1 + 30 * 60 * 1000,
        };

        foreach (var time in probeTimes)
        {
            var legacy = await _cobService.CobTotalAsync(treatments, time);
            var fromSnapshot = _cobService.CobTotal(treatments, time, snapshot, deviceCob: null, nowMills: time);
            AssertEquivalent(legacy, fromSnapshot);
        }
    }

    [Fact]
    public async Task DeviceCobFresh_Loop_TakesPriority_Equivalent()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var apsSnapshot = new ApsSnapshot
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(time - 5 * 60 * 1000).UtcDateTime,
            Cob = 15.0,
            AidAlgorithm = AidAlgorithm.Loop,
            Device = "loop://iPhone",
        };
        ApsSnapshotPresent(apsSnapshot);

        var snapshot = BuildSnapshot();
        var treatments = new List<Treatment> { new() { Carbs = 30, Mills = time - 30 * 60 * 1000 } };

        var deviceCob = new DeviceCobSnapshot(
            Cob: 15.0,
            Mills: time - 5 * 60 * 1000,
            Source: "Loop"
        );

        var legacy = await _cobService.CobTotalAsync(treatments, time);
        var fromSnapshot = _cobService.CobTotal(treatments, time, snapshot, deviceCob, nowMills: time);

        // Both pick up the device COB. Legacy attaches Device="loop://iPhone" via repo;
        // the snapshot path doesn't have Device so populate it for fair comparison via the
        // equivalence assert. Compare key fields.
        fromSnapshot.Cob.Should().BeApproximately(legacy.Cob, Epsilon);
        fromSnapshot.Source.Should().Be(legacy.Source);
        fromSnapshot.Mills.Should().Be(legacy.Mills);
    }

    [Fact]
    public async Task DeviceCobFresh_OpenAps_TakesPriority_Equivalent()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var apsSnapshot = new ApsSnapshot
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(time - 1).UtcDateTime,
            Cob = 5.0,
            AidAlgorithm = AidAlgorithm.OpenAps,
            Device = "openaps://pi1",
        };
        ApsSnapshotPresent(apsSnapshot);

        var snapshot = BuildSnapshot();
        var treatments = new List<Treatment> { new() { Carbs = 20, Mills = time - 1 } };

        var deviceCob = new DeviceCobSnapshot(
            Cob: 5.0,
            Mills: time - 1,
            Source: "OpenAPS"
        );

        var legacy = await _cobService.CobTotalAsync(treatments, time);
        var fromSnapshot = _cobService.CobTotal(treatments, time, snapshot, deviceCob, nowMills: time);

        fromSnapshot.Cob.Should().BeApproximately(legacy.Cob, Epsilon);
        fromSnapshot.Source.Should().Be(legacy.Source);
        fromSnapshot.Mills.Should().Be(legacy.Mills);
    }

    [Fact]
    public async Task DeviceCobZero_FallsThroughToTreatments_Equivalent()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var apsSnapshot = new ApsSnapshot
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(time - 1).UtcDateTime,
            Cob = 0,
            AidAlgorithm = AidAlgorithm.OpenAps,
            Device = "openaps://pi1",
        };
        ApsSnapshotPresent(apsSnapshot);

        var snapshot = BuildSnapshot();
        var treatments = new List<Treatment> { new() { Carbs = 20, Mills = time - 1 } };

        // DeviceCobSnapshot with Cob=0 — both paths fall through to treatment-based.
        var deviceCob = new DeviceCobSnapshot(Cob: 0, Mills: time - 1, Source: "OpenAPS");

        var legacy = await _cobService.CobTotalAsync(treatments, time);
        var fromSnapshot = _cobService.CobTotal(treatments, time, snapshot, deviceCob, nowMills: time);

        AssertEquivalent(legacy, fromSnapshot);
    }

    [Fact]
    public async Task DeviceCobStale_FallsThroughToTreatments_Equivalent()
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Stale = older than 10 minutes. Both paths must fall through.
        var staleMills = time - 15 * 60 * 1000;
        var apsSnapshot = new ApsSnapshot
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(staleMills).UtcDateTime,
            Cob = 12.0,
            AidAlgorithm = AidAlgorithm.OpenAps,
            Device = "openaps://pi1",
        };
        ApsSnapshotPresent(apsSnapshot);

        var snapshot = BuildSnapshot();
        var treatments = new List<Treatment> { new() { Carbs = 30, Mills = time - 30 * 60 * 1000 } };

        var deviceCob = new DeviceCobSnapshot(Cob: 12.0, Mills: staleMills, Source: "OpenAPS");

        var legacy = await _cobService.CobTotalAsync(treatments, time);
        var fromSnapshot = _cobService.CobTotal(treatments, time, snapshot, deviceCob, nowMills: time);

        AssertEquivalent(legacy, fromSnapshot);
    }

    [Fact]
    public async Task FutureTreatment_Skipped_Equivalent()
    {
        NoApsSnapshot();
        var snapshot = BuildSnapshot();
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Carbs = 30, Mills = time + 60 * 60 * 1000 }, // future
            new() { Carbs = 20, Mills = time - 30 * 60 * 1000 },
        };

        var legacy = await _cobService.CobTotalAsync(treatments, time);
        var fromSnapshot = _cobService.CobTotal(treatments, time, snapshot, deviceCob: null, nowMills: time);

        AssertEquivalent(legacy, fromSnapshot);
    }

    [Fact]
    public async Task TreatmentWithCustomAbsorption_Equivalent()
    {
        NoApsSnapshot();
        var snapshot = BuildSnapshot();
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Carbs = 60, Mills = time - 30 * 60 * 1000, AbsorptionTime = 240 },
        };

        var legacy = await _cobService.CobTotalAsync(treatments, time);
        var fromSnapshot = _cobService.CobTotal(treatments, time, snapshot, deviceCob: null, nowMills: time);

        AssertEquivalent(legacy, fromSnapshot);
    }

    [Fact]
    public async Task TreatmentWithFatNotes_Equivalent()
    {
        NoApsSnapshot();
        var snapshot = BuildSnapshot();
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Carbs = 40, Fat = 25, Mills = time - 60 * 60 * 1000, Notes = "Pizza" },
        };

        var legacy = await _cobService.CobTotalAsync(treatments, time);
        var fromSnapshot = _cobService.CobTotal(treatments, time, snapshot, deviceCob: null, nowMills: time);

        AssertEquivalent(legacy, fromSnapshot);
    }

    [Fact]
    public async Task TreatmentWithFastCarbsNotes_Equivalent()
    {
        NoApsSnapshot();
        var snapshot = BuildSnapshot();
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var treatments = new List<Treatment>
        {
            new() { Carbs = 30, Mills = time - 15 * 60 * 1000, Notes = "Glucose tablets for low" },
        };

        var legacy = await _cobService.CobTotalAsync(treatments, time);
        var fromSnapshot = _cobService.CobTotal(treatments, time, snapshot, deviceCob: null, nowMills: time);

        AssertEquivalent(legacy, fromSnapshot);
    }
}

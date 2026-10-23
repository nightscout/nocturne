using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Clinical.Treatments;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

public class CurrentIobCobReaderTests
{
    private readonly List<ApsSnapshot> _snapshots = [];
    private readonly Mock<IBolusRepository> _boluses = new();
    private readonly Mock<ITempBasalRepository> _tempBasals = new();
    private readonly Mock<ICarbIntakeRepository> _carbs = new();
    private readonly List<Bolus> _storedBoluses = [];
    private readonly List<CarbIntake> _storedCarbs = [];

    public CurrentIobCobReaderTests()
    {
        _boluses
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<BolusKind?>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _storedBoluses.ToList());
        _tempBasals
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<TempBasal>());
        _carbs
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _storedCarbs.ToList());
    }

    private CurrentIobCobReader NewReader(string? partition = "full")
    {
        var aps = new Mock<IApsSnapshotRepository>();
        aps.Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime? from, DateTime? to, string? _, string? _, int limit, int _, bool _, CancellationToken _) =>
                _snapshots
                    .Where(s => s.Timestamp >= from && s.Timestamp <= to)
                    .OrderByDescending(s => s.Timestamp)
                    .Take(limit)
                    .ToList()
                    .AsEnumerable());
        var pump = new Mock<IPumpSnapshotRepository>();
        pump.Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<PumpSnapshot>());

        var therapy = new Mock<ITherapySettingsResolver>();
        therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        therapy.Setup(t => t.GetDIAAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(4.0);
        therapy.Setup(t => t.GetCarbAbsorptionRateAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(30.0);
        var sensitivity = new Mock<ISensitivityResolver>();
        sensitivity.Setup(s => s.GetSensitivityAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(50.0);
        var carbRatio = new Mock<ICarbRatioResolver>();
        carbRatio.Setup(c => c.GetCarbRatioAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(10.0);
        var basal = new Mock<IBasalRateResolver>();
        basal.Setup(b => b.GetBasalRateAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(1.0);

        var iob = new IobCalculator(therapy.Object, sensitivity.Object, basal.Object, aps.Object, pump.Object);
        var cob = new CobCalculator(
            NullLogger<CobCalculator>.Instance, iob, sensitivity.Object, carbRatio.Object, therapy.Object, aps.Object);

        return new CurrentIobCobReader(
            iob, cob, _boluses.Object, _tempBasals.Object, _carbs.Object,
            TestTenantCache.Create(partition: partition));
    }

    private void Snapshot(AidAlgorithm algorithm, int minutesAgo, double? iob, double? cob) =>
        _snapshots.Add(new ApsSnapshot
        {
            AidAlgorithm = algorithm,
            Timestamp = DateTime.UtcNow.AddMinutes(-minutesAgo),
            Iob = iob,
            Cob = cob,
        });

    private void VerifyBolusReads(Times times) =>
        _boluses.Verify(
            r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<BolusKind?>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            times);

    [Theory]
    [InlineData(AidAlgorithm.AndroidAps)]
    [InlineData(AidAlgorithm.Trio)]
    [InlineData(AidAlgorithm.OpenAps)]
    public async Task RecentOrefSnapshot_SuppliesIobAndCob(AidAlgorithm algorithm)
    {
        Snapshot(algorithm, 5, iob: 1.254, cob: 23);

        var current = await NewReader().GetAsync();

        current.Iob.Should().Be(1.25);
        current.IobDisplay.Should().Be("1.25");
        current.IobSource.Should().Be("OpenAPS");
        current.Cob.Should().Be(23);
        current.CobSource.Should().Be("OpenAPS");
    }

    [Fact]
    public async Task RecentLoopSnapshot_SuppliesItsValuesAndNamesLoop()
    {
        Snapshot(AidAlgorithm.Loop, 3, iob: 0.8, cob: 12);

        var current = await NewReader().GetAsync();

        current.Iob.Should().Be(0.8);
        current.IobSource.Should().Be("Loop");
        current.Cob.Should().Be(12);
        current.CobSource.Should().Be("Loop");
    }

    [Fact]
    public async Task SnapshotOlderThanThirtyMinutes_FallsBackToTreatments()
    {
        Snapshot(AidAlgorithm.AndroidAps, 45, iob: 9.99, cob: 99);
        _storedBoluses.Add(new Bolus { Timestamp = DateTime.UtcNow.AddMinutes(-30), Insulin = 2 });

        var current = await NewReader().GetAsync();

        current.IobSource.Should().NotBe("OpenAPS");
        current.Iob.Should().BeInRange(0.01, 2);
        current.Cob.Should().Be(0);
    }

    [Fact]
    public async Task NoSnapshotAndNoTreatments_IsZeroWithNoSource()
    {
        var current = await NewReader().GetAsync();

        current.Iob.Should().Be(0);
        current.IobDisplay.Should().Be("0");
        current.IobSource.Should().BeNull();
        current.Cob.Should().Be(0);
    }

    [Fact]
    public async Task NegativeIob_KeepsTwoDecimalsAsNightscoutShowsIt()
    {
        Snapshot(AidAlgorithm.AndroidAps, 5, iob: -0.3, cob: 0);

        var current = await NewReader().GetAsync();

        current.IobDisplay.Should().Be("-0.30");
    }

    [Fact]
    public async Task FractionalCob_IsRoundedToOneDecimalAsNightscoutShowsIt()
    {
        Snapshot(AidAlgorithm.AndroidAps, 5, iob: 1, cob: 17.8333333);

        var current = await NewReader().GetAsync();

        current.Cob.Should().Be(17.8);
    }

    [Fact]
    public async Task SecondCallWithinAMinute_IsServedFromTheCache()
    {
        var reader = NewReader();

        await reader.GetAsync();
        await reader.GetAsync();

        VerifyBolusReads(Times.Once());
    }

    [Fact]
    public async Task ReaderWithoutAPartition_NeverTouchesTheCache()
    {
        var reader = NewReader(partition: null);

        await reader.GetAsync();
        await reader.GetAsync();

        VerifyBolusReads(Times.Exactly(2));
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Models.Widget;
using Nocturne.Infrastructure.Data.Abstractions;
using Xunit;

namespace Nocturne.API.Tests.Services.Analytics;

[Trait("Category", "Unit")]
public class WidgetSummaryServiceBasalInjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<ITempBasalRepository> _tempBasals = new();
    private readonly Mock<IBasalInjectionRepository> _injections = new();

    private static BasalInjection Dose(double hoursAgo, double units, string? insulin = "Tresiba", double dia = 24) =>
        new()
        {
            Timestamp = Now.UtcDateTime.AddHours(-hoursAgo),
            Units = units,
            InsulinContext = insulin is null ? null : new TreatmentInsulinContext { InsulinName = insulin, Dia = dia },
        };

    private static TempBasal Temp(double hoursAgo, double minutes = 30) =>
        new()
        {
            StartTimestamp = Now.UtcDateTime.AddHours(-hoursAgo),
            EndTimestamp = Now.UtcDateTime.AddHours(-hoursAgo).AddMinutes(minutes),
            Rate = 1,
        };

    private void Injections(params BasalInjection[] doses) =>
        _injections
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime? from, DateTime? to, string? _, string? _, int _, int _, bool _, CancellationToken _) =>
                doses.Where(d => d.Timestamp >= from && d.Timestamp <= to).ToList());

    private void Temps(params TempBasal[] temps) =>
        _tempBasals
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime? from, DateTime? to, string? _, string? _, int _, int _, bool _, CancellationToken _) =>
                temps.Where(t => t.StartTimestamp >= from && t.StartTimestamp <= to).ToList());

    private async Task<V4BasalInjectionStatus?> BasalAsync()
    {
        var time = new FakeTimeProvider(Now);
        var entries = new Mock<IEntryService>();
        entries
            .Setup(e => e.GetEntriesAsync(
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var trackers = new Mock<ITrackerRepository>();
        trackers.Setup(t => t.GetActiveInstancesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var service = new WidgetSummaryService(
            entries.Object,
            TestIobCobReader.Empty(),
            InsulinDeliveryReaders.Over(_tempBasals.Object, _injections.Object, time),
            Mock.Of<IApsSnapshotRepository>(),
            trackers.Object,
            Mock.Of<INotificationV1Service>(),
            Mock.Of<IGlucoseStatusClassifier>(),
            Mock.Of<IAlertRepository>(),
            Mock.Of<ICategoryReadContext>(),
            time,
            NullLogger<WidgetSummaryService>.Instance);

        return (await service.GetSummaryAsync()).BasalInjection;
    }

    [Fact]
    public async Task ASteadyDailyDose_ReadsAsItsDoseOverTwentyFourHours()
    {
        Injections(Dose(33, 18), Dose(9, 18));

        var basal = await BasalAsync();

        basal.Should().NotBeNull();
        basal!.Units.Should().Be(18);
        basal.Mills.Should().Be(Now.AddHours(-9).ToUnixTimeMilliseconds());
        basal.InsulinName.Should().Be("Tresiba");
        basal.EstimatedRate.Should().Be(0.75);
    }

    [Fact]
    public async Task ALaterDose_IsTheLastDoseAndSetsTheRate()
    {
        Injections(Dose(9, 18), Dose(3, 12));

        var basal = await BasalAsync();

        basal!.Units.Should().Be(12);
        basal.EstimatedRate.Should().Be(0.5, "12 U lasts 24 hours from three hours ago");
    }

    [Fact]
    public async Task ADoseWhoseCoverHasRunOut_HasNoEstimatedRate()
    {
        Injections(Dose(30, 18));

        var basal = await BasalAsync();

        basal.Should().NotBeNull();
        basal!.Units.Should().Be(18);
        basal.EstimatedRate.Should().BeNull("no dose covers now once the 24 hour cap has passed");
    }

    [Fact]
    public async Task ADoseOfALongerActingInsulin_SpreadsOverItsDia()
    {
        Injections(Dose(9, 21, dia: 42));

        (await BasalAsync())!.EstimatedRate.Should().Be(0.5);
    }

    [Fact]
    public async Task ADoseWithoutAnInsulin_HasNoInsulinName()
    {
        Injections(Dose(9, 18, insulin: null));

        (await BasalAsync())!.InsulinName.Should().BeNull();
    }

    [Fact]
    public async Task ADoseWithAShortDia_LastsNoLongerThanIt()
    {
        Injections(Dose(9, 18, dia: 12));

        (await BasalAsync())!.EstimatedRate.Should().Be(1.5);
    }

    [Fact]
    public async Task DosesAtTheSameInstant_AreOneDose()
    {
        Injections(Dose(9, 10), Dose(9, 8));

        var basal = await BasalAsync();

        basal!.Units.Should().Be(18);
        basal.EstimatedRate.Should().Be(0.75);
    }

    [Fact]
    public async Task ATenantWithNoInjections_HasNoBasalEntry()
    {
        Temps(Temp(1));

        (await BasalAsync()).Should().BeNull();
    }

    [Fact]
    public async Task ATenantWithARecentTempBasal_HasNoBasalEntryWhateverItInjects()
    {
        Injections(Dose(9, 18));
        Temps(Temp(2));

        (await BasalAsync()).Should().BeNull("a pump reports the basal");
    }

    [Fact]
    public async Task ATempBasalOlderThanADay_DoesNotMakeATenantAPumpUser()
    {
        Injections(Dose(9, 18));
        Temps(Temp(30));

        (await BasalAsync()).Should().NotBeNull();
    }

    [Fact]
    public async Task ADoseInTheFuture_IsNotTheLastDose()
    {
        Injections(Dose(9, 18), Dose(-2, 99));

        (await BasalAsync())!.Units.Should().Be(18);
    }
}

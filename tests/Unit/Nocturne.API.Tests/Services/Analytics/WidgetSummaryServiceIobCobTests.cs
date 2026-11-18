using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Models.Widget;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Services.Analytics;

[Trait("Category", "Unit")]
public class WidgetSummaryServiceIobCobTests
{
    private readonly DbContextOptions<NocturneDbContext> _options =
        new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    [Fact]
    public async Task IobAndCob_fromACalculation_areKnown()
    {
        var summary = await Summary(Reading(new IobResult { Iob = 1.234 }, new CobResult { Cob = 12.4 }, insulin: true, carbs: true));

        summary.Iob.Should().Be(1.23);
        summary.IobKnown.Should().BeTrue();
        summary.Cob.Should().Be(12);
        summary.CobKnown.Should().BeTrue();
    }

    [Fact]
    public async Task ARealZeroCob_isKnown()
    {
        var summary = await Summary(Reading(new IobResult { Iob = 0.5 }, new CobResult { Cob = 0 }, insulin: true, carbs: true));

        summary.Cob.Should().Be(0);
        summary.CobKnown.Should().BeTrue();
    }

    [Fact]
    public async Task ARealZeroIob_withInsulinInTheWindow_isKnown()
    {
        var summary = await Summary(Reading(new IobResult { Iob = 0 }, new CobResult(), insulin: true));

        summary.Iob.Should().Be(0);
        summary.IobKnown.Should().BeTrue();
    }

    [Fact]
    public async Task Iob_withNoInsulinInTheWindowAndNoDevice_isUnknown()
    {
        var summary = await Summary(Reading(new IobResult { Iob = 0 }, new CobResult(), insulin: false));

        summary.Iob.Should().Be(0);
        summary.IobKnown.Should().BeFalse();
    }

    [Fact]
    public async Task Cob_withNoCarbsInTheWindowAndNoDevice_isUnknown()
    {
        var summary = await Summary(Reading(new IobResult { Iob = 1 }, new CobResult { Cob = 0 }, insulin: true));

        summary.Cob.Should().Be(0);
        summary.CobKnown.Should().BeFalse();
        summary.IobKnown.Should().BeTrue();
    }

    [Fact]
    public async Task Cob_reportedByADevice_isKnownWithNoCarbsInTheWindow()
    {
        var summary = await Summary(
            Reading(new IobResult(), new CobResult { Cob = 9, ReportedByDevice = true }, insulin: false));

        summary.Cob.Should().Be(9);
        summary.CobKnown.Should().BeTrue();
    }
    [Fact]
    public async Task Iob_reportedByADevice_isKnownWithNoInsulinInTheWindow()
    {
        var summary = await Summary(
            Reading(new IobResult { Iob = 2.5, ReportedByDevice = true }, new CobResult(), insulin: false));

        summary.Iob.Should().Be(2.5);
        summary.IobKnown.Should().BeTrue();
    }

    [Fact]
    public async Task Cob_whenItsCalculationFailed_isUnknownAndTheIobIsKept()
    {
        var summary = await Summary(Reading(new IobResult { Iob = 1 }, cob: null, insulin: true));

        summary.Cob.Should().Be(0);
        summary.CobKnown.Should().BeFalse();
        summary.Iob.Should().Be(1);
        summary.IobKnown.Should().BeTrue();
    }

    [Fact]
    public async Task IobAndCob_whenTheReadThrows_areUnknown()
    {
        var reader = new Mock<IIobCobReader>();
        reader
            .Setup(r => r.ReadTotalAsync(It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var summary = await NewService(reader.Object).GetSummaryAsync();

        summary.Iob.Should().Be(0);
        summary.IobKnown.Should().BeFalse();
        summary.Cob.Should().Be(0);
        summary.CobKnown.Should().BeFalse();
    }

    private static IobCobReading Reading(IobResult iob, CobResult? cob, bool insulin, bool carbs = false) =>
        new(iob, cob, new IobCobInputs([], [], carbs ? [new CarbIntake()] : []))
        {
            InsulinInWindow = insulin,
        };

    private Task<V4SummaryResponse> Summary(IobCobReading reading)
    {
        var reader = new Mock<IIobCobReader>();
        reader
            .Setup(r => r.ReadTotalAsync(It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(reading);
        return NewService(reader.Object).GetSummaryAsync();
    }

    private WidgetSummaryService NewService(IIobCobReader iobCobReader)
    {
        var entries = new Mock<IEntryService>();
        entries.Setup(e => e.GetEntriesAsync(
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var trackers = new Mock<ITrackerRepository>();
        trackers.Setup(t => t.GetActiveInstancesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var contextFactory = new Mock<IDbContextFactory<NocturneDbContext>>();
        contextFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(new NocturneDbContext(_options) { TenantId = Guid.NewGuid() }));

        return new WidgetSummaryService(
            entries.Object,
            iobCobReader,
            InsulinDeliveryReaders.Over(),
            Mock.Of<IApsSnapshotRepository>(),
            trackers.Object,
            Mock.Of<INotificationV1Service>(),
            new GlucoseStatusClassifier(
                new ConfigurationBuilder().Build(), NullLogger<GlucoseStatusClassifier>.Instance),
            new AlertRepository(contextFactory.Object.AsNocturneFactory()),
            Mock.Of<ICategoryReadContext>(),
            TimeProvider.System, NullLogger<WidgetSummaryService>.Instance);
    }
}

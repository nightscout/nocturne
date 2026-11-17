using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Helpers;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// <see cref="TreatmentReadService"/> under the legacy 4-day default window a v1 treatment find with
/// field filters carries (<see cref="LegacyFindDateWindow"/>).
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentReadServiceFindWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly long WindowStartMills = Now.AddDays(-4).ToUnixTimeMilliseconds();

    [Fact]
    public async Task ReadService_PushesWindowDownToProjection()
    {
        var projection = new Mock<IV4ToLegacyProjectionService>();
        projection
            .Setup(p => p.GetProjectedTreatmentsAsync(
                It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Treatment>());

        await NewReadService(projection.Object, NullLogger<TreatmentReadService>.Instance).QueryAsync(
            new TreatmentQuery
            {
                Find = LegacyFindDateWindow.Apply("find[id][$eq]=abc", Now),
                Count = int.MaxValue,
            });

        projection.Verify(p => p.GetProjectedTreatmentsAsync(
            WindowStartMills, null, It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task FilteredCount_LogsWhenItHitsTheFetchCap()
    {
        const int maxFilterFetch = 3;
        var projected = Enumerable.Range(0, maxFilterFetch)
            .Select(i => new Treatment { EventType = "Note", Mills = i })
            .ToList();
        var projection = new Mock<IV4ToLegacyProjectionService>();
        projection
            .Setup(p => p.GetProjectedTreatmentsAsync(
                It.IsAny<long?>(), It.IsAny<long?>(), maxFilterFetch, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(projected);
        var logger = new Mock<ILogger<TreatmentReadService>>();

        var service = NewReadService(projection.Object, logger.Object);
        service.MaxFilterFetch = maxFilterFetch;

        await service.CountAsync("{\"eventType\":\"Temp Basal\"}");

        logger.Verify(l => l.Log(
            LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    private static TreatmentReadService NewReadService(
        IV4ToLegacyProjectionService projection, ILogger<TreatmentReadService> logger) =>
        new(projection, Mock.Of<ITreatmentDecomposer>(),
            Mock.Of<ITempBasalRepository>(), Mock.Of<IBolusRepository>(), Mock.Of<ICarbIntakeRepository>(),
            Mock.Of<IBGCheckRepository>(), Mock.Of<INoteRepository>(), Mock.Of<IDeviceEventRepository>(),
            Mock.Of<IBolusCalculationRepository>(), Mock.Of<IProfileActivationRepository>(), Mock.Of<IStateSpanService>(), logger);
}

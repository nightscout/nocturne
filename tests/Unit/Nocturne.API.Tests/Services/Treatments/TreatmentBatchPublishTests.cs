using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Nocturne.API.Services.ConnectorPublishing;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Connectors.Core.Models;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// A connector's treatment publish through the real service and store: a failure of the database
/// ends the batch as a failed publish, and a defect reaches the connector run.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentBatchPublishTests
{
    private readonly Mock<IPerTreatmentDecomposer> _decomposer = new Mock<IPerTreatmentDecomposer>().AnswersBatchPerTreatment();
    private readonly TreatmentPublisher _publisher;

    public TreatmentBatchPublishTests()
    {
        _decomposer
            .Setup(d => d.ResolveStoredIdentitiesAsync(It.IsAny<IReadOnlyList<Treatment>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Treatment>());
        _decomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<Treatment>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var result = new DecompositionResult();
                result.CreatedRecords.Add(new Note { Id = Guid.CreateVersion7() });
                return result;
            });

        var store = new TreatmentReadService(
            Mock.Of<IV4ToLegacyProjectionService>(),
            _decomposer.Object,
            Mock.Of<ITempBasalRepository>(),
            Mock.Of<IBolusRepository>(),
            Mock.Of<ICarbIntakeRepository>(),
            Mock.Of<IBGCheckRepository>(),
            Mock.Of<INoteRepository>(),
            Mock.Of<IDeviceEventRepository>(),
            Mock.Of<IBolusCalculationRepository>(),
            Mock.Of<IProfileActivationRepository>(),
            Mock.Of<IStateSpanService>(),
            NullLogger<TreatmentReadService>.Instance);
        var service = new TreatmentService(
            store,
            _decomposer.Object,
            Mock.Of<ITreatmentCache>(),
            Mock.Of<IDataEventSink<Treatment>>(),
            Mock.Of<IPatientInsulinRepository>(),
            NullLogger<TreatmentService>.Instance);

        _publisher = new TreatmentPublisher(
            service,
            Mock.Of<IBolusRepository>(),
            Mock.Of<ICarbIntakeRepository>(),
            Mock.Of<IBGCheckRepository>(),
            Mock.Of<IBolusCalculationRepository>(),
            Mock.Of<ITempBasalRepository>(),
            Mock.Of<IBasalInjectionRepository>(),
            Mock.Of<INoteRepository>(),
            Mock.Of<IDeviceEventRepository>(),
            Mock.Of<IPatientInsulinRepository>(),
            Mock.Of<IBasalRateResolver>(),
            Mock.Of<ITherapySettingsResolver>(),
            Mock.Of<IPatientDeviceStamper>(),
            Mock.Of<IAuditContext>(),
            new PublishSkipTally(),
            new SettableTenantAccessor(),
            NullLogger<TreatmentPublisher>.Instance);
    }

    private static List<Treatment> Batch() =>
    [
        new() { Id = "t1", EventType = "Note", Notes = "first", Mills = 1000 },
        new() { Id = "t2", EventType = "Note", Notes = "second", Mills = 2000 },
    ];

    private void FailsTheSecond(Exception failure) =>
        _decomposer
            .Setup(d => d.DecomposeAsync(
                It.Is<Treatment>(t => t.Notes == "second"), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

    [Fact]
    public async Task PublishTreatmentsAsync_WhenTheStoreFailsPartWayThroughABatch_AnswersAFailedPublish()
    {
        FailsTheSecond(new DbUpdateException("save failed", new NpgsqlException("connection refused")));

        var published = await _publisher.PublishTreatmentsAsync(Batch(), "test-source", WriteOrigin.Live);

        published.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(DefectsInABatch))]
    public async Task PublishTreatmentsAsync_WhenAWriteHitsADefect_PropagatesIt(Exception defect)
    {
        FailsTheSecond(defect);

        var act = () => _publisher.PublishTreatmentsAsync(Batch(), "test-source", WriteOrigin.Live);

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(defect);
    }

    public static TheoryData<Exception> DefectsInABatch => new()
    {
        new InvalidOperationException("a second operation was started on this context"),
        new ObjectDisposedException("NocturneDbContext"),
    };
}

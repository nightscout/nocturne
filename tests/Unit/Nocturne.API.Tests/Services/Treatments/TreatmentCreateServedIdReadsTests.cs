using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// A client re-sending treatments it read back (AAPS's nightscoutId, Loop's objectIdCache) sends the
/// ids reads served, which a create keys to the stored records with the same reads however many it sends.
/// </summary>
public class TreatmentCreateServedIdReadsTests
{
    private readonly List<Mock> _stores = [];

    [Fact]
    public async Task AResendOfFiftyServedIdsReadsWhatAResendOfOneReads()
    {
        var one = await ReadsAsync(1);
        var fifty = await ReadsAsync(50);

        fifty.Should().Be(one);
        one.Should().Be(LegacyTreatmentTables.ByServedPrecedence.Count + 1,
            "each table is read once for every served id, and the state spans once for those no record holds");
    }

    private async Task<int> ReadsAsync(int count)
    {
        _stores.Clear();
        var projection = Store<IV4ToLegacyProjectionService>();
        projection.Setup(p => p.GetStateSpanTreatmentIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        var service = new TreatmentReadService(
            projection.Object,
            new Mock<IPerTreatmentDecomposer>().AnswersBatchPerTreatment().Object,
            Table<ITempBasalRepository, TempBasal>().Object,
            Table<IBolusRepository, Bolus>().Object,
            Table<ICarbIntakeRepository, CarbIntake>().Object,
            Table<IBGCheckRepository, BGCheck>().Object,
            Table<INoteRepository, Note>().Object,
            Table<IDeviceEventRepository, DeviceEvent>().Object,
            Table<IBolusCalculationRepository, BolusCalculation>().Object,
            Table<IProfileActivationRepository, ProfileActivation>().Object,
            Mock.Of<IStateSpanService>(),
            NullLogger<TreatmentReadService>.Instance);

        var resent = Enumerable.Range(0, count)
            .Select(i => new Treatment { Id = MongoObjectId.FromGuid(Guid.CreateVersion7()), EventType = "Note", Notes = $"n{i}", Mills = 1_780_000_000_000 + i })
            .ToList();
        await service.CreateAsync(resent, WriteOrigin.Live);

        return _stores.Sum(s => s.Invocations.Count);
    }

    private Mock<T> Store<T>() where T : class
    {
        var mock = new Mock<T>();
        _stores.Add(mock);
        return mock;
    }

    private Mock<TRepository> Table<TRepository, TRecord>()
        where TRepository : class, ILegacyKeyedRepository<TRecord>
        where TRecord : class, IV4Record
    {
        var mock = Store<TRepository>();
        mock.As<ILegacyKeyedRepository<TRecord>>()
            .Setup(r => r.GetByServedIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return mock;
    }
}

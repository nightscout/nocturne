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
/// The response a create names each treatment by is read the same way.
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

    [Fact]
    public async Task ACreateOfFiftyCarbOnlyTreatmentsReadsWhatACreateOfOneReads()
    {
        var one = (await CreateCarbsAsync(1)).Reads;
        var fifty = await CreateCarbsAsync(50);

        fifty.Reads.Should().Be(one);
        fifty.Created.Should().HaveCount(50);
    }

    [Fact]
    public async Task ACreateOfCarbsNamesEachByTheBolusItIsServedWith()
    {
        var created = await CreateCarbsAsync(3);

        created.Created.Select(c => c.Id).Should().Equal(created.Served.Select(b => b.Id.ToString()));
        created.Stored.OfType<StoredTreatment.Named>().Select(n => n.Mills)
            .Should().Equal(created.Served.Select(b => b.Mills));
    }

    [Fact]
    public async Task ACreateOfFiftyOverridesReadsWhatACreateOfOneReads()
    {
        var one = (await CreateOverridesAsync(1)).Reads;
        var fifty = await CreateOverridesAsync(50);

        fifty.Reads.Should().Be(one);
        fifty.Created.Select(c => c.Id).Should().Equal(fifty.Spans.Values.Select(s => s.Id));
    }

    private async Task<int> ReadsAsync(int count)
    {
        var resent = Enumerable.Range(0, count)
            .Select(i => new Treatment { Id = MongoObjectId.FromGuid(Guid.CreateVersion7()), EventType = "Note", Notes = $"n{i}", Mills = 1_780_000_000_000 + i })
            .ToList();
        return (await CreateAsync(resent, _ => new DecompositionResult())).Reads;
    }

    private async Task<(int Reads, IReadOnlyList<Treatment> Created, IReadOnlyList<StoredTreatment> Stored, List<Bolus> Served)> CreateCarbsAsync(int count)
    {
        var served = new List<Bolus>();
        var meals = new List<IV4Record>();
        var treatments = Enumerable.Range(0, count)
            .Select(i => new Treatment { Id = $"carb-{i}", EventType = "Carb Correction", Carbs = 20, Mills = 1_780_000_000_000 + i })
            .ToList();
        var result = await CreateAsync(treatments, treatment =>
        {
            var correlationId = Guid.CreateVersion7();
            var carb = new CarbIntake { Id = Guid.CreateVersion7(), CorrelationId = correlationId, Carbs = 20, LegacyId = treatment.Id };
            var bolus = new Bolus { Id = Guid.CreateVersion7(), CorrelationId = correlationId, Insulin = 2, Timestamp = DateTime.UnixEpoch.AddMilliseconds(treatment.Mills + 5) };
            served.Add(bolus);
            meals.Add(bolus);
            meals.Add(carb);
            var decomposed = new DecompositionResult { CorrelationId = correlationId };
            decomposed.CreatedRecords.Add(carb);
            return decomposed;
        }, meals: meals);
        return (result.Reads, result.Created, result.Stored, served);
    }

    private async Task<(int Reads, IReadOnlyList<Treatment> Created, Dictionary<string, Treatment> Spans)> CreateOverridesAsync(int count)
    {
        var treatments = Enumerable.Range(0, count)
            .Select(i => new Treatment { Id = $"override-{i}", EventType = "Temporary Override", Duration = 30, Mills = 1_780_000_000_000 + i })
            .ToList();
        var spans = treatments.ToDictionary(
            t => t.Id!, t => new Treatment { Id = Guid.CreateVersion7().ToString(), EventType = t.EventType, Mills = t.Mills });
        var result = await CreateAsync(treatments, treatment =>
        {
            var decomposed = new DecompositionResult { CorrelationId = Guid.CreateVersion7() };
            decomposed.CreatedRecords.Add(new StateSpan { OriginalId = treatment.Id, Category = StateSpanCategory.Override });
            return decomposed;
        }, spans: spans);
        return (result.Reads, result.Created, spans);
    }

    private async Task<(int Reads, IReadOnlyList<Treatment> Created, IReadOnlyList<StoredTreatment> Stored)> CreateAsync(
        IReadOnlyList<Treatment> treatments, Func<Treatment, DecompositionResult> decompose,
        List<IV4Record>? meals = null, IReadOnlyDictionary<string, Treatment>? spans = null)
    {
        _stores.Clear();
        var decomposer = new Mock<IPerTreatmentDecomposer>().AnswersBatchPerTreatment();
        decomposer.Setup(d => d.DecomposeAsync(It.IsAny<Treatment>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Treatment treatment, WriteOrigin _, CancellationToken _) => decompose(treatment));
        var projection = Store<IV4ToLegacyProjectionService>();
        projection.Setup(p => p.GetStateSpanTreatmentIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        projection.Setup(p => p.GetProjectedStateSpanTreatmentsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<string> keys, CancellationToken _) =>
                (IReadOnlyDictionary<string, Treatment>)keys.ToDictionary(k => k, k => spans![k]));
        var bolusRepository = Table<IBolusRepository, Bolus>();
        bolusRepository.Setup(r => r.GetByCorrelationIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Guid> ids, CancellationToken _) => meals?.OfType<Bolus>().Where(b => ids.Contains(b.CorrelationId!.Value)) ?? []);
        var carbRepository = Table<ICarbIntakeRepository, CarbIntake>();
        carbRepository.Setup(r => r.GetByCorrelationIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Guid> ids, CancellationToken _) => meals?.OfType<CarbIntake>().Where(c => ids.Contains(c.CorrelationId!.Value)) ?? []);
        var service = new TreatmentReadService(
            projection.Object,
            decomposer.Object,
            Table<ITempBasalRepository, TempBasal>().Object,
            bolusRepository.Object,
            carbRepository.Object,
            Table<IBGCheckRepository, BGCheck>().Object,
            Table<INoteRepository, Note>().Object,
            Table<IDeviceEventRepository, DeviceEvent>().Object,
            Table<IBolusCalculationRepository, BolusCalculation>().Object,
            Table<IProfileActivationRepository, ProfileActivation>().Object,
            Mock.Of<IStateSpanService>(),
            NullLogger<TreatmentReadService>.Instance);

        var stored = new List<StoredTreatment>();
        var created = await service.CreateAsync(treatments, WriteOrigin.Live, CancellationToken.None, stored);

        return (_stores.Sum(s => s.Invocations.Count), [.. created], stored);
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

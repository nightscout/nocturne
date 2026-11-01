using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.ProfileActivations;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.V4;

/// <summary>
/// A treatment batch costs the same calls on its stores whatever its size: every dependency is a
/// double that answers the set-based forms, and the calls a batch of one and a batch of fifty make
/// are counted.
/// </summary>
/// <remarks>
/// A mock-level count cannot see a store's own statements; the set-based reads are capped per call
/// against PostgreSQL in <c>TreatmentBatchReadsTests</c>. Two writes are one per record, and no batch
/// here makes them: the drops of a reclassified treatment's spans, since a resend that changes a
/// treatment's kind is rare; and keying a meal's carbs stored with no legacy id to its bolus, which
/// only a meal entered in Nocturne and backfilled needs.
/// </remarks>
public class TreatmentDecomposerFixedQueryTests : IDisposable
{
    private const long Start = 1_780_000_000_000;

    private readonly NocturneDbContext _context = TestDbContextFactory.CreateInMemoryContext();
    private readonly List<Mock> _doubles = [];
    private int _contextOpens;

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    public static TheoryData<string> Kinds() =>
    [
        "Bolus Wizard", "Meal Bolus", "Carb Correction", "BG Check", "Note", "Temp Basal", "Percent Temp Basal",
        "Profile Switch", "Temporary Override", "Temporary Target", "Pump Suspend", "Site Change", "Mixed",
    ];

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ABatchOfFiftyCostsWhatABatchOfOneCosts(string kind)
    {
        var one = await CallsAsync(Batch(kind, kind == "Mixed" ? Mix.Length : 1));
        var fifty = await CallsAsync(Batch(kind, 50));

        fifty.Should().Be(one, $"a batch of {kind} treatments reads and writes each store a fixed number of times");
        one.Should().BeGreaterThan(0);
    }

    /// <summary>The kinds a mixed batch carries, one of each in its smallest batch.</summary>
    private static readonly string[] Mix = ["Bolus Wizard", "Temp Basal", "Profile Switch", "Temporary Override", "Note", "Pump Suspend"];

    private static List<Treatment> Batch(string kind, int size)
    {
        return [.. Enumerable.Range(0, size).Select(i => Treatment(kind == "Mixed" ? Mix[i % Mix.Length] : kind, i))];
    }

    private static Treatment Treatment(string kind, int i)
    {
        var treatment = new Treatment
        {
            Id = $"fixed-{kind.Replace(' ', '-')}-{i}",
            Mills = Start + i * 300_000L,
            EnteredBy = "AAPS",
            PumpType = "OMNIPOD_DASH",
            PumpSerial = "P-1",
        };
        switch (kind)
        {
            case "Bolus Wizard":
                (treatment.EventType, treatment.Insulin, treatment.Carbs, treatment.BloodGlucoseInput, treatment.Notes, treatment.FoodType) =
                    ("Bolus Wizard", 2.5, 30, 140, "lunch", "pasta");
                break;
            case "Meal Bolus":
                (treatment.EventType, treatment.Insulin, treatment.Carbs) = ("Meal Bolus", 3, 40);
                break;
            case "Carb Correction":
                (treatment.EventType, treatment.Carbs, treatment.FoodType) = ("Carb Correction", 15, "juice");
                break;
            case "BG Check":
                (treatment.EventType, treatment.Glucose, treatment.GlucoseType) = ("BG Check", 120, "Finger");
                break;
            case "Note":
                (treatment.EventType, treatment.Notes) = ("Note", "a note");
                break;
            case "Temp Basal":
                (treatment.EventType, treatment.Absolute, treatment.Duration) = ("Temp Basal", 0.8, 30);
                break;
            case "Percent Temp Basal":
                (treatment.EventType, treatment.Percent, treatment.Duration) = ("Temp Basal", -50, 30);
                break;
            case "Profile Switch":
                (treatment.EventType, treatment.Profile, treatment.Duration, treatment.Percentage) = ("Profile Switch", "Day", 60, 110);
                break;
            case "Temporary Override":
                (treatment.EventType, treatment.Duration, treatment.Reason) = ("Temporary Override", 60, "Workout");
                break;
            case "Temporary Target":
                (treatment.EventType, treatment.Duration, treatment.TargetTop, treatment.TargetBottom) = ("Temporary Target", 60, 140, 120);
                break;
            case "Pump Suspend":
                treatment.EventType = i % 2 == 0 ? "Pump Suspend" : "Pump Resume";
                break;
            case "Site Change":
                treatment.EventType = "Site Change";
                break;
        }

        return treatment;
    }

    private async Task<int> CallsAsync(IReadOnlyList<Treatment> treatments)
    {
        _doubles.Clear();
        _contextOpens = 0;
        await Decomposer().DecomposeBatchAsync(treatments, WriteOrigin.Live);
        return _doubles.Sum(d => d.Invocations.Count) + _contextOpens;
    }

    private Mock<T> Double<T>() where T : class
    {
        var mock = new Mock<T>();
        _doubles.Add(mock);
        return mock;
    }

    private Mock<TRepository> Table<TRepository, TRecord>()
        where TRepository : class, ILegacyKeyedRepository<TRecord>
        where TRecord : class, V4Models.IV4Record
    {
        var mock = Double<TRepository>();
        mock.As<ILegacyKeyedRepository<TRecord>>()
            .Setup(r => r.GetByLegacyIdsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        mock.As<IBulkUpsertRepository<TRecord>>()
            .Setup(r => r.BulkUpsertAsync(It.IsAny<IEnumerable<TRecord>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<TRecord> records, WriteOrigin _, CancellationToken _) =>
                new BulkWrite<TRecord>([.. records.Select(Stored)], 0));
        return mock;
    }

    private static T Stored<T>(T record) where T : V4Models.IV4Record
    {
        if (record.Id == Guid.Empty && record is V4Models.V4RecordBase v4)
            v4.Id = Guid.CreateVersion7();
        return record;
    }

    private TreatmentDecomposer Decomposer()
    {
        var activations = Table<IProfileActivationRepository, V4Models.ProfileActivation>();
        activations.Setup(r => r.GetHeldLegacyIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>());
        activations.Setup(r => r.GetOverlappingAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        activations.Setup(r => r.GetStartingAfterAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        activations.Setup(r => r.GetRequestedAtOrBeforeAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var spans = Double<IStateSpanService>();
        spans.Setup(s => s.GetStateSpansAroundAsync(
                It.IsAny<StateSpanCategory>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<IReadOnlyCollection<string?>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        spans.Setup(s => s.UpsertStateSpansAsync(It.IsAny<IReadOnlyList<StateSpan>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<StateSpan> written, CancellationToken _) =>
                [.. written.Select(s => new StateSpanUpsert(s, UpsertOutcome.Created))]);
        spans.Setup(s => s.UpdateStateSpansAsync(It.IsAny<IReadOnlyList<StateSpan>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<StateSpan> written, CancellationToken _) => [.. written]);

        var devices = Double<IDeviceService>();
        devices.Setup(s => s.ResolveManyAsync(It.IsAny<IReadOnlyList<DeviceSighting>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<DeviceSighting> sightings, CancellationToken _) => [.. sightings.Select(_ => (Guid?)null)]);
        devices.Setup(s => s.GetPatientDeviceLinksAsync(It.IsAny<IEnumerable<Guid?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IPatientDeviceLinks>());

        var foods = Double<ITreatmentFoodService>();
        foods.Setup(s => s.GetByCarbIntakeIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var profiles = Double<IActiveProfileResolver>();
        profiles.Setup(r => r.GetActiveInsulinContextsAsync(It.IsAny<IReadOnlyList<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<long> times, CancellationToken _) => [.. times.Select(_ => (V4Models.TreatmentInsulinContext?)null)]);
        var basal = Double<IBasalRateResolver>();
        basal.Setup(r => r.FindBasalRatesAsync(It.IsAny<IReadOnlyList<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<long> times, CancellationToken _) => [.. times.Select(_ => (double?)1.0)]);
        var carbs = Table<ICarbIntakeRepository, V4Models.CarbIntake>();
        carbs.Setup(r => r.GetByCorrelationIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        return new TreatmentDecomposer(
            new CountingContexts(new TestNocturneDbContextFactory(_context), () => _contextOpens++),
            Table<IBolusRepository, V4Models.Bolus>().Object,
            Table<ITempBasalRepository, V4Models.TempBasal>().Object,
            carbs.Object,
            Table<IBGCheckRepository, V4Models.BGCheck>().Object,
            Table<INoteRepository, V4Models.Note>().Object,
            Table<IDeviceEventRepository, V4Models.DeviceEvent>().Object,
            Table<IBolusCalculationRepository, V4Models.BolusCalculation>().Object,
            spans.Object,
            activations.Object,
            new ProfileActivationWriter(activations.Object, Double<ITenantCache>().Object, Double<ITempBasalRateReresolver>().Object),
            foods.Object,
            devices.Object,
            Double<IPatientDeviceStamper>().Object,
            profiles.Object,
            Double<IPatientInsulinRepository>().Object,
            new RelativeTempBasalRates(basal.Object),
            Mock.Of<IAuditContext>(),
            NullLogger<TreatmentDecomposer>.Instance);
    }
}

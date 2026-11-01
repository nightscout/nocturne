using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.TestDoubles;

/// <summary>
/// Answers the set-based reads a treatment create makes through the per-key forms, so a test sets up
/// and verifies one lookup at a time.
/// </summary>
internal static class LegacyTreatmentRepositoryMockExtensions
{
    /// <summary>
    /// <see cref="ILegacyKeyedRepository{TRecord}.GetByServedIdsAsync"/> through the legacy id, uuid range
    /// and uuid-prefix lookups of each id.
    /// </summary>
    public static Mock<TRepository> AnswersServedIdsPerKey<TRepository, TRecord>(this Mock<TRepository> mock)
        where TRepository : class, ILegacyKeyedRepository<TRecord>
        where TRecord : class, IV4Record
    {
        mock.As<ILegacyKeyedRepository<TRecord>>()
            .Setup(r => r.GetByServedIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyCollection<string> ids, CancellationToken ct) =>
            {
                var repository = (ILegacyKeyedRepository<TRecord>)mock.Object;
                var found = new List<TRecord?>();
                foreach (var id in ids)
                {
                    found.Add(await repository.GetByLegacyIdAsync(id, ct));
                    if (MongoObjectId.TryGetGuidPrefixRange(id, out var low, out var high))
                        found.Add(await repository.GetByGuidRangeAsync(low, high, ct));
                    found.Add(await repository.GetByLegacyIdUuidPrefixAsync(id, ct));
                }
                return (IReadOnlyList<TRecord>)[.. found.OfType<TRecord>().DistinctBy(r => r.Id)];
            });
        return mock;
    }

    /// <summary>Every keyed treatment repository of the store answering <see cref="AnswersServedIdsPerKey{TRepository, TRecord}"/>.</summary>
    public static void AnswerServedIdsPerKey(
        Mock<ITempBasalRepository> tempBasals, Mock<IBolusRepository> boluses, Mock<ICarbIntakeRepository> carbs,
        Mock<IBGCheckRepository> bgChecks, Mock<INoteRepository> notes, Mock<IDeviceEventRepository> deviceEvents,
        Mock<IBolusCalculationRepository> calculations)
    {
        tempBasals.AnswersServedIdsPerKey<ITempBasalRepository, TempBasal>();
        boluses.AnswersServedIdsPerKey<IBolusRepository, Bolus>();
        carbs.AnswersServedIdsPerKey<ICarbIntakeRepository, CarbIntake>();
        bgChecks.AnswersServedIdsPerKey<IBGCheckRepository, BGCheck>();
        notes.AnswersServedIdsPerKey<INoteRepository, Note>();
        deviceEvents.AnswersServedIdsPerKey<IDeviceEventRepository, DeviceEvent>();
        calculations.AnswersServedIdsPerKey<IBolusCalculationRepository, BolusCalculation>();
        AnswersCorrelationIdsPerId(boluses, carbs);
    }

    /// <summary>The meal tables' correlation-id reads through the per-id forms.</summary>
    public static void AnswersCorrelationIdsPerId(Mock<IBolusRepository> boluses, Mock<ICarbIntakeRepository> carbs)
    {
        boluses.Setup(r => r.GetByCorrelationIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<Guid> ids, CancellationToken ct) =>
            {
                var found = new List<Bolus>();
                foreach (var id in ids)
                    found.AddRange(await boluses.Object.GetByCorrelationIdAsync(id, ct) ?? []);
                return found.AsEnumerable();
            });
        carbs.Setup(r => r.GetByCorrelationIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<Guid> ids, CancellationToken ct) =>
            {
                var found = new List<CarbIntake>();
                foreach (var id in ids)
                    found.AddRange(await carbs.Object.GetByCorrelationIdAsync(id, ct) ?? []);
                return found.AsEnumerable();
            });
    }

    /// <summary><see cref="IV4ToLegacyProjectionService.GetStateSpanTreatmentIdsAsync"/> through the per-id form.</summary>
    public static Mock<IV4ToLegacyProjectionService> AnswersSpanIdsPerId(this Mock<IV4ToLegacyProjectionService> mock)
    {
        mock.Setup(p => p.GetStateSpanTreatmentIdsAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyCollection<string> ids, CancellationToken ct) =>
            {
                var keys = new Dictionary<string, string>();
                foreach (var id in ids)
                {
                    if (await mock.Object.GetStateSpanTreatmentIdAsync(id, ct) is { } key)
                        keys[id] = key;
                }
                return (IReadOnlyDictionary<string, string>)keys;
            });
        return mock;
    }
}

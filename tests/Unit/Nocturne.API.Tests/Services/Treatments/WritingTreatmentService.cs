using Nocturne.Core.Models.V4;
using Nocturne.API.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>
/// An <see cref="ITreatmentService"/> whose writes go through a real <see cref="TreatmentDecomposer"/>
/// into the given repositories, for a consumer test that asserts on what was stored. Reads project
/// those repositories through <paramref name="contexts"/>; with none they serve nothing.
/// </summary>
internal static class WritingTreatmentService
{
    public static ITreatmentService Over(
        TreatmentDecomposer decomposer,
        ITempBasalRepository tempBasals,
        IBolusRepository boluses,
        ICarbIntakeRepository carbs,
        IBGCheckRepository bgChecks,
        INoteRepository notes,
        IDeviceEventRepository deviceEvents,
        IBolusCalculationRepository calculations,
        INocturneDbContextFactory? contexts = null,
        ITreatmentFoodService? foods = null)
    {
        IV4ToLegacyProjectionService projection = contexts is null
            ? new Mock<IV4ToLegacyProjectionService>().AnswersSpanIdsPerId().Object
            : new V4ToLegacyProjectionService(
                boluses, carbs, bgChecks, notes, deviceEvents, tempBasals, calculations,
                Mock.Of<IProfileActivationRepository>(), foods ?? Mock.Of<ITreatmentFoodService>(), contexts,
                NullLogger<V4ToLegacyProjectionService>.Instance);

        var store = new TreatmentReadService(
            projection, decomposer,
            tempBasals, boluses, carbs, bgChecks, notes, deviceEvents, calculations,
            new Mock<IProfileActivationRepository>().AnswersServedIdsPerKey<IProfileActivationRepository, ProfileActivation>().Object,
            Mock.Of<IStateSpanService>(),
            NullLogger<TreatmentReadService>.Instance);

        return new TreatmentService(
            store, decomposer, Mock.Of<ITreatmentCache>(), Mock.Of<IDataEventSink<Treatment>>(),
            Mock.Of<IPatientInsulinRepository>(), NullLogger<TreatmentService>.Instance);
    }
}

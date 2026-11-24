using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.ProfileActivations;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Services.V4;

internal static partial class TreatmentDecomposerDoubles
{
    /// <summary>
    /// A decomposer over <paramref name="contexts"/> whose every other collaborator is a loose double unless
    /// the test names it. The profile activation store and the keys are real, over <paramref name="contexts"/>.
    /// </summary>
    public static TreatmentDecomposer Build(
        INocturneDbContextFactory contexts,
        IDecomposedTreatmentKeys? keys = null,
        IBolusRepository? bolus = null,
        ITempBasalRepository? tempBasal = null,
        ICarbIntakeRepository? carbIntake = null,
        IBGCheckRepository? bgCheck = null,
        INoteRepository? note = null,
        IDeviceEventRepository? deviceEvent = null,
        IBolusCalculationRepository? bolusCalculation = null,
        IStateSpanService? stateSpans = null,
        IProfileActivationRepository? profileActivations = null,
        ProfileActivationWriter? profileActivationWriter = null,
        ITreatmentFoodService? foods = null,
        IDeviceService? devices = null,
        IPatientDeviceStamper? stamper = null,
        IActiveProfileResolver? activeProfiles = null,
        IPatientInsulinRepository? insulin = null,
        RelativeTempBasalRates? relativeTempBasalRates = null,
        IAuditContext? audit = null,
        ILogger<TreatmentDecomposer>? logger = null,
        TimeProvider? time = null,
        IDeviceEventReactor? deviceEventReactor = null,
        IRecordChangePublisher? changes = null)
    {
        time ??= TimeProvider.System;
        profileActivations ??= ProfileActivationTestDeps.Repository(contexts);
        return new TreatmentDecomposer(
            contexts,
            keys ?? new DecomposedTreatmentKeys(contexts, time),
            bolus ?? Mock.Of<IBolusRepository>(),
            tempBasal ?? Mock.Of<ITempBasalRepository>(),
            carbIntake ?? Mock.Of<ICarbIntakeRepository>(),
            bgCheck ?? Mock.Of<IBGCheckRepository>(),
            note ?? Mock.Of<INoteRepository>(),
            deviceEvent ?? Mock.Of<IDeviceEventRepository>(),
            bolusCalculation ?? Mock.Of<IBolusCalculationRepository>(),
            stateSpans ?? Mock.Of<IStateSpanService>(),
            profileActivations,
            profileActivationWriter ?? ProfileActivationTestDeps.Writer(profileActivations),
            foods ?? Mock.Of<ITreatmentFoodService>(),
            devices ?? new UnresolvedDevices(),
            stamper ?? Mock.Of<IPatientDeviceStamper>(),
            activeProfiles ?? new Mock<IActiveProfileResolver>().AnswersInsulinContexts(_ => null).Object,
            insulin ?? Mock.Of<IPatientInsulinRepository>(),
            relativeTempBasalRates ?? TempBasalTestDeps.Unscheduled(),
            audit ?? Mock.Of<IAuditContext>(),
            logger ?? NullLogger<TreatmentDecomposer>.Instance,
            time,
            deviceEventReactor,
            changes);
    }

    /// <summary>
    /// A decomposer over the real repositories of <paramref name="contexts"/>, whose food service holds no lines
    /// unless the test names one.
    /// </summary>
    public static TreatmentDecomposer Stored(
        INocturneDbContextFactory contexts,
        IDuplicateLinker dedup,
        IAuditContext audit,
        ITempBasalRepository? tempBasal = null,
        IStateSpanService? stateSpans = null,
        ITreatmentFoodService? foods = null,
        IDeviceService? devices = null,
        IPatientDeviceStamper? stamper = null,
        IActiveProfileResolver? activeProfiles = null,
        IPatientInsulinRepository? insulin = null,
        RelativeTempBasalRates? relativeTempBasalRates = null,
        IDeviceEventReactor? deviceEventReactor = null,
        IRecordChangePublisher? changes = null)
    {
        if (foods is null)
        {
            var noLines = new Mock<ITreatmentFoodService>();
            noLines
                .Setup(s => s.GetByCarbIntakeIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            foods = noLines.Object;
        }

        return Build(
            contexts,
            bolus: new BolusRepository(contexts, dedup, audit, NullLogger<BolusRepository>.Instance),
            tempBasal: tempBasal ?? new TempBasalRepository(contexts, dedup, audit, NullLogger<TempBasalRepository>.Instance),
            carbIntake: new CarbIntakeRepository(contexts, dedup, audit, NullLogger<CarbIntakeRepository>.Instance),
            bgCheck: new BGCheckRepository(contexts, dedup, audit, NullLogger<BGCheckRepository>.Instance),
            note: new NoteRepository(contexts, dedup, audit, NullLogger<NoteRepository>.Instance),
            deviceEvent: new DeviceEventRepository(contexts, dedup, audit, NullLogger<DeviceEventRepository>.Instance),
            bolusCalculation: new BolusCalculationRepository(contexts, dedup, audit, NullLogger<BolusCalculationRepository>.Instance),
            stateSpans: stateSpans,
            foods: foods,
            devices: devices,
            stamper: stamper,
            activeProfiles: activeProfiles,
            insulin: insulin,
            relativeTempBasalRates: relativeTempBasalRates,
            audit: audit,
            deviceEventReactor: deviceEventReactor,
            changes: changes);
    }
}

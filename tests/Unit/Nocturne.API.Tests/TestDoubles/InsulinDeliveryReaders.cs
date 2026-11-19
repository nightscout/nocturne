using Moq;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;

namespace Nocturne.API.Tests.TestDoubles;

internal static class InsulinDeliveryReaders
{
    public static InsulinDeliveryReader Over(
        ITempBasalRepository? tempBasals = null,
        IBasalInjectionRepository? basalInjections = null,
        TimeProvider? time = null) =>
        new(
            Mock.Of<IBolusRepository>(),
            Mock.Of<ICarbIntakeRepository>(),
            tempBasals ?? Mock.Of<ITempBasalRepository>(),
            basalInjections ?? Mock.Of<IBasalInjectionRepository>(),
            Mock.Of<IStateSpanRepository>(),
            Mock.Of<IBasalSegmentService>(),
            Mock.Of<ITherapySettingsResolver>(),
            new StatisticsService(time ?? TimeProvider.System),
            time ?? TimeProvider.System);
}

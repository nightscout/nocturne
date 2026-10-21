using Moq;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;

namespace Nocturne.API.Tests.Infrastructure;

public static class TempBasalTestDeps
{
    /// <summary>Relative temp basal resolution for a tenant with no basal schedule in force.</summary>
    public static RelativeTempBasalRates Unscheduled() => Rates(Mock.Of<IBasalRateResolver>());

    public static RelativeTempBasalRates Rates(
        IBasalRateResolver basalRates,
        ITempBasalRepository? tempBasals = null,
        IBasalScheduleRepository? basalSchedules = null) =>
        new(
            basalRates,
            tempBasals ?? NoneUnresolved(),
            basalSchedules ?? NoSchedules(),
            new ProfileVersionCache(TestTenantCache.Create()));

    private static ITempBasalRepository NoneUnresolved()
    {
        var repo = new Mock<ITempBasalRepository>();
        repo.Setup(r => r.GetUnresolvedRelativeAsync(It.IsAny<(DateTime, Guid)?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return repo.Object;
    }

    private static IBasalScheduleRepository NoSchedules()
    {
        var repo = new Mock<IBasalScheduleRepository>();
        repo.Setup(r => r.GetByProfileNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return repo.Object;
    }
}

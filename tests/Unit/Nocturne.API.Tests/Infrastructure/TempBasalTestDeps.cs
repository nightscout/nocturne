using Moq;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Profiles.Resolvers;

namespace Nocturne.API.Tests.Infrastructure;

public static class TempBasalTestDeps
{
    /// <summary>Relative temp basal resolution for a tenant with no basal schedule in force.</summary>
    public static RelativeTempBasalRates Unscheduled() => Rates(Mock.Of<IBasalRateResolver>());

    public static RelativeTempBasalRates Rates(IBasalRateResolver basalRates) => new(basalRates);

    /// <summary>A re-resolver that does nothing, for writes whose temp basal rates a test does not follow.</summary>
    public static ITempBasalRateReresolver NoReresolution() => Mock.Of<ITempBasalRateReresolver>();
}

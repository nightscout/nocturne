using Moq;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Profiles.Resolvers;

namespace Nocturne.API.Tests.Infrastructure;

public static class TempBasalTestDeps
{
    /// <summary>Relative temp basal resolution for a tenant with no therapy settings on file.</summary>
    public static RelativeTempBasalRates Unscheduled() =>
        new(Mock.Of<IBasalRateResolver>(), Mock.Of<ITherapySettingsResolver>());
}

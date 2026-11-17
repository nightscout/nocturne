using Moq;
using Nocturne.Core.Contracts.Profiles.Resolvers;

namespace Nocturne.API.Tests.Services.Profiles.Resolvers;

internal static class ActiveProfileResolverMock
{
    /// <summary>
    /// An <see cref="IActiveProfileResolver"/> whose default store is <paramref name="defaultProfile"/> at
    /// every time, and whose store read is the one the mocked active profile names, else the default.
    /// </summary>
    public static Mock<IActiveProfileResolver> WithDefault(string defaultProfile = "Default")
    {
        var mock = new Mock<IActiveProfileResolver>();
        mock.Setup(r => r.GetDefaultProfileNameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(defaultProfile);
        mock.Setup(r => r.GetProfileNameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(async (long timeMills, CancellationToken ct) =>
                await mock.Object.GetActiveProfileNameAsync(timeMills, ct) ?? defaultProfile);
        return mock;
    }
}

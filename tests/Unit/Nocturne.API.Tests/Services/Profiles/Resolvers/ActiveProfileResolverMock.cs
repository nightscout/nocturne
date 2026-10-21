using Moq;
using Nocturne.Core.Contracts.Profiles.Resolvers;

namespace Nocturne.API.Tests.Services.Profiles.Resolvers;

internal static class ActiveProfileResolverMock
{
    /// <summary>An <see cref="IActiveProfileResolver"/> whose default store is <paramref name="defaultProfile"/>.</summary>
    public static Mock<IActiveProfileResolver> WithDefault(string defaultProfile = "Default")
    {
        var mock = new Mock<IActiveProfileResolver>();
        mock.Setup(r => r.GetDefaultProfileNameAsync(It.IsAny<CancellationToken>())).ReturnsAsync(defaultProfile);
        return mock;
    }
}

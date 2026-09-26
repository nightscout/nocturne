using Xunit;

namespace Nocturne.API.Tests.Integration.Infrastructure;

/// <summary>
/// Tests sharing one <see cref="ApiIntegrationTestFixture"/>: one API and one database for the
/// collection, cleaned between tests by <see cref="ApiIntegrationTestBase"/>.
/// </summary>
[CollectionDefinition("ApiIntegration")]
public class ApiIntegrationTestCollection : ICollectionFixture<ApiIntegrationTestFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}


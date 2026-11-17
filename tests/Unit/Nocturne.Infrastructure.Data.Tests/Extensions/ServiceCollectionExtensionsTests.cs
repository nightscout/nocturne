using Microsoft.Extensions.DependencyInjection;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.Extensions;

[Trait("Category", "Unit")]
public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddPostgreSqlInfrastructure_ResolvesTheContextFactory_FromTheRootAndFromAScope()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPostgreSqlInfrastructure(
            "Host=localhost;Database=nocturne_test;Username=nocturne_app;Password=x",
            configuration: null);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        provider.GetRequiredService<INocturneDbContextFactory>().Should().NotBeNull(
            "singletons and hosted services take it from the root");
        scope.ServiceProvider.GetRequiredService<INocturneDbContextFactory>().Should().NotBeNull();
        scope.ServiceProvider.GetService<NocturneDbContext>().Should().BeNull(
            "every context comes from the factory and is disposed by the method that opened it");
    }
}

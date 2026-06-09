using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Tests.Extensions;

[Trait("Category", "Unit")]
public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddPostgreSqlInfrastructure_RegistersCarrierResettingFactory_AsTheDbContextFactory()
    {
        // The chokepoint is only effective if it is the registered IDbContextFactory: a raw-factory
        // caller resolves this interface, so if a refactor silently dropped the decoration, every
        // raw lease would again inherit a pooled context's stale tenant/share carrier. Guards that.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IHttpContextAccessor>());
        services.AddPostgreSqlInfrastructure("Host=localhost;Database=nocturne_test;Username=nocturne_app;Password=x");

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<NocturneDbContext>>();

        factory.Should().BeOfType<CarrierResettingDbContextFactory>(
            "every IDbContextFactory<NocturneDbContext> resolution must funnel through the carrier-reset chokepoint");
    }
}

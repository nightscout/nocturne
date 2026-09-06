using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Infrastructure.Data.Configuration;

namespace Nocturne.API.Tests.Configuration;

/// <summary>
/// Guards the composition in <c>Program.cs</c> that assembles the PostgreSQL options. The pool is
/// only built outside the Testing environment, but the options are resolved and registered either
/// way, so a test host can observe what the host would have handed the registration.
/// </summary>
[Trait("Category", "Unit")]
public class PostgreSqlOptionsCompositionTests
{
    private sealed class TunedPoolFactory : AuthenticationTestFactory
    {
        internal const int MaxPoolSize = 37;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            // Host configuration, not ConfigureAppConfiguration: the factory's app-configuration
            // sources are applied at Build(), by which time the top-level statements that resolve
            // these options have already read the configuration.
            builder.UseSetting(
                $"{PostgreSqlConfiguration.SectionName}:{nameof(PostgreSqlConfiguration.MaxPoolSize)}",
                MaxPoolSize.ToString());
        }
    }

    [Fact]
    public void HostConfiguration_ReachesTheResolvedPostgreSqlOptions()
    {
        using var factory = new TunedPoolFactory();

        var options = factory.Services.GetRequiredService<PostgreSqlConfiguration>();

        options.MaxPoolSize.Should().Be(
            TunedPoolFactory.MaxPoolSize,
            "the host must resolve its database options from its own configuration");
        options.EnableDetailedErrors.Should().BeFalse("Testing is not Development");
        options.EnableSensitiveDataLogging.Should().BeFalse("Testing is not Development");
    }
}

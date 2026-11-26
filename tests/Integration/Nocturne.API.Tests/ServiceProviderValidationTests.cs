using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Constants;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Builds the API's real service registrations with <c>ValidateOnBuild</c> and
/// <c>ValidateScopes</c>, which <c>Program.cs</c> turns on only in Development. A singleton that
/// captures a scoped service, or a registration nothing can construct, fails here in every
/// environment rather than at the first request that reaches it.
/// </summary>
[Trait("Category", "Integration")]
public class ServiceProviderValidationTests(ServiceProviderValidationTests.Database fixture)
    : IClassFixture<ServiceProviderValidationTests.Database>
{
    /// <summary>The database the API starts against; its startup migrations and reconcilers need one.</summary>
    public sealed class Database() : MigratedDatabase("di_validation");

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task TheApiServiceProvider_ValidatesOnBuild(string environment)
    {
        Exception? validation = null;
        var validated = false;

        await using var factory = new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting($"ConnectionStrings:{ServiceNames.PostgreSql}", fixture.AppConnectionString);
            builder.UseSetting($"ConnectionStrings:{ServiceNames.PostgreSql}-migrator", fixture.MigratorConnectionString);
            builder.UseSetting("INSTANCE_KEY", "di-validation-instance-key-of-at-least-32-chars");
            builder.UseSetting("BASE_DOMAIN", "localhost");
            builder.UseSetting("DemoService:Enabled", "false");
            builder.ConfigureTestServices(services =>
            {
                try
                {
                    using var provider = services.BuildServiceProvider(new ServiceProviderOptions
                    {
                        ValidateOnBuild = true,
                        ValidateScopes = true,
                    });
                }
                catch (Exception ex)
                {
                    validation = ex;
                }

                validated = true;
            });
        });

        _ = factory.Services;

        validated.Should().BeTrue("the registrations were captured from the real program");
        validation.Should().BeNull();
    }
}

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Constants;

namespace Nocturne.API.Tests.Integration;

/// <summary>The service descriptors the real <c>Program</c> registers.</summary>
internal static class RegisteredServices
{
    public static async Task<List<ServiceDescriptor>> CaptureAsync(ServiceProviderValidationTests.MigratedDatabase fixture)
    {
        var database = fixture.Database;
        List<ServiceDescriptor>? captured = null;

        await using var factory = new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting($"ConnectionStrings:{ServiceNames.PostgreSql}", database.AppConnectionString);
            builder.UseSetting($"ConnectionStrings:{ServiceNames.PostgreSql}-migrator", database.MigratorConnectionString);
            builder.UseSetting("INSTANCE_KEY", "di-validation-instance-key-of-at-least-32-chars");
            builder.UseSetting("BASE_DOMAIN", "localhost");
            builder.UseSetting("DemoService:Enabled", "false");
            builder.ConfigureTestServices(services => captured = [.. services]);
        });

        _ = factory.Services;
        return captured ?? throw new InvalidOperationException("The registrations were not captured from the real program.");
    }
}

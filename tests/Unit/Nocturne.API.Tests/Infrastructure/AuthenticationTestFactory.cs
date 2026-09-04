using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// Custom WebApplicationFactory for authentication tests that mocks external dependencies
/// </summary>
public class AuthenticationTestFactory : SqliteWebAppFactoryBase<Nocturne.API.Program>
{
    /// <summary>
    /// The API secret configured for the test tenant. Tests that validate API secret
    /// authentication should send SHA1(ApiSecret) in the api-secret header.
    /// </summary>
    public const string ApiSecret = "test-api-secret-for-authentication-tests";

    protected override string InstanceKey => ApiSecret;

    protected override string? ApiSecretHash => TestDatabaseSeeder.Sha1Hex(ApiSecret);

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        RemoveService<IFoodRepository>(services);
        RemoveService<ISettingsRepository>(services);

        var conn = Connection;
        services.AddDbContext<NocturneDbContext>(options =>
            options.UseSqlite(conn)
                .ConfigureWarnings(w =>
                    w.Ignore(RelationalEventId.PendingModelChangesWarning)));

        var mockCacheService = new Mock<ICacheService>();
        mockCacheService
            .Setup(x => x.GetAsync<object>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((object?)null);
        mockCacheService
            .Setup(x =>
                x.SetAsync(
                    It.IsAny<string>(),
                    It.IsAny<object>(),
                    It.IsAny<TimeSpan?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.CompletedTask);
        services.AddSingleton(mockCacheService.Object);

        services.AddSingleton(new Mock<IFoodRepository>().Object);
        services.AddSingleton(new Mock<ISettingsRepository>().Object);
        services.AddSingleton(new Mock<IAuthorizationService>().Object);

        services.AddMemoryCache();
    }

    protected override void ConfigureTestLogging(ILoggingBuilder logging)
    {
        logging.ClearProviders();
#if DEBUG
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Warning);
#endif
    }
}

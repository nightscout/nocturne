using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// Request services for a controller under test, carrying the request's
/// <see cref="ICategoryReadContext"/> as <c>MemberScopeMiddleware</c> leaves it, plus the MVC
/// services <c>ControllerBase.Problem</c> resolves.
/// </summary>
public static class TestRequestServices
{
    /// <summary>Request services for a caller limited to the last 24 hours.</summary>
    public static IServiceProvider HistoryClamped() => Build(clamped: true);

    /// <summary>Request services for a caller that is, or is not, limited to the last 24 hours.</summary>
    public static IServiceProvider Build(bool clamped)
    {
        var category = new CategoryReadContext();
        if (clamped)
            category.ClampMemberHistory();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMvcCore();
        services.AddSingleton<ICategoryReadContext>(category);
        return services.BuildServiceProvider();
    }
}

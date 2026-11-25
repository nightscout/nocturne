using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// Request services for a controller under test, carrying the request's
/// <see cref="ICategoryReadContext"/> as <c>MemberScopeMiddleware</c> leaves it, the MVC
/// services <c>ControllerBase.Problem</c> resolves, and a live <see cref="LiveSession"/>.
/// </summary>
public static class TestRequestServices
{
    /// <summary>Request services for a caller limited to the last 24 hours.</summary>
    public static IServiceProvider HistoryClamped() => Build(clamped: true);

    /// <summary>
    /// Request services for a caller that is, or is not, limited to the last 24 hours, with any
    /// further services the action resolves.
    /// </summary>
    public static IServiceProvider Build(bool clamped, Action<IServiceCollection>? also = null)
    {
        var category = new CategoryReadContext();
        if (clamped)
            category.ClampMemberHistory();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMvcCore();
        services.AddSingleton<ICategoryReadContext>(category);
        services.AddLiveSession();
        also?.Invoke(services);
        return services.BuildServiceProvider();
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Audit;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// The services <see cref="AlertSweepJob"/> resolves around the passes a test drives. A test
/// registers what it drives, and this fills in the rest with quiet doubles; a service the sweep
/// gains is added here once.
/// </summary>
internal static class SweepServices
{
    /// <summary>
    /// Adds a double for each sweep service the collection does not already register: the notices
    /// with nothing to report, a settable tenant and the scope's audit context.
    /// </summary>
    public static IServiceCollection AddSweepServices(this IServiceCollection services)
    {
        services.TryAddSingleton(Mock.Of<IUnevaluableRuleNotice>());
        services.TryAddSingleton(NoHeldRules.Notice());
        services.TryAddSingleton(Mock.Of<IClockAnomalyNotice>());
        services.TryAddSingleton(Mock.Of<IAlertChannelNotice>());
        services.TryAddScoped<ITenantAccessor>(_ => new SettableTenantAccessor());
        services.TryAddScoped<IAuditContext, AuditContext>();
        return services;
    }
}

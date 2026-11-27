using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// The one place a test builds an <see cref="AlertDeliveryService"/>. A test passes what it drives
/// and this fills in the rest with quiet doubles; a dependency the service gains is added here once.
/// </summary>
internal static class DeliveryServices
{
    /// <summary>
    /// Builds the service over <paramref name="contexts"/>, <paramref name="tenant"/> and the
    /// channel providers a test delivers to. Left out, the members are the repository over
    /// <paramref name="contexts"/>, the broadcast and the alarm relay are mocks that do nothing, the
    /// logger discards and the clock is the system's.
    /// </summary>
    public static AlertDeliveryService Build(
        INocturneDbContextFactory contexts,
        ITenantAccessor tenant,
        IServiceProvider providers,
        ITenantMembershipRepository? members = null,
        ISignalRBroadcastService? broadcast = null,
        IEngineAlarmRelay? alarmRelay = null,
        ILogger<AlertDeliveryService>? logger = null,
        TimeProvider? clock = null) =>
        new(
            contexts,
            members ?? new TenantMembershipRepository(contexts, AccountLinkRoutes.Off),
            tenant,
            broadcast ?? Mock.Of<ISignalRBroadcastService>(),
            alarmRelay ?? Mock.Of<IEngineAlarmRelay>(),
            providers,
            logger ?? NullLogger<AlertDeliveryService>.Instance,
            clock ?? TimeProvider.System);
}

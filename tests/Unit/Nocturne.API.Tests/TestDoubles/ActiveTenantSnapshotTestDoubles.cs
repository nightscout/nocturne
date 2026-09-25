using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Tests.TestDoubles;

internal static class ActiveTenantSnapshotTestDoubles
{
    /// <summary>A snapshot for a service under test that never reaches its tenant read.</summary>
    public static ActiveTenantSnapshot Unread() =>
        new(Mock.Of<IDbContextFactory<NocturneDbContext>>(), TimeProvider.System, NullLogger<ActiveTenantSnapshot>.Instance);

    /// <summary>Registers an <see cref="ActiveTenantSnapshot"/> over the registered context factory.</summary>
    public static IServiceCollection AddActiveTenantSnapshot(this IServiceCollection services) =>
        services.AddSingleton(sp => new ActiveTenantSnapshot(
            sp.GetRequiredService<IDbContextFactory<NocturneDbContext>>(),
            TimeProvider.System,
            NullLogger<ActiveTenantSnapshot>.Instance));
}

using Nocturne.Tests.Shared.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Tests.TestDoubles;

internal static class ActiveTenantSnapshotTestDoubles
{
    /// <summary>A snapshot for a service under test that never reaches its tenant read.</summary>
    public static ActiveTenantSnapshot Unread() =>
        new(new TenantRepository(Mock.Of<IDbContextFactory<NocturneDbContext>>().AsNocturneFactory()), TimeProvider.System, NullLogger<ActiveTenantSnapshot>.Instance);

    /// <summary>Registers an <see cref="ActiveTenantSnapshot"/> over the registered context factory.</summary>
    public static IServiceCollection AddActiveTenantSnapshot(this IServiceCollection services) =>
        services.AddSingleton(sp => new ActiveTenantSnapshot(
            new TenantRepository(sp.GetRequiredService<IDbContextFactory<NocturneDbContext>>().AsNocturneFactory()),
            TimeProvider.System,
            NullLogger<ActiveTenantSnapshot>.Instance));
}

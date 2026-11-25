using Nocturne.API.Tests.Services.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// The real <see cref="TenantService"/> over a test's context, for a controller test whose member writes
/// it runs, such as a role change.
/// </summary>
public static class TestTenantService
{
    public static TenantService Over(NocturneDbContext db) => new(
        db.AsFactory(),
        new TenantRepository(db.AsFactory()),
        new TenantMembershipRepository(db.AsFactory(), AccountLinkRoutes.Off),
        new SubjectRepository(db.AsFactory(), AccountLinkRoutes.Off),
        new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new OperatorConfiguration()),
        Mock.Of<IHttpClientFactory>(),
        Mock.Of<ITenantRoleService>(),
        new GrantRevocationService(new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()))),
        TimeProvider.System,
        Mock.Of<ILogger<TenantService>>(),
        AccountLinkRoutes.Off, InertTeamStaff.Announcer());
}

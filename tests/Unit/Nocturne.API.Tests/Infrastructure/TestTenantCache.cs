using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Cache.Configuration;
using Nocturne.Infrastructure.Cache.Services;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>Builds a real <see cref="TenantCache"/> over in-memory tiers for one fixed reader.</summary>
public static class TestTenantCache
{
    public static readonly Guid DefaultTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public static ITenantCache Create(
        IMemoryCache? local = null,
        ICacheService? distributed = null,
        Guid? tenantId = null,
        string? partition = "full",
        bool demo = false)
    {
        local ??= new MemoryCache(new MemoryCacheOptions());
        distributed ??= Distributed(local);

        var tenant = new Mock<ITenantAccessor>();
        var id = tenantId ?? DefaultTenantId;
        tenant.Setup(t => t.Context).Returns(new TenantContext(id, "test", "Test", true, IsDemo: false));
        tenant.Setup(t => t.TenantId).Returns(id);
        tenant.Setup(t => t.IsResolved).Returns(true);

        return new TenantCache(
            distributed,
            local,
            tenant.Object,
            Mock.Of<IReaderCachePartition>(p => p.Current == partition),
            Mock.Of<IDemoModeService>(d => d.IsEnabled == demo));
    }

    public static MemoryCacheService Distributed(IMemoryCache local) =>
        new(local, Options.Create(new CacheConfiguration { KeyPrefix = "nocturne", DefaultExpirationSeconds = 300 }),
            NullLogger<MemoryCacheService>.Instance);
}

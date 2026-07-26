using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Pins the tenant in the cache key directly. The handler also compares the cached session's
/// tenant against the resolved one, and that comparison alone makes a cross-tenant replay fail —
/// so a key that dropped the tenant would leave the handler tests green. These assert the key
/// itself, which is the half that stops one tenant's entry from ever being served to another.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GuestSessionCacheServiceTests
{
    private static readonly Guid TenantA = Guid.CreateVersion7();
    private static readonly Guid TenantB = Guid.CreateVersion7();
    private static readonly Guid GrantId = Guid.CreateVersion7();

    private static GuestSessionCacheService Build() =>
        new(new MemoryCache(new MemoryCacheOptions()));

    private static GuestSessionInfo SessionFor(Guid tenantId) =>
        new(GrantId, tenantId, Guid.CreateVersion7(), [], null, DateTime.UtcNow.AddHours(1));

    [Fact]
    public void An_entry_cached_for_one_tenant_is_not_returned_for_another()
    {
        var cache = Build();

        cache.Set(TenantA, GrantId, SessionFor(TenantA));

        cache.TryGet(TenantB, GrantId, out var other).Should().BeFalse(
            "the same grant id under a different tenant must be a cache miss, so the miss falls "
            + "through to the tenant-scoped database lookup");
        other.Should().BeNull();
    }

    [Fact]
    public void An_entry_is_returned_for_the_tenant_it_was_cached_under()
    {
        var cache = Build();

        cache.Set(TenantA, GrantId, SessionFor(TenantA));

        cache.TryGet(TenantA, GrantId, out var hit).Should().BeTrue();
        hit!.TenantId.Should().Be(TenantA);
    }

    [Fact]
    public void Eviction_only_removes_the_entry_for_the_tenant_it_names()
    {
        var cache = Build();
        cache.Set(TenantA, GrantId, SessionFor(TenantA));
        cache.Set(TenantB, GrantId, SessionFor(TenantB));

        cache.Evict(TenantA, GrantId);

        cache.TryGet(TenantA, GrantId, out _).Should().BeFalse();
        cache.TryGet(TenantB, GrantId, out _).Should().BeTrue(
            "eviction is keyed on the revoked grant's own tenant, so it must not clear another "
            + "tenant's entry for the same grant id");
    }

    [Fact]
    public void A_negative_entry_is_cached_per_tenant()
    {
        var cache = Build();

        cache.Set(TenantA, GrantId, null);

        cache.TryGet(TenantA, GrantId, out var cached).Should().BeTrue();
        cached.Should().BeNull();
        cache.TryGet(TenantB, GrantId, out _).Should().BeFalse();
    }
}

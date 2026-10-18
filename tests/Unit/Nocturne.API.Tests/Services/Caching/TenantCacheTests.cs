using Microsoft.Extensions.Caching.Memory;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Platform;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Services.Caching;

/// <summary>
/// <see cref="TenantCache"/> over the real <see cref="ReaderCachePartition"/>: who shares a cached
/// value with whom.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TenantCacheTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

    private readonly MemoryCache _local = new(new MemoryCacheOptions());
    private readonly ICacheService _distributed;

    public TenantCacheTests() => _distributed = TestTenantCache.Distributed(_local);

    public void Dispose() => _local.Dispose();

    private static CategoryReadContext Owner() => new();

    private static CategoryReadContext ClampedMember()
    {
        var reader = new CategoryReadContext();
        reader.ClampMemberHistory();
        return reader;
    }

    private static CategoryReadContext Share(string csv, bool fullHistory = true)
    {
        var reader = new CategoryReadContext();
        reader.MarkShare();
        reader.SetVisibleCategories(csv);
        reader.SetFullHistory(fullHistory);
        return reader;
    }

    private TenantCache Cache(ICategoryReadContext reader, Guid? tenantId = null, bool demo = false)
    {
        var id = tenantId ?? TenantA;
        var tenant = new Mock<ITenantAccessor>();
        tenant.Setup(t => t.Context).Returns(new TenantContext(id, "t", "T", true, IsDemo: false));
        tenant.Setup(t => t.TenantId).Returns(id);
        tenant.Setup(t => t.IsResolved).Returns(true);
        return new TenantCache(
            _distributed,
            _local,
            tenant.Object,
            new ReaderCachePartition(reader),
            Mock.Of<IDemoModeService>(d => d.IsEnabled == demo));
    }

    private static async Task<string?> ReadAsync(ITenantCache cache) =>
        (await cache.GetAsync<Box>("family:key"))?.Value;

    private static Task WriteAsync(ITenantCache cache, string value) =>
        cache.SetAsync("family:key", new Box(value), Ttl);

    private sealed record Box(string Value);

    [Fact]
    public void Owners_and_unclamped_members_share_the_full_partition()
    {
        new ReaderCachePartition(Owner()).Current.Should().Be(ReaderCachePartition.Full);
    }

    [Fact]
    public async Task Readers_in_the_full_partition_share_a_value()
    {
        await WriteAsync(Cache(Owner()), "owner");

        (await ReadAsync(Cache(Owner()))).Should().Be("owner");
    }

    [Fact]
    public async Task Shares_with_the_same_categories_in_any_order_share_a_partition()
    {
        var first = new ReaderCachePartition(Share($"{Scope.GlucoseRead},{Scope.TreatmentsRead}"));
        var second = new ReaderCachePartition(Share($"{Scope.TreatmentsRead}, {Scope.GlucoseRead}"));

        second.Current.Should().Be(first.Current);
        await WriteAsync(Cache(Share($"{Scope.GlucoseRead},{Scope.TreatmentsRead}")), "share");
        (await ReadAsync(Cache(Share($"{Scope.TreatmentsRead},{Scope.GlucoseRead}")))).Should().Be("share");
    }

    [Fact]
    public async Task Shares_with_different_categories_do_not_share_a_value()
    {
        await WriteAsync(Cache(Share($"{Scope.GlucoseRead},{Scope.TreatmentsRead}")), "wide");

        (await ReadAsync(Cache(Share(Scope.GlucoseRead)))).Should().BeNull();
    }

    [Fact]
    public async Task A_share_and_the_owner_do_not_share_a_value()
    {
        await WriteAsync(Cache(Owner()), "owner");
        (await ReadAsync(Cache(Share(Scope.GlucoseRead)))).Should().BeNull();

        await WriteAsync(Cache(Share(Scope.GlucoseRead)), "share");
        (await ReadAsync(Cache(Owner()))).Should().Be("owner");
    }

    [Fact]
    public async Task A_share_without_full_history_is_clamped_and_gets_no_partition()
    {
        new ReaderCachePartition(Share(Scope.GlucoseRead, fullHistory: false)).Current.Should().BeNull();

        await WriteAsync(Cache(Share(Scope.GlucoseRead, fullHistory: true)), "full-history");
        (await ReadAsync(Cache(Share(Scope.GlucoseRead, fullHistory: false)))).Should().BeNull();

        await WriteAsync(Cache(Share(Scope.GlucoseRead, fullHistory: false)), "last-24h");
        (await ReadAsync(Cache(Share(Scope.GlucoseRead, fullHistory: true)))).Should().Be("full-history");
    }

    [Fact]
    public async Task A_clamped_reader_neither_reads_nor_writes_either_tier()
    {
        await WriteAsync(Cache(Owner()), "owner");
        Cache(Owner()).SetLocal("family:local", "owner", Ttl);
        var clamped = Cache(ClampedMember());

        (await ReadAsync(clamped)).Should().BeNull();
        clamped.TryGetLocal<string>("family:local", out _).Should().BeFalse();

        await WriteAsync(clamped, "clamped");
        clamped.SetLocal("family:local", "clamped", Ttl);
        (await ReadAsync(Cache(Owner()))).Should().Be("owner");
        Cache(Owner()).TryGetLocal<string>("family:local", out var local).Should().BeTrue();
        local.Should().Be("owner");
    }

    [Fact]
    public async Task A_clamped_reader_always_computes()
    {
        var calls = 0;
        var clamped = Cache(ClampedMember());

        await clamped.GetOrSetAsync("family:key", () => Task.FromResult(new Box($"{++calls}")), Ttl);
        await clamped.GetOrSetAsync("family:key", () => Task.FromResult(new Box($"{++calls}")), Ttl);
        await clamped.GetOrCreateLocalAsync("family:local", () => Task.FromResult(++calls), Ttl);
        await clamped.GetOrCreateLocalAsync("family:local", () => Task.FromResult(++calls), Ttl);

        calls.Should().Be(4);
    }

    [Fact]
    public async Task The_demo_segment_separates_values()
    {
        await WriteAsync(Cache(Owner(), demo: true), "demo");

        (await ReadAsync(Cache(Owner(), demo: false))).Should().BeNull();
        (await ReadAsync(Cache(Owner(), demo: true))).Should().Be("demo");
    }

    [Fact]
    public async Task Different_tenants_never_collide()
    {
        await WriteAsync(Cache(Owner(), TenantA), "a");
        Cache(Owner(), TenantA).SetLocal("family:local", "a", Ttl);

        (await ReadAsync(Cache(Owner(), TenantB))).Should().BeNull();
        Cache(Owner(), TenantB).TryGetLocal<string>("family:local", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Removing_a_family_clears_it_in_every_partition_and_demo_segment_of_the_tenant_only()
    {
        var share = Share(Scope.GlucoseRead);
        await WriteAsync(Cache(Owner()), "owner");
        await WriteAsync(Cache(Owner(), demo: true), "demo");
        await WriteAsync(Cache(share), "share");
        await Cache(Owner()).SetAsync("other:key", new Box("other family"), Ttl);
        await WriteAsync(Cache(Owner(), TenantB), "tenant b");

        await Cache(ClampedMember()).RemoveFamilyAsync("family");

        (await ReadAsync(Cache(Owner()))).Should().BeNull();
        (await ReadAsync(Cache(Owner(), demo: true))).Should().BeNull();
        (await ReadAsync(Cache(share))).Should().BeNull();
        (await Cache(Owner()).GetAsync<Box>("other:key")).Should().NotBeNull();
        (await ReadAsync(Cache(Owner(), TenantB))).Should().Be("tenant b");
    }

    [Fact]
    public async Task Removing_a_tenant_clears_every_family_of_that_tenant_only()
    {
        await WriteAsync(Cache(Share(Scope.GlucoseRead)), "share");
        await Cache(Owner()).SetAsync("other:key", new Box("other family"), Ttl);
        await WriteAsync(Cache(Owner(), TenantB), "tenant b");

        await Cache(Owner()).RemoveTenantAsync(TenantA);

        (await ReadAsync(Cache(Share(Scope.GlucoseRead)))).Should().BeNull();
        (await Cache(Owner()).GetAsync<Box>("other:key")).Should().BeNull();
        (await ReadAsync(Cache(Owner(), TenantB))).Should().Be("tenant b");
    }

    [Fact]
    public async Task Without_a_resolved_tenant_reads_miss_writes_drop_and_removal_does_nothing()
    {
        await WriteAsync(Cache(Owner()), "owner");
        var tenant = new Mock<ITenantAccessor>();
        var unresolved = new TenantCache(
            _distributed, _local, tenant.Object, new ReaderCachePartition(Owner()), Mock.Of<IDemoModeService>());

        (await ReadAsync(unresolved)).Should().BeNull();
        await WriteAsync(unresolved, "unresolved");
        unresolved.SetLocal("family:local", "unresolved", Ttl);
        unresolved.TryGetLocal<string>("family:local", out _).Should().BeFalse();
        await unresolved.RemoveFamilyAsync("family");

        (await ReadAsync(Cache(Owner()))).Should().Be("owner");
    }

    [Fact]
    public async Task The_local_tier_holds_null()
    {
        var calls = 0;
        var cache = Cache(Owner());

        await cache.GetOrCreateLocalAsync<string?>("family:null", () => { calls++; return Task.FromResult<string?>(null); }, Ttl);
        await cache.GetOrCreateLocalAsync<string?>("family:null", () => { calls++; return Task.FromResult<string?>(null); }, Ttl);

        calls.Should().Be(1);
    }
}

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
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    private readonly MemoryCache _local = new(new MemoryCacheOptions());
    private readonly ICacheService _distributed;
    private readonly CacheFlights _flights = new();

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
            _flights,
            tenant.Object,
            new ReaderCachePartition(reader),
            Mock.Of<IDemoModeService>(d => d.IsEnabled == demo));
    }

    private static async Task<string?> ReadAsync(ITenantCache cache, string key = "family:key") =>
        (await cache.GetOrSetUnlessNullAsync<Box>(key, () => Task.FromResult<Box?>(null), Ttl))?.Value;

    private static Task WriteAsync(ITenantCache cache, string value, string key = "family:key") =>
        cache.GetOrSetAsync(key, () => Task.FromResult(new Box(value)), Ttl);

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
        await WriteAsync(Cache(Owner()), "other family", "other:key");
        await WriteAsync(Cache(Owner(), TenantB), "tenant b");

        await Cache(ClampedMember()).RemoveFamilyAsync("family");

        (await ReadAsync(Cache(Owner()))).Should().BeNull();
        (await ReadAsync(Cache(Owner(), demo: true))).Should().BeNull();
        (await ReadAsync(Cache(share))).Should().BeNull();
        (await ReadAsync(Cache(Owner()), "other:key")).Should().Be("other family");
        (await ReadAsync(Cache(Owner(), TenantB))).Should().Be("tenant b");
    }

    [Fact]
    public async Task Removing_a_tenant_clears_every_family_of_that_tenant_only()
    {
        await WriteAsync(Cache(Share(Scope.GlucoseRead)), "share");
        await WriteAsync(Cache(Owner()), "other family", "other:key");
        await WriteAsync(Cache(Owner(), TenantB), "tenant b");

        await Cache(Owner()).RemoveTenantAsync(TenantA);

        (await ReadAsync(Cache(Share(Scope.GlucoseRead)))).Should().BeNull();
        (await ReadAsync(Cache(Owner()), "other:key")).Should().BeNull();
        (await ReadAsync(Cache(Owner(), TenantB))).Should().Be("tenant b");
    }

    [Fact]
    public async Task Without_a_resolved_tenant_reads_miss_writes_drop_and_removal_does_nothing()
    {
        await WriteAsync(Cache(Owner()), "owner");
        var tenant = new Mock<ITenantAccessor>();
        var unresolved = new TenantCache(
            _distributed, _local, _flights, tenant.Object, new ReaderCachePartition(Owner()), Mock.Of<IDemoModeService>());

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

    private static Func<Task<Box>> Held(TaskCompletionSource<Box> held, Action onRun, CancellationToken ct = default) =>
        async () =>
        {
            onRun();
            return await held.Task.WaitAsync(ct);
        };

    [Fact]
    public async Task Concurrent_cold_reads_of_one_key_run_the_factory_once()
    {
        var runs = 0;
        var held = new TaskCompletionSource<Box>();

        var reads = Enumerable.Range(0, 8)
            .Select(_ => Cache(Owner()).GetOrSetAsync("family:key", Held(held, () => Interlocked.Increment(ref runs)), Ttl))
            .ToList();
        held.SetResult(new Box("loaded"));

        (await Task.WhenAll(reads)).Should().AllSatisfy(box => box.Value.Should().Be("loaded"));
        runs.Should().Be(1);
        (await ReadAsync(Cache(Owner()))).Should().Be("loaded");
    }

    [Fact]
    public async Task Concurrent_cold_local_reads_of_one_key_run_the_factory_once()
    {
        var runs = 0;
        var held = new TaskCompletionSource<Box>();

        var reads = Enumerable.Range(0, 8)
            .Select(_ => Cache(Owner()).GetOrCreateLocalAsync("family:local", Held(held, () => Interlocked.Increment(ref runs)), Ttl))
            .ToList();
        held.SetResult(new Box("loaded"));

        (await Task.WhenAll(reads)).Should().AllSatisfy(box => box.Value.Should().Be("loaded"));
        runs.Should().Be(1);
    }

    [Fact]
    public async Task A_warm_hit_does_not_call_the_factory()
    {
        await WriteAsync(Cache(Owner()), "warm");
        Cache(Owner()).SetLocal("family:local", new Box("warm"), Ttl);
        var calls = 0;

        var distributed = await Cache(Owner()).GetOrSetAsync("family:key", () => Task.FromResult(new Box($"{++calls}")), Ttl);
        var local = await Cache(Owner()).GetOrCreateLocalAsync("family:local", () => Task.FromResult(new Box($"{++calls}")), Ttl);

        distributed.Value.Should().Be("warm");
        local.Value.Should().Be("warm");
        calls.Should().Be(0);
    }

    [Fact]
    public async Task When_the_owner_cancels_a_waiting_caller_runs_its_own_factory()
    {
        using var ownerCts = new CancellationTokenSource();
        var ownerRuns = 0;
        var joinerRuns = 0;
        var held = new TaskCompletionSource<Box>();

        var owner = Cache(Owner()).GetOrSetAsync(
            "family:key", Held(held, () => ownerRuns++, ownerCts.Token), Ttl, ownerCts.Token);
        var joiner = Cache(Owner()).GetOrSetAsync(
            "family:key", () => { joinerRuns++; return Task.FromResult(new Box("joiner")); }, Ttl);
        joinerRuns.Should().Be(0);
        ownerCts.Cancel();

        var ownerRead = () => owner.WaitAsync(Bound);
        await ownerRead.Should().ThrowAsync<OperationCanceledException>();
        (await joiner.WaitAsync(Bound)).Value.Should().Be("joiner");
        ownerRuns.Should().Be(1);
        joinerRuns.Should().Be(1);
    }

    [Fact]
    public async Task A_cancelling_joiner_throws_its_own_cancellation_and_the_owner_finishes()
    {
        using var joinerCts = new CancellationTokenSource();
        var held = new TaskCompletionSource<Box>();
        var joinerRuns = 0;

        var owner = Cache(Owner()).GetOrSetAsync("family:key", Held(held, () => { }), Ttl);
        var joiner = Cache(Owner()).GetOrSetAsync(
            "family:key", () => { joinerRuns++; return Task.FromResult(new Box("joiner")); }, Ttl, joinerCts.Token);
        joinerCts.Cancel();

        var joinerRead = () => joiner;
        (await joinerRead.Should().ThrowAsync<OperationCanceledException>())
            .Which.CancellationToken.Should().Be(joinerCts.Token);
        owner.IsCompleted.Should().BeFalse();
        held.SetResult(new Box("owner"));
        (await owner).Value.Should().Be("owner");
        joinerRuns.Should().Be(0);
    }

    [Fact]
    public async Task A_failure_that_is_not_a_cancellation_reaches_every_waiting_caller()
    {
        var held = new TaskCompletionSource<Box>();
        var joinerRuns = 0;

        var owner = Cache(Owner()).GetOrSetAsync("family:key", Held(held, () => { }), Ttl);
        var joiner = Cache(Owner()).GetOrSetAsync(
            "family:key", () => { joinerRuns++; return Task.FromResult(new Box("joiner")); }, Ttl);
        held.SetException(new InvalidOperationException("database down"));

        var ownerRead = () => owner;
        var joinerRead = () => joiner;
        await ownerRead.Should().ThrowAsync<InvalidOperationException>().WithMessage("database down");
        await joinerRead.Should().ThrowAsync<InvalidOperationException>().WithMessage("database down");
        joinerRuns.Should().Be(0);
    }

    [Fact]
    public async Task Different_partitions_do_not_share_a_run()
    {
        var held = new TaskCompletionSource<Box>();
        var shareRuns = 0;

        var owner = Cache(Owner()).GetOrSetAsync("family:key", Held(held, () => { }), Ttl);
        var share = await Cache(Share(Scope.GlucoseRead)).GetOrSetAsync(
            "family:key", () => { shareRuns++; return Task.FromResult(new Box("share")); }, Ttl).WaitAsync(Bound);

        share.Value.Should().Be("share");
        shareRuns.Should().Be(1);
        held.SetResult(new Box("owner"));
        (await owner).Value.Should().Be("owner");
    }

    [Fact]
    public async Task After_a_failed_read_the_next_read_runs_its_factory()
    {
        var failed = () => Cache(Owner()).GetOrSetAsync(
            "family:key", () => Task.FromException<Box>(new InvalidOperationException("database down")), Ttl);
        await failed.Should().ThrowAsync<InvalidOperationException>();

        var next = await Cache(Owner()).GetOrSetAsync(
            "family:key", () => Task.FromResult(new Box("loaded")), Ttl).WaitAsync(Bound);

        next.Value.Should().Be("loaded");
    }

    [Fact]
    public async Task After_a_read_and_an_invalidation_the_next_read_runs_its_factory()
    {
        await Cache(Owner()).GetOrSetAsync("family:key", () => Task.FromResult(new Box("old")), Ttl);
        await Cache(Owner()).RemoveFamilyAsync("family");

        var next = await Cache(Owner()).GetOrSetAsync(
            "family:key", () => Task.FromResult(new Box("new")), Ttl).WaitAsync(Bound);

        next.Value.Should().Be("new");
    }
}

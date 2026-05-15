using FluentAssertions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

public class ConnectorTokenCacheTests
{
    private readonly ConnectorTokenCache _cache = new();

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenNoCachedSession()
    {
        var result = await _cache.GetAsync("dexcom", Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ReturnsCachedSession_WhenNotExpired()
    {
        var tenantId = Guid.NewGuid();
        var session = new ConnectorSession("token-123", DateTime.UtcNow.AddHours(1));

        await _cache.SetAsync("dexcom", tenantId, session);
        var result = await _cache.GetAsync("dexcom", tenantId);

        result.Should().NotBeNull();
        result!.Token.Should().Be("token-123");
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenSessionExpired()
    {
        var tenantId = Guid.NewGuid();
        var session = new ConnectorSession("expired-token", DateTime.UtcNow.AddMinutes(-1));

        await _cache.SetAsync("dexcom", tenantId, session);
        var result = await _cache.GetAsync("dexcom", tenantId);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_IsolatesTenants()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await _cache.SetAsync("dexcom", tenantA, new ConnectorSession("token-A", DateTime.UtcNow.AddHours(1)));
        await _cache.SetAsync("dexcom", tenantB, new ConnectorSession("token-B", DateTime.UtcNow.AddHours(1)));

        var resultA = await _cache.GetAsync("dexcom", tenantA);
        var resultB = await _cache.GetAsync("dexcom", tenantB);

        resultA!.Token.Should().Be("token-A");
        resultB!.Token.Should().Be("token-B");
    }

    [Fact]
    public async Task Invalidate_RemovesEntry()
    {
        var tenantId = Guid.NewGuid();
        await _cache.SetAsync("dexcom", tenantId, new ConnectorSession("token-123", DateTime.UtcNow.AddHours(1)));

        _cache.Invalidate("dexcom", tenantId);
        var result = await _cache.GetAsync("dexcom", tenantId);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Invalidate_DoesNotAffectOtherTenants()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await _cache.SetAsync("dexcom", tenantA, new ConnectorSession("token-A", DateTime.UtcNow.AddHours(1)));
        await _cache.SetAsync("dexcom", tenantB, new ConnectorSession("token-B", DateTime.UtcNow.AddHours(1)));

        _cache.Invalidate("dexcom", tenantA);

        var resultA = await _cache.GetAsync("dexcom", tenantA);
        var resultB = await _cache.GetAsync("dexcom", tenantB);

        resultA.Should().BeNull();
        resultB!.Token.Should().Be("token-B");
    }

    [Fact]
    public async Task GetLockAsync_ReturnsSameSemaphore_ForSameTenant()
    {
        var tenantId = Guid.NewGuid();

        var lock1 = await _cache.GetLockAsync("dexcom", tenantId);
        var lock2 = await _cache.GetLockAsync("dexcom", tenantId);

        ReferenceEquals(lock1, lock2).Should().BeTrue();
    }

    [Fact]
    public async Task GetLockAsync_ReturnsDifferentSemaphores_ForDifferentTenants()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var lockA = await _cache.GetLockAsync("dexcom", tenantA);
        var lockB = await _cache.GetLockAsync("dexcom", tenantB);

        ReferenceEquals(lockA, lockB).Should().BeFalse();
    }

    [Fact]
    public async Task DifferentConnectors_SameTenant_AreIsolated()
    {
        var tenantId = Guid.NewGuid();

        await _cache.SetAsync("dexcom", tenantId,
            new ConnectorSession("dexcom-token", DateTime.UtcNow.AddHours(1)));
        await _cache.SetAsync("glooko", tenantId,
            new ConnectorSession("glooko-token", DateTime.UtcNow.AddHours(1)));

        (await _cache.GetAsync("dexcom", tenantId))!.Token.Should().Be("dexcom-token");
        (await _cache.GetAsync("glooko", tenantId))!.Token.Should().Be("glooko-token");
    }
}

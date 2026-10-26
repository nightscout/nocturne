using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// The owner's rename touches only the name, and is seen on the next request rather than when
/// the cached tenant context expires.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TenantServiceDisplayNameTests : IDisposable
{
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _otherTenantId = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    public TenantServiceDisplayNameTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenantId, "mine");
        _db.SeedTenant(_otherTenantId, "theirs");
    }

    public void Dispose() => _db.Dispose();

    private TenantService Service() => new(
        _db.ContextFactory.AsNocturneFactory(),
        _cache,
        Options.Create(new OperatorConfiguration()),
        Mock.Of<IHttpClientFactory>(),
        Mock.Of<ITenantRoleService>(),
        new GrantRevocationService(new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()))),
        Mock.Of<ILogger<TenantService>>());

    [Fact]
    public async Task RenamesOnlyThatTenantAndKeepsItsSlug()
    {
        var saved = await Service().SetDisplayNameAsync(_tenantId, "Sam's site");

        saved.DisplayName.Should().Be("Sam's site");
        saved.Slug.Should().Be("mine");
        (await Service().GetSettingsAsync(_tenantId)).DisplayName.Should().Be("Sam's site");
        (await Service().GetSettingsAsync(_otherTenantId)).DisplayName.Should().NotBe("Sam's site");
    }

    [Fact]
    public async Task EvictsTheCachedTenantContext()
    {
        var stale = new TenantContext(_tenantId, "mine", "Old name", IsActive: true, IsDemo: false);
        _cache.Set(TenantResolutionMiddleware.TenantCacheKey("mine"), stale);
        _cache.Set(TenantResolutionMiddleware.SoleTenantCacheKey, stale);

        await Service().SetDisplayNameAsync(_tenantId, "New name");

        _cache.TryGetValue(TenantResolutionMiddleware.TenantCacheKey("mine"), out _).Should().BeFalse();
        _cache.TryGetValue(TenantResolutionMiddleware.SoleTenantCacheKey, out _).Should().BeFalse();
    }

    [Fact]
    public async Task ThrowsForAnUnknownTenant()
    {
        var act = () => Service().SetDisplayNameAsync(Guid.CreateVersion7(), "Anything");

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }
}

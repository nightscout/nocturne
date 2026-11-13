using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Models.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Security;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// A tenant write that changes what a share link resolves to is seen by the next request on that
/// link, not when the cached resolution expires.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TenantServiceShareLinkEvictionTests : IDisposable
{
    private const string Token = "share-token";

    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    public TenantServiceShareLinkEvictionTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenantId, "mine");
    }

    public void Dispose() => _db.Dispose();

    private TenantRepository Tenants() => new(_db.ContextFactory.AsNocturneFactory());

    private TenantService Service() => new(
        _db.ContextFactory.AsNocturneFactory(),
        Tenants(),
        new TenantMembershipRepository(_db.ContextFactory.AsNocturneFactory()),
        new SubjectRepository(_db.ContextFactory.AsNocturneFactory()),
        _cache,
        Options.Create(new OperatorConfiguration()),
        Mock.Of<IHttpClientFactory>(),
        Mock.Of<ITenantRoleService>(),
        new GrantRevocationService(
            new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()))),
        TimeProvider.System,
        Mock.Of<ILogger<TenantService>>());

    private ShareTokenCacheService ShareLinks() =>
        new(_cache, Tenants(), Mock.Of<ILogger<ShareTokenCacheService>>());

    private async Task<TenantContext?> ResolveShareLinkAsync() =>
        await ShareLinks().ResolveWithoutRecordingAccessAsync(Token, CancellationToken.None);

    private async Task SharedAndCachedAsync()
    {
        await Tenants().SetShareTokenAsync(
            _tenantId, new ShareTokenState(CredentialHash.ShareToken(Token), "cipher", DateTime.UtcNow), CancellationToken.None);

        (await ResolveShareLinkAsync())!.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task DeactivatingATenantStopsItsShareLinkResolvingAsActive()
    {
        await SharedAndCachedAsync();

        await Service().UpdateAsync(_tenantId, "Mine", isActive: false);

        (await ResolveShareLinkAsync())!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task DeletingATenantStopsItsShareLinkResolving()
    {
        await SharedAndCachedAsync();

        await Service().DeleteAsync(_tenantId);

        (await ResolveShareLinkAsync()).Should().BeNull();
    }
}

using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Infrastructure.Data.Repositories;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// A slug becomes the first DNS label of the tenant's host, and a label is at most 63 characters.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TenantServiceValidateSlugTests : IDisposable
{
    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqlite();

    private TenantService Service() => new(
        _db.ContextFactory.AsNocturneFactory(),
        new TenantRepository(_db.ContextFactory.AsNocturneFactory()),
        new TenantMembershipRepository(_db.ContextFactory.AsNocturneFactory()),
        new SubjectRepository(_db.ContextFactory.AsNocturneFactory()),
        new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new OperatorConfiguration()),
        Mock.Of<IHttpClientFactory>(),
        Mock.Of<ITenantRoleService>(),
        new GrantRevocationService(
            new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()))),
        TimeProvider.System,
        Mock.Of<ILogger<TenantService>>());

    [Theory]
    [InlineData(3)]
    [InlineData(63)]
    public async Task ValidateSlugAsync_AcceptsSlugUpToOneDnsLabel(int length)
    {
        var result = await Service().ValidateSlugAsync(new string('a', length));

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(64)]
    public async Task ValidateSlugAsync_RejectsSlugOutsideThreeToSixtyThreeCharacters(int length)
    {
        var result = await Service().ValidateSlugAsync(new string('a', length));

        result.IsValid.Should().BeFalse();
        result.Message.Should().Contain("3-63 characters");
    }

    [Fact]
    public async Task ValidateSlugAsync_RejectsAReservedSlug()
    {
        var result = await Service().ValidateSlugAsync("dev");

        result.IsValid.Should().BeFalse();
        result.Message.Should().Be("This name is reserved");
    }

    [Fact]
    public async Task ValidateSlugAsync_AcceptsAReservedSlugWhenAllowed()
    {
        var result = await Service().ValidateSlugAsync("dev", allowReserved: true);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateSlugAsync_StillRejectsAMalformedSlugWhenReservedIsAllowed()
    {
        var result = await Service().ValidateSlugAsync("d", allowReserved: true);

        result.IsValid.Should().BeFalse();
    }

    public void Dispose() => _db.Dispose();
}

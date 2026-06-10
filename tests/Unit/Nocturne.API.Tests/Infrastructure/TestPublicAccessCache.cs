using System;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// Builds a real <see cref="PublicAccessCacheService"/> for tests, backed by an empty in-memory
/// database. It resolves no Public subject, so callers see anonymous read access reported as
/// disabled — the correct default for tests that don't set up public access.
/// </summary>
internal static class TestPublicAccessCache
{
    public static PublicAccessCacheService Create()
    {
        var factory = new Mock<IDbContextFactory<NocturneDbContext>>();
        factory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => TestDbContextFactory.CreateInMemoryContext($"public_access_{Guid.NewGuid()}"));

        return new PublicAccessCacheService(
            new MemoryCache(new MemoryCacheOptions()),
            factory.Object,
            NullLogger<PublicAccessCacheService>.Instance);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Middleware.Handlers;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Middleware.Handlers;

/// <summary>
/// <see cref="DirectGrantTokenHandler.RecordLastUsedAsync"/> is started fire-and-forget from a
/// request that has already authenticated, so what it writes is never observed by the caller and a
/// failure must never reach it.
/// </summary>
[Trait("Category", "Unit")]
public class DirectGrantLastUsedTests : IDisposable
{
    private readonly SqliteTestDatabase _db;
    private readonly Mock<IDbContextFactory<NocturneDbContext>> _dbContextFactory;

    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();
    private readonly Guid _grantId = Guid.CreateVersion7();

    public DirectGrantLastUsedTests()
    {
        _db = TestDbContextFactory.CreateSqlite();

        using (var ctx = _db.CreateContext(_tenantId))
        {
            ctx.Tenants.Add(new TenantEntity
            {
                Id = _tenantId,
                Slug = "default",
                DisplayName = "Default",
                IsActive = true,
            });
            ctx.Subjects.Add(new SubjectEntity
            {
                Id = _subjectId,
                Name = "Test User",
                IsActive = true,
            });
            ctx.OAuthGrants.Add(new OAuthGrantEntity
            {
                Id = _grantId,
                SubjectId = _subjectId,
                TenantId = _tenantId,
                GrantType = OAuthGrantTypes.Direct,
                TokenHash = "token-hash",
                Scopes = [Scope.GlucoseRead],
                CreatedAt = DateTime.UtcNow,
            });
            ctx.SaveChanges();
        }

        _dbContextFactory = new Mock<IDbContextFactory<NocturneDbContext>>();
        _dbContextFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _db.CreateContext(_tenantId));
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Records_when_where_and_by_what_the_grant_was_presented()
    {
        await DirectGrantTokenHandler.RecordLastUsedAsync(
            _dbContextFactory.Object, Mock.Of<ILogger>(), _grantId, _tenantId, "203.0.113.7", "xDrip4iOS");

        await using var ctx = _db.CreateContext(_tenantId);
        var grant = await ctx.OAuthGrants.AsNoTracking().FirstAsync(g => g.Id == _grantId);

        grant.LastUsedAt.Should().NotBeNull();
        grant.LastUsedIp.Should().Be("203.0.113.7");
        grant.LastUsedUserAgent.Should().Be("xDrip4iOS");
    }

    [Fact]
    public async Task A_failure_is_swallowed_rather_than_thrown_at_the_request()
    {
        var logger = new Mock<ILogger>();
        var brokenFactory = new Mock<IDbContextFactory<NocturneDbContext>>();
        brokenFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no connection"));

        var record = async () => await DirectGrantTokenHandler.RecordLastUsedAsync(
            brokenFactory.Object, logger.Object, _grantId, _tenantId, null, null);

        await record.Should().NotThrowAsync();
    }
}

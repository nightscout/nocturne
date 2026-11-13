using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Cache.Configuration;
using Nocturne.Infrastructure.Cache.Services;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Refresh-token rotation against a real schema, where the claim on the presented token and the
/// insert of its successor have to commit or fail together.
/// </summary>
[Trait("Category", "Unit")]
public class RefreshTokenRotationPersistenceTests : IDisposable
{
    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqlite();
    private readonly Guid _subjectId = Guid.CreateVersion7();
    private readonly Mock<IJwtService> _jwtService = new();
    private readonly RotationSuccessorCache _successorCache = new(new MemoryCacheService(
        new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new CacheConfiguration()),
        NullLogger<MemoryCacheService>.Instance));

    public RefreshTokenRotationPersistenceTests()
    {
        _jwtService.Setup(j => j.HashRefreshToken(It.IsAny<string>())).Returns((string t) => $"hash:{t}");
        _jwtService.Setup(j => j.GenerateRefreshToken()).Returns(() => Guid.NewGuid().ToString("N"));

        using var seed = _db.CreateContext();
        seed.Subjects.Add(new SubjectEntity { Id = _subjectId, Name = "Owner", IsActive = true });
        seed.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_failed_successor_insert_leaves_the_presented_token_valid_and_rotatable()
    {
        var presented = await Service(_db.Options).CreateRefreshTokenAsync(_subjectId, "session-a");
        var failing = Service(OptionsWith(new FirstTransactionFault(TransactionFault.SaveChanges)));

        var act = () => failing.RotateRefreshTokenAsync(presented);

        await act.Should().ThrowAsync<TransientFault>();
        (await Service(_db.Options).ValidateRefreshTokenAsync(presented))
            .Should().Be(new RefreshTokenSession(_subjectId, "session-a"));

        var successor = await failing.RotateRefreshTokenAsync(presented);

        successor.Should().NotBeNull();
        (await Service(_db.Options).ValidateRefreshTokenAsync(successor!))
            .Should().Be(new RefreshTokenSession(_subjectId, "session-a"));
        (await Service(_db.Options).ValidateRefreshTokenAsync(presented)).Should().BeNull();
    }

    [Fact]
    public async Task A_rotation_whose_commit_reports_failure_but_landed_returns_its_successor()
    {
        var presented = await Service(_db.Options).CreateRefreshTokenAsync(_subjectId, "session-a");
        var fault = new FirstTransactionFault(TransactionFault.AfterCommit);

        var successor = await Service(TransactionFaultOptions.RetryingSqlite(_db.Connection, fault))
            .RotateRefreshTokenAsync(presented);

        fault.Fired.Should().BeTrue();
        successor.Should().NotBeNull();
        (await Service(_db.Options).ValidateRefreshTokenAsync(successor!)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_legacy_chain_keeps_one_session_id_across_rotation()
    {
        var presented = await Service(_db.Options).CreateRefreshTokenAsync(_subjectId, oidcSessionId: null);
        var service = Service(_db.Options);
        var before = await service.FindOwnerAsync(presented);

        var successor = await service.RotateRefreshTokenAsync(presented);

        (await service.FindOwnerAsync(successor!)).Should().Be(before);
        var sessions = await service.GetActiveSessionsForSubjectAsync(_subjectId);
        sessions.Should().ContainSingle().Which.OidcSessionId.Should().Be(before!.SessionId);

        (await service.RevokeSessionForSubjectAsync(_subjectId, before.SessionId!, "test"))
            .Should().Be(1);
        (await service.ValidateRefreshTokenAsync(successor!)).Should().BeNull();
    }

    [Theory]
    [InlineData(SuccessorRevocation.Logout)]
    [InlineData(SuccessorRevocation.DeviceRevoke)]
    [InlineData(SuccessorRevocation.ReuseDetection)]
    public async Task A_grace_replay_of_a_revoked_successor_is_refused(SuccessorRevocation revocation)
    {
        var service = Service(_db.Options);
        var presented = await service.CreateRefreshTokenAsync(_subjectId, "session-a");
        var successor = await service.RotateRefreshTokenAsync(presented);

        switch (revocation)
        {
            case SuccessorRevocation.Logout:
                await service.RevokeRefreshTokenAsync(successor!, "User logout");
                break;
            case SuccessorRevocation.DeviceRevoke:
                await service.RevokeSessionForSubjectAsync(_subjectId, "session-a", "Revoked by user");
                break;
            case SuccessorRevocation.ReuseDetection:
                var stale = await service.CreateRefreshTokenAsync(_subjectId, "session-a");
                await service.RotateRefreshTokenAsync(stale);
                await Backdate(stale, RefreshTokenRotation.GracePeriod * 2);
                (await service.RotateRefreshTokenAsync(stale)).Should().BeNull();
                break;
        }

        (await service.ValidateRefreshTokenAsync(successor!)).Should().BeNull();
        (await service.RotateRefreshTokenAsync(presented)).Should().BeNull();
    }

    [Fact]
    public async Task A_grace_replay_of_an_expired_successor_is_refused()
    {
        var service = Service(_db.Options);
        var presented = await service.CreateRefreshTokenAsync(_subjectId, "session-a");
        var successor = await service.RotateRefreshTokenAsync(presented);

        await using (var db = _db.CreateContext())
        {
            var row = await db.RefreshTokens.SingleAsync(t => t.TokenHash == $"hash:{successor}");
            row.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }

        (await service.RotateRefreshTokenAsync(presented)).Should().BeNull();
    }

    [Fact]
    public async Task A_grace_replay_of_a_live_successor_returns_it()
    {
        var service = Service(_db.Options);
        var presented = await service.CreateRefreshTokenAsync(_subjectId, "session-a");
        var successor = await service.RotateRefreshTokenAsync(presented);

        (await service.RotateRefreshTokenAsync(presented)).Should().Be(successor);
    }

    [Fact]
    public async Task A_demo_session_rotated_inside_the_grace_period_still_replays_its_successor()
    {
        await using (var seed = _db.CreateContext())
        {
            var subject = await seed.Subjects.SingleAsync(s => s.Id == _subjectId);
            subject.IsDemoSubject = true;
            await seed.SaveChangesAsync();
        }

        var service = Service(_db.Options);
        var presented = await service.CreateRefreshTokenAsync(_subjectId, "session-a");
        var successor = await service.RotateRefreshTokenAsync(presented);

        (await service.RotateRefreshTokenAsync(presented)).Should().Be(successor);
    }

    public enum SuccessorRevocation
    {
        Logout,
        DeviceRevoke,
        ReuseDetection,
    }

    private async Task Backdate(string rotated, TimeSpan age)
    {
        await using var db = _db.CreateContext();
        var row = await db.RefreshTokens.SingleAsync(t => t.TokenHash == $"hash:{rotated}");
        row.RevokedAt = DateTime.UtcNow - age;
        await db.SaveChangesAsync();
    }

    private DbContextOptions<NocturneDbContext> OptionsWith(IInterceptor fault) =>
        new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_db.Connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .AddInterceptors(fault)
            .Options;

    private RefreshTokenService Service(DbContextOptions<NocturneDbContext> options) =>
        new(
            new EfFirstPartyTokenRepository(new OptionsFactory(options).AsNocturneFactory()),
            _jwtService.Object,
            _successorCache,
            Options.Create(new JwtOptions { RefreshTokenLifetimeDays = 7 }),
            NullLogger<RefreshTokenService>.Instance);

    private sealed class OptionsFactory(DbContextOptions<NocturneDbContext> options)
        : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="OAuthTokenRepository"/> over SQLite, which runs the conditional updates the claims are
/// made of: each claim is granted once, the pinned sweep reaches only its tenant, and a lookup
/// carries what the token's grant says. The revoke of a grant's tokens runs on a unit of work and
/// is covered with the grant repository.
/// </summary>
public class OAuthTokenRepositoryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _otherTenant = Guid.CreateVersion7();
    private readonly Guid _subject = Guid.CreateVersion7();
    private readonly Guid _client = Guid.CreateVersion7();
    private readonly Guid _grant = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;

    public OAuthTokenRepositoryTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant).SeedTenant(_otherTenant, "other");
        using var db = _db.CreateContext();
        db.Subjects.Add(new SubjectEntity { Id = _subject, Name = "Owner" });
        db.OAuthClients.Add(new OAuthClientEntity { Id = _client, ClientId = "app", RedirectUris = "[]" });
        db.OAuthGrants.Add(new OAuthGrantEntity
        {
            Id = _grant,
            ClientEntityId = _client,
            SubjectId = _subject,
            Scopes = ["glucose.read"],
            GrantType = "app",
            LimitTo24Hours = true,
        });
        db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private OAuthTokenRepository Repository(Guid? tenant = null) =>
        new(new TestNocturneDbContextFactory(_db.CreateContext(tenant ?? _tenant)));

    private NewAuthorizationCode Code(string hash, DateTime? expiresAt = null) => new(
        _client, _subject, hash, ["glucose.read"], "https://app.example/cb", "challenge",
        expiresAt ?? Now.AddMinutes(10), LimitTo24Hours: false);

    private NewOAuthRefreshToken Token(string hash, Guid? tenant = null, Guid? grant = null) =>
        new(tenant ?? _tenant, grant ?? _grant, hash, Now.AddDays(90));

    [Fact]
    public async Task AnAuthorizationCodeIsClaimedOnce()
    {
        var repo = Repository();
        await repo.StoreAuthorizationCodeAsync(Code("code"), CancellationToken.None);

        (await repo.TryClaimAuthorizationCodeAsync("code", Now, CancellationToken.None)).Should().BeTrue();
        (await repo.TryClaimAuthorizationCodeAsync("code", Now, CancellationToken.None)).Should().BeFalse();

        var stored = await repo.FindAuthorizationCodeAsync("code", CancellationToken.None);
        stored!.RedeemedAt.Should().Be(Now);
        stored.ClientId.Should().Be("app");
    }

    [Fact]
    public async Task AnExpiredOrUnknownAuthorizationCodeCannotBeClaimed()
    {
        var repo = Repository();
        await repo.StoreAuthorizationCodeAsync(Code("stale", expiresAt: Now.AddMinutes(-1)), CancellationToken.None);

        (await repo.TryClaimAuthorizationCodeAsync("stale", Now, CancellationToken.None)).Should().BeFalse();
        (await repo.TryClaimAuthorizationCodeAsync("missing", Now, CancellationToken.None)).Should().BeFalse();
        (await repo.FindAuthorizationCodeAsync("stale", CancellationToken.None))!.RedeemedAt.Should().BeNull();
    }

    [Fact]
    public async Task ALiveRefreshTokenIsRotatedOnceAndLinkedToItsSuccessor()
    {
        var repo = Repository();
        var token = await repo.StoreRefreshTokenAsync(Token("t1"), CancellationToken.None);
        var successor = await repo.StoreRefreshTokenAsync(Token("t2"), CancellationToken.None);
        var rival = await repo.StoreRefreshTokenAsync(Token("t3"), CancellationToken.None);

        (await repo.TryRotateRefreshTokenAsync(token, successor, Now, CancellationToken.None)).Should().BeTrue();
        (await repo.TryRotateRefreshTokenAsync(token, rival, Now.AddSeconds(1), CancellationToken.None)).Should().BeFalse();

        var rotated = await repo.GetRefreshTokenAsync(token, CancellationToken.None);
        rotated!.RevokedAt.Should().Be(Now);
        rotated.ReplacedById.Should().Be(successor);
        (await repo.IsRefreshTokenSignedOutAsync(token, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task GraceRedemptionsStopAtTheCap()
    {
        var repo = Repository();
        var token = await repo.StoreRefreshTokenAsync(Token("t1"), CancellationToken.None);

        (await repo.TryRedeemRefreshTokenGraceAsync(token, maxRedemptions: 2, CancellationToken.None)).Should().BeTrue();
        (await repo.TryRedeemRefreshTokenGraceAsync(token, maxRedemptions: 2, CancellationToken.None)).Should().BeTrue();
        (await repo.TryRedeemRefreshTokenGraceAsync(token, maxRedemptions: 2, CancellationToken.None)).Should().BeFalse();

        (await repo.GetRefreshTokenAsync(token, CancellationToken.None))!.GraceRedemptions.Should().Be(2);
    }

    [Fact]
    public async Task ATokenRevokedWithoutASuccessorIsSignedOut()
    {
        var repo = Repository();
        var token = await repo.StoreRefreshTokenAsync(Token("t1"), CancellationToken.None);

        (await repo.RevokeRefreshTokenAsync(token, Now, CancellationToken.None)).Should().BeTrue();
        (await repo.RevokeRefreshTokenAsync(token, Now.AddHours(1), CancellationToken.None)).Should().BeFalse();

        (await repo.IsRefreshTokenSignedOutAsync(token, CancellationToken.None)).Should().BeTrue();
        (await repo.GetRefreshTokenAsync(token, CancellationToken.None))!.RevokedAt.Should().Be(Now, "a revocation is not re-stamped");
    }

    [Fact]
    public async Task ARefreshTokenCarriesWhatItsGrantSays()
    {
        var repo = Repository();
        await repo.StoreRefreshTokenAsync(Token("t1"), CancellationToken.None);

        var found = await repo.FindRefreshTokenAsync("t1", CancellationToken.None);

        found!.Grant.Should().Be(new OAuthRefreshTokenGrant(
            _grant, _tenant, _subject, found.Grant!.Scopes, "app", LimitTo24Hours: true, IsRevoked: false));
        found.Grant.Scopes.Should().Equal("glucose.read");
        (await repo.FindRefreshTokenAsync("missing", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ADeviceCodeIsRedeemedOnce()
    {
        var repo = Repository();
        var id = await repo.StoreDeviceCodeAsync(
            new NewDeviceCode("app", "device", "BCDFGHJK", ["glucose.read"], Now.AddMinutes(30), 5), CancellationToken.None);
        (await repo.ApproveDeviceCodeAsync(id, _subject, _grant, Now, CancellationToken.None)).Should().BeTrue();

        (await repo.TryRedeemDeviceCodeAsync(id, Now, CancellationToken.None)).Should().BeTrue();
        (await repo.TryRedeemDeviceCodeAsync(id, Now.AddSeconds(1), CancellationToken.None)).Should().BeFalse();

        var stored = await repo.FindDeviceCodeAsync("device", CancellationToken.None);
        stored!.RedeemedAt.Should().Be(Now);
        stored.GrantId.Should().Be(_grant);
        stored.SubjectId.Should().Be(_subject);
        (await repo.UserCodeExistsAsync("BCDFGHJK", CancellationToken.None)).Should().BeTrue();
        (await repo.FindDeviceCodeByUserCodeAsync("BCDFGHJK", CancellationToken.None))!.Id.Should().Be(id);
    }

    [Fact]
    public async Task APollIsRecordedAndEachBumpAddsToTheInterval()
    {
        var repo = Repository();
        var id = await repo.StoreDeviceCodeAsync(
            new NewDeviceCode("app", "device", "BCDFGHJK", ["glucose.read"], Now.AddMinutes(30), 5), CancellationToken.None);

        (await repo.RecordDevicePollAsync(id, Now, CancellationToken.None)).Should().BeTrue();
        (await repo.RecordDevicePollAsync(Guid.CreateVersion7(), Now, CancellationToken.None)).Should().BeFalse();
        (await repo.BumpDeviceCodeIntervalAsync(id, 5, CancellationToken.None)).Should().BeTrue();
        (await repo.BumpDeviceCodeIntervalAsync(id, 5, CancellationToken.None)).Should().BeTrue();
        (await repo.BumpDeviceCodeIntervalAsync(Guid.CreateVersion7(), 5, CancellationToken.None)).Should().BeFalse();

        var stored = await repo.FindDeviceCodeAsync("device", CancellationToken.None);
        stored!.LastPolledAt.Should().Be(Now);
        stored.Interval.Should().Be(15);
        (await repo.ApproveDeviceCodeAsync(Guid.CreateVersion7(), _subject, _grant, Now, CancellationToken.None)).Should().BeFalse();
        (await repo.DenyDeviceCodeAsync(Guid.CreateVersion7(), Now, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task DeletingExpiredCodesIsPinnedToTheTenantGiven()
    {
        var cutoff = Now.AddHours(-1);
        await Repository().StoreAuthorizationCodeAsync(Code("old", expiresAt: cutoff.AddMinutes(-1)), CancellationToken.None);
        await Repository().StoreAuthorizationCodeAsync(Code("live"), CancellationToken.None);
        await Repository().StoreDeviceCodeAsync(
            new NewDeviceCode("app", "old-device", "BCDFGHJK", ["glucose.read"], cutoff.AddMinutes(-1), 5), CancellationToken.None);
        await Repository(_otherTenant).StoreDeviceCodeAsync(
            new NewDeviceCode("app", "other-device", "MNPQRSTV", ["glucose.read"], cutoff.AddMinutes(-1), 5), CancellationToken.None);

        var deleted = await Repository(_otherTenant).DeleteExpiredCodesAsync(_tenant, cutoff, CancellationToken.None);

        deleted.Should().Be(new ExpiredCodeDeletion(DeviceCodes: 1, AuthorizationCodes: 1));
        await using var check = _db.CreateContext(Guid.Empty);
        (await check.OAuthAuthorizationCodes.IgnoreQueryFilters().Select(c => c.CodeHash).ToListAsync()).Should().Equal("live");
        (await check.OAuthDeviceCodes.IgnoreQueryFilters().Select(d => d.DeviceCodeHash).ToListAsync()).Should().Equal("other-device");
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.ClientDevices;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Pins the set of consequences every revoke applies, once, for a grant of each type. The three
/// services keep their own ownership and grant-type checks; the consequences themselves are the
/// same mechanism, so a test per service would assert the same thing three times.
/// </summary>
public class GrantRevocationTests : IDisposable
{
    private const string App = OAuthGrantTypes.App;
    private const string Direct = OAuthGrantTypes.Direct;
    private const string ApiKey = "api-key";
    private const string Guest = OAuthGrantTypes.Guest;

    private readonly SqliteTestDatabase _db;
    private readonly GuestSessionCacheService _sessionCache;
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _ownerSubjectId = Guid.CreateVersion7();

    public GrantRevocationTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenantId);
        _sessionCache = new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()));
    }

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData(App)]
    [InlineData(Direct)]
    [InlineData(ApiKey)]
    [InlineData(Guest)]
    public async Task Revoking_a_grant_applies_every_consequence_and_spares_other_grants(string grantKind)
    {
        using var db = _db.CreateContext();

        await SeedSubjectAsync(db, _ownerSubjectId);

        var target = NewGrant(grantKind, _ownerSubjectId);
        var other = NewGrant(grantKind, _ownerSubjectId);
        db.OAuthGrants.AddRange(target, other);
        await db.SaveChangesAsync();

        var targetToken = AddRefreshToken(db, target.Id);
        var otherToken = AddRefreshToken(db, other.Id);
        var targetDevice = AddDevice(db, _ownerSubjectId, target.Id);
        var otherDevice = AddDevice(db, _ownerSubjectId, other.Id);
        await db.SaveChangesAsync();

        _sessionCache.Set(_tenantId, target.Id, Session(target.Id));
        _sessionCache.Set(_tenantId, other.Id, Session(other.Id));

        await RevokeAsync(db, grantKind, target.Id);

        (await db.OAuthGrants.AsNoTracking().SingleAsync(g => g.Id == target.Id))
            .RevokedAt.Should().NotBeNull();
        (await db.OAuthGrants.AsNoTracking().SingleAsync(g => g.Id == other.Id))
            .RevokedAt.Should().BeNull();

        (await db.OAuthRefreshTokens.AsNoTracking().SingleAsync(t => t.Id == targetToken))
            .RevokedAt.Should().NotBeNull();
        (await db.OAuthRefreshTokens.AsNoTracking().SingleAsync(t => t.Id == otherToken))
            .RevokedAt.Should().BeNull();

        (await db.ClientDevices.AsNoTracking().AnyAsync(d => d.Id == targetDevice)).Should().BeFalse();
        (await db.ClientDevices.AsNoTracking().AnyAsync(d => d.Id == otherDevice)).Should().BeTrue();

        _sessionCache.TryGet(_tenantId, target.Id, out _).Should().BeFalse();
        _sessionCache.TryGet(_tenantId, other.Id, out _).Should().BeTrue();
    }

    private async Task RevokeAsync(NocturneDbContext db, string grantKind, Guid grantId)
    {
        switch (grantKind)
        {
            case Guest:
                var guestLinks = new GuestLinkService(
                    db,
                    _sessionCache,
                    new GrantRevocationService(_sessionCache),
                    NullLogger<GuestLinkService>.Instance);
                (await guestLinks.RevokeAsync(grantId, _ownerSubjectId)).Should().BeTrue();
                break;

            case App:
                var grants = new OAuthGrantService(
                    db,
                    _db.ContextFactory,
                    new Mock<IOAuthClientService>().Object,
                    _sessionCache,
                    new GrantRevocationService(_sessionCache),
                    NullLogger<OAuthGrantService>.Instance);
                await grants.RevokeGrantAsync(grantId);
                break;

            default:
                var directGrants = new DirectGrantService(
                    new Mock<IAuthAuditService>().Object,
                    new GrantRevocationService(_sessionCache),
                    NullLogger<DirectGrantService>.Instance);
                (await directGrants.RevokeAsync(
                    db, grantId, subjectId: null, ipAddress: null, userAgent: null)).Should().BeTrue();
                break;
        }
    }

    private static OAuthGrantEntity NewGrant(string grantKind, Guid subjectId)
    {
        if (grantKind == ApiKey)
        {
            return OAuthGrantEntity.AdoptedLegacyCredential(
                subjectId,
                "Imported uploader",
                [Scope.GlucoseRead],
                tokenHash: Guid.NewGuid().ToString("N"));
        }

        return new OAuthGrantEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            GrantType = grantKind,
            Scopes = [Scope.GlucoseRead],
            Label = grantKind,
        };
    }

    private static Guid AddRefreshToken(NocturneDbContext db, Guid grantId)
    {
        var token = new OAuthRefreshTokenEntity
        {
            Id = Guid.CreateVersion7(),
            GrantId = grantId,
            TokenHash = Guid.NewGuid().ToString("N"),
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(90),
        };
        db.OAuthRefreshTokens.Add(token);
        return token.Id;
    }

    private static Guid AddDevice(NocturneDbContext db, Guid subjectId, Guid grantId)
    {
        var device = new ClientDeviceEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            GrantId = grantId,
            InstallId = Guid.NewGuid().ToString("N"),
            Kind = DeviceKinds.Prelude,
        };
        db.ClientDevices.Add(device);
        return device.Id;
    }

    private static GuestSessionInfo Session(Guid grantId) =>
        new(grantId, Guid.Empty, Guid.Empty, [], null, DateTime.UtcNow.AddHours(1));

    private static async Task SeedSubjectAsync(NocturneDbContext db, Guid subjectId)
    {
        db.Subjects.Add(new SubjectEntity { Id = subjectId, Name = "Owner" });
        await db.SaveChangesAsync();
    }
}

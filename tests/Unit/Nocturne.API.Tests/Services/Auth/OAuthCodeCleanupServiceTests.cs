using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Docs;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// The sweep deletes a registered client nothing has used within a day, on every tenant, and keeps
/// any client a grant, a live code or the Scalar docs page still names.
/// </summary>
[Trait("Category", "Unit")]
public class OAuthCodeCleanupServiceTests : IDisposable
{
    private readonly Guid _tenantA = Guid.CreateVersion7();
    private readonly Guid _tenantB = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _seed;

    private static readonly DateTime LongAgo = DateTime.UtcNow - OAuthCodeCleanupService.UnusedClientRetention - TimeSpan.FromHours(1);

    public OAuthCodeCleanupServiceTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenantA);
        _seed = _db.CreateContext(Guid.Empty);
        _seed.Tenants.Add(new TenantEntity { Id = _tenantB, Slug = "tenant-b" });
        _seed.Subjects.Add(new SubjectEntity { Id = _subjectId, Name = "Owner" });
        _seed.SaveChanges();
    }

    private OAuthCodeCleanupService CreateService() =>
        new(_seed.AsFactory(), NullLogger<OAuthCodeCleanupService>.Instance);

    private async Task<OAuthClientEntity> ClientAsync(
        Guid tenantId, string clientId, DateTime createdAt, string? softwareId = null)
    {
        var client = new OAuthClientEntity
        {
            Id = Guid.CreateVersion7(),
            ClientId = clientId,
            SoftwareId = softwareId,
            RedirectUris = "[]",
        };
        await using var db = _db.CreateContext(tenantId);
        db.OAuthClients.Add(client);
        await db.SaveChangesAsync();
        await db.OAuthClients.Where(c => c.Id == client.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.CreatedAt, createdAt));
        return client;
    }

    private async Task<List<string>> RemainingAsync(Guid tenantId)
    {
        await using var db = _db.CreateContext(tenantId);
        return await db.OAuthClients.Select(c => c.ClientId).ToListAsync();
    }

    [Fact]
    public async Task CleanupAsync_DeletesUnusedClientsOlderThanADay_OnEveryTenant()
    {
        await ClientAsync(_tenantA, "unused-a", LongAgo);
        await ClientAsync(_tenantB, "unused-b", LongAgo);

        await CreateService().CleanupAsync(CancellationToken.None);

        (await RemainingAsync(_tenantA)).Should().BeEmpty();
        (await RemainingAsync(_tenantB)).Should().BeEmpty();
    }

    [Fact]
    public async Task CleanupAsync_KeepsAnUnusedClientYoungerThanADay()
    {
        await ClientAsync(_tenantA, "young", DateTime.UtcNow - TimeSpan.FromHours(23));

        await CreateService().CleanupAsync(CancellationToken.None);

        (await RemainingAsync(_tenantA)).Should().Equal("young");
    }

    [Fact]
    public async Task CleanupAsync_KeepsAClientWhoseOnlyGrantIsRevoked()
    {
        var client = await ClientAsync(_tenantA, "revoked", LongAgo);
        await using (var db = _db.CreateContext(_tenantA))
        {
            db.OAuthGrants.Add(new OAuthGrantEntity
            {
                Id = Guid.CreateVersion7(),
                ClientEntityId = client.Id,
                SubjectId = _subjectId,
                Scopes = ["glucose.read"],
                GrantType = "app",
                RevokedAt = LongAgo,
            });
            await db.SaveChangesAsync();
        }

        await CreateService().CleanupAsync(CancellationToken.None);

        (await RemainingAsync(_tenantA)).Should().Equal("revoked");
    }

    [Fact]
    public async Task CleanupAsync_KeepsAClientWithALiveAuthorizationCode()
    {
        var client = await ClientAsync(_tenantA, "auth-code", LongAgo);
        await using (var db = _db.CreateContext(_tenantA))
        {
            db.OAuthAuthorizationCodes.Add(new OAuthAuthorizationCodeEntity
            {
                Id = Guid.CreateVersion7(),
                ClientEntityId = client.Id,
                SubjectId = _subjectId,
                CodeHash = "hash",
                RedirectUri = "com.example.app://oauth/callback",
                CodeChallenge = "challenge",
                ExpiresAt = DateTime.UtcNow.AddMinutes(10),
            });
            await db.SaveChangesAsync();
        }

        await CreateService().CleanupAsync(CancellationToken.None);

        (await RemainingAsync(_tenantA)).Should().Equal("auth-code");
    }

    [Fact]
    public async Task CleanupAsync_KeepsAClientWithALiveDeviceCode()
    {
        await ClientAsync(_tenantA, "device-code", LongAgo);
        await using (var db = _db.CreateContext(_tenantA))
        {
            db.OAuthDeviceCodes.Add(new OAuthDeviceCodeEntity
            {
                Id = Guid.CreateVersion7(),
                ClientId = "device-code",
                DeviceCodeHash = "hash",
                UserCode = "ABCD1234",
                Scopes = ["glucose.read"],
                ExpiresAt = DateTime.UtcNow.AddMinutes(10),
            });
            await db.SaveChangesAsync();
        }

        await CreateService().CleanupAsync(CancellationToken.None);

        (await RemainingAsync(_tenantA)).Should().Equal("device-code");
    }

    [Fact]
    public async Task CleanupAsync_DeletesAClientWhoseOnlyCodeHasExpired()
    {
        await ClientAsync(_tenantA, "expired-code", LongAgo);
        await using (var db = _db.CreateContext(_tenantA))
        {
            db.OAuthDeviceCodes.Add(new OAuthDeviceCodeEntity
            {
                Id = Guid.CreateVersion7(),
                ClientId = "expired-code",
                DeviceCodeHash = "hash",
                UserCode = "EFGH5678",
                Scopes = ["glucose.read"],
                ExpiresAt = LongAgo,
            });
            await db.SaveChangesAsync();
        }

        await CreateService().CleanupAsync(CancellationToken.None);

        (await RemainingAsync(_tenantA)).Should().BeEmpty();
    }

    [Fact]
    public async Task CleanupAsync_KeepsTheScalarClient()
    {
        await ClientAsync(_tenantA, "scalar", LongAgo, ScalarAuthProvider.ScalarSoftwareId);

        await CreateService().CleanupAsync(CancellationToken.None);

        (await RemainingAsync(_tenantA)).Should().Equal("scalar");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task CleanupAsync_ATenantThatFails_DoesNotStopTheOthers(bool tenantAFails, bool retriesExhausted)
    {
        await ClientAsync(_tenantA, "unused-a", LongAgo);
        await ClientAsync(_tenantB, "unused-b", LongAgo);
        var failing = tenantAFails ? _tenantA : _tenantB;
        var service = new OAuthCodeCleanupService(
            new FailingForTenantFactory(_seed.AsFactory(), failing, retriesExhausted),
            NullLogger<OAuthCodeCleanupService>.Instance);

        await service.CleanupAsync(CancellationToken.None);

        (await RemainingAsync(failing)).Should().HaveCount(1);
        (await RemainingAsync(tenantAFails ? _tenantB : _tenantA)).Should().BeEmpty();
    }

    public void Dispose()
    {
        _seed.Dispose();
        _db.Dispose();
    }

    private sealed class TenantDatabaseException : DbException;

    private sealed class FailingForTenantFactory(
        INocturneDbContextFactory inner, Guid failingTenant, bool retriesExhausted)
        : INocturneDbContextFactory
    {
        public ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default) => inner.CreateAsync(ct);

        public ValueTask<NocturneDbContext> CreateForTenantAsync(Guid tenantId, CancellationToken ct = default) =>
            tenantId == failingTenant
                ? throw (retriesExhausted
                    ? new RetryLimitExceededException("retry limit", new TenantDatabaseException())
                    : new TenantDatabaseException())
                : inner.CreateForTenantAsync(tenantId, ct);

        public ValueTask<NocturneDbContext> CreateForSubjectAsync(Guid subjectId, CancellationToken ct = default) =>
            inner.CreateForSubjectAsync(subjectId, ct);

        public ValueTask<NocturneDbContext> CreatePlatformAsync(CancellationToken ct = default) =>
            inner.CreatePlatformAsync(ct);

        public ValueTask<NocturneDbContext> CreateSystemPinnedAsync(Guid tenantId, CancellationToken ct = default) =>
            inner.CreateSystemPinnedAsync(tenantId, ct);
    }
}

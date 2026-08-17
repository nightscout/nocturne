using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Data.Sqlite;
using Moq;
using Nocturne.API.Middleware.Handlers;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Middleware.Handlers;

public class ApiKeyHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;
    private readonly Mock<IDbContextFactory<NocturneDbContext>> _dbContextFactory;
    private readonly ApiKeyHandler _handler;

    private readonly Guid _testTenantId = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();

    private static readonly DateTime Now = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Now, TimeSpan.Zero));

    public ApiKeyHandlerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        using (var ctx = new NocturneDbContext(_dbOptions) { TenantId = _testTenantId })
        {
            ctx.Database.EnsureCreated();

            ctx.Tenants.Add(new TenantEntity
            {
                Id = _testTenantId,
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
            ctx.SaveChanges();
        }

        _dbContextFactory = new Mock<IDbContextFactory<NocturneDbContext>>();
        _dbContextFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new NocturneDbContext(_dbOptions) { TenantId = _testTenantId });

        var logger = new Mock<ILogger<ApiKeyHandler>>();
        _handler = new ApiKeyHandler(_dbContextFactory.Object, _clock, logger.Object);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task AuthenticateAsync_NocPrefixedToken_LooksUpBySha256TokenHash()
    {
        var token = "noc_myapikey12345";
        var tokenHash = DirectGrantTokenHandler.ComputeSha256Hex(token);

        await using (var ctx = new NocturneDbContext(_dbOptions) { TenantId = _testTenantId })
        {
            ctx.OAuthGrants.Add(new OAuthGrantEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = _subjectId,
                TenantId = _testTenantId,
                GrantType = OAuthGrantTypes.Direct,
                TokenHash = tokenHash,
                Scopes = ["glucose.read", "treatments.readwrite"],
                CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var context = CreateHttpContext();
        context.Request.Headers["api-secret"] = token;

        var result = await _handler.AuthenticateAsync(context);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.AuthContext);
        Assert.Equal(AuthType.ApiKey, result.AuthContext!.AuthType);
        Assert.Equal(_subjectId, result.AuthContext.SubjectId);
        Assert.Contains("glucose.read", result.AuthContext.Scopes);
        Assert.Contains("treatments.readwrite", result.AuthContext.Scopes);
    }

    [Fact]
    public async Task AuthenticateAsync_NonPrefixedValue_LooksUpBySha1LegacySecretHash()
    {
        var legacySecret = "myplaintextsecret";
        var sha1Hash = HashUtils.Sha1Hex(legacySecret);

        await using (var ctx = new NocturneDbContext(_dbOptions) { TenantId = _testTenantId })
        {
            ctx.OAuthGrants.Add(new OAuthGrantEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = _subjectId,
                TenantId = _testTenantId,
                GrantType = OAuthGrantTypes.Direct,
                LegacySecretHash = sha1Hash,
                Scopes = ["glucose.read"],
                CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var context = CreateHttpContext();
        context.Request.Headers["api-secret"] = sha1Hash;

        var result = await _handler.AuthenticateAsync(context);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.AuthContext);
        Assert.Equal(AuthType.ApiKey, result.AuthContext!.AuthType);
        Assert.Equal(_subjectId, result.AuthContext.SubjectId);
        Assert.Contains("glucose.read", result.AuthContext.Scopes);
    }

    [Fact]
    public async Task AuthenticateAsync_UppercaseSha1Hash_StillMatchesLowercaseStoredHash()
    {
        // The stored LegacySecretHash is canonical lowercase (HashUtils.Sha1Hex). Some clients
        // (e.g. Android) send the SHA-1 hex uppercased; the handler must normalize before the
        // case-sensitive column comparison, or authentication fails despite a valid secret.
        var legacySecret = "myplaintextsecret";
        var storedHash = HashUtils.Sha1Hex(legacySecret); // lowercase

        await using (var ctx = new NocturneDbContext(_dbOptions) { TenantId = _testTenantId })
        {
            ctx.OAuthGrants.Add(new OAuthGrantEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = _subjectId,
                TenantId = _testTenantId,
                GrantType = OAuthGrantTypes.Direct,
                LegacySecretHash = storedHash,
                Scopes = ["glucose.read"],
                CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var context = CreateHttpContext();
        context.Request.Headers["api-secret"] = storedHash.ToUpperInvariant();

        var result = await _handler.AuthenticateAsync(context);

        Assert.True(result.Succeeded);
        Assert.Equal(_subjectId, result.AuthContext!.SubjectId);
        Assert.Contains("glucose.read", result.AuthContext.Scopes);
    }

    [Fact]
    public async Task AuthenticateAsync_UnderscoreApiSecretHeader_IsAccepted()
    {
        // Some legacy Nightscout clients send the underscore spelling "api_secret" rather than
        // the canonical "api-secret"; the handler accepts both.
        var legacySecret = "underscoresecret";
        var sha1Hash = HashUtils.Sha1Hex(legacySecret);

        await using (var ctx = new NocturneDbContext(_dbOptions) { TenantId = _testTenantId })
        {
            ctx.OAuthGrants.Add(new OAuthGrantEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = _subjectId,
                TenantId = _testTenantId,
                GrantType = OAuthGrantTypes.Direct,
                LegacySecretHash = sha1Hash,
                Scopes = ["glucose.read"],
                CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var context = CreateHttpContext();
        context.Request.Headers["api_secret"] = sha1Hash;

        var result = await _handler.AuthenticateAsync(context);

        Assert.True(result.Succeeded);
        Assert.Equal(_subjectId, result.AuthContext!.SubjectId);
        Assert.Contains("glucose.read", result.AuthContext.Scopes);
    }

    [Fact]
    public async Task AuthenticateAsync_MintedTokenSentPreHashed_AuthenticatesAndDoesNotNudge()
    {
        // A minted noc_ token carries both a SHA-256 TokenHash (verbatim clients) and a SHA-1
        // LegacySecretHash (clients that pre-hash, e.g. Loop/AAPS/Trio). When such a client sends
        // SHA-1(token) in the api-secret header, it must authenticate via the legacy lookup path —
        // and must NOT trigger the migrated-secret rotation nudge.
        var token = "noc_minteduploaderkey";
        var tokenHash = DirectGrantTokenHandler.ComputeSha256Hex(token);
        var sha1Hash = HashUtils.Sha1Hex(token);

        await using (var ctx = new NocturneDbContext(_dbOptions) { TenantId = _testTenantId })
        {
            ctx.OAuthGrants.Add(new OAuthGrantEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = _subjectId,
                TenantId = _testTenantId,
                GrantType = OAuthGrantTypes.Direct,
                TokenHash = tokenHash,
                LegacySecretHash = sha1Hash,
                IsMigrated = false,
                Scopes = ["health.readwrite"],
                CreatedAt = DateTime.UtcNow,
                LastUsedAt = null,
            });
            await ctx.SaveChangesAsync();
        }

        var mockNotificationService = new Mock<IInAppNotificationService>();
        var context = CreateHttpContextWithServices(mockNotificationService.Object);
        context.Request.Headers["api-secret"] = sha1Hash;

        var result = await _handler.AuthenticateAsync(context);

        Assert.True(result.Succeeded);
        Assert.Equal(_subjectId, result.AuthContext!.SubjectId);
        Assert.Contains("health.readwrite", result.AuthContext.Scopes);

        await Task.Delay(200);

        mockNotificationService.Verify(s => s.CreateNotificationAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<NotificationCategory?>(), It.IsAny<NotificationUrgency?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<List<NotificationActionDto>?>(),
            It.IsAny<ResolutionConditions?>(), It.IsAny<Dictionary<string, object>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AuthenticateAsync_RevokedGrant_ReturnsFailure()
    {
        var token = "noc_revokedkey123";
        var tokenHash = DirectGrantTokenHandler.ComputeSha256Hex(token);

        await using (var ctx = new NocturneDbContext(_dbOptions) { TenantId = _testTenantId })
        {
            ctx.OAuthGrants.Add(new OAuthGrantEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = _subjectId,
                TenantId = _testTenantId,
                GrantType = OAuthGrantTypes.Direct,
                TokenHash = tokenHash,
                Scopes = ["glucose.read"],
                CreatedAt = DateTime.UtcNow,
                RevokedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var context = CreateHttpContext();
        context.Request.Headers["api-secret"] = token;

        var result = await _handler.AuthenticateAsync(context);

        Assert.False(result.Succeeded);
        Assert.False(result.ShouldSkip);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task AuthenticateAsync_MissingHeader_ReturnsSkip()
    {
        var context = CreateHttpContext();

        var result = await _handler.AuthenticateAsync(context);

        Assert.True(result.ShouldSkip);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task AuthenticateAsync_NoMatchingGrant_ReturnsFailure()
    {
        var context = CreateHttpContext();
        context.Request.Headers["api-secret"] = "noc_nonexistentkey";

        var result = await _handler.AuthenticateAsync(context);

        Assert.False(result.Succeeded);
        Assert.False(result.ShouldSkip);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task AuthenticateAsync_ResolvedGrantScopes_AreUsedNotHardcoded()
    {
        var token = "noc_scopedkey456";
        var tokenHash = DirectGrantTokenHandler.ComputeSha256Hex(token);

        await using (var ctx = new NocturneDbContext(_dbOptions) { TenantId = _testTenantId })
        {
            ctx.OAuthGrants.Add(new OAuthGrantEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = _subjectId,
                TenantId = _testTenantId,
                GrantType = OAuthGrantTypes.Direct,
                TokenHash = tokenHash,
                Scopes = ["glucose.read"],
                CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var context = CreateHttpContext();
        context.Request.Headers["api-secret"] = token;

        var result = await _handler.AuthenticateAsync(context);

        Assert.True(result.Succeeded);
        Assert.Single(result.AuthContext!.Scopes);
        Assert.Equal("glucose.read", result.AuthContext.Scopes[0]);
        Assert.DoesNotContain("*", result.AuthContext.Permissions);
    }

    [Fact]
    public async Task AuthenticateAsync_SecretQueryParam_CheckedWhenHeaderAbsent()
    {
        var token = "noc_queryparam789";
        var tokenHash = DirectGrantTokenHandler.ComputeSha256Hex(token);

        await using (var ctx = new NocturneDbContext(_dbOptions) { TenantId = _testTenantId })
        {
            ctx.OAuthGrants.Add(new OAuthGrantEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = _subjectId,
                TenantId = _testTenantId,
                GrantType = OAuthGrantTypes.Direct,
                TokenHash = tokenHash,
                Scopes = ["glucose.read", "treatments.read"],
                CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var context = CreateHttpContext();
        context.Request.QueryString = new QueryString($"?secret={token}");

        var result = await _handler.AuthenticateAsync(context);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.AuthContext);
        Assert.Equal(AuthType.ApiKey, result.AuthContext!.AuthType);
        Assert.Equal(_subjectId, result.AuthContext.SubjectId);
    }

    [Fact]
    public async Task AuthenticateAsync_GrantExpiringOneTickFromNow_ReturnsSuccess()
    {
        var token = await SeedTokenGrantAsync("noc_expiringkey001", Now.AddTicks(1));

        var result = await _handler.AuthenticateAsync(ApiSecretContext(token));

        Assert.True(result.Succeeded);
        Assert.Equal(_subjectId, result.AuthContext!.SubjectId);
    }

    [Fact]
    public async Task AuthenticateAsync_GrantExpiringExactlyNow_ReturnsFailure()
    {
        var token = await SeedTokenGrantAsync("noc_expiringkey002", Now);

        var result = await _handler.AuthenticateAsync(ApiSecretContext(token));

        Assert.False(result.Succeeded);
        Assert.False(result.ShouldSkip);
    }

    [Fact]
    public async Task AuthenticateAsync_GrantExpiredOneTickAgo_ReturnsFailure()
    {
        var token = await SeedTokenGrantAsync("noc_expiringkey003", Now.AddTicks(-1));

        var result = await _handler.AuthenticateAsync(ApiSecretContext(token));

        Assert.False(result.Succeeded);
        Assert.False(result.ShouldSkip);
    }

    [Fact]
    public async Task AuthenticateAsync_LegacySecretGrantExpiredOneTickAgo_ReturnsFailure()
    {
        var sha1Hash = HashUtils.Sha1Hex("expiredlegacysecret");
        await SeedGrantAsync(tokenHash: null, legacySecretHash: sha1Hash, expiresAt: Now.AddTicks(-1));

        var result = await _handler.AuthenticateAsync(ApiSecretContext(sha1Hash));

        Assert.False(result.Succeeded);
        Assert.False(result.ShouldSkip);
    }

    [Fact]
    public async Task AuthenticateAsync_LegacySecretGrantExpiringOneTickFromNow_ReturnsSuccess()
    {
        var sha1Hash = HashUtils.Sha1Hex("livelegacysecret");
        await SeedGrantAsync(tokenHash: null, legacySecretHash: sha1Hash, expiresAt: Now.AddTicks(1));

        var result = await _handler.AuthenticateAsync(ApiSecretContext(sha1Hash));

        Assert.True(result.Succeeded);
        Assert.Equal(_subjectId, result.AuthContext!.SubjectId);
    }

    [Fact]
    public async Task AuthenticateAsync_GrantWithNoExpiry_ReturnsSuccessYearsAfterCreation()
    {
        var token = await SeedTokenGrantAsync(
            "noc_openendedkey001", expiresAt: null, createdAt: Now.AddYears(-5));

        var result = await _handler.AuthenticateAsync(ApiSecretContext(token));

        Assert.True(result.Succeeded);
        Assert.Equal(_subjectId, result.AuthContext!.SubjectId);
    }

    private async Task<string> SeedTokenGrantAsync(
        string token, DateTime? expiresAt, DateTime? createdAt = null)
    {
        await SeedGrantAsync(
            DirectGrantTokenHandler.ComputeSha256Hex(token), null, expiresAt, createdAt);
        return token;
    }

    private async Task SeedGrantAsync(
        string? tokenHash, string? legacySecretHash, DateTime? expiresAt, DateTime? createdAt = null)
    {
        await using var ctx = new NocturneDbContext(_dbOptions) { TenantId = _testTenantId };
        ctx.OAuthGrants.Add(new OAuthGrantEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = _subjectId,
            TenantId = _testTenantId,
            GrantType = OAuthGrantTypes.Direct,
            TokenHash = tokenHash,
            LegacySecretHash = legacySecretHash,
            Scopes = ["glucose.read"],
            CreatedAt = createdAt ?? Now,
            ExpiresAt = expiresAt,
        });
        await ctx.SaveChangesAsync();
    }

    private DefaultHttpContext ApiSecretContext(string apiSecret)
    {
        var context = CreateHttpContext();
        context.Request.Headers["api-secret"] = apiSecret;
        return context;
    }

    [Fact]
    public void Priority_Is400()
    {
        Assert.Equal(400, _handler.Priority);
    }

    [Fact]
    public void Name_IsApiKeyHandler()
    {
        Assert.Equal("ApiKeyHandler", _handler.Name);
    }

    [Fact]
    public async Task AuthenticateAsync_NoTenantContext_ReturnsFailure()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["api-secret"] = "noc_sometoken";

        var result = await _handler.AuthenticateAsync(context);

        Assert.False(result.Succeeded);
        Assert.False(result.ShouldSkip);
    }

    [Fact]
    public async Task AuthenticateAsync_LegacyGrant_FirstUse_SendsRotationNudge()
    {
        var legacySecret = "firstusesecret";
        var sha1Hash = HashUtils.Sha1Hex(legacySecret);
        var grantId = Guid.CreateVersion7();

        await using (var ctx = new NocturneDbContext(_dbOptions) { TenantId = _testTenantId })
        {
            ctx.OAuthGrants.Add(new OAuthGrantEntity
            {
                Id = grantId,
                SubjectId = _subjectId,
                TenantId = _testTenantId,
                GrantType = OAuthGrantTypes.Direct,
                LegacySecretHash = sha1Hash,
                IsMigrated = true,
                Scopes = ["*"],
                CreatedAt = DateTime.UtcNow,
                LastUsedAt = null,
            });
            await ctx.SaveChangesAsync();
        }

        var mockNotificationService = new Mock<IInAppNotificationService>();
        mockNotificationService
            .Setup(s => s.CreateNotificationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<NotificationCategory?>(), It.IsAny<NotificationUrgency?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<List<NotificationActionDto>?>(),
                It.IsAny<ResolutionConditions?>(), It.IsAny<Dictionary<string, object>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InAppNotificationDto());

        var context = CreateHttpContextWithServices(mockNotificationService.Object);
        context.Request.Headers["api-secret"] = sha1Hash;

        var result = await _handler.AuthenticateAsync(context);

        Assert.True(result.Succeeded);

        // Allow the fire-and-forget task to complete
        await Task.Delay(200);

        mockNotificationService.Verify(s => s.CreateNotificationAsync(
            _subjectId.ToString(),
            "api-key-rotation",
            "Rotate your API key",
            NotificationCategory.ActionRequired,
            NotificationUrgency.Info,
            "key",
            "api-key-handler",
            "Your API key has full access. Create per-device keys with least privilege.",
            grantId.ToString(),
            It.Is<List<NotificationActionDto>>(a => a.Count == 1 && a[0].ActionId == "manage-keys" && a[0].Label == "Manage Keys"),
            It.IsAny<ResolutionConditions?>(),
            It.IsAny<Dictionary<string, object>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AuthenticateAsync_LegacyGrant_AlreadyUsed_DoesNotSendNudge()
    {
        var legacySecret = "alreadyusedsecret";
        var sha1Hash = HashUtils.Sha1Hex(legacySecret);

        await using (var ctx = new NocturneDbContext(_dbOptions) { TenantId = _testTenantId })
        {
            ctx.OAuthGrants.Add(new OAuthGrantEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = _subjectId,
                TenantId = _testTenantId,
                GrantType = OAuthGrantTypes.Direct,
                LegacySecretHash = sha1Hash,
                IsMigrated = true,
                Scopes = ["*"],
                CreatedAt = DateTime.UtcNow,
                LastUsedAt = DateTime.UtcNow.AddDays(-1),
            });
            await ctx.SaveChangesAsync();
        }

        var mockNotificationService = new Mock<IInAppNotificationService>();

        var context = CreateHttpContextWithServices(mockNotificationService.Object);
        context.Request.Headers["api-secret"] = sha1Hash;

        var result = await _handler.AuthenticateAsync(context);

        Assert.True(result.Succeeded);

        await Task.Delay(200);

        mockNotificationService.Verify(s => s.CreateNotificationAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<NotificationCategory?>(), It.IsAny<NotificationUrgency?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<List<NotificationActionDto>?>(),
            It.IsAny<ResolutionConditions?>(), It.IsAny<Dictionary<string, object>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Items["TenantContext"] = new TenantContext(_testTenantId, "default", "Default", true, IsDemo: false);
        return context;
    }

    private DefaultHttpContext CreateHttpContextWithServices(IInAppNotificationService notificationService)
    {
        var services = new ServiceCollection();
        services.AddSingleton(notificationService);
        var serviceProvider = services.BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = serviceProvider,
        };
        context.Items["TenantContext"] = new TenantContext(_testTenantId, "default", "Default", true, IsDemo: false);
        return context;
    }
}

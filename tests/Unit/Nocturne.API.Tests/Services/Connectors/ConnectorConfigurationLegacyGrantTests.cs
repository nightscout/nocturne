using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Services.Realtime;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Tests.Services.Connectors;

public class ConnectorConfigurationLegacyGrantTests : IDisposable
{
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _dbContext;
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();
    private readonly Mock<IAuditContext> _auditContext;
    private readonly Mock<ISecretEncryptionService> _encryptionService;
    private readonly ConnectorConfigurationService _service;

    public ConnectorConfigurationLegacyGrantTests()
    {
        _db = TestDbContextFactory.CreateSqlite();

        _dbContext = _db.CreateContext(_tenantId);

        // Seed required entities for FK constraints
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = "test",
            DisplayName = "Test",
            IsActive = true,
        });
        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = _subjectId,
            Name = "Test User",
            IsActive = true,
        });
        _dbContext.SaveChanges();

        _encryptionService = new Mock<ISecretEncryptionService>();
        _encryptionService.Setup(e => e.IsConfigured).Returns(true);
        _encryptionService
            .Setup(e => e.EncryptSecrets(It.IsAny<Dictionary<string, string>>()))
            .Returns<Dictionary<string, string>>(d => d);

        _auditContext = new Mock<IAuditContext>();
        _auditContext.Setup(a => a.SubjectId).Returns(_subjectId);

        var broadcastService = Mock.Of<ISignalRBroadcastService>();
        var configuration = new ConfigurationBuilder().Build();
        var environment = Mock.Of<IHostEnvironment>();
        var logger = NullLogger<ConnectorConfigurationService>.Instance;

        _service = new ConnectorConfigurationService(
            new ConnectorConfigurationRepository(_dbContext.AsFactory()),
            new SubjectRepository(_dbContext.AsFactory(), AccountLinkRoutes.Off),
            new OAuthGrantRepository(_dbContext.AsFactory()),
            _encryptionService.Object,
            broadcastService,
            _auditContext.Object,
            configuration,
            environment,
            Enumerable.Empty<IConnectorCacheInvalidator>(),
            TimeProvider.System,
            logger);
    }

    private static readonly IReadOnlySet<string> OwnerScopes = new HashSet<string> { Scope.FullAccess };

    private ConnectorSecretSaver Saver => new(new CredentialMaker(_subjectId, AccessEndsAt: null), OwnerScopes, HistoryClamped: false);

    public void Dispose()
    {
        _dbContext.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task SaveSecrets_NightscoutWithApiSecret_CreatesLegacyGrant()
    {
        var apiSecret = "my-nightscout-secret";
        var secrets = new Dictionary<string, string> { ["ApiSecret"] = apiSecret };

        await _service.SaveSecretsAsync("Nightscout", secrets, modifiedBy: null, saver: Saver);

        var grant = await _dbContext.OAuthGrants
            .SingleOrDefaultAsync(g => g.TenantId == _tenantId && g.GrantType == OAuthGrantTypes.Direct);

        grant.Should().NotBeNull();
        grant!.LegacySecretHash.Should().Be(HashUtils.Sha1Hex(apiSecret));
        grant.Label.Should().Be("Nightscout (migrated)");
        grant.TokenHash.Should().BeNull();
        grant.SubjectId.Should().Be((await _dbContext.FindDeviceSubjectOf(_tenantId))!.Value,
            "the secret belongs to the site, so it outlives the membership of whoever saved it");
        grant.Scopes.Should().Contain("glucose.readwrite");
        grant.Scopes.Should().Contain("treatments.readwrite");
        grant.Scopes.Should().Contain("devices.readwrite");
        grant.RevokedAt.Should().BeNull();
        grant.CreatedBySubjectId.Should().Be(_subjectId);
        grant.ExpiresAt.Should().BeNull("a maker whose access does not end makes a credential that does not stop");
    }

    [Fact]
    public async Task SaveSecrets_ByAMakerWhoseAccessEnds_StopsTheCredentialThen()
    {
        var accessEndsAt = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);

        await _service.SaveSecretsAsync(
            "Nightscout", new Dictionary<string, string> { ["ApiSecret"] = "camp-secret" }, modifiedBy: null,
            saver: new ConnectorSecretSaver(new CredentialMaker(_subjectId, accessEndsAt), OwnerScopes, HistoryClamped: false));

        var grant = await _dbContext.OAuthGrants.SingleAsync(g => g.GrantType == OAuthGrantTypes.Direct);
        grant.ExpiresAt.Should().Be(accessEndsAt);
        grant.CreatedBySubjectId.Should().Be(_subjectId);
    }

    [Fact]
    public async Task SaveSecrets_NightscoutSameSecretTwice_DoesNotDuplicate()
    {
        var apiSecret = "my-nightscout-secret";
        var secrets = new Dictionary<string, string> { ["ApiSecret"] = apiSecret };

        await _service.SaveSecretsAsync("Nightscout", secrets, modifiedBy: null, saver: Saver);
        await _service.SaveSecretsAsync("Nightscout", secrets, modifiedBy: null, saver: Saver);

        var grants = await _dbContext.OAuthGrants
            .Where(g => g.TenantId == _tenantId
                     && g.GrantType == OAuthGrantTypes.Direct
                     && g.LegacySecretHash != null)
            .ToListAsync();

        grants.Should().HaveCount(1);
    }

    [Fact]
    public async Task SaveSecrets_NonNightscoutConnector_DoesNotCreateGrant()
    {
        var secrets = new Dictionary<string, string> { ["ApiSecret"] = "some-secret" };

        await _service.SaveSecretsAsync("Dexcom", secrets, modifiedBy: null, saver: Saver);

        var grants = await _dbContext.OAuthGrants
            .Where(g => g.TenantId == _tenantId && g.GrantType == OAuthGrantTypes.Direct)
            .ToListAsync();

        grants.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveSecrets_NightscoutWithoutApiSecret_DoesNotCreateGrant()
    {
        var secrets = new Dictionary<string, string> { ["Url"] = "https://my.nightscout.example" };

        await _service.SaveSecretsAsync("Nightscout", secrets, modifiedBy: null, saver: Saver);

        var grants = await _dbContext.OAuthGrants
            .Where(g => g.TenantId == _tenantId && g.GrantType == OAuthGrantTypes.Direct)
            .ToListAsync();

        grants.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveSecrets_NightscoutWithNoMaker_DoesNotCreateGrant()
    {
        await _service.SaveSecretsAsync("Nightscout", new Dictionary<string, string> { ["ApiSecret"] = "s" }, modifiedBy: null, saver: null);

        (await _dbContext.OAuthGrants.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task SaveSecrets_NightscoutCaseInsensitive_CreatesLegacyGrant()
    {
        var secrets = new Dictionary<string, string> { ["apiSecret"] = "test-secret" };

        await _service.SaveSecretsAsync("nightscout", secrets, modifiedBy: null, saver: Saver);

        var grant = await _dbContext.OAuthGrants
            .SingleOrDefaultAsync(g => g.TenantId == _tenantId && g.GrantType == OAuthGrantTypes.Direct);

        grant.Should().NotBeNull();
        grant!.LegacySecretHash.Should().Be(HashUtils.Sha1Hex("test-secret"));
    }
}
